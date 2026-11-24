using System;
using System.Collections.Generic;
using System.IO;
using IntuneWrapper.Core;
using Xunit;

namespace IntuneWrapper.Tests
{
    public class CaptureTests
    {
        private static UninstallEntry Entry(string key, string name = null, string version = "1.0", string uninstall = null,
            string quiet = null, string hive = "HKLM", bool is32 = false, bool msi = false)
        {
            return new UninstallEntry
            {
                Hive = hive, Is32BitView = is32, KeyName = key, DisplayName = name ?? key, DisplayVersion = version,
                UninstallString = uninstall, QuietUninstallString = quiet, WindowsInstaller = msi,
            };
        }

        [Fact]
        public void Diff_reports_new_and_changed_entries()
        {
            var before = new List<UninstallEntry>
            {
                Entry("Stable", uninstall: "u.exe"),
                Entry("Upgraded", version: "1.0", uninstall: "u.exe"),
                Entry("Moved", uninstall: "old.exe"),
                Entry("Removed"),
            };
            var after = new List<UninstallEntry>
            {
                Entry("Stable", uninstall: "u.exe"),
                Entry("Upgraded", version: "2.0", uninstall: "u.exe"),
                Entry("Moved", uninstall: "new.exe"),
                Entry("Brand New App_is1", "Brand New App"),
            };

            SnapshotDiff diff = UninstallRegistry.Compare(before, after);
            Assert.Single(diff.New);
            Assert.Equal("Brand New App_is1", diff.New[0].KeyName);
            Assert.Equal(2, diff.Changed.Count);
            Assert.Contains(diff.Changed, e => e.KeyName == "Upgraded");
            Assert.Contains(diff.Changed, e => e.KeyName == "Moved");
        }

        [Fact]
        public void Same_key_name_in_other_view_or_hive_counts_as_new()
        {
            var before = new List<UninstallEntry> { Entry("App", is32: false) };
            var after = new List<UninstallEntry> { Entry("App", is32: false), Entry("App", is32: true), Entry("App", hive: "HKCU") };
            Assert.Equal(2, UninstallRegistry.Compare(before, after).New.Count);
        }

        [Fact]
        public void Quiet_uninstall_string_is_preferred()
        {
            var commands = new PackageCommands { Notes = new List<string> { "WARNING: The uninstall command is a placeholder. x" } };
            CommandGenerator.ApplyCapture(commands,
                Entry("Bundle", "Bundle", "3.1", uninstall: "\"C:\\b.exe\" /uninstall", quiet: "\"C:\\b.exe\" /uninstall /quiet"),
                InstallerFramework.WixBurn);
            Assert.Equal("\"C:\\b.exe\" /uninstall /quiet", commands.Uninstall.Value);
            Assert.Equal(Confidence.Detected, commands.Uninstall.Confidence);
            Assert.DoesNotContain(commands.Notes, n => n.StartsWith("WARNING: The uninstall command is a placeholder"));

            // No exe or install folder known: the File rule still needs Path and File or folder.
            Assert.Equal(DetectionRuleType.File, commands.Detection.Type);
            Assert.False(commands.Detection.IsComplete);
            Assert.Equal(Confidence.Unknown, commands.Detection.Confidence);
        }

        [Fact]
        public void Capture_builds_file_rule_from_display_icon_exe()
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var entry = Entry("App", "App", "5.0", uninstall: "\"" + pf + "\\NotInstalledApp\\uninstall.exe\"");
            entry.DisplayIcon = "\"" + pf + "\\NotInstalledApp\\app.exe\",0";
            var commands = new PackageCommands();
            CommandGenerator.ApplyCapture(commands, entry, InstallerFramework.Install4j);

            Assert.Equal("\"" + pf + "\\NotInstalledApp\\uninstall.exe\" -q", commands.Uninstall.Value);
            DetectionRule rule = commands.Detection;
            Assert.Equal(DetectionRuleType.File, rule.Type);
            Assert.True(rule.IsComplete);
            Assert.Equal(Confidence.Suggested, rule.Confidence); // file not on this machine, so no version
            Assert.Equal(
                @"Rule type: File | Path: %ProgramFiles%\NotInstalledApp | File or folder: app.exe | Detection method: File or folder exists | " +
                "Associated with a 32-bit app on 64-bit clients: No",
                rule.Summary(" | "));
        }

        [Fact]
        public void Capture_finds_main_exe_in_install_location_and_reads_its_version()
        {
            using (var dir = new TempDir())
            {
                // Like REW: DisplayIcon is an .ico, the install folder has the app and its uninstaller.
                string install = Path.Combine(dir.Path, "REW");
                Directory.CreateDirectory(install);
                File.WriteAllBytes(Path.Combine(install, "uninstall.exe"), new byte[50000]);
                File.WriteAllBytes(Path.Combine(install, "tool.exe"), new byte[100]);
                string thisExe = typeof(CaptureTests).Assembly.Location; // a real file with a version resource
                File.Copy(thisExe, Path.Combine(install, "roomeqwizard.exe"));

                var entry = Entry("4549-9647-2313-4375", "REW 5.31.3", "5.31.3", uninstall: "\"" + install + "\\uninstall.exe\"", hive: "HKCU");
                entry.InstallLocation = install;
                entry.DisplayIcon = install + "\\.install4j\\installer.ico";
                var commands = new PackageCommands();
                CommandGenerator.ApplyCapture(commands, entry, InstallerFramework.Install4j);

                DetectionRule rule = commands.Detection;
                Assert.Equal(DetectionRuleType.File, rule.Type);
                Assert.Equal(Portal.ToIntunePath(install), rule.FilePath); // temp is under %LocalAppData%
                Assert.Equal("roomeqwizard.exe", rule.FileOrFolder); // largest non-helper exe; uninstall.exe is skipped
                Assert.Equal(FileDetectionMethod.Version, rule.FileMethod);
                Assert.Equal(ComparisonOperator.GreaterThanOrEqualTo, rule.FileOperator);
                Assert.Equal(CommandGenerator.ReadFileVersion(thisExe), rule.FileValue);
                Assert.Equal(Confidence.Detected, rule.Confidence);
                Assert.True(rule.IsComplete);
            }
        }

