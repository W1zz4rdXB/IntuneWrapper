using System;
using System.Collections.Generic;
using System.IO;

namespace IntuneWrapper.Core
{
    public enum InputState
    {
        /// <summary>The input folder has no file with this name yet.</summary>
        Missing,
        /// <summary>The same file (same SHA-256) is already in the input folder.</summary>
        Identical,
        /// <summary>A different file with the same name is in the input folder.</summary>
        Different,
        /// <summary>The installer was loaded from the input folder itself.</summary>
        IsSource
    }

    /// <summary>
    /// The packaging folder structure:
    /// <code>
    /// &lt;root&gt;\IntuneWinAppUtil.exe
    /// &lt;root&gt;\&lt;program name&gt;\input\&lt;installer&gt;          (copy of the original)
    /// &lt;root&gt;\&lt;program name&gt;\output\&lt;installer&gt;.intunewin
    /// </code>
    /// IntuneWinAppUtil packages the input folder, so only the installer ends up in the package.
    /// </summary>
    public sealed class PackagingLayout
    {
        public const string InputFolderName = "input";
        public const string OutputFolderName = "output";

        public PackagingLayout(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("The packaging folder is not set.");
            Root = Path.GetFullPath(root.Trim());
        }

        public string Root { get; }

        public string ToolPath => Path.Combine(Root, ToolManager.ToolFileName);

        public string AppFolder(string appName) => Path.Combine(Root, FolderName(appName));
        public string InputFolder(string appName) => Path.Combine(AppFolder(appName), InputFolderName);
        public string OutputFolder(string appName) => Path.Combine(AppFolder(appName), OutputFolderName);

        /// <summary>Folder name for a program: its name with characters Windows does not allow replaced.</summary>
        public static string FolderName(string appName)
        {
            if (CommandGenerator.ContainsPlaceholder(appName))
            {
                throw new PackagingException("Fill in the Name on the App information tab first; it is used as the folder name.");
            }
            string name = Summary.SanitizeFileName(appName.Trim());
            string upper = name.ToUpperInvariant();
            if (upper == "CON" || upper == "PRN" || upper == "AUX" || upper == "NUL" ||
                System.Text.RegularExpressions.Regex.IsMatch(upper, @"^(COM|LPT)\d$"))
            {
                name = "_" + name;
            }
            return name;
        }

        /// <summary>
        /// Copies the additional files/folders into the input folder (folders keep their structure) and writes
        /// install.cmd when <paramref name="script"/> is given. A stale install.cmd generated earlier is removed
        /// when no script is needed any more.
        /// </summary>
        public void PrepareItems(string appName, IEnumerable<PackageItem> items, string script)
        {
            string input = InputFolder(appName);
            try
            {
                Directory.CreateDirectory(input);
                foreach (PackageItem item in items)
                {
                    string target = Path.Combine(input, item.Name);
                    if (string.Equals(Path.GetFullPath(item.SourcePath).TrimEnd('\\'), target, StringComparison.OrdinalIgnoreCase))
                        continue; // already in the input folder
                    if (Directory.Exists(item.SourcePath))
                        CopyDirectory(item.SourcePath, target);
                    else if (File.Exists(item.SourcePath))
                        File.Copy(item.SourcePath, target, true);
                    else
                        throw new PackagingException("An additional item no longer exists: " + item.SourcePath);
                }

                string scriptPath = Path.Combine(input, InstallScript.FileName);
                if (script != null)
                {
                    // ANSI code page, like a batch file written in Notepad, so cmd.exe reads it correctly.
                    File.WriteAllText(scriptPath, script, System.Text.Encoding.Default);
                }
                else if (File.Exists(scriptPath) && File.ReadAllText(scriptPath).Contains(InstallScript.Header))
                {
                    File.Delete(scriptPath);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new PackagingException("The additional files could not be copied to " + input + ":\n" + ex.Message, ex);
            }
        }

        /// <summary>Names expected in the input folder: the setup, the additional items and install.cmd.</summary>
        public static HashSet<string> ExpectedInputNames(string setupFile, IEnumerable<PackageItem> items, bool usesScript)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { setupFile };
            foreach (PackageItem item in items) names.Add(item.Name);
            if (usesScript) names.Add(InstallScript.FileName);
            return names;
        }

        internal static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(target, dir.Substring(source.TrimEnd('\\').Length + 1)));
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(target, file.Substring(source.TrimEnd('\\').Length + 1)), true);
        }

        public InputState CheckInput(string installerPath, string appName)
        {
            string target = Path.Combine(InputFolder(appName), Path.GetFileName(installerPath));
            if (string.Equals(Path.GetFullPath(installerPath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                return InputState.IsSource;
            if (!File.Exists(target))
                return InputState.Missing;
            if (new FileInfo(target).Length != new FileInfo(installerPath).Length)
                return InputState.Different;
            return string.Equals(InstallerInspector.ComputeSha256(target), InstallerInspector.ComputeSha256(installerPath),
                StringComparison.OrdinalIgnoreCase)
                ? InputState.Identical
                : InputState.Different;
        }

        /// <summary>
        /// Creates the program's input and output folders and copies the installer into input (unless it is
        /// already there). A different file with the same name is only replaced when <paramref name="overwrite"/>
        /// is true. Returns the installer's path in the input folder.
        /// </summary>
        public string PrepareInput(string installerPath, string appName, bool overwrite)
        {
            string input = InputFolder(appName);
            string target = Path.Combine(input, Path.GetFileName(installerPath));
            try
            {
                Directory.CreateDirectory(input);
                Directory.CreateDirectory(OutputFolder(appName));
                InputState state = CheckInput(installerPath, appName);
                if (state == InputState.Different && !overwrite)
                {
                    throw new PackagingException("A different file with the same name is already in " + input + ".");
                }
                if (state == InputState.Missing || state == InputState.Different)
                {
                    string temp = target + ".copying";
                    File.Copy(installerPath, temp, true);
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(temp, target);
                    Logger.Info("Copied " + Path.GetFileName(installerPath) + " to " + input);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new PackagingException("The installer could not be copied to " + input + ":\n" + ex.Message, ex);
            }
            return target;
        }
    }
}
