using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;

namespace IntuneWrapper.Core
{
    // Option lists and labels mirror the Intune admin center "Add Win32 app" wizard, so values can be
    // entered field by field exactly as shown there.

    public enum DetectionRuleType
    {
        Msi,
        File,
        Registry
    }

    public enum FileDetectionMethod
    {
        Exists,
        DoesNotExist,
        DateModified,
        DateCreated,
        Version,
        SizeInMB
    }

    public enum RegistryDetectionMethod
    {
        KeyExists,
        KeyDoesNotExist,
        StringComparison,
        IntegerComparison,
        VersionComparison,
        ValueExists,
        ValueDoesNotExist
    }

    public enum ComparisonOperator
    {
        Equals,
        NotEqualTo,
        GreaterThan,
        GreaterThanOrEqualTo,
        LessThan,
        LessThanOrEqualTo
    }

    [Flags]
    public enum OsArchitecture
    {
        None = 0,
        X86 = 1,
        X64 = 2,
        Arm64 = 4
    }

    public static class Portal
    {
        public const string DefaultMinimumOs = "Windows 10 1607";
        public const int DefaultInstallTimeMinutes = 60;

        /// <summary>The default return codes Intune pre-fills for a Win32 app.</summary>
        public const string ReturnCodes = "0 Success, 1707 Success, 3010 Soft reboot, 1641 Hard reboot, 1618 Retry";

        public static readonly string[] MinimumOsOptions =
        {
            "Windows 10 1607", "Windows 10 1703", "Windows 10 1709", "Windows 10 1803", "Windows 10 1809",
            "Windows 10 1903", "Windows 10 1909", "Windows 10 2004", "Windows 10 20H2", "Windows 10 21H1",
            "Windows 10 21H2", "Windows 10 22H2", "Windows 11 21H2", "Windows 11 22H2", "Windows 11 23H2",
            "Windows 11 24H2",
        };

        public static string Label(DetectionRuleType type)
        {
            return type == DetectionRuleType.Msi ? "MSI" : type.ToString();
        }

        public static string Label(FileDetectionMethod method)
        {
            switch (method)
            {
                case FileDetectionMethod.Exists: return "File or folder exists";
                case FileDetectionMethod.DoesNotExist: return "File or folder does not exist";
                case FileDetectionMethod.DateModified: return "Date modified";
                case FileDetectionMethod.DateCreated: return "Date created";
                case FileDetectionMethod.Version: return "String (version)";
                default: return "Size in MB";
            }
        }

        public static string Label(RegistryDetectionMethod method)
        {
            switch (method)
            {
                case RegistryDetectionMethod.KeyExists: return "Key exists";
                case RegistryDetectionMethod.KeyDoesNotExist: return "Key does not exist";
                case RegistryDetectionMethod.StringComparison: return "String comparison";
                case RegistryDetectionMethod.IntegerComparison: return "Integer comparison";
                case RegistryDetectionMethod.VersionComparison: return "Version comparison";
                case RegistryDetectionMethod.ValueExists: return "Value exists";
                default: return "Value does not exist";
            }
        }

        public static string Label(ComparisonOperator op)
        {
            switch (op)
            {
                case ComparisonOperator.Equals: return "Equals";
                case ComparisonOperator.NotEqualTo: return "Not equal to";
                case ComparisonOperator.GreaterThan: return "Greater than";
                case ComparisonOperator.GreaterThanOrEqualTo: return "Greater than or equal to";
                case ComparisonOperator.LessThan: return "Less than";
                default: return "Less than or equal to";
            }
        }

        public static string Label(OsArchitecture arch)
        {
            var parts = new List<string>();
            if ((arch & OsArchitecture.X86) != 0) parts.Add("x86");
            if ((arch & OsArchitecture.X64) != 0) parts.Add("x64");
            if ((arch & OsArchitecture.Arm64) != 0) parts.Add("ARM64");
            return parts.Count == 0 ? "(none selected)" : string.Join(", ", parts);
        }

        public static string YesNo(bool value) => value ? "Yes" : "No";

        public static bool NeedsOperator(FileDetectionMethod method) => method >= FileDetectionMethod.DateModified;

        public static bool NeedsOperator(RegistryDetectionMethod method) =>
            method == RegistryDetectionMethod.StringComparison || method == RegistryDetectionMethod.IntegerComparison ||
            method == RegistryDetectionMethod.VersionComparison;

        public static bool NeedsValueName(RegistryDetectionMethod method) =>
            method != RegistryDetectionMethod.KeyExists && method != RegistryDetectionMethod.KeyDoesNotExist;