        [Fact]
        public void Capture_falls_back_to_install_folder()
        {
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var entry = Entry("Tool_is1", "Tool", "2.0", uninstall: "\"" + pf86 + "\\Vendor\\NoSuchTool\\unins000.exe\"", is32: true);
            entry.InstallLocation = pf86 + "\\Vendor\\NoSuchTool\\";
            var commands = new PackageCommands();
            CommandGenerator.ApplyCapture(commands, entry, InstallerFramework.InnoSetup);
            Assert.Equal(DetectionRuleType.File, commands.Detection.Type);
            Assert.Equal(@"%ProgramFiles(x86)%\Vendor", commands.Detection.FilePath);
            Assert.Equal("NoSuchTool", commands.Detection.FileOrFolder);
            Assert.Equal(FileDetectionMethod.Exists, commands.Detection.FileMethod);
            Assert.False(commands.Detection.File32Bit);
            Assert.Equal("2.0", commands.AppVersion.Value);
        }

        [Fact]
        public void Capture_keeps_msi_file_rule_when_entry_has_no_location()
        {
            DetectionRule fromMsi = DetectionRule.ForFile(new FileTarget
            {
                Folder = @"%ProgramFiles%\Contoso", FileName = "app.exe", Version = "1.2.0.0"
            }, Confidence.Suggested);
            var commands = new PackageCommands { Detection = fromMsi };
            const string code = "{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}";
            CommandGenerator.ApplyCapture(commands, Entry(code, "Inner", "1.2", uninstall: "MsiExec.exe /X" + code, msi: true),
                InstallerFramework.Unknown);
            Assert.Equal("app.exe", commands.Detection.FileOrFolder);
            Assert.Equal("1.2.0.0", commands.Detection.FileValue);
        }

        [Fact]
        public void Capture_fills_missing_publisher()
        {
            var commands = new PackageCommands { Publisher = new CommandField(CommandGenerator.PublisherPlaceholder, Confidence.Unknown) };
            var entry = Entry("A", "A", "1", uninstall: "\"C:\\A\\unins000.exe\"");
            entry.Publisher = "Contoso";
            CommandGenerator.ApplyCapture(commands, entry, InstallerFramework.InnoSetup);
            Assert.Equal("Contoso", commands.Publisher.Value);
        }

        [Fact]
        public void Inno_uninstall_string_gets_silent_switches()
        {
            var commands = new PackageCommands();
            CommandGenerator.ApplyCapture(commands,
                Entry("App_is1", "App", "5.0", uninstall: "\"C:\\Program Files (x86)\\App\\unins000.exe\"", is32: true),
                InstallerFramework.InnoSetup);
            Assert.Equal("\"C:\\Program Files (x86)\\App\\unins000.exe\" /VERYSILENT /NORESTART", commands.Uninstall.Value);
            Assert.Equal(Confidence.Suggested, commands.Uninstall.Confidence);
            Assert.Equal(DetectionRuleType.File, commands.Detection.Type);
        }

        [Fact]
        public void Unknown_framework_guesses_from_uninstaller_name()
        {
            var commands = new PackageCommands();
            CommandGenerator.ApplyCapture(commands, Entry("X_is1", uninstall: "\"C:\\X\\unins001.exe\""), InstallerFramework.Unknown);
            Assert.Equal("\"C:\\X\\unins001.exe\" /VERYSILENT /NORESTART", commands.Uninstall.Value);

            CommandGenerator.ApplyCapture(commands, Entry("Y", uninstall: "\"C:\\Y\\remove.exe\""), InstallerFramework.Unknown);
            Assert.Equal("\"C:\\Y\\remove.exe\"", commands.Uninstall.Value);
            Assert.Equal(Confidence.Unknown, commands.Uninstall.Confidence);
        }

        [Fact]
        public void Msi_entry_becomes_msiexec_x_and_product_code_detection()
        {
            const string code = "{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}";
            var commands = new PackageCommands();
            CommandGenerator.ApplyCapture(commands, Entry(code, "Inner", "1.2", uninstall: "MsiExec.exe /I" + code, msi: true),
                InstallerFramework.Unknown);
            Assert.Equal("msiexec /x " + code + " /qn /norestart", commands.Uninstall.Value);
            Assert.Equal(Confidence.Detected, commands.Uninstall.Confidence);
            Assert.Equal(DetectionRuleType.File, commands.Detection.Type);
            Assert.Equal(code, commands.Detection.MsiProductCode);
            Assert.StartsWith("Rule type: File | Path:", commands.Detection.Summary(" | "));
        }

        [Fact]
        public void Entry_without_uninstall_string_keeps_placeholder()
        {
            var commands = new PackageCommands();
            CommandGenerator.ApplyCapture(commands, Entry("Odd"), InstallerFramework.Nsis);
            Assert.True(commands.Uninstall.IsPlaceholder);
        }

        [Fact]
        public void Live_snapshot_reads_without_throwing()
        {
            UninstallSnapshot snapshot = UninstallRegistry.TakeSnapshot();
            Assert.NotNull(snapshot.Entries);
            Assert.All(snapshot.Entries, e => Assert.False(string.IsNullOrEmpty(e.KeyName)));
        }
    }
}
