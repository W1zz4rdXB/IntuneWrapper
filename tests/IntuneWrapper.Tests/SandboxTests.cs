using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml;
using IntuneWrapper.Core;
using Microsoft.Win32;
using Xunit;

namespace IntuneWrapper.Tests
{
    public class SandboxTests
    {
        [Fact]
        public void Configuration_maps_folders_and_starts_the_agent()
        {
            string xml = SandboxLauncher.BuildConfiguration(@"D:\Downloads\A & B", @"C:\Temp\work");
            var doc = new XmlDocument();
            doc.LoadXml(xml); // valid XML even with '&' in a path
            XmlNodeList folders = doc.SelectNodes("/Configuration/MappedFolders/MappedFolder");
            Assert.Equal(2, folders.Count);
            Assert.Equal(@"D:\Downloads\A & B", folders[0]["HostFolder"].InnerText);
            Assert.Equal("true", folders[0]["ReadOnly"].InnerText);
            Assert.Equal(@"C:\Work", folders[1]["SandboxFolder"].InnerText);
            Assert.Equal("false", folders[1]["ReadOnly"].InnerText);
            Assert.Equal(@"C:\Work\IntuneWrapper.exe --sandbox-capture C:\Work C:\Input",
                doc.SelectSingleNode("/Configuration/LogonCommand/Command").InnerText);
        }

        [Fact]
        public void Prepare_writes_agent_job_and_wsb()
        {
            using (var dir = new TempDir())
            {
                string installer = Fixtures.WriteFakeExe(dir, "setup.exe", null);
                string agent = Fixtures.WriteFakeExe(dir, "agent.exe", null);
                string work = Path.Combine(dir.Path, "work");
                string wsb = SandboxLauncher.Prepare(agent, installer, new SandboxJob { InstallerFileName = "setup.exe", InstallCommand = "\"setup.exe\" /S" }, work);
                Assert.True(File.Exists(wsb));
                Assert.True(File.Exists(Path.Combine(work, "IntuneWrapper.exe")));
                SandboxJob job = Json.Deserialize<SandboxJob>(File.ReadAllText(Path.Combine(work, SandboxAgent.JobFile)));
                Assert.Equal("\"setup.exe\" /S", job.InstallCommand);
                Assert.Contains("<HostFolder>" + dir.Path + "</HostFolder>", File.ReadAllText(wsb));
            }
        }

        [Fact]
        public void Sandbox_mode_refuses_to_run_on_a_real_pc()
        {
            Assert.False(SandboxAgent.IsInsideSandbox); // tests never run as WDAGUtilityAccount
            using (var dir = new TempDir())
            {
                // Would install and then shut down; outside the Sandbox it must do nothing.
                Assert.Equal(2, SandboxAgent.RunInSandbox(dir.Path, dir.Path));
                Assert.Empty(Directory.GetFiles(dir.Path));
            }
        }

        [Fact]
        public void Best_entry_is_the_visible_app_named_like_the_product()
        {
            var diff = new SnapshotDiff();
            diff.New.Add(new UninstallEntry { Hive = "HKLM", KeyName = "{RUNTIME}", DisplayName = "Java runtime", UninstallString = "x", SystemComponent = true });
            diff.New.Add(new UninstallEntry { Hive = "HKLM", KeyName = "Other", DisplayName = "Helper tool", UninstallString = "x" });
            diff.New.Add(new UninstallEntry { Hive = "HKLM", KeyName = "4549", DisplayName = "REW 5.31.3", UninstallString = "x" });
            Assert.Equal("4549", SandboxAgent.SelectBest(diff, "REW").KeyName);
            Assert.Null(SandboxAgent.SelectBest(new SnapshotDiff(), "REW"));
        }