        /// <summary>Architecture from the MSI Template summary property, e.g. "x64;1033" or "Intel;1033".</summary>
        public static OsArchitecture ArchitectureFromMsiTemplate(string template)
        {
            string platform = (template ?? "").Split(';')[0].Trim();
            if (platform.Equals("x64", StringComparison.OrdinalIgnoreCase) || platform.Equals("AMD64", StringComparison.OrdinalIgnoreCase))
                return OsArchitecture.X64;
            if (platform.Equals("Arm64", StringComparison.OrdinalIgnoreCase))
                return OsArchitecture.Arm64;
            if (platform.Equals("Intel", StringComparison.OrdinalIgnoreCase) || platform.Length == 0)
                return OsArchitecture.X86 | OsArchitecture.X64; // 32-bit packages install on both
            return OsArchitecture.None;
        }

        /// <summary>
        /// Rewrites well-known folders to the environment variables Intune file rules accept, so the
        /// rule works on every client (C:\Program Files\App -> %ProgramFiles%\App).
        /// </summary>
        public static string ToIntunePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            path = path.Trim().Trim('"').TrimEnd('\\');
            var map = new[]
            {
                Tuple.Create(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "%ProgramFiles(x86)%"),
                Tuple.Create(Environment.GetEnvironmentVariable("ProgramW6432") ?? "", "%ProgramFiles%"),
                Tuple.Create(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "%ProgramFiles%"),
                Tuple.Create(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "%ProgramData%"),
                Tuple.Create(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LocalAppData%"),
                Tuple.Create(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%AppData%"),
            };
            foreach (Tuple<string, string> m in map)
            {
                string folder = m.Item1.TrimEnd('\\');
                if (folder.Length == 0) continue;
                if (path.Equals(folder, StringComparison.OrdinalIgnoreCase)) return m.Item2;
                if (path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase)) return m.Item2 + path.Substring(folder.Length);
            }
            return path;
        }

        /// <summary>Parses a DisplayIcon value such as "C:\App\app.exe",0 into folder and file name.</summary>
        public static bool TryParseIconPath(string displayIcon, out string folder, out string file)
        {
            folder = file = null;
            if (string.IsNullOrWhiteSpace(displayIcon)) return false;
            string value = Regex.Replace(displayIcon.Trim(), @",\s*-?\d+$", "").Trim().Trim('"');
            if (!value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || value.IndexOf('\\') < 0) return false;
            folder = Path.GetDirectoryName(value);
            file = Path.GetFileName(value);
            return !string.IsNullOrEmpty(folder) && !string.IsNullOrEmpty(file);
        }
    }

    /// <summary>
    /// The checks the Intune admin center applies to File detection rules. A value that passes here is
    /// accepted by the portal; each message says what to fix.
    /// </summary>
    public static class FileRuleValidator
    {
        // Intune: "Path cannot contain the following characters: / * ? " < > |" (straight and curly quotes).
        private static readonly char[] PathForbidden = { '/', '*', '?', '"', '\u201C', '\u201D', '<', '>', '|' };

        // Intune: "File or folder name cannot contain the following characters: \ / : * ? " < > |".
        private static readonly char[] NameForbidden = { '\\', '/', ':', '*', '?', '"', '\u201C', '\u201D', '<', '>', '|' };

        private static readonly Regex LocalRoot = new Regex(@"^([A-Za-z]:\\|%[A-Za-z0-9_()]+%(\\|$))");

        public static string ValidatePath(string path)
        {
            if (CommandGenerator.ContainsPlaceholder(path))
                return "Path is required: run Capture, use Browse..., or type the folder (e.g. %ProgramFiles%\\App).";
            path = path.Trim();
            if (path.IndexOfAny(PathForbidden) >= 0)
                return "Path cannot contain / * ? \" < > |.";
            if (path.StartsWith(@"\\"))
                return "Path must be a local path, not a network path.";
            if (!LocalRoot.IsMatch(path))
                return "Path must start with a drive (C:\\...) or an environment variable (%ProgramFiles%\\...).";
            if (path.IndexOf(':', 2) >= 0)
                return "Path can only contain ':' after the drive letter.";
            if (path.Length >= 260)
                return "Path is too long (260 characters or more).";
            if (path.Contains(@"\\"))
                return "Path contains an empty folder name (\\\\).";
            return null;
        }

        public static string ValidateFileOrFolder(string name)
        {
            if (CommandGenerator.ContainsPlaceholder(name))
                return "File or folder is required: run Capture, use Browse..., or type the file name (e.g. app.exe).";
            name = name.Trim();
            if (name.IndexOfAny(NameForbidden) >= 0)
                return "File or folder must be a name only (no folder), and cannot contain \\ / : * ? \" < > |.";
            if (name.Length >= 255)
                return "File or folder must be shorter than 255 characters.";
            if (name == "." || name == ".." || name.EndsWith("."))
                return "File or folder cannot be '.' or '..' or end with a period.";
            return null;
        }

