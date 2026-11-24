using System;
using System.IO;
using System.Runtime.Serialization;

namespace IntuneWrapper.Core
{
    public enum InstallContext
    {
        System,
        User
    }

    /// <summary>User settings (SPEC 4.9). Missing keys get defaults (FR-33).</summary>
    [DataContract]
    public sealed class Settings
    {
        public Settings()
        {
            ApplyDefaults();
        }

        [DataMember(Name = "toolPath", EmitDefaultValue = false)]
        public string ToolPath { get; set; }

        /// <summary>
        /// Root of the packaging structure: IntuneWinAppUtil.exe and one folder per program with
        /// input and output subfolders (see <see cref="PackagingLayout"/>).
        /// </summary>
        [DataMember(Name = "packagingFolder")]
        public string PackagingFolder { get; set; }

        [DataMember(Name = "defaultInstallContext")]
        public string DefaultInstallContextName { get; set; }

        [DataMember(Name = "showLogByDefault")]
        public bool ShowLogByDefault { get; set; }

        /// <summary>GitHub release tag of the tool we downloaded, with its hash to know it is still that file.</summary>
        [DataMember(Name = "toolReleaseTag", EmitDefaultValue = false)]
        public string ToolReleaseTag { get; set; }

        [DataMember(Name = "toolSha256", EmitDefaultValue = false)]
        public string ToolSha256 { get; set; }

        public InstallContext DefaultInstallContext
        {
            get
            {
                InstallContext ctx;
                return Enum.TryParse(DefaultInstallContextName, true, out ctx) ? ctx : InstallContext.System;
            }
            set { DefaultInstallContextName = value.ToString(); }
        }

        // DataContractJsonSerializer skips constructors, so defaults are applied here as well.
        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            ApplyDefaults();
        }

        [OnDeserialized]
        private void OnDeserialized(StreamingContext context)
        {
            // Older versions saved "outputFolder"; it is replaced by the packaging folder.
            if (string.IsNullOrWhiteSpace(PackagingFolder))
            {
                PackagingFolder = AppPaths.DefaultPackagingFolder;
            }
            if (string.IsNullOrWhiteSpace(DefaultMinimumOs))
            {
                DefaultMinimumOs = Portal.DefaultMinimumOs;
            }
            if (string.IsNullOrWhiteSpace(DefaultInstallContextName))
            {
                DefaultInstallContextName = InstallContext.System.ToString();
            }
        }

        private void ApplyDefaults()
        {
            ToolPath = null;
            PackagingFolder = AppPaths.DefaultPackagingFolder;
            DefaultInstallContextName = InstallContext.System.ToString();
            ShowLogByDefault = false;
            DefaultMinimumOs = Portal.DefaultMinimumOs;
        }

        [DataMember(Name = "defaultMinimumOs")]
        public string DefaultMinimumOs { get; set; }

    }

    public static class SettingsStore
    {
        /// <summary>
        /// Loads settings. A missing file gives defaults. A corrupt file is backed up and replaced
        /// with defaults; <paramref name="warning"/> then explains what happened.
        /// </summary>
        public static Settings Load(AppPaths paths, out string warning)
        {
            warning = null;
            string file = paths.SettingsFile;
            if (!File.Exists(file))
            {
                return new Settings();
            }
            try
            {
                string text = File.ReadAllText(file);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return new Settings();
                }
                return Json.Deserialize<Settings>(text) ?? new Settings();
            }
            catch (Exception ex) when (ex is SerializationException || ex is IOException || ex is UnauthorizedAccessException
                                       || ex is ArgumentException || ex is InvalidCastException)
            {
                string backup = BackupCorrupt(file);
                warning = "Settings file was unreadable and has been reset to defaults."
                          + (backup != null ? " The old file was saved as " + backup + "." : "");
                Logger.Warn(warning + " " + ex.Message);
                return new Settings();
            }
        }

        public static void Save(AppPaths paths, Settings settings)
        {
            Json.WriteFileAtomic(paths.SettingsFile, Json.Serialize(settings));
        }

        internal static string BackupCorrupt(string file)
        {
            try
            {
                string backup = file + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";
                File.Copy(file, backup, true);
                File.Delete(file);
                return backup;
            }
            catch
            {
                return null;
            }
        }
    }
}
