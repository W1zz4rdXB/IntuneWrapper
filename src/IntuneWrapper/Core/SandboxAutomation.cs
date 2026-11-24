using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace IntuneWrapper.Core
{
    /// <summary>What the Sandbox run should do. Written by the host to job.json in the shared work folder.</summary>
    [DataContract]
    public sealed class SandboxJob
    {
        [DataMember(Name = "installerFileName")] public string InstallerFileName { get; set; }
        [DataMember(Name = "installCommand")] public string InstallCommand { get; set; }
        [DataMember(Name = "framework")] public InstallerFramework Framework { get; set; }
        [DataMember(Name = "productName")] public string ProductName { get; set; }
        [DataMember(Name = "installTimeoutMinutes")] public int InstallTimeoutMinutes { get; set; } = 30;
        [DataMember(Name = "uninstallTimeoutMinutes")] public int UninstallTimeoutMinutes { get; set; } = 15;

        /// <summary>How long to keep watching the registry after the install command returns.</summary>
        [DataMember(Name = "settleSeconds")] public int SettleSeconds { get; set; } = 60;
    }

    /// <summary>What happened inside the Sandbox. Written to result.json.</summary>
    [DataContract]
    public sealed class SandboxResult
    {
        [DataMember(Name = "completed")] public bool Completed { get; set; }
        [DataMember(Name = "error")] public string Error { get; set; }

        [DataMember(Name = "installExitCode")] public int? InstallExitCode { get; set; }
        [DataMember(Name = "installTimedOut")] public bool InstallTimedOut { get; set; }
        [DataMember(Name = "installSeconds")] public int InstallSeconds { get; set; }

        [DataMember(Name = "newEntries")] public List<UninstallEntry> NewEntries { get; set; } = new List<UninstallEntry>();
        [DataMember(Name = "changedEntries")] public List<UninstallEntry> ChangedEntries { get; set; } = new List<UninstallEntry>();
        [DataMember(Name = "selectedKeyPath")] public string SelectedKeyPath { get; set; }

        [DataMember(Name = "uninstallCommand")] public string UninstallCommand { get; set; }
        [DataMember(Name = "detection")] public DetectionRule Detection { get; set; }
        [DataMember(Name = "detectionFoundAfterInstall")] public bool DetectionFoundAfterInstall { get; set; }

        [DataMember(Name = "uninstallRan")] public bool UninstallRan { get; set; }
        [DataMember(Name = "uninstallExitCode")] public int? UninstallExitCode { get; set; }
        [DataMember(Name = "uninstallTimedOut")] public bool UninstallTimedOut { get; set; }
        [DataMember(Name = "entryRemovedAfterUninstall")] public bool EntryRemovedAfterUninstall { get; set; }
        [DataMember(Name = "detectionGoneAfterUninstall")] public bool DetectionGoneAfterUninstall { get; set; }

        [DataMember(Name = "log")] public List<string> Log { get; set; } = new List<string>();

        public UninstallEntry Selected =>
            NewEntries.Concat(ChangedEntries).FirstOrDefault(e => e.KeyPath == SelectedKeyPath);

        /// <summary>Install, detection and uninstall all verified.</summary>
        public bool FullyVerified =>
            Completed && Selected != null && DetectionFoundAfterInstall && EntryRemovedAfterUninstall && DetectionGoneAfterUninstall;
    }

    /// <summary>
    /// Runs inside Windows Sandbox (IntuneWrapper.exe --sandbox-capture C:\Work): snapshot, silent install,
    /// snapshot, find the app's entry and main exe, check the File rule, run the uninstall command, and
    /// check that the app is gone. No UI; progress goes to status.txt, the outcome to result.json.
    /// </summary>
    public static class SandboxAgent
    {
        public const string JobFile = "job.json";
        public const string ResultFile = "result.json";
        public const string StatusFile = "status.txt";

        /// <summary>Windows Sandbox always signs in as this built-in account.</summary>
        public const string SandboxUser = "WDAGUtilityAccount";

        public static bool IsInsideSandbox =>
            string.Equals(Environment.UserName, SandboxUser, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Entry point for --sandbox-capture. Refuses to run outside Windows Sandbox: it installs software and
        /// shuts the machine down when done, which must never happen on a real PC.
        /// </summary>
        public static int RunInSandbox(string workDir, string inputDir)
        {
            if (!IsInsideSandbox)
            {
                Logger.Warn("--sandbox-capture refused: not running inside Windows Sandbox (user " + Environment.UserName + ").");
                return 2;
            }
            SandboxResult result = Run(workDir, inputDir);
            try
            {
                // Closing the Sandbox discards it; the host already has result.json.
                Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 15") { UseShellExecute = false, CreateNoWindow = true });
            }
            catch
            {
                // The user can close the window.
            }
            return result.Completed ? 0 : 1;
        }

        public static SandboxResult Run(string workDir, string inputDir)
        {
            var result = new SandboxResult();
            Action<string> status = line =>
            {
                string stamped = DateTime.Now.ToString("HH:mm:ss") + "  " + line;
                result.Log.Add(stamped);
                try
                {
                    File.AppendAllText(Path.Combine(workDir, StatusFile), stamped + Environment.NewLine);
                }
                catch
                {
                    // Status is best effort.
                }
            };

            try
            {
                SandboxJob job = Json.Deserialize<SandboxJob>(File.ReadAllText(Path.Combine(workDir, JobFile)));
                status("Started. Install command: " + job.InstallCommand);

                // Run from a local copy of the whole package (setup, drivers, install.cmd): some installers write
                // next to themselves, and the shared folder is read-only.
                string installDir = Path.Combine(Path.GetTempPath(), "IntuneWrapperInstall");
                if (Directory.Exists(installDir)) Directory.Delete(installDir, true);
                PackagingLayout.CopyDirectory(inputDir, installDir);
                if (!File.Exists(Path.Combine(installDir, job.InstallerFileName)))
                    throw new FileNotFoundException("The setup file is not in the package folder: " + job.InstallerFileName);

                status("Taking the registry snapshot before installing...");
                UninstallSnapshot before = UninstallRegistry.TakeSnapshot();

                status("Installing silently (timeout " + job.InstallTimeoutMinutes + " min)...");
                var watch = Stopwatch.StartNew();
                int? exit = RunCommand(job.InstallCommand, installDir, TimeSpan.FromMinutes(job.InstallTimeoutMinutes));
                result.InstallSeconds = (int)watch.Elapsed.TotalSeconds;
                result.InstallExitCode = exit;
                result.InstallTimedOut = exit == null;
                status(exit == null
                    ? "The install command did not finish within " + job.InstallTimeoutMinutes + " min. It is probably waiting for input (not silent)."
                    : "Install command finished with exit code " + exit + " after " + result.InstallSeconds + " s.");

                // Some installers return before their child processes finish: wait until an entry appears and settles.
                SnapshotDiff diff = WaitForEntries(before, job.SettleSeconds, status);
                result.NewEntries = diff.New;
                result.ChangedEntries = diff.Changed;
                foreach (UninstallEntry e in diff.New.Concat(diff.Changed)) DescribeMainExecutable(e);

                UninstallEntry selected = SelectBest(diff, job.ProductName);
                if (selected == null)
                {
                    status("No new uninstall entry was found.");
                    result.Completed = true;
                    return Finish(result, workDir, status);
                }
                result.SelectedKeyPath = selected.KeyPath;
                status("Found: " + selected.Label + " (" + selected.KeyPath + ")");

                var commands = new PackageCommands();
                CommandGenerator.ApplyCapture(commands, selected, job.Framework);
                result.UninstallCommand = commands.Uninstall.Value;
                result.Detection = commands.Detection;
                result.DetectionFoundAfterInstall = commands.Detection.IsComplete && DetectionPresent(commands.Detection);
                status("Detection rule: " + commands.Detection.Summary(" | ") +
                       (result.DetectionFoundAfterInstall ? "  -> found" : "  -> NOT found"));

                if (!commands.Uninstall.IsPlaceholder)
                {
                    status("Uninstalling with: " + commands.Uninstall.Value);
                    int? uexit = RunCommand(commands.Uninstall.Value, installDir, TimeSpan.FromMinutes(job.UninstallTimeoutMinutes));
                    result.UninstallRan = true;
                    result.UninstallExitCode = uexit;
                    result.UninstallTimedOut = uexit == null;
                    status(uexit == null
                        ? "The uninstall command did not finish within " + job.UninstallTimeoutMinutes + " min (probably not silent)."
                        : "Uninstall command finished with exit code " + uexit + ".");

                    // Uninstallers often hand off to a copy of themselves and return early: poll for removal.
                    for (int i = 0; i < Math.Max(1, job.SettleSeconds / 5); i++)
                    {
                        result.EntryRemovedAfterUninstall = !UninstallRegistry.TakeSnapshot().Entries.Any(e => e.KeyPath == selected.KeyPath);
                        result.DetectionGoneAfterUninstall = !DetectionPresent(commands.Detection);
                        if (result.EntryRemovedAfterUninstall && result.DetectionGoneAfterUninstall) break;
                        Thread.Sleep(5000);
                    }
                    status("After uninstall: entry " + (result.EntryRemovedAfterUninstall ? "removed" : "STILL PRESENT") +
                           ", detection file " + (result.DetectionGoneAfterUninstall ? "gone" : "STILL PRESENT") + ".");
                }
                result.Completed = true;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                status("ERROR: " + ex);
            }
            return Finish(result, workDir, status);
        }

        private static SandboxResult Finish(SandboxResult result, string workDir, Action<string> status)
        {
            status("Done.");
            Json.WriteFileAtomic(Path.Combine(workDir, ResultFile), Json.Serialize(result));
            return result;
        }

        private static SnapshotDiff WaitForEntries(UninstallSnapshot before, int settleSeconds, Action<string> status)
        {
            SnapshotDiff diff = UninstallRegistry.Compare(before.Entries, UninstallRegistry.TakeSnapshot().Entries);
            int stableRounds = 0;
            int previous = diff.New.Count;
            for (int waited = 0; waited < settleSeconds; waited += 5)
            {
                if (diff.New.Count > 0 && stableRounds >= 1) break;
                if (waited == 0) status("Waiting for the installation to settle...");
                Thread.Sleep(5000);
                diff = UninstallRegistry.Compare(before.Entries, UninstallRegistry.TakeSnapshot().Entries);
                stableRounds = diff.New.Count == previous ? stableRounds + 1 : 0;
                previous = diff.New.Count;
            }
            status(diff.New.Count + " new and " + diff.Changed.Count + " changed uninstall entries.");
            return diff;
        }

        /// <summary>Records the main exe as an Intune path, so the host can build the rule without the files.</summary>
        internal static void DescribeMainExecutable(UninstallEntry entry)
        {
            string exe = CommandGenerator.FindMainExecutable(entry);
            if (exe == null || !File.Exists(exe)) return;
            entry.MainExecutable = Portal.ToIntunePath(Path.GetDirectoryName(exe)) + "\\" + Path.GetFileName(exe);
            entry.MainExecutableVersion = CommandGenerator.ReadFileVersion(exe);
        }

        /// <summary>The app's own entry: visible, named like the product, with an uninstall command.</summary>
        public static UninstallEntry SelectBest(SnapshotDiff diff, string productName)
        {
            string product = MsiFileLocator.Normalize(productName);
            Func<UninstallEntry, int> score = e =>
                (e.SystemComponent ? 0 : 4) +
                (string.IsNullOrWhiteSpace(e.DisplayName) ? 0 : 2) +
                (string.IsNullOrWhiteSpace(e.UninstallString) && string.IsNullOrWhiteSpace(e.QuietUninstallString) ? 0 : 2) +
                (product.Length > 0 && MsiFileLocator.Normalize(e.DisplayName).Contains(product) ? 3 : 0) +
                (e.MainExecutable != null ? 1 : 0);
            return diff.New.OrderByDescending(score).FirstOrDefault()
                   ?? diff.Changed.OrderByDescending(score).FirstOrDefault();
        }

        /// <summary>Evaluates a File rule on this machine the way the Intune agent would.</summary>
        public static bool DetectionPresent(DetectionRule rule)
        {
            if (rule == null || rule.Type != DetectionRuleType.File || !rule.IsComplete) return false;
            string folder = rule.FilePath;
            if (rule.File32Bit && Environment.Is64BitOperatingSystem)
            {
                folder = folder.Replace("%ProgramFiles%", "%ProgramFiles(x86)%")
                               .Replace("%CommonProgramFiles%", "%CommonProgramFiles(x86)%");
            }
            string path = Path.Combine(Environment.ExpandEnvironmentVariables(folder), rule.FileOrFolder);
            bool exists = File.Exists(path) || Directory.Exists(path);
            switch (rule.FileMethod)
            {
                case FileDetectionMethod.DoesNotExist:
                    return !exists;
                case FileDetectionMethod.Version:
                    if (!File.Exists(path)) return false;
                    Version actual, wanted;
                    return Version.TryParse(CommandGenerator.ReadFileVersion(path) ?? "", out actual)
                           && Version.TryParse(Pad(rule.FileValue), out wanted)
                           && Compare(actual, wanted, rule.FileOperator);
                default:
                    return exists;
            }
        }

        private static string Pad(string version)
        {
            string v = (version ?? "").Trim();
            return v.Contains(".") ? v : v + ".0";
        }

        private static bool Compare(Version a, Version b, ComparisonOperator op)
        {
            int c = a.CompareTo(b);
            switch (op)
            {
                case ComparisonOperator.Equals: return c == 0;
                case ComparisonOperator.NotEqualTo: return c != 0;
                case ComparisonOperator.GreaterThan: return c > 0;
                case ComparisonOperator.GreaterThanOrEqualTo: return c >= 0;
                case ComparisonOperator.LessThan: return c < 0;
                default: return c <= 0;
            }
        }

        /// <summary>Runs a command line through cmd.exe like Intune does. Returns the exit code, or null on timeout.</summary>
        public static int? RunCommand(string commandLine, string workingDirectory, TimeSpan timeout)
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                "/d /s /c \"" + commandLine + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory,
            };
            // Intune runs commands like "setup.exe" -q from the package folder, which relies on cmd finding
            // programs in the current directory. Some environments switch that off; switch it back on.
            psi.EnvironmentVariables.Remove("NoDefaultCurrentDirectoryInExePath");
            using (Process p = Process.Start(psi))
            {
                if (p.WaitForExit((int)Math.Min(int.MaxValue, timeout.TotalMilliseconds))) return p.ExitCode;
                try
                {
                    p.Kill();
                }
                catch
                {
                    // Already gone.
                }
                return null;
            }
        }
    }

    /// <summary>Host side: checks Windows Sandbox, prepares the shared folder and .wsb file, and waits for the result.</summary>
    public static class SandboxLauncher
    {
        public static bool IsAvailable =>
            File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsSandbox.exe")) ||
            File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative", "WindowsSandbox.exe"));

        /// <summary>Only one Windows Sandbox can run at a time.</summary>
        public static bool IsRunning =>
            new[] { "WindowsSandbox", "WindowsSandboxClient", "WindowsSandboxRemoteSession" }
                .Any(n => Process.GetProcessesByName(n).Length > 0);

        /// <summary>Command that turns the feature on (run elevated; a restart is needed).</summary>
        public const string EnableArguments = "/online /enable-feature /featurename:Containers-DisposableClientVM /all /norestart";

        /// <summary>Creates the work folder with the agent exe, job.json and the .wsb file. Returns the .wsb path.</summary>
        public static string Prepare(string agentExe, string installerPath, SandboxJob job, string workDir)
        {
            Directory.CreateDirectory(workDir);
            File.Copy(agentExe, Path.Combine(workDir, "IntuneWrapper.exe"), true);
            Json.WriteFileAtomic(Path.Combine(workDir, SandboxAgent.JobFile), Json.Serialize(job));
            string wsb = Path.Combine(workDir, "IntuneWrapper-capture.wsb");
            File.WriteAllText(wsb, BuildConfiguration(Path.GetDirectoryName(installerPath), workDir));
            return wsb;
        }

        /// <summary>
        /// The Sandbox configuration: the installer's folder read-only at C:\Input, the work folder writable at
        /// C:\Work, and the agent started at logon. vGPU is off for a faster, lighter Sandbox.
        /// </summary>
        public static string BuildConfiguration(string inputFolder, string workFolder)
        {
            return
                "<Configuration>\r\n" +
                "  <VGpu>Disable</VGpu>\r\n" +
                "  <MappedFolders>\r\n" +
                "    <MappedFolder>\r\n" +
                "      <HostFolder>" + SecurityElement.Escape(inputFolder) + "</HostFolder>\r\n" +
                "      <SandboxFolder>C:\\Input</SandboxFolder>\r\n" +
                "      <ReadOnly>true</ReadOnly>\r\n" +
                "    </MappedFolder>\r\n" +
                "    <MappedFolder>\r\n" +
                "      <HostFolder>" + SecurityElement.Escape(workFolder) + "</HostFolder>\r\n" +
                "      <SandboxFolder>C:\\Work</SandboxFolder>\r\n" +
                "      <ReadOnly>false</ReadOnly>\r\n" +
                "    </MappedFolder>\r\n" +
                "  </MappedFolders>\r\n" +
                "  <LogonCommand>\r\n" +
                "    <Command>C:\\Work\\IntuneWrapper.exe --sandbox-capture C:\\Work C:\\Input</Command>\r\n" +
                "  </LogonCommand>\r\n" +
                "</Configuration>\r\n";
        }

        public static void Start(string wsbPath)
        {
            Process.Start(new ProcessStartInfo(wsbPath) { UseShellExecute = true });
        }

        /// <summary>Waits for result.json, reporting new status lines. Returns null when cancelled or timed out.</summary>
        public static async Task<SandboxResult> WaitAsync(string workDir, TimeSpan timeout, Action<string> onStatus,
            CancellationToken cancellationToken)
        {
            string resultFile = Path.Combine(workDir, SandboxAgent.ResultFile);
            string statusFile = Path.Combine(workDir, SandboxAgent.StatusFile);
            int linesSeen = 0;
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (File.Exists(statusFile))
                    {
                        string[] lines = ReadShared(statusFile).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                        for (; linesSeen < lines.Length; linesSeen++) onStatus?.Invoke(lines[linesSeen]);
                    }
                    if (File.Exists(resultFile))
                    {
                        return Json.Deserialize<SandboxResult>(ReadShared(resultFile));
                    }
                }
                catch (IOException)
                {
                    // Being written; try again.
                }
                catch (SerializationException)
                {
                    // Partially written; try again.
                }
                await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
            }
            return null;
        }

        private static string ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs))
            {
                return reader.ReadToEnd();
            }
        }

        /// <summary>Closes the Sandbox window (discarding it), e.g. after cancel.</summary>
        public static void Close()
        {
            foreach (string name in new[] { "WindowsSandboxClient", "WindowsSandboxRemoteSession", "WindowsSandbox" })
            {
                foreach (Process p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        p.CloseMainWindow();
                        if (!p.WaitForExit(3000)) p.Kill();
                    }
                    catch
                    {
                        // Not ours to close, or already gone.
                    }
                }
            }
        }
    }
}
