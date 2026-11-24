using System;
using System.IO;
using System.Linq;
using IntuneWrapper.Core;
using Xunit;

namespace IntuneWrapper.Tests
{
    public class StorageTests
    {
        [Fact]
        public void Missing_settings_file_gives_defaults()
        {
            using (var dir = new TempDir())
            {
                string warning;
                Settings s = SettingsStore.Load(new AppPaths(dir.Path), out warning);
                Assert.Null(warning);
                Assert.Null(s.ToolPath);
                Assert.Equal(AppPaths.DefaultPackagingFolder, s.PackagingFolder);
                Assert.Equal(InstallContext.System, s.DefaultInstallContext);
                Assert.False(s.ShowLogByDefault);
            }
        }

        [Fact]
        public void Missing_keys_get_defaults()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                File.WriteAllText(paths.SettingsFile, "{ \"toolPath\": \"C:\\\\t\\\\IntuneWinAppUtil.exe\", \"extra\": 1 }");
                string warning;
                Settings s = SettingsStore.Load(paths, out warning);
                Assert.Null(warning);
                Assert.Equal(@"C:\t\IntuneWinAppUtil.exe", s.ToolPath);
                Assert.Equal(AppPaths.DefaultPackagingFolder, s.PackagingFolder);
                Assert.Equal(InstallContext.System, s.DefaultInstallContext);
            }
        }

        [Fact]
        public void Corrupt_settings_are_reset_with_backup()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                File.WriteAllText(paths.SettingsFile, "{ this is not json");
                string warning;
                Settings s = SettingsStore.Load(paths, out warning);
                Assert.NotNull(warning);
                Assert.Equal(AppPaths.DefaultPackagingFolder, s.PackagingFolder);
                Assert.False(File.Exists(paths.SettingsFile));
                Assert.Single(Directory.GetFiles(dir.Path, "settings.json.corrupt-*.bak"));
            }
        }

        [Fact]
        public void Settings_round_trip()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                var s = new Settings
                {
                    ToolPath = @"C:\x\IntuneWinAppUtil.exe",
                    PackagingFolder = @"D:\out",
                    DefaultInstallContext = InstallContext.User,
                    ShowLogByDefault = true
                };
                SettingsStore.Save(paths, s);
                SettingsStore.Save(paths, s); // second save goes through File.Replace
                string warning;
                Settings loaded = SettingsStore.Load(paths, out warning);
                Assert.Equal(s.ToolPath, loaded.ToolPath);
                Assert.Equal(s.PackagingFolder, loaded.PackagingFolder);
                Assert.Equal(InstallContext.User, loaded.DefaultInstallContext);
                Assert.True(loaded.ShowLogByDefault);
            }
        }

        [Fact]
        public void Default_packaging_folder_is_local_not_the_desktop()
        {
            string folder = AppPaths.DefaultPackagingFolder;
            Assert.False(folder.StartsWith(@"\\"), folder);
            Assert.DoesNotContain("Desktop", folder);
            if (new DriveInfo("D").IsReady) Assert.Equal(@"D:\Packaging", folder);
        }

        [Fact]
        public void Old_output_folder_setting_is_replaced_by_packaging_folder()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                File.WriteAllText(paths.SettingsFile,
                    "{ \"outputFolder\": \"D:\\\\IntuneOutput\", \"toolPath\": \"C:\\\\t\\\\IntuneWinAppUtil.exe\" }");
                string warning;
                Settings s = SettingsStore.Load(paths, out warning);
                Assert.Null(warning);
                Assert.Equal(AppPaths.DefaultPackagingFolder, s.PackagingFolder);
                Assert.Equal(@"C:\t\IntuneWinAppUtil.exe", s.ToolPath);
            }
        }

        [Fact]
        public void Custom_packaging_folder_is_kept()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                SettingsStore.Save(paths, new Settings { PackagingFolder = @"E:\Packages" });
                string warning;
                Assert.Equal(@"E:\Packages", SettingsStore.Load(paths, out warning).PackagingFolder);
                Assert.Null(warning);
            }
        }

        [Fact]
        public void Layout_builds_program_input_and_output_folders()
        {
            var layout = new PackagingLayout(@"D:\Packaging\");
            Assert.Equal(@"D:\Packaging\IntuneWinAppUtil.exe", layout.ToolPath);
            Assert.Equal(@"D:\Packaging\REW", layout.AppFolder("REW"));
            Assert.Equal(@"D:\Packaging\REW\input", layout.InputFolder("REW"));
            Assert.Equal(@"D:\Packaging\REW\output", layout.OutputFolder("REW"));
            Assert.Equal(@"D:\Packaging\My_App (x64)", layout.AppFolder("  My/App (x64) "));
            Assert.Equal("_CON", PackagingLayout.FolderName("CON"));
            Assert.Throws<PackagingException>(() => layout.AppFolder(CommandGenerator.PublisherPlaceholder));
            Assert.Throws<PackagingException>(() => layout.AppFolder(" "));
        }

        [Fact]
        public void Prepare_input_copies_installer_once_and_protects_different_files()
        {
            using (var dir = new TempDir())
            {
                string original = Fixtures.WriteFakeExe(dir, "setup.exe", "Inno Setup");
                var layout = new PackagingLayout(Path.Combine(dir.Path, "Packaging"));

                Assert.Equal(InputState.Missing, layout.CheckInput(original, "App"));
                string copied = layout.PrepareInput(original, "App", false);
                Assert.Equal(Path.Combine(dir.Path, @"Packaging\App\input\setup.exe"), copied);
                Assert.True(File.Exists(copied));
                Assert.True(Directory.Exists(layout.OutputFolder("App")));
                Assert.Equal(InputState.Identical, layout.CheckInput(original, "App"));
                Assert.Equal(InputState.IsSource, layout.CheckInput(copied, "App"));

                // A different installer with the same name is not replaced without permission.
                File.WriteAllBytes(original, Fixtures.FakeExe("Nullsoft Install System"));
                Assert.Equal(InputState.Different, layout.CheckInput(original, "App"));
                Assert.Throws<PackagingException>(() => layout.PrepareInput(original, "App", false));
                layout.PrepareInput(original, "App", true);
                Assert.Equal(InputState.Identical, layout.CheckInput(original, "App"));
                Assert.False(File.Exists(copied + ".copying"));
            }
        }

        [Fact]
        public void History_round_trips_all_portal_fields()
        {
            using (var dir = new TempDir())
            {
                var store = new HistoryStore(new AppPaths(dir.Path));
                var rule = DetectionRule.Unknown();
                rule.Type = DetectionRuleType.File;
                rule.FilePath = @"%ProgramFiles%\App";
                rule.FileOrFolder = "app.exe";
                rule.File32Bit = true;
                var c = new PackageCommands
                {
                    AppName = new CommandField("App", Confidence.Detected),
                    Description = new CommandField("Desc", Confidence.Edited),
                    Publisher = new CommandField("Contoso", Confidence.Detected),
                    AppVersion = new CommandField("2.0", Confidence.Detected),
                    Install = new CommandField("\"setup.exe\" /S", Confidence.Suggested),
                    Uninstall = new CommandField("\"C:\\App\\uninst.exe\" /S", Confidence.Detected),
                    InstallTimeMinutes = 30,
                    AllowAvailableUninstall = false,
                    InstallContext = InstallContext.User,
                    Restart = RestartBehavior.ReturnCodes,
                    Architecture = OsArchitecture.X86 | OsArchitecture.X64,
                    MinimumOs = "Windows 11 22H2",
                    Detection = rule,
                };
                var info = new InstallerInfo { Path = @"C:\src\setup.exe", Type = InstallerType.Exe, Sha256 = "AB" };
                store.Add(HistoryEntry.Create(info, c, new PackagingResult { Success = true, OutputPath = @"D:\out\setup.intunewin" }));

                PackageCommands r = store.Load().Single().ToCommands(InstallContext.System);
                Assert.Equal("App", r.AppName.Value);
                Assert.Equal("Desc", r.Description.Value);
                Assert.Equal("Contoso", r.Publisher.Value);
                Assert.Equal("2.0", r.AppVersion.Value);
                Assert.Equal(30, r.InstallTimeMinutes);
                Assert.False(r.AllowAvailableUninstall);
                Assert.Equal(InstallContext.User, r.InstallContext);
                Assert.Equal(RestartBehavior.ReturnCodes, r.Restart);
                Assert.Equal(OsArchitecture.X86 | OsArchitecture.X64, r.Architecture);
                Assert.Equal("Windows 11 22H2", r.MinimumOs);
                Assert.Equal(DetectionRuleType.File, r.Detection.Type);
                Assert.Equal("app.exe", r.Detection.FileOrFolder);
                Assert.True(r.Detection.File32Bit);
                Assert.Equal(Confidence.Edited, r.Detection.Confidence);
            }
        }

        [Fact]
        public void Old_history_entry_without_rule_still_restores()
        {
            var entry = new HistoryEntry { FileName = "a.exe", Install = "\"a.exe\" /S", Uninstall = "u", Detection = "Registry: X" };
            PackageCommands r = entry.ToCommands(InstallContext.System);
            Assert.False(r.Detection.IsComplete);
            Assert.Contains(r.Notes, n => n.Contains("Registry: X"));
            Assert.Equal("a", r.AppName.Value);
        }

        [Fact]
        public void History_keeps_last_50_newest_first()
        {
            using (var dir = new TempDir())
            {
                var store = new HistoryStore(new AppPaths(dir.Path));
                DateTime start = DateTime.Now.AddHours(-1);
                for (int i = 0; i < 55; i++)
                {
                    store.Add(new HistoryEntry { FileName = "f" + i + ".exe", Timestamp = start.AddSeconds(i), Success = i % 2 == 0 });
                }
                var entries = store.Load();
                Assert.Equal(50, entries.Count);
                Assert.Equal("f54.exe", entries[0].FileName);
                Assert.DoesNotContain(entries, e => e.FileName == "f4.exe");
            }
        }

        [Fact]
        public void History_delete_removes_entry()
        {
            using (var dir = new TempDir())
            {
                var store = new HistoryStore(new AppPaths(dir.Path));
                var a = new HistoryEntry { FileName = "a.exe", Install = "x", Notes = new System.Collections.Generic.List<string> { "n" } };
                var b = new HistoryEntry { FileName = "b.exe" };
                store.Add(a);
                store.Add(b);
                store.Delete(a.Id);
                var entries = store.Load();
                Assert.Single(entries);
                Assert.Equal("b.exe", entries[0].FileName);
            }
        }

        [Fact]
        public void Corrupt_history_is_reset_quietly_with_backup()
        {
            using (var dir = new TempDir())
            {
                var paths = new AppPaths(dir.Path);
                File.WriteAllText(paths.HistoryFile, "[[[garbage");
                var store = new HistoryStore(paths);
                Assert.Empty(store.Load());
                Assert.NotNull(store.LastWarning);
                Assert.Single(Directory.GetFiles(dir.Path, "history.json.corrupt-*.bak"));

                store.Add(new HistoryEntry { FileName = "ok.exe" });
                Assert.Equal("ok.exe", store.Load().Single().FileName);
            }
        }

        [Fact]
        public void Old_logs_are_cleaned_up()
        {
            using (var dir = new TempDir())
            {
                var now = new DateTime(2026, 11, 19);
                File.WriteAllText(Path.Combine(dir.Path, "app-2026-11-19.log"), "");
                File.WriteAllText(Path.Combine(dir.Path, "app-2026-11-06.log"), "");
                File.WriteAllText(Path.Combine(dir.Path, "app-2026-11-05.log"), "");
                File.WriteAllText(Path.Combine(dir.Path, "app-2025-01-01.log"), "");
                Assert.Equal(2, Logger.Cleanup(dir.Path, now));
                Assert.Equal(2, Directory.GetFiles(dir.Path).Length);
            }
        }
    }
}