        [Fact]
        public void File_rule_is_evaluated_like_the_intune_agent()
        {
            using (var dir = new TempDir())
            {
                string exe = Path.Combine(dir.Path, "app.exe");
                File.Copy(typeof(SandboxTests).Assembly.Location, exe);
                string version = CommandGenerator.ReadFileVersion(exe);
                var rule = DetectionRule.ForFile(new FileTarget { Folder = dir.Path, FileName = "app.exe", Version = version }, Confidence.Detected);
                Assert.True(SandboxAgent.DetectionPresent(rule));

                rule.FileValue = "9999.0";
                Assert.False(SandboxAgent.DetectionPresent(rule)); // installed version is lower

                rule.FileMethod = FileDetectionMethod.Exists;
                rule.FileOrFolder = "missing.exe";
                Assert.False(SandboxAgent.DetectionPresent(rule));
            }
        }

        [Fact]
        public void Commands_run_through_cmd_with_exit_codes_and_timeout()
        {
            string temp = Path.GetTempPath();
            Assert.Equal(3, SandboxAgent.RunCommand("exit 3", temp, TimeSpan.FromSeconds(30)));
            Assert.Equal(0, SandboxAgent.RunCommand("\"" + Path.Combine(Environment.SystemDirectory, "whoami.exe") + "\" /?", temp, TimeSpan.FromSeconds(30)));
            Assert.Null(SandboxAgent.RunCommand("ping -n 30 127.0.0.1", temp, TimeSpan.FromSeconds(1)));

            // A program in the working folder is found by name, like Intune's "setup.exe" commands.
            using (var dir = new TempDir())
            {
                File.WriteAllText(dir.File("setup.cmd"), "@exit /b 7");
                Assert.Equal(7, SandboxAgent.RunCommand("\"setup.cmd\"", dir.Path, TimeSpan.FromSeconds(30)));
            }
        }

        [Fact]
        public void Result_round_trips_through_json()
        {
            var r = new SandboxResult
            {
                Completed = true,
                InstallExitCode = 0,
                SelectedKeyPath = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\App",
                Detection = DetectionRule.ForFile(new FileTarget { Folder = @"%ProgramFiles%\App", FileName = "app.exe", Version = "1.0.0.0" }, Confidence.Detected),
            };
            r.NewEntries.Add(new UninstallEntry { Hive = "HKLM", KeyName = "App", MainExecutable = @"%ProgramFiles%\App\app.exe", MainExecutableVersion = "1.0.0.0" });
            SandboxResult back = Json.Deserialize<SandboxResult>(Json.Serialize(r));
            Assert.Equal("App", back.Selected.KeyName);
            Assert.Equal(@"%ProgramFiles%\App\app.exe", back.Selected.MainExecutable);
            Assert.Equal("app.exe", back.Detection.FileOrFolder);
        }