        public static string ValidateValue(FileDetectionMethod method, string value)
        {
            if (string.IsNullOrWhiteSpace(value) || CommandGenerator.ContainsPlaceholder(value))
                return "Value is required for " + Portal.Label(method) + ".";
            value = value.Trim();
            switch (method)
            {
                case FileDetectionMethod.Version:
                    return Regex.IsMatch(value, @"^\d+(\.\d+){0,3}$") ? null : "Value must be a version like 1.2.3.4.";
                case FileDetectionMethod.SizeInMB:
                    int size;
                    return int.TryParse(value, out size) && size >= 0 ? null : "Value must be a whole number of MB.";
                case FileDetectionMethod.DateModified:
                case FileDetectionMethod.DateCreated:
                    DateTime date;
                    return DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out date) ? null : "Value must be a date like 2026-11-19.";
                default:
                    return null;
            }
        }

        /// <summary>
        /// Splits a full file path typed into Path or File or folder ("C:\App\app.exe") into folder and name.
        /// Returns false when the value is not a full path to a file.
        /// </summary>
        public static bool TrySplitFullPath(string value, out string folder, out string name)
        {
            folder = name = null;
            if (string.IsNullOrWhiteSpace(value)) return false;
            value = value.Trim().Trim('"', '\u201C', '\u201D').TrimEnd('\\');
            if (!LocalRoot.IsMatch(value) || value.IndexOfAny(PathForbidden) >= 0) return false;
            int slash = value.LastIndexOf('\\');
            if (slash <= 2 || slash == value.Length - 1) return false;
            string last = value.Substring(slash + 1);
            if (!System.IO.Path.HasExtension(last)) return false;
            folder = value.Substring(0, slash);
            name = last;
            return true;
        }
    }

    /// <summary>
    /// A detection rule as entered in Intune. All three rule types keep their values, so switching the
    /// type in the UI does not lose what Capture pre-filled for the other types.
    /// </summary>
    [DataContract]
    public sealed class DetectionRule
    {
        public const string KeyPathPlaceholder = "<KEY PATH: use Capture mode>";
        public const string FilePathPlaceholder = "<PATH: use Capture mode>";
        public const string FileNamePlaceholder = "<FILE OR FOLDER: use Capture mode>";
        public const string ProductCodePlaceholder = "<PRODUCT CODE: could not be read>";
        public const string ValuePlaceholder = "<VALUE: required>";

        [DataMember(Name = "type")] public DetectionRuleType Type { get; set; }
        [DataMember(Name = "confidence")] public Confidence Confidence { get; set; }

        [DataMember(Name = "msiProductCode")] public string MsiProductCode { get; set; }
        [DataMember(Name = "msiVersionCheck")] public bool MsiVersionCheck { get; set; }
        [DataMember(Name = "msiOperator")] public ComparisonOperator MsiOperator { get; set; }
        [DataMember(Name = "msiVersion")] public string MsiVersion { get; set; }

        [DataMember(Name = "filePath")] public string FilePath { get; set; }
        [DataMember(Name = "fileOrFolder")] public string FileOrFolder { get; set; }
        [DataMember(Name = "fileMethod")] public FileDetectionMethod FileMethod { get; set; }
        [DataMember(Name = "fileOperator")] public ComparisonOperator FileOperator { get; set; }
        [DataMember(Name = "fileValue")] public string FileValue { get; set; }
        [DataMember(Name = "file32Bit")] public bool File32Bit { get; set; }

        [DataMember(Name = "registryKeyPath")] public string RegistryKeyPath { get; set; }
        [DataMember(Name = "registryValueName")] public string RegistryValueName { get; set; }
        [DataMember(Name = "registryMethod")] public RegistryDetectionMethod RegistryMethod { get; set; }
        [DataMember(Name = "registryOperator")] public ComparisonOperator RegistryOperator { get; set; }
        [DataMember(Name = "registryValue")] public string RegistryValue { get; set; }
        [DataMember(Name = "registry32Bit")] public bool Registry32Bit { get; set; }

        public static DetectionRule Unknown()
        {
            return new DetectionRule
            {
                // Detection is always a File rule (team standard); the other types are kept for old history only.
                Type = DetectionRuleType.File,
                Confidence = Confidence.Unknown,
                MsiProductCode = ProductCodePlaceholder,
                FilePath = FilePathPlaceholder,
                FileOrFolder = FileNamePlaceholder,
                FileMethod = FileDetectionMethod.Exists,
                RegistryKeyPath = KeyPathPlaceholder,
                RegistryValueName = "DisplayVersion",
                RegistryMethod = RegistryDetectionMethod.StringComparison,
                RegistryOperator = ComparisonOperator.Equals,
                RegistryValue = ValuePlaceholder,
            };
        }

        public DetectionRule Clone() => (DetectionRule)MemberwiseClone();

        /// <summary>
        /// File rule for <paramref name="target"/>: "String (version) Greater than or equal to" when the file
        /// version is known, so newer versions also count as installed; otherwise "File or folder exists".
        /// </summary>
        public static DetectionRule ForFile(FileTarget target, Confidence confidence)
        {
            DetectionRule rule = Unknown();
            rule.FilePath = target.Folder;
            rule.FileOrFolder = target.FileName;
            rule.File32Bit = target.Is32Bit;
            if (!string.IsNullOrWhiteSpace(target.Version))
            {
                rule.FileMethod = FileDetectionMethod.Version;
                rule.FileOperator = ComparisonOperator.GreaterThanOrEqualTo;
                rule.FileValue = target.Version;
            }
            else
            {
                rule.FileMethod = FileDetectionMethod.Exists;
            }
            rule.Confidence = rule.IsComplete ? confidence : Confidence.Unknown;
            return rule;
        }

        /// <summary>The fields Intune shows for the active rule type, with portal labels and values.</summary>
        public List<KeyValuePair<string, string>> Fields()
        {
            var f = new List<KeyValuePair<string, string>>();
            Action<string, string> add = (k, v) => f.Add(new KeyValuePair<string, string>(k, v ?? ""));
            add("Rule type", Portal.Label(Type));
            switch (Type)
            {
                case DetectionRuleType.Msi:
                    add("MSI product code", MsiProductCode);
                    add("MSI product version check", Portal.YesNo(MsiVersionCheck));
                    if (MsiVersionCheck)
                    {
                        add("Operator", Portal.Label(MsiOperator));
                        add("Value", MsiVersion);
                    }
                    break;
                case DetectionRuleType.File:
                    add("Path", FilePath);
                    add("File or folder", FileOrFolder);
                    add("Detection method", Portal.Label(FileMethod));
                    if (Portal.NeedsOperator(FileMethod))
                    {
                        add("Operator", Portal.Label(FileOperator));
                        add("Value", FileValue);
                    }
                    add("Associated with a 32-bit app on 64-bit clients", Portal.YesNo(File32Bit));
                    break;
                default:
                    add("Key path", RegistryKeyPath);
                    if (Portal.NeedsValueName(RegistryMethod)) add("Value name", RegistryValueName);
                    add("Detection method", Portal.Label(RegistryMethod));
                    if (Portal.NeedsOperator(RegistryMethod))
                    {
                        add("Operator", Portal.Label(RegistryOperator));
                        add("Value", RegistryValue);
                    }
                    add("Associated with a 32-bit app on 64-bit clients", Portal.YesNo(Registry32Bit));
                    break;
            }
            return f;
        }

        /// <summary>Fields of the active rule type that Intune would reject (missing, placeholder or invalid).</summary>
        public List<string> MissingFields()
        {
            return Problems().Keys.ToList();
        }

        /// <summary>
        /// Field label -> reason, for every field Intune would reject. File rules are checked against the
        /// same rules the Intune admin center applies, so values copied from here are accepted there.
        /// </summary>
        public Dictionary<string, string> Problems()
        {
            var problems = new Dictionary<string, string>();
            if (Type == DetectionRuleType.File)
            {
                Add(problems, "Path", FileRuleValidator.ValidatePath(FilePath));
                Add(problems, "File or folder", FileRuleValidator.ValidateFileOrFolder(FileOrFolder));
                if (Portal.NeedsOperator(FileMethod))
                    Add(problems, "Value", FileRuleValidator.ValidateValue(FileMethod, FileValue));
                return problems;
            }
            foreach (var kv in Fields())
            {
                if (kv.Key != "Rule type" && CommandGenerator.ContainsPlaceholder(kv.Value))
                    problems[kv.Key] = kv.Key + " is required.";
            }
            return problems;
        }

        private static void Add(Dictionary<string, string> problems, string field, string error)
        {
            if (error != null) problems[field] = error;
        }

        public bool IsComplete => MissingFields().Count == 0;

        public string Summary(string separator)
        {
            return string.Join(separator, Fields().Select(kv => kv.Key + ": " + kv.Value));
        }
    }
}
