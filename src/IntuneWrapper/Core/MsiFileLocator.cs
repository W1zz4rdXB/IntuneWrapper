using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace IntuneWrapper.Core
{
    /// <summary>The app's main executable as Intune should look for it in a File detection rule.</summary>
    public sealed class FileTarget
    {
        /// <summary>Folder, using Intune environment variables, e.g. %ProgramFiles%\Vendor\App.</summary>
        public string Folder { get; set; }
        public string FileName { get; set; }
        /// <summary>File version (a.b.c.d), or null when unknown.</summary>
        public string Version { get; set; }
        /// <summary>The folder is a 32-bit folder (e.g. ProgramFilesFolder); Intune's 32-bit option must be Yes.</summary>
        public bool Is32Bit { get; set; }
    }

    /// <summary>
    /// Finds the main .exe an MSI installs and where, from its Directory, Component and File tables. The
    /// install folder can be changed at install time (e.g. INSTALLDIR=...), so the result is a suggestion.
    /// </summary>
    public static class MsiFileLocator
    {
        public sealed class DirectoryRow
        {
            public string Id, Parent, DefaultDir;
        }

        public sealed class FileRow
        {
            public string FileName, Directory, Version;
            public long Size;
        }

        // Standard MSI folder properties and how Intune file rules express them (with the 32-bit option).
        private static readonly Dictionary<string, Tuple<string, bool>> KnownFolders =
            new Dictionary<string, Tuple<string, bool>>(StringComparer.OrdinalIgnoreCase)
            {
                { "ProgramFiles64Folder", Tuple.Create("%ProgramFiles%", false) },
                { "ProgramFilesFolder", Tuple.Create("%ProgramFiles%", true) },
                { "CommonFiles64Folder", Tuple.Create("%CommonProgramFiles%", false) },
                { "CommonFilesFolder", Tuple.Create("%CommonProgramFiles%", true) },
                { "CommonAppDataFolder", Tuple.Create("%ProgramData%", false) },
                { "LocalAppDataFolder", Tuple.Create("%LocalAppData%", false) },
                { "AppDataFolder", Tuple.Create("%AppData%", false) },
                { "WindowsFolder", Tuple.Create("%WinDir%", false) },
                { "System64Folder", Tuple.Create("%WinDir%\\System32", false) },
                { "SystemFolder", Tuple.Create("%WinDir%\\System32", true) },
            };

        public static FileTarget Find(string msiPath, string productName)
        {
            List<DirectoryRow> dirs = MsiReader.ReadRows(msiPath, "SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`", 3)
                .Select(r => new DirectoryRow { Id = r[0], Parent = r[1], DefaultDir = r[2] }).ToList();
            Dictionary<string, string> componentDirs = MsiReader.ReadRows(msiPath, "SELECT `Component`, `Directory_` FROM `Component`", 2)
                .GroupBy(r => r[0]).ToDictionary(g => g.Key, g => g.First()[1]);
            List<FileRow> files = MsiReader.ReadRows(msiPath, "SELECT `Component_`, `FileName`, `Version`, `FileSize` FROM `File`", 4)
                .Select(r =>
                {
                    string dir;
                    long size;
                    componentDirs.TryGetValue(r[0], out dir);
                    long.TryParse(r[3], out size);
                    return new FileRow { Directory = dir, FileName = LongName(r[1]), Version = r[2], Size = size };
                })
                .ToList();
            return Resolve(dirs, files, productName);
        }

        /// <summary>Picks the main exe and builds its Intune folder path. Null when it cannot be determined.</summary>
        public static FileTarget Resolve(IList<DirectoryRow> dirs, IList<FileRow> files, string productName)
        {
            var byId = dirs.GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var candidates = files
                .Where(f => f.Directory != null && f.FileName != null && f.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                .Where(f => !IsHelperExe(f.FileName))
                .Select(f => new { File = f, Folder = BuildFolder(f.Directory, byId) })
                .Where(c => c.Folder != null)
                .ToList();
            if (candidates.Count == 0) return null;

            string product = Normalize(productName);
            var best = candidates
                .OrderByDescending(c => product.Length > 0 && Normalize(c.File.FileName).Contains(product) ? 1 : 0)
                .ThenByDescending(c => c.File.Size)
                .First();
            return new FileTarget
            {
                Folder = best.Folder.Item1,
                Is32Bit = best.Folder.Item2,
                FileName = best.File.FileName,
                Version = string.IsNullOrWhiteSpace(best.File.Version) || !char.IsDigit(best.File.Version[0]) ? null : best.File.Version,
            };
        }

        private static Tuple<string, bool> BuildFolder(string dirId, Dictionary<string, DirectoryRow> byId)
        {
            var segments = new List<string>();
            string id = dirId;
            for (int guard = 0; guard < 64 && id != null; guard++)
            {
                Tuple<string, bool> known;
                if (KnownFolders.TryGetValue(id, out known))
                {
                    segments.Reverse();
                    string path = known.Item1 + (segments.Count > 0 ? "\\" + string.Join("\\", segments) : "");
                    return Tuple.Create(path, known.Item2);
                }
                DirectoryRow row;
                if (!byId.TryGetValue(id, out row)) return null;
                if (string.IsNullOrEmpty(row.Parent) || string.Equals(row.Parent, row.Id, StringComparison.OrdinalIgnoreCase))
                    return null; // reached TARGETDIR without a known folder
                string name = TargetName(row.DefaultDir);
                if (name != null) segments.Add(name);
                id = row.Parent;
            }
            return null;
        }

        /// <summary>DefaultDir is "target:source", each "short|long"; "." means the parent folder itself.</summary>
        internal static string TargetName(string defaultDir)
        {
            if (string.IsNullOrEmpty(defaultDir)) return null;
            string target = defaultDir.Split(':')[0];
            string name = LongName(target);
            return name == "." || name.Length == 0 ? null : name;
        }

        internal static string LongName(string value)
        {
            if (value == null) return null;
            int bar = value.IndexOf('|');
            return bar >= 0 ? value.Substring(bar + 1) : value;
        }

        internal static bool IsHelperExe(string fileName)
        {
            return Regex.IsMatch(fileName, @"^(unins|uninst|uninstall|update|updater|crash|helper|setup|install|vcredist|elevate)",
                RegexOptions.IgnoreCase);
        }

        internal static string Normalize(string value)
        {
            return Regex.Replace(value ?? "", "[^A-Za-z0-9]", "").ToLowerInvariant();
        }
    }
}