        [Fact]
        public void Detection_from_sandbox_entry_uses_its_main_executable()
        {
            var entry = new UninstallEntry
            {
                Hive = "HKLM", KeyName = "4549-9647-2313-4375", DisplayName = "REW 5.31.3", DisplayVersion = "5.31.3",
                UninstallString = @"""C:\Program Files\REW\uninstall.exe""",
                MainExecutable = @"%ProgramFiles%\REW\roomeqwizard.exe", MainExecutableVersion = "5.31.3.0",
            };
            var commands = new PackageCommands();
            CommandGenerator.ApplyCapture(commands, entry, InstallerFramework.Install4j);
            Assert.Equal(@"""C:\Program Files\REW\uninstall.exe"" -q", commands.Uninstall.Value);
            Assert.Equal(
                @"Rule type: File | Path: %ProgramFiles%\REW | File or folder: roomeqwizard.exe | Detection method: String (version) | " +
                "Operator: Greater than or equal to | Value: 5.31.3.0 | Associated with a 32-bit app on 64-bit clients: No",
                commands.Detection.Summary(" | "));
            Assert.Equal(Confidence.Detected, commands.Detection.Confidence);
        }

        /// <summary>
        /// The whole agent run on this machine with a harmless fake installer: it registers a per-user
        /// uninstall entry and copies whoami.exe as the "app"; its uninstaller removes both again.
        /// </summary>
        [Fact]
        public void Agent_installs_detects_and_uninstalls_a_fake_app()
        {
            string id = "IWTest_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + id;
            string appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), id);
            using (var dir = new TempDir())
            {
                try
                {
                    string input = Path.Combine(dir.Path, "input");
                    string work = Path.Combine(dir.Path, "work");
                    Directory.CreateDirectory(input);
                    Directory.CreateDirectory(work);
                    // PowerShell instead of reg.exe: reg.exe can be blocked by policy (DisableRegistryTools).
                    string psKey = @"HKCU:\" + key;
                    string ps = "powershell.exe -NoProfile -NonInteractive -Command ";
                    File.WriteAllText(Path.Combine(input, "install.cmd"), string.Join("\r\n", new[]
                    {
                        "@echo off",
                        "set APPDIR=%LOCALAPPDATA%\\" + id,
                        "mkdir \"%APPDIR%\"",
                        "copy /y \"%SystemRoot%\\System32\\whoami.exe\" \"%APPDIR%\\iwtestapp.exe\" >nul",
                        "(echo @echo off& echo " + ps + "\"Remove-Item -LiteralPath '" + psKey + "' -Recurse -Force\"& echo rmdir /s /q \"%APPDIR%\") > \"%APPDIR%\\uninstall.cmd\"",
                        ps + "\"$k = New-Item -Path '" + psKey + "' -Force; " +
                            "Set-ItemProperty $k.PSPath DisplayName 'IW Test App 1.0'; " +
                            "Set-ItemProperty $k.PSPath DisplayVersion '1.0'; " +
                            "Set-ItemProperty $k.PSPath Publisher 'Contoso'; " +
                            "Set-ItemProperty $k.PSPath InstallLocation '%APPDIR%'; " +
                            "Set-ItemProperty $k.PSPath UninstallString '\\\"%APPDIR%\\uninstall.cmd\\\"'\"",
                        "exit /b %errorlevel%",
                    }));
                    Json.WriteFileAtomic(Path.Combine(work, SandboxAgent.JobFile), Json.Serialize(new SandboxJob
                    {
                        InstallerFileName = "install.cmd",
                        InstallCommand = "\"install.cmd\"",
                        Framework = InstallerFramework.Unknown,
                        ProductName = "IW Test App",
                        InstallTimeoutMinutes = 2,
                        UninstallTimeoutMinutes = 2,
                        SettleSeconds = 10,
                    }));

                    SandboxResult r = SandboxAgent.Run(work, input);

                    Assert.True(r.Completed, r.Error + "\n" + string.Join("\n", r.Log));
                    Assert.True(r.InstallExitCode == 0, "exit " + r.InstallExitCode + "\n" + string.Join("\n", r.Log));
                    UninstallEntry entry = r.Selected;
                    Assert.NotNull(entry);
                    Assert.Equal(id, entry.KeyName);
                    Assert.Equal("%LocalAppData%\\" + id + "\\iwtestapp.exe", entry.MainExecutable);
                    Assert.Equal(DetectionRuleType.File, r.Detection.Type);
                    Assert.Equal("%LocalAppData%\\" + id, r.Detection.FilePath);
                    Assert.Equal("iwtestapp.exe", r.Detection.FileOrFolder);
                    Assert.Equal(FileDetectionMethod.Version, r.Detection.FileMethod);
                    Assert.True(r.DetectionFoundAfterInstall, string.Join("\n", r.Log));
                    Assert.True(r.UninstallRan);
                    Assert.True(r.EntryRemovedAfterUninstall, string.Join("\n", r.Log));
                    Assert.True(r.DetectionGoneAfterUninstall, string.Join("\n", r.Log));
                    Assert.True(r.FullyVerified);
                    Assert.True(File.Exists(Path.Combine(work, SandboxAgent.ResultFile)));
                }
                finally
                {
                    // Clean up even if the test failed half-way.
                    Registry.CurrentUser.DeleteSubKeyTree(key, false);
                    if (Directory.Exists(appDir)) Directory.Delete(appDir, true);
                }
            }
        }
    }
}
