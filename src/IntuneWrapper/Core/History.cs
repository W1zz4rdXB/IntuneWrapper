using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace IntuneWrapper.Core
{
    [DataContract]
    public sealed class HistoryEntry
    {
        [DataMember(Name = "id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
        [DataMember(Name = "timestamp")] public DateTime Timestamp { get; set; } = DateTime.Now;
        [DataMember(Name = "fileName")] public string FileName { get; set; }
        [DataMember(Name = "sourcePath")] public string SourcePath { get; set; }
        [DataMember(Name = "sha256")] public string Sha256 { get; set; }
        [DataMember(Name = "productName")] public string ProductName { get; set; }
        [DataMember(Name = "productVersion")] public string ProductVersion { get; set; }
        [DataMember(Name = "publisher")] public string Publisher { get; set; }
        [DataMember(Name = "installerType")] public string InstallerType { get; set; }
        [DataMember(Name = "framework")] public string Framework { get; set; }
        [DataMember(Name = "outputPath")] public string OutputPath { get; set; }
        [DataMember(Name = "success")] public bool Success { get; set; }
        [DataMember(Name = "install")] public string Install { get; set; }
        [DataMember(Name = "uninstall")] public string Uninstall { get; set; }
        [DataMember(Name = "detection")] public string Detection { get; set; }
        [DataMember(Name = "installContext")] public string InstallContext { get; set; }
        [DataMember(Name = "restart")] public string Restart { get; set; }
        [DataMember(Name = "notes")] public List<string> Notes { get; set; }

        // Intune portal fields (1.1.0). "detection" above keeps a one-line summary.
        [DataMember(Name = "appName")] public string AppName { get; set; }
        [DataMember(Name = "description")] public string Description { get; set; }
        [DataMember(Name = "appVersion")] public string AppVersion { get; set; }
        [DataMember(Name = "installTimeMinutes")] public int InstallTimeMinutes { get; set; }
        [DataMember(Name = "allowAvailableUninstall")] public bool AllowAvailableUninstall { get; set; }
        [DataMember(Name = "architecture")] public int Architecture { get; set; }
        [DataMember(Name = "minimumOs")] public string MinimumOs { get; set; }
        [DataMember(Name = "detectionRule")] public DetectionRule DetectionRule { get; set; }
        [DataMember(Name = "items", EmitDefaultValue = false)] public List<PackageItem> Items { get; set; }

        public static HistoryEntry Create(InstallerInfo info, PackageCommands c, PackagingResult result)
        {
            return new HistoryEntry
            {
                FileName = info.FileName,
                SourcePath = info.Path,
                Sha256 = info.Sha256,
                ProductName = info.ProductName,
                ProductVersion = info.ProductVersion,
                Publisher = c.Publisher.Value,
                InstallerType = info.Type.ToString(),
                Framework = info.Framework.ToString(),
                OutputPath = result != null && result.Success ? result.OutputPath : null,
                Success = result != null && result.Success,
                Install = c.Install.Value,
                Uninstall = c.Uninstall.Value,
                Detection = c.Detection.Summary(" | "),
                InstallContext = c.InstallContext.ToString(),
                Restart = c.Restart.ToString(),
                Notes = c.Notes.ToList(),
                AppName = c.AppName.Value,
                Description = c.Description.Value,
                AppVersion = c.AppVersion.Value,
                InstallTimeMinutes = c.InstallTimeMinutes,
                AllowAvailableUninstall = c.AllowAvailableUninstall,
                Architecture = (int)c.Architecture,
                MinimumOs = c.MinimumOs,
                DetectionRule = c.Detection.Clone(),
                Items = c.Items.Count > 0 ? c.Items.ToList() : null,
            };
        }

        /// <summary>Rebuilds the commands. Values are marked Edited (or Unknown when still a placeholder).</summary>
        public PackageCommands ToCommands(InstallContext defaultContext)
        {
            Func<string, CommandField> field = v => new CommandField(v ?? "",
                CommandGenerator.ContainsPlaceholder(v) ? Confidence.Unknown : Confidence.Edited);
            InstallContext context;
            RestartBehavior restart;
            var c = new PackageCommands
            {
                AppName = field(string.IsNullOrWhiteSpace(AppName) ? ProductName ?? System.IO.Path.GetFileNameWithoutExtension(FileName ?? "") : AppName),
                Description = field(string.IsNullOrWhiteSpace(Description) ? CommandGenerator.DescriptionPlaceholder : Description),
                Publisher = field(string.IsNullOrWhiteSpace(Publisher) ? CommandGenerator.PublisherPlaceholder : Publisher),
                AppVersion = new CommandField(AppVersion ?? ProductVersion ?? "", Confidence.Edited),
                Install = field(Install),
                Uninstall = field(string.IsNullOrWhiteSpace(Uninstall) ? CommandGenerator.UninstallPlaceholder : Uninstall),
                InstallTimeMinutes = InstallTimeMinutes > 0 ? InstallTimeMinutes : Portal.DefaultInstallTimeMinutes,
                AllowAvailableUninstall = DetectionRule == null || AllowAvailableUninstall,
                InstallContext = Enum.TryParse(InstallContext, out context) ? context : defaultContext,
                Restart = Enum.TryParse(Restart, out restart) ? restart : RestartBehavior.Suppress,
                Architecture = Architecture != 0 ? (OsArchitecture)Architecture : OsArchitecture.X64,
                ArchitectureConfidence = Architecture != 0 ? Confidence.Edited : Confidence.Suggested,
                MinimumOs = string.IsNullOrWhiteSpace(MinimumOs) ? Portal.DefaultMinimumOs : MinimumOs,
                Notes = new List<string>(),
                Items = Items != null ? Items.ToList() : new List<PackageItem>(),
            };
            if (DetectionRule != null)
            {
                c.Detection = DetectionRule.Clone();
                if (c.Detection.Type != DetectionRuleType.File)
                {
                    // Detection is always a File rule now; keep the old rule visible in the notes.
                    c.Notes.Add("Detection saved by an older version as " + DetectionRule.Summary(" | ") +
                                ". Detection rules now always use File: check Path and File or folder.");
                    c.Detection.Type = DetectionRuleType.File;
                }
                c.Detection.Confidence = c.Detection.IsComplete ? Confidence.Edited : Confidence.Unknown;
            }
            else
            {
                c.Detection = Core.DetectionRule.Unknown();
                if (!string.IsNullOrWhiteSpace(Detection)) c.Notes.Add("Detection saved by an older version: " + Detection);
            }
            return c;
        }
    }

    [DataContract]
    internal sealed class HistoryFile
    {
        [DataMember(Name = "version")] public int Version { get; set; } = 1;
        [DataMember(Name = "entries")] public List<HistoryEntry> Entries { get; set; } = new List<HistoryEntry>();
    }

    /// <summary>Last 50 conversions in history.json (SPEC 4.8). A convenience: never fatal.</summary>
    public sealed class HistoryStore
    {
        public const int MaxEntries = 50;
        private readonly AppPaths _paths;

        public HistoryStore(AppPaths paths)
        {
            _paths = paths;
        }

        /// <summary>Set when the last Load found a corrupt file and reset it (FR-31).</summary>
        public string LastWarning { get; private set; }

        /// <summary>Newest first.</summary>
        public List<HistoryEntry> Load()
        {
            LastWarning = null;
            string file = _paths.HistoryFile;
            if (!File.Exists(file))
            {
                return new List<HistoryEntry>();
            }
            try
            {
                string text = File.ReadAllText(file);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return new List<HistoryEntry>();
                }
                HistoryFile data = Json.Deserialize<HistoryFile>(text);
                return (data?.Entries ?? new List<HistoryEntry>())
                    .Where(e => e != null)
                    .OrderByDescending(e => e.Timestamp)
                    .ToList();
            }
            catch (Exception ex)
            {
                string backup = SettingsStore.BackupCorrupt(file);
                LastWarning = "History file was unreadable and has been reset." +
                              (backup != null ? " Backup: " + backup : "");
                Logger.Warn(LastWarning + " " + ex.Message);
                return new List<HistoryEntry>();
            }
        }

        public void Save(IEnumerable<HistoryEntry> entries)
        {
            var data = new HistoryFile
            {
                Entries = entries.OrderByDescending(e => e.Timestamp).Take(MaxEntries).ToList()
            };
            Json.WriteFileAtomic(_paths.HistoryFile, Json.Serialize(data));
        }

        public void Add(HistoryEntry entry)
        {
            List<HistoryEntry> entries = Load();
            entries.Insert(0, entry);
            Save(entries);
        }

        public void Delete(string id)
        {
            List<HistoryEntry> entries = Load();
            entries.RemoveAll(e => e.Id == id);
            Save(entries);
        }
    }
}
