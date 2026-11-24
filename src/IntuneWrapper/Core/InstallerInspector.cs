using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace IntuneWrapper.Core
{
    public enum InstallerType
    {
        Msi,
        Exe
    }

    /// <summary>Metadata about a loaded installer (SPEC 9.4 Get-InstallerInfo).</summary>
    public sealed class InstallerInfo
    {
        public string Path { get; set; }
        public string FileName => System.IO.Path.GetFileName(Path);
        public string SourceFolder => System.IO.Path.GetDirectoryName(Path);
        public InstallerType Type { get; set; }
        public long Size { get; set; }
        public string ProductName { get; set; }
        public string ProductVersion { get; set; }
        public string Publisher { get; set; }
        public string Description { get; set; }
        public string Sha256 { get; set; }
        public InstallerFramework Framework { get; set; }

        /// <summary>MSI only. Null when it could not be read.</summary>
        public string ProductCode { get; set; }

        /// <summary>MSI only: the main exe it installs, for the File detection rule. Null when not found.</summary>
        public FileTarget MsiMainFile { get; set; }

        /// <summary>MSI only: Template summary property, e.g. "x64;1033".</summary>
        public string MsiTemplate { get; set; }

        /// <summary>MSI only: why the Property table could not be read.</summary>
        public string MsiReadError { get; set; }

        /// <summary>Product name and version, falling back to the file name.</summary>
        public string DisplayName
        {
            get
            {
                string name = string.IsNullOrWhiteSpace(ProductName)
                    ? System.IO.Path.GetFileNameWithoutExtension(Path)
                    : ProductName.Trim();
                return string.IsNullOrWhiteSpace(ProductVersion) ? name : name + " " + ProductVersion.Trim();
            }
        }

        public string TypeLabel
        {
            get
            {
                return Type == InstallerType.Msi
                    ? "MSI"
                    : "EXE: " + SignatureScanner.DisplayName(Framework);
            }
        }
    }

    /// <summary>Thrown for problems the user can fix: wrong file type, folder, locked file.</summary>
    public sealed class InstallerInputException : Exception
    {
        public InstallerInputException(string message, Exception inner = null) : base(message, inner)
        {
        }
    }

    public static class InstallerInspector
    {
        public static readonly string[] SupportedExtensions = { ".exe", ".msi" };

        /// <summary>
        /// Validates a dropped or browsed path (FR-1, FR-3). Returns null when valid, otherwise a
        /// plain-language error.
        /// </summary>
        public static string Validate(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "No file was selected.";
            }
            if (Directory.Exists(path))
            {
                return "Folders are not supported. Drop a single .exe or .msi installer file.";
            }
            if (!File.Exists(path))
            {
                return "The file does not exist: " + path;
            }
            string ext = System.IO.Path.GetExtension(path);
            if (!string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(ext, ".msi", StringComparison.OrdinalIgnoreCase))
            {
                return "Unsupported file type \"" + ext + "\". Only .exe and .msi installers are supported.";
            }
            return null;
        }

        public static InstallerInfo Inspect(string path)
        {
            string error = Validate(path);
            if (error != null)
            {
                throw new InstallerInputException(error);
            }

            var info = new InstallerInfo
            {
                Path = System.IO.Path.GetFullPath(path),
                Type = string.Equals(System.IO.Path.GetExtension(path), ".msi", StringComparison.OrdinalIgnoreCase)
                    ? InstallerType.Msi
                    : InstallerType.Exe
            };

            try
            {
                info.Size = new FileInfo(info.Path).Length;
                info.Sha256 = ComputeSha256(info.Path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new InstallerInputException(
                    "The file could not be read: " + info.Path + Environment.NewLine +
                    "It may be open in another program or blocked by permissions. Close any program using it and try again.",
                    ex);
            }

            if (info.Type == InstallerType.Msi)
            {
                ReadMsi(info);
            }
            else
            {
                ReadExe(info);
            }
            return info;
        }

        private static void ReadMsi(InstallerInfo info)
        {
            info.Framework = InstallerFramework.None;
            info.MsiTemplate = MsiReader.ReadTemplate(info.Path);
            try
            {
                Dictionary<string, string> props = MsiReader.ReadProperties(info.Path);
                string value;
                if (props.TryGetValue("ProductCode", out value)) info.ProductCode = value;
                if (props.TryGetValue("ProductName", out value)) info.ProductName = value;
                if (props.TryGetValue("ProductVersion", out value)) info.ProductVersion = value;
                if (props.TryGetValue("Manufacturer", out value)) info.Publisher = value;
                if (string.IsNullOrWhiteSpace(info.ProductCode))
                {
                    info.ProductCode = null;
                    info.MsiReadError = "The MSI has no ProductCode property.";
                }
            }
            catch (Exception ex)
            {
                info.ProductCode = null;
                info.MsiReadError = ex.Message;
                Logger.Warn("MSI property read failed for " + info.FileName + ": " + ex.Message);
            }

            try
            {
                info.MsiMainFile = MsiFileLocator.Find(info.Path, info.ProductName);
            }
            catch (Exception ex)
            {
                // Databases without File/Directory tables (or unreadable ones) just get no suggestion.
                Logger.Info("No main executable found in " + info.FileName + ": " + ex.Message);
            }
        }

        private static void ReadExe(InstallerInfo info)
        {
            try
            {
                FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(info.Path);
                info.ProductName = Clean(fvi.ProductName) ?? Clean(fvi.FileDescription);
                info.ProductVersion = Clean(fvi.ProductVersion) ?? Clean(fvi.FileVersion);
                info.Publisher = Clean(fvi.CompanyName);
                info.Description = Clean(fvi.FileDescription);
            }
            catch (Exception ex)
            {
                Logger.Warn("Version info read failed for " + info.FileName + ": " + ex.Message);
            }

            try
            {
                info.Framework = SignatureScanner.Detect(info.Path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new InstallerInputException(
                    "The file could not be read: " + info.Path + Environment.NewLine +
                    "Close any program using it and try again.", ex);
            }
        }

        private static string Clean(string value)
        {
            if (value == null) return null;
            value = value.Trim().Trim('\0');
            return value.Length == 0 ? null : value;
        }

        public static string ComputeSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024))
            {
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
            }
        }

        /// <summary>Other files/folders next to the installer that IntuneWinAppUtil would also package (FR-26).</summary>
        public static List<string> OtherEntriesInFolder(string installerPath)
        {
            var result = new List<string>();
            string folder = System.IO.Path.GetDirectoryName(installerPath);
            foreach (string entry in Directory.EnumerateFileSystemEntries(folder))
            {
                if (!string.Equals(entry, installerPath, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(entry);
                }
            }
            return result;
        }

        public static string FormatSize(long bytes)
        {
            string[] units = { "bytes", "KB", "MB", "GB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return unit == 0 ? bytes + " bytes" : value.ToString("0.0") + " " + units[unit];
        }
    }
}
