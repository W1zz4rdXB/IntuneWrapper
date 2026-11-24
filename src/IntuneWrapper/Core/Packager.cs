using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IntuneWrapper.Core
{
    public sealed class PackagingRequest
    {
        public string ToolPath { get; set; }
        public string SourceFolder { get; set; }
        public string SetupFile { get; set; }
        public string OutputFolder { get; set; }

        /// <summary>Set only after the user confirmed overwriting an existing package (FR-11).</summary>
        public bool Overwrite { get; set; }

        public string ExpectedOutputPath =>
            Path.Combine(OutputFolder, Path.GetFileNameWithoutExtension(SetupFile) + ".intunewin");
    }

    public sealed class PackagingResult
    {
        public bool Success { get; set; }
        public bool Cancelled { get; set; }
        public int? ExitCode { get; set; }
        public string OutputPath { get; set; }
        public long OutputSize { get; set; }
        public string Arguments { get; set; }
        public List<string> Log { get; } = new List<string>();

        /// <summary>Plain-language explanation for the user (FR-25).</summary>
        public string Message { get; set; }

        public IEnumerable<string> LastLogLines(int count) => Log.Skip(Math.Max(0, Log.Count - count));
    }

    /// <summary>Thrown before a conversion starts, for problems the user can fix.</summary>
    public sealed class PackagingException : Exception
    {
        public PackagingException(string message, Exception inner = null) : base(message, inner)
        {
        }
    }

    /// <summary>Runs IntuneWinAppUtil.exe (SPEC 4.6, 9.4 Invoke-IntunePackage).</summary>
    public static class Packager
    {
        /// <summary>
        /// -c source -s setup -o output -q. Quiet mode makes the tool overwrite silently, so the
        /// overwrite decision is taken before this runs (see <see cref="RunAsync"/>).
        /// </summary>
        public static string BuildArguments(PackagingRequest request)
        {
            return CommandLine.Join(new[]
            {
                new KeyValuePair<string, string>("-c", request.SourceFolder),
                new KeyValuePair<string, string>("-s", request.SetupFile),
                new KeyValuePair<string, string>("-o", request.OutputFolder),
                new KeyValuePair<string, string>("-q", null),
            });
        }

        /// <summary>Creates the output folder and proves it is writable (SPEC 7).</summary>
        public static void EnsureOutputWritable(string outputFolder)
        {
            try
            {
                Directory.CreateDirectory(outputFolder);
                string probe = Path.Combine(outputFolder, ".intunewrapper-write-test-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "");
                File.Delete(probe);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException
                                       || ex is ArgumentException)
            {
                throw new PackagingException("The output folder is not writable: " + outputFolder +
                                             Environment.NewLine + "Choose another folder in Settings or with Browse.", ex);
            }
        }

        public static void Validate(PackagingRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.ToolPath) || !File.Exists(request.ToolPath))
            {
                throw new PackagingException("IntuneWinAppUtil.exe was not found. Use Tools > Locate or download the tool.");
            }
            string setup = Path.Combine(request.SourceFolder, request.SetupFile);
            if (!File.Exists(setup))
            {
                throw new PackagingException("The installer no longer exists: " + setup);
            }
            try
            {
                using (new FileStream(setup, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new PackagingException("The installer cannot be read: " + setup + Environment.NewLine +
                                             "Close any program that is using it and try again.", ex);
            }
            if (string.Equals(Path.GetFullPath(request.SourceFolder).TrimEnd('\\'),
                    Path.GetFullPath(request.OutputFolder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                throw new PackagingException("The output folder is the same as the installer's folder, so earlier packages " +
                                             "would be packaged too. Choose a different output folder.");
            }
            EnsureOutputWritable(request.OutputFolder);
            if (File.Exists(request.ExpectedOutputPath) && !request.Overwrite)
            {
                throw new PackagingException("A package with this name already exists and overwriting was not confirmed: " +
                                             request.ExpectedOutputPath);
            }
        }

        public static async Task<PackagingResult> RunAsync(PackagingRequest request, Action<string> onOutput,
            CancellationToken cancellationToken)
        {
            Validate(request);
            var result = new PackagingResult { Arguments = BuildArguments(request) };
            string output = request.ExpectedOutputPath;

            // Move an existing package aside instead of deleting it, so a failed run can restore it.
            string backup = null;
            if (File.Exists(output))
            {
                backup = output + ".previous";
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(output, backup);
            }

            Action<string> log = line =>
            {
                if (line == null) return;
                lock (result.Log) result.Log.Add(line);
                onOutput?.Invoke(line);
            };

            Logger.Info("Packaging: \"" + request.ToolPath + "\" " + result.Arguments);
            try
            {
                result.ExitCode = await RunProcessAsync(request.ToolPath, result.Arguments, request.SourceFolder, log,
                    cancellationToken).ConfigureAwait(false);
                Logger.Info("IntuneWinAppUtil exit code: " + result.ExitCode);
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                result.Message = "Conversion cancelled.";
                TryDelete(output);
                Restore(backup, output);
                Logger.Info("Packaging cancelled by user.");
                return result;
            }
            catch (Exception ex)
            {
                Restore(backup, output);
                result.Message = "IntuneWinAppUtil.exe could not be started: " + ex.Message;
                Logger.Error("Failed to start IntuneWinAppUtil", ex);
                return result;
            }

            // Exit code 0 is not proof of success: the file must exist (prototype issue).
            var file = new FileInfo(output);
            if (result.ExitCode == 0 && file.Exists && file.Length > 0)
            {
                result.Success = true;
                result.OutputPath = output;
                result.OutputSize = file.Length;
                result.Message = "Package created.";
                TryDelete(backup);
            }
            else
            {
                if (!file.Exists || file.Length == 0) TryDelete(output);
                Restore(backup, output);
                result.Message = ExplainFailure(result.ExitCode, file.Exists, result.Log);
            }
            return result;
        }

        internal static string ExplainFailure(int? exitCode, bool outputExists, IList<string> log)
        {
            string text = string.Join("\n", log).ToLowerInvariant();
            var causes = new List<string>();
            if (text.Contains("access") && text.Contains("denied"))
                causes.Add("Access was denied to the source or output folder.");
            if (text.Contains("not found") || text.Contains("does not exist") || text.Contains("could not find"))
                causes.Add("A path was not found. Check that the installer and folders still exist.");
            if (text.Contains("being used by another process"))
                causes.Add("A file is in use. Close programs using the installer or the output file.");
            if (text.Contains("disk") && text.Contains("space"))
                causes.Add("The disk is full.");
            if (text.Contains("too long"))
                causes.Add("A path is too long. Move the installer to a shorter folder path.");
            if (causes.Count == 0)
                causes.Add("Common causes: the source folder is not accessible, the output folder is not writable, " +
                           "there is not enough disk space, or the installer is in use.");

            string head = exitCode == 0 && !outputExists
                ? "IntuneWinAppUtil finished without an error code, but no .intunewin file was created."
                : "IntuneWinAppUtil failed with exit code " + exitCode + ".";
            return head + " " + string.Join(" ", causes) + " See the log for details.";
        }

        private static Task<int> RunProcessAsync(string exe, string arguments, string workingDirectory,
            Action<string> log, CancellationToken cancellationToken)
        {
            var psi = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                WorkingDirectory = workingDirectory,
            };

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var outputDone = new CountdownEvent(2);

            process.OutputDataReceived += (s, e) => { if (e.Data == null) outputDone.Signal(); else log(e.Data); };
            process.ErrorDataReceived += (s, e) => { if (e.Data == null) outputDone.Signal(); else log("[stderr] " + e.Data); };
            process.Exited += (s, e) =>
            {
                outputDone.Wait(TimeSpan.FromSeconds(5));
                try
                {
                    tcs.TrySetResult(process.ExitCode);
                }
                finally
                {
                    process.Dispose();
                }
            };

            process.Start();
            // Close stdin so the tool can never block on a prompt.
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() =>
                {
                    try
                    {
                        if (!process.HasExited) process.Kill();
                    }
                    catch
                    {
                        // Already exited or disposed.
                    }
                    tcs.TrySetCanceled();
                });
            }
            return tcs.Task;
        }

        private static void Restore(string backup, string output)
        {
            if (backup == null || !File.Exists(backup)) return;
            try
            {
                if (File.Exists(output)) File.Delete(output);
                File.Move(backup, output);
            }
            catch (Exception ex)
            {
                Logger.Warn("Could not restore previous package " + backup + ": " + ex.Message);
            }
        }

        private static void TryDelete(string path)
        {
            if (path == null) return;
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // Ignore.
            }
        }
    }
}
