using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Security;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace IntuneWrapper.Core
{
    /// <summary>One subkey under an Uninstall key. Serializable so a Sandbox run can hand it back.</summary>
    [DataContract]
    public sealed class UninstallEntry
    {
        [DataMember(Name = "hive")] public string Hive { get; set; }                 // HKLM or HKCU
        [DataMember(Name = "is32BitView")] public bool Is32BitView { get; set; }     // HKLM WOW6432Node
        [DataMember(Name = "keyName")] public string KeyName { get; set; }
        [DataMember(Name = "displayName")] public string DisplayName { get; set; }
        [DataMember(Name = "displayVersion")] public string DisplayVersion { get; set; }
        [DataMember(Name = "publisher")] public string Publisher { get; set; }
        [DataMember(Name = "uninstallString")] public string UninstallString { get; set; }
        [DataMember(Name = "quietUninstallString")] public string QuietUninstallString { get; set; }
        [DataMember(Name = "installLocation")] public string InstallLocation { get; set; }
        [DataMember(Name = "displayIcon")] public string DisplayIcon { get; set; }
        [DataMember(Name = "windowsInstaller")] public bool WindowsInstaller { get; set; }
        [DataMember(Name = "systemComponent")] public bool SystemComponent { get; set; }

        /// <summary>
        /// Main exe found on the machine where the app was installed (e.g. inside Windows Sandbox), with its
        /// file version. Used for the File detection rule when the file is not on this machine.
        /// </summary>
        [DataMember(Name = "mainExecutable", EmitDefaultValue = false)] public string MainExecutable { get; set; }
        [DataMember(Name = "mainExecutableVersion", EmitDefaultValue = false)] public string MainExecutableVersion { get; set; }

        /// <summary>Stable identity used for the diff: hive, view and key name.</summary>
        public string KeyPath
        {
            get
            {
                string baseKey = Hive == "HKLM" && Is32BitView
                    ? @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                    : @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
                return Hive + @"\" + baseKey + @"\" + KeyName;
            }
        }

        /// <summary>Path for the Intune registry detection rule (Intune applies WOW redirection itself).</summary>
        public string DetectionKeyPath
        {
            get
            {
                string hive = Hive == "HKLM" ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER";
                return hive + @"\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + KeyName;
            }
        }

        public bool IsMsiProductCodeKey => Regex.IsMatch(KeyName ?? "", @"^\{[0-9A-Fa-f\-]{36}\}$");

        public string Label => (string.IsNullOrWhiteSpace(DisplayName) ? KeyName : DisplayName)
                               + (string.IsNullOrWhiteSpace(DisplayVersion) ? "" : " " + DisplayVersion);
    }

    public sealed class UninstallSnapshot
    {
        public List<UninstallEntry> Entries { get; } = new List<UninstallEntry>();

        /// <summary>Keys that could not be read, usually because of permissions.</summary>
        public List<string> AccessErrors { get; } = new List<string>();

        public DateTime TakenAt { get; set; } = DateTime.Now;
    }

    public sealed class SnapshotDiff
    {
        public List<UninstallEntry> New { get; } = new List<UninstallEntry>();
        public List<UninstallEntry> Changed { get; } = new List<UninstallEntry>();
    }

    /// <summary>Registry snapshot and diff for capture mode (SPEC 4.5, 5.4).</summary>
    public static class UninstallRegistry
    {
        private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        public static bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        public static UninstallSnapshot TakeSnapshot()
        {
            var snapshot = new UninstallSnapshot();
            if (Environment.Is64BitOperatingSystem)
            {
                Read(snapshot, RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM", false);
                Read(snapshot, RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM", true);
            }
            else
            {
                Read(snapshot, RegistryHive.LocalMachine, RegistryView.Default, "HKLM", false);
            }
            Read(snapshot, RegistryHive.CurrentUser, RegistryView.Default, "HKCU", false);
            return snapshot;
        }

        private static void Read(UninstallSnapshot snapshot, RegistryHive hive, RegistryView view, string hiveName, bool is32)
        {
            string where = hiveName + (is32 ? " (32-bit)" : "") + @"\" + UninstallKey;
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                using (RegistryKey uninstall = root.OpenSubKey(UninstallKey, false))
                {
                    if (uninstall == null)
                    {
                        return;
                    }
                    foreach (string name in uninstall.GetSubKeyNames())
                    {
                        try
                        {
                            using (RegistryKey sub = uninstall.OpenSubKey(name, false))
                            {
                                if (sub == null) continue;
                                snapshot.Entries.Add(new UninstallEntry
                                {
                                    Hive = hiveName,
                                    Is32BitView = is32,
                                    KeyName = name,
                                    DisplayName = sub.GetValue("DisplayName") as string,
                                    DisplayVersion = sub.GetValue("DisplayVersion") as string,
                                    Publisher = sub.GetValue("Publisher") as string,
                                    UninstallString = sub.GetValue("UninstallString") as string,
                                    QuietUninstallString = sub.GetValue("QuietUninstallString") as string,
                                    InstallLocation = sub.GetValue("InstallLocation") as string,
                                    DisplayIcon = sub.GetValue("DisplayIcon") as string,
                                    WindowsInstaller = IsOne(sub.GetValue("WindowsInstaller")),
                                    SystemComponent = IsOne(sub.GetValue("SystemComponent")),
                                });
                            }
                        }
                        catch (Exception ex) when (ex is SecurityException || ex is UnauthorizedAccessException)
                        {
                            snapshot.AccessErrors.Add(where + @"\" + name);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is SecurityException || ex is UnauthorizedAccessException)
            {
                snapshot.AccessErrors.Add(where);
            }
        }

        private static bool IsOne(object value)
        {
            return value is int && (int)value == 1;
        }

        /// <summary>
        /// New = key path absent from <paramref name="before"/>. Changed = DisplayVersion or
        /// UninstallString differs (SPEC 5.4).
        /// </summary>
        public static SnapshotDiff Compare(IEnumerable<UninstallEntry> before, IEnumerable<UninstallEntry> after)
        {
            var old = new Dictionary<string, UninstallEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (UninstallEntry e in before)
            {
                old[e.KeyPath] = e;
            }

            var diff = new SnapshotDiff();
            foreach (UninstallEntry e in after)
            {
                UninstallEntry previous;
                if (!old.TryGetValue(e.KeyPath, out previous))
                {
                    diff.New.Add(e);
                }
                else if (!string.Equals(previous.DisplayVersion, e.DisplayVersion, StringComparison.Ordinal)
                         || !string.Equals(previous.UninstallString, e.UninstallString, StringComparison.Ordinal))
                {
                    diff.Changed.Add(e);
                }
            }

            // Visible apps first; hidden system components (e.g. a bundle's inner MSIs) last.
            Sort(diff.New);
            Sort(diff.Changed);
            return diff;
        }

        private static void Sort(List<UninstallEntry> list)
        {
            List<UninstallEntry> sorted = list
                .OrderBy(e => e.SystemComponent || string.IsNullOrWhiteSpace(e.DisplayName) ? 1 : 0)
                .ThenBy(e => e.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();
            list.Clear();
            list.AddRange(sorted);
        }
    }
}
