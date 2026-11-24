using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace IntuneWrapper.Core
{
    /// <summary>How sure the app is about a generated value (FR-13).</summary>
    public enum Confidence
    {
        /// <summary>Read from the file itself.</summary>
        Detected,
        /// <summary>Inferred from a known framework: verify it.</summary>
        Suggested,
        /// <summary>Placeholder that must be filled in.</summary>
        Unknown,
        /// <summary>Changed by the user in this session.</summary>
        Edited
    }

    public enum RestartBehavior
    {
        Suppress,
        ReturnCodes,
        AllowRestart,
        ForceRestart
    }

    public sealed class CommandField
    {
        public CommandField(string value, Confidence confidence)
        {
            Value = value;
            Confidence = confidence;
        }

        public string Value { get; set; }
        public Confidence Confidence { get; set; }

        /// <summary>True when the value is empty or still contains one of the app's &lt;UPPERCASE: ...&gt; placeholders.</summary>
        public bool IsPlaceholder => CommandGenerator.ContainsPlaceholder(Value);
    }

    /// <summary>
    /// Everything needed to create the Win32 app in the Intune admin center, grouped like the wizard:
    /// App information, Program, Requirements, Detection rules.
    /// </summary>
    public sealed class PackageCommands
    {
        // App information
        public CommandField AppName { get; set; } = new CommandField("", Confidence.Unknown);
        public CommandField Description { get; set; } = new CommandField("", Confidence.Unknown);
        public CommandField Publisher { get; set; } = new CommandField("", Confidence.Unknown);
        public CommandField AppVersion { get; set; } = new CommandField("", Confidence.Unknown);

        // Program
        public CommandField Install { get; set; } = new CommandField("", Confidence.Unknown);
        public CommandField Uninstall { get; set; } = new CommandField(CommandGenerator.UninstallPlaceholder, Confidence.Unknown);
        public int InstallTimeMinutes { get; set; } = Portal.DefaultInstallTimeMinutes;
        public bool AllowAvailableUninstall { get; set; } = true;
        public InstallContext InstallContext { get; set; }
        public RestartBehavior Restart { get; set; }

        // Requirements
        public OsArchitecture Architecture { get; set; } = OsArchitecture.X64;
        public Confidence ArchitectureConfidence { get; set; } = Confidence.Suggested;
        public string MinimumOs { get; set; } = Portal.DefaultMinimumOs;

        // Detection rules
        public DetectionRule Detection { get; set; } = DetectionRule.Unknown();

        /// <summary>Additional files and folders packaged with the setup (e.g. drivers).</summary>
        public List<PackageItem> Items { get; set; } = new List<PackageItem>();

        /// <summary>True when install.cmd is generated to run the setup plus additional installers/drivers.</summary>
        public bool UsesInstallScript => InstallScript.IsNeeded(Items);

        /// <summary>
        /// The command to enter in Intune: the setup command, or install.cmd when the package also installs
        /// drivers or other installers (the setup command then runs inside install.cmd).
        /// </summary>
        public string IntuneInstallCommand => UsesInstallScript ? InstallScript.IntuneCommand : Install.Value;

        public List<string> Notes { get; set; } = new List<string>();

        /// <summary>Required portal fields that are empty or still placeholders, by wizard step.</summary>
        public List<string> MissingRequired()
        {
            var missing = new List<string>();
            if (AppName.IsPlaceholder) missing.Add("App information: Name");
            if (Description.IsPlaceholder) missing.Add("App information: Description");
            if (Publisher.IsPlaceholder) missing.Add("App information: Publisher");
            if (Install.IsPlaceholder) missing.Add("Program: Install command");
            if (Uninstall.IsPlaceholder) missing.Add("Program: Uninstall command");
            if (Architecture == OsArchitecture.None) missing.Add("Requirements: Operating system architecture");
            if (string.IsNullOrWhiteSpace(MinimumOs)) missing.Add("Requirements: Minimum operating system");
            foreach (string field in Detection.MissingFields()) missing.Add("Detection rules: " + field);
            return missing;
        }

        /// <summary>True when something still needs the user's attention (missing field or Unknown value).</summary>
        public bool NeedsAttention =>
            MissingRequired().Count > 0
            || Install.Confidence == Confidence.Unknown || Uninstall.Confidence == Confidence.Unknown
            || Detection.Confidence == Confidence.Unknown;
    }

    public static class CommandGenerator
    {
        private static readonly Regex PlaceholderPattern = new Regex(@"<[A-Z][A-Z ]+:[^>]*>");

        public static bool ContainsPlaceholder(string value)
        {
            return string.IsNullOrWhiteSpace(value) || PlaceholderPattern.IsMatch(value);
        }

        public const string UninstallPlaceholder = "<UNINSTALL COMMAND: use Capture mode>";
        public const string UninstallStringPlaceholder = "<UNINSTALL STRING: use Capture mode>";
        public const string PublisherPlaceholder = "<PUBLISHER: required>";
        public const string DescriptionPlaceholder = "<DESCRIPTION: required>";

        /// <summary>Silent switches appended to a captured UninstallString, per framework (SPEC 5.2).</summary>
        public static string UninstallSwitches(InstallerFramework framework)
        {
            switch (framework)
            {
                case InstallerFramework.InnoSetup: return "/VERYSILENT /NORESTART";
                case InstallerFramework.Nsis: return "/S";
                case InstallerFramework.InstallShield: return "/s /v\"/qn\"";
                case InstallerFramework.Install4j: return "-q";
                default: return null;
            }
        }

        public static PackageCommands Generate(InstallerInfo info, InstallContext context, string minimumOs = Portal.DefaultMinimumOs)
        {
            PackageCommands result = info.Type == InstallerType.Msi ? ForMsi(info) : ForExe(info);
            FillAppInformation(result, info);
            result.InstallContext = context;
            result.Restart = RestartBehavior.Suppress;
            result.MinimumOs = string.IsNullOrWhiteSpace(minimumOs) ? Portal.DefaultMinimumOs : minimumOs;
            if (result.Uninstall.IsPlaceholder)
            {
                result.Notes.Add("WARNING: The uninstall command is a placeholder. Fill it in or use Capture uninstall before uploading to Intune.");
            }
            return result;
        }

        private static void FillAppInformation(PackageCommands result, InstallerInfo info)
        {
            bool hasName = !string.IsNullOrWhiteSpace(info.ProductName);
            result.AppName = new CommandField(hasName ? info.ProductName.Trim() : Path.GetFileNameWithoutExtension(info.Path),
                hasName ? Confidence.Detected : Confidence.Suggested);

            string description = !string.IsNullOrWhiteSpace(info.Description) &&
                                 !string.Equals(info.Description, info.ProductName, StringComparison.OrdinalIgnoreCase)
                ? info.Description.Trim()
                : result.AppName.Value;
            result.Description = new CommandField(description, Confidence.Suggested);

            result.Publisher = string.IsNullOrWhiteSpace(info.Publisher)
                ? new CommandField(PublisherPlaceholder, Confidence.Unknown)
                : new CommandField(info.Publisher.Trim(), Confidence.Detected);

            result.AppVersion = string.IsNullOrWhiteSpace(info.ProductVersion)
                ? new CommandField("", Confidence.Unknown)
                : new CommandField(info.ProductVersion.Trim(), Confidence.Detected);
        }

        private static PackageCommands ForMsi(InstallerInfo info)
        {
            var result = new PackageCommands
            {
                Install = new CommandField("msiexec /i " + CommandLine.Quote(info.FileName, force: true) + " /qn /norestart",
                    Confidence.Detected)
            };

            OsArchitecture arch = info.MsiTemplate != null ? Portal.ArchitectureFromMsiTemplate(info.MsiTemplate) : OsArchitecture.None;
            if (arch != OsArchitecture.None)
            {
                result.Architecture = arch;
                result.ArchitectureConfidence = Confidence.Detected;
            }

            if (info.ProductCode != null)
            {
                result.Uninstall = new CommandField("msiexec /x " + info.ProductCode + " /qn /norestart", Confidence.Detected);
                result.Notes.Add("MSI: product code read from the Property table. Commands are standard msiexec switches.");
            }
            else
            {
                result.Uninstall = new CommandField("msiexec /x " + DetectionRule.ProductCodePlaceholder + " /qn /norestart", Confidence.Unknown);
                result.Notes.Add("WARNING: The MSI product code could not be read (" + info.MsiReadError +
                                 "). Look it up with Orca or install the MSI on a test machine.");
            }

            // Detection is always a File rule: the main exe from the MSI's File and Directory tables.
            if (info.MsiMainFile != null)
            {
                result.Detection = DetectionRule.ForFile(info.MsiMainFile, Confidence.Suggested);
                result.Notes.Add("Detection: " + info.MsiMainFile.FileName + " in " + info.MsiMainFile.Folder +
                                 ", taken from the MSI's file tables (default install folder). Verify the path if the " +
                                 "install command changes the folder (e.g. INSTALLDIR=...).");
            }
            else
            {
                result.Detection = DetectionRule.Unknown();
                result.Notes.Add("Detection: no executable was found in the MSI's file tables. Fill in the File rule " +
                                 "(Path and File or folder) after a test install.");
            }
            if (info.ProductCode != null)
            {
                result.Detection.MsiProductCode = info.ProductCode;
                result.Detection.MsiVersion = info.ProductVersion ?? "";
            }
            return result;
        }

        private static PackageCommands ForExe(InstallerInfo info)
        {
            string file = CommandLine.Quote(info.FileName, force: true);
            var result = new PackageCommands();
            string name = SignatureScanner.DisplayName(info.Framework);

            switch (info.Framework)
            {
                case InstallerFramework.InnoSetup:
                    result.Install = new CommandField(file + " /VERYSILENT /SUPPRESSMSGBOXES /NORESTART", Confidence.Suggested);
                    result.Uninstall = new CommandField(
                        "\"" + UninstallStringPlaceholder + "\" " + UninstallSwitches(info.Framework), Confidence.Unknown);
                    break;
                case InstallerFramework.Nsis:
                    result.Install = new CommandField(file + " /S", Confidence.Suggested);
                    result.Uninstall = new CommandField(
                        "\"" + UninstallStringPlaceholder + "\" " + UninstallSwitches(info.Framework), Confidence.Unknown);
                    result.Notes.Add("NSIS: /S is case-sensitive. Some NSIS uninstallers return before removal finishes; " +
                                     "add _?=<install folder> to make them wait.");
                    break;
                case InstallerFramework.InstallShield:
                    result.Install = new CommandField(file + " /s /v\"/qn /norestart\"", Confidence.Suggested);
                    result.Uninstall = new CommandField(
                        "\"" + UninstallStringPlaceholder + "\" " + UninstallSwitches(info.Framework), Confidence.Unknown);
                    result.Notes.Add("InstallShield: /v passes switches to the embedded MSI. Older InstallScript " +
                                     "(non-MSI) setups need a recorded setup.iss response file instead.");
                    break;
                case InstallerFramework.WixBurn:
                    result.Install = new CommandField(file + " /quiet /norestart", Confidence.Suggested);
                    result.Uninstall = new CommandField(UninstallPlaceholder, Confidence.Unknown);
                    result.Notes.Add("WiX Burn: the bundle registers a QuietUninstallString. Capture mode will pick it up.");
                    break;
                case InstallerFramework.Squirrel:
                    result.Install = new CommandField(file + " --silent", Confidence.Suggested);
                    result.Uninstall = new CommandField(UninstallPlaceholder, Confidence.Unknown);
                    result.Notes.Add("Squirrel installs per user into %LocalAppData%. Use the User install context.");
                    break;
                case InstallerFramework.Install4j:
                    result.Install = new CommandField(file + " -q", Confidence.Suggested);
                    result.Uninstall = new CommandField(
                        "\"" + UninstallStringPlaceholder + "\" " + UninstallSwitches(info.Framework), Confidence.Unknown);
                    result.Notes.Add("install4j: -q runs unattended. Add -dir \"<folder>\" to choose the install folder. " +
                                     "The uninstaller (uninstall.exe in the install folder) also takes -q.");
                    break;
                default:
                    result.Install = new CommandField(file + " /S", Confidence.Unknown);
                    result.Uninstall = new CommandField(UninstallPlaceholder, Confidence.Unknown);
                    result.Notes.Add("WARNING: Installer framework not recognised. \"/S\" is only a guess: check the vendor's " +
                                     "documentation or run the installer with /? on a test machine.");
                    break;
            }

            if (info.Framework != InstallerFramework.Unknown)
            {
                result.Notes.Insert(0, "EXE built with " + name + " (signature found in the file). Switches are the framework " +
                                       "defaults: verify them on a test machine.");
                result.Notes.Add("The uninstall path depends on the registry entry the installer creates. Use Capture uninstall " +
                                 "to read the real command.");
            }

            result.Architecture = OsArchitecture.X64;
            result.ArchitectureConfidence = Confidence.Suggested;
            result.Notes.Add("Requirements: x64 is suggested because most clients are 64-bit. Add x86 or ARM64 if you deploy to those.");
            result.Detection = DetectionRule.Unknown();
            return result;
        }

        private static readonly Regex MsiExecUninstall = new Regex(
            @"msiexec(\.exe)?\s+.*?/[IX]\s*(?<code>\{[0-9A-F\-]{36}\})", RegexOptions.IgnoreCase);

        /// <summary>
        /// Turns a captured registry entry into an uninstall command and detection rule (FR-18, FR-19).
        /// Prefers QuietUninstallString, converts MSI entries to msiexec /x, and otherwise appends the
        /// framework's silent switches to UninstallString. Missing app information is filled in too.
        /// </summary>
        public static void ApplyCapture(PackageCommands commands, UninstallEntry entry, InstallerFramework framework)
        {
            commands.Notes.RemoveAll(n => n.StartsWith("WARNING: The uninstall command is a placeholder", StringComparison.Ordinal)
                                          || n.StartsWith("Captured:", StringComparison.Ordinal));

            Match msi = string.IsNullOrEmpty(entry.UninstallString) ? Match.Empty : MsiExecUninstall.Match(entry.UninstallString);
            string productCode = entry.IsMsiProductCodeKey ? entry.KeyName : (msi.Success ? msi.Groups["code"].Value.ToUpperInvariant() : null);

            if (!string.IsNullOrWhiteSpace(entry.QuietUninstallString))
            {
                commands.Uninstall = new CommandField(entry.QuietUninstallString.Trim(), Confidence.Detected);
                commands.Notes.Add("Captured: QuietUninstallString from " + entry.KeyPath + ".");
            }
            else if (productCode != null && (entry.WindowsInstaller || msi.Success))
            {
                commands.Uninstall = new CommandField("msiexec /x " + productCode + " /qn /norestart", Confidence.Detected);
                commands.Notes.Add("Captured: Windows Installer entry " + productCode + ", uninstalled with msiexec /x.");
            }
            else if (!string.IsNullOrWhiteSpace(entry.UninstallString))
            {
                string raw = entry.UninstallString.Trim();
                InstallerFramework effective = framework;
                if (UninstallSwitches(effective) == null)
                {
                    effective = GuessFrameworkFromUninstaller(raw);
                }
                string switches = UninstallSwitches(effective);
                if (switches != null)
                {
                    string command = raw.IndexOf(switches, StringComparison.OrdinalIgnoreCase) >= 0 ? raw : raw + " " + switches;
                    commands.Uninstall = new CommandField(command, Confidence.Suggested);
                    commands.Notes.Add("Captured: UninstallString from " + entry.KeyPath + " with " +
                                       SignatureScanner.DisplayName(effective) + " silent switches added. Verify on a test machine.");
                }
                else
                {
                    commands.Uninstall = new CommandField(raw, Confidence.Unknown);
                    commands.Notes.Add("Captured: UninstallString from " + entry.KeyPath +
                                       ". WARNING: no silent switch is known for this uninstaller; add one or it may show UI.");
                }
            }
            else
            {
                commands.Uninstall = new CommandField(UninstallPlaceholder, Confidence.Unknown);
                commands.Notes.Add("Captured: WARNING: the selected entry has no uninstall string.");
            }

            // Fill app information the file itself did not provide.
            if (commands.Publisher.IsPlaceholder && !string.IsNullOrWhiteSpace(entry.Publisher))
                commands.Publisher = new CommandField(entry.Publisher.Trim(), Confidence.Detected);
            if (string.IsNullOrWhiteSpace(commands.AppVersion.Value) && !string.IsNullOrWhiteSpace(entry.DisplayVersion))
                commands.AppVersion = new CommandField(entry.DisplayVersion.Trim(), Confidence.Detected);

            commands.Detection = BuildDetection(entry, productCode, commands.Detection);
            if (entry.Hive == "HKLM" && !entry.Is32BitView && commands.ArchitectureConfidence == Confidence.Suggested)
            {
                commands.Notes.Add("Captured: the app registered in the 64-bit registry view, so x64 is a good architecture choice.");
            }
            if (entry.Hive == "HKCU")
            {
                commands.Notes.Add("Captured: the entry is per user (HKCU). Use the User install context.");
            }
        }

        /// <summary>
        /// Builds the File detection rule from a captured entry. Detection is always a File rule:
        /// 1. the exe in DisplayIcon, or the main exe found in InstallLocation, with its file version
        ///    (read on this machine, where the app was just installed);
        /// 2. otherwise the install folder itself ("File or folder exists");
        /// 3. otherwise the rule found earlier (e.g. from the MSI's file tables), or placeholders.
        /// </summary>
        internal static DetectionRule BuildDetection(UninstallEntry entry, string productCode, DetectionRule existing = null)
        {
            DetectionRule rule = null;
            // Found on the install machine (e.g. inside Windows Sandbox) takes precedence over a local search.
            string exe = entry.MainExecutable ?? FindMainExecutable(entry);
            if (exe != null)
            {
                bool onDisk = File.Exists(exe);
                string version = onDisk ? ReadFileVersion(exe) : entry.MainExecutableVersion;
                rule = DetectionRule.ForFile(new FileTarget
                {
                    Folder = Portal.ToIntunePath(Path.GetDirectoryName(exe)),
                    FileName = Path.GetFileName(exe),
                    Version = version,
                    Is32Bit = false, // paths use explicit %ProgramFiles(x86)% where needed
                }, onDisk || entry.MainExecutable != null ? Confidence.Detected : Confidence.Suggested);
            }
            else if (!string.IsNullOrWhiteSpace(entry.InstallLocation))
            {
                string location = entry.InstallLocation.Trim().Trim('"').TrimEnd('\\');
                string parent = Path.GetDirectoryName(location);
                if (!string.IsNullOrEmpty(parent))
                {
                    rule = DetectionRule.ForFile(new FileTarget
                    {
                        Folder = Portal.ToIntunePath(parent),
                        FileName = Path.GetFileName(location),
                    }, Confidence.Suggested);
                }
            }

            if (rule == null)
            {
                rule = existing != null && existing.Type == DetectionRuleType.File && existing.IsComplete
                    ? existing.Clone()
                    : DetectionRule.Unknown();
            }
            if (productCode != null)
            {
                rule.MsiProductCode = productCode;
                rule.MsiVersion = entry.DisplayVersion ?? "";
            }
            return rule;
        }

        /// <summary>The app's main exe: DisplayIcon when it is an exe, else the best exe in InstallLocation.</summary>
        internal static string FindMainExecutable(UninstallEntry entry)
        {
            string folder, file;
            if (Portal.TryParseIconPath(entry.DisplayIcon, out folder, out file) && !MsiFileLocator.IsHelperExe(file))
            {
                return Path.Combine(folder, file);
            }
            if (string.IsNullOrWhiteSpace(entry.InstallLocation)) return null;
            string location = entry.InstallLocation.Trim().Trim('"').TrimEnd('\\');
            try
            {
                if (!Directory.Exists(location)) return null;
                string product = MsiFileLocator.Normalize(entry.DisplayName != null
                    ? Regex.Replace(entry.DisplayName, @"\s*v?\d+(\.\d+)+.*$", "")   // "REW 5.31.3" -> "REW"
                    : "");
                return new DirectoryInfo(location).GetFiles("*.exe", SearchOption.TopDirectoryOnly)
                    .Where(f => !MsiFileLocator.IsHelperExe(f.Name))
                    .OrderByDescending(f => product.Length > 0 && MsiFileLocator.Normalize(f.Name).Contains(product) ? 1 : 0)
                    .ThenByDescending(f => f.Length)
                    .Select(f => f.FullName)
                    .FirstOrDefault();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                return null;
            }
        }

        internal static string ReadFileVersion(string path)
        {
            try
            {
                var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                if (fvi.FileMajorPart == 0 && fvi.FileMinorPart == 0 && fvi.FileBuildPart == 0 && fvi.FilePrivatePart == 0)
                    return null;
                return fvi.FileMajorPart + "." + fvi.FileMinorPart + "." + fvi.FileBuildPart + "." + fvi.FilePrivatePart;
            }
            catch
            {
                return null;
            }
        }

        internal static InstallerFramework GuessFrameworkFromUninstaller(string uninstallString)
        {
            string lower = uninstallString.ToLowerInvariant();
            if (Regex.IsMatch(lower, @"unins\d{3}\.exe")) return InstallerFramework.InnoSetup;
            if (Regex.IsMatch(lower, @"\\uninst(all)?\.exe|\\uninstall\.exe")) return InstallerFramework.Nsis;
            return InstallerFramework.Unknown;
        }

        /// <summary>Device restart behavior, worded as in the Intune admin center.</summary>
        public static string RestartLabel(RestartBehavior restart)
        {
            switch (restart)
            {
                case RestartBehavior.ReturnCodes: return "Determine behavior based on return codes";
                case RestartBehavior.AllowRestart: return "App install may force a device restart";
                case RestartBehavior.ForceRestart: return "Intune will force a mandatory device restart";
                default: return "No specific action";
            }
        }
    }
}
