using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using IntuneWrapper.Core;
using Xunit;

namespace IntuneWrapper.Tests
{
    public class PackageItemTests
    {
        [Fact]
        public void Default_actions_depend_on_what_is_added()
        {
            using (var dir = new TempDir())
            {
                string drivers = Path.Combine(dir.Path, "Drivers");
                Directory.CreateDirectory(Path.Combine(drivers, "x64"));
                File.WriteAllText(Path.Combine(drivers, "x64", "device.inf"), "[Version]");
                string plain = Path.Combine(dir.Path, "Config");
                Directory.CreateDirectory(plain);
                string msi = Fixtures.CreateMsi(dir.File("Driver Setup.msi"), new Dictionary<string, string> { { "ProductCode", "{12345678-ABCD-4EF0-9876-0123456789AB}" } });
                string exe = Fixtures.WriteFakeExe(dir, "usbdriver.exe", "Inno Setup");
                string inf = dir.File("single.inf");
                File.WriteAllText(inf, "[Version]");
                string txt = dir.File("settings.xml");
                File.WriteAllText(txt, "<x/>");

                PackageItem d = PackageItem.Create(drivers + "\\");
                Assert.Equal("Drivers", d.Name);
                Assert.True(d.IsFolder);
                Assert.Equal(PackageItemAction.InstallInfDrivers, d.Action);
                Assert.Equal("pnputil /add-driver \"Drivers\\*.inf\" /subdirs /install", d.Command);

                Assert.Equal(PackageItemAction.Include, PackageItem.Create(plain).Action);
                Assert.Null(PackageItem.Create(plain).Command);
                Assert.Equal("msiexec /i \"Driver Setup.msi\" /qn /norestart", PackageItem.Create(msi).Command);
                Assert.Equal("\"usbdriver.exe\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART", PackageItem.Create(exe).Command);
                Assert.Equal("pnputil /add-driver \"single.inf\" /install", PackageItem.Create(inf).Command);
                Assert.Equal(PackageItemAction.Include, PackageItem.Create(txt).Action);
            }
        }

        [Fact]
        public void Script_is_only_needed_when_something_runs()
        {
            var include = new PackageItem { SourcePath = @"C:\x\Config", Action = PackageItemAction.Include };
            var drivers = new PackageItem { SourcePath = @"C:\x\Drivers", Action = PackageItemAction.InstallInfDrivers };
            Assert.False(InstallScript.IsNeeded(new[] { include }));
            Assert.True(InstallScript.IsNeeded(new[] { include, drivers }));

            var c = new PackageCommands { Install = new CommandField("\"setup.exe\" /S", Confidence.Suggested) };
            Assert.Equal("\"setup.exe\" /S", c.IntuneInstallCommand);
            c.Items.Add(include);
            Assert.Equal("\"setup.exe\" /S", c.IntuneInstallCommand);
            c.Items.Add(drivers);
            Assert.Equal("install.cmd", c.IntuneInstallCommand);
        }

        [Fact]
        public void Script_runs_setup_then_items_and_uses_64bit_pnputil()
        {
            var items = new List<PackageItem>
            {
                new PackageItem { SourcePath = @"C:\x\Config", Action = PackageItemAction.Include },
                new PackageItem { SourcePath = @"C:\x\Drivers", Action = PackageItemAction.InstallInfDrivers, Command = "pnputil /add-driver \"Drivers\\*.inf\" /subdirs /install" },
                new PackageItem { SourcePath = @"C:\x\extra.msi", Action = PackageItemAction.RunInstaller, Command = "msiexec /i \"extra.msi\" /qn /norestart" },
            };
            string script = InstallScript.Build("My App", "\"setup.exe\" /S", items);
            int setup = script.IndexOf("\"setup.exe\" /S >> \"%LOG%\" 2>&1", StringComparison.Ordinal);
            int pnp = script.IndexOf("\"%PNPUTIL%\" /add-driver \"Drivers\\*.inf\" /subdirs /install >> \"%LOG%\" 2>&1", StringComparison.Ordinal);
            int msi = script.IndexOf("msiexec /i \"extra.msi\" /qn /norestart >> \"%LOG%\" 2>&1", StringComparison.Ordinal);
            Assert.True(setup > 0 && pnp > setup && msi > pnp, script);
            Assert.DoesNotContain("Config", script);
            Assert.Contains("Sysnative\\pnputil.exe", script);
            Assert.Contains("cd /d \"%~dp0\"", script);
            Assert.Contains("My_App-install.log", script);
            Assert.Contains(InstallScript.Header, script);
            Assert.Contains("if \"%RC%\"==\"259\" set RC=0", script); // only for the pnputil step
            Assert.Equal(1, CountOf(script, "\"259\""));
        }

        /// <summary>Runs a generated install.cmd with fake steps: exit codes, order, logging and stop-on-failure.</summary>
        [Theory]
        [InlineData(0, 0, 0, 0, "1,2,3")]
        [InlineData(0, 3010, 0, 3010, "1,2,3")]    // restart needed is passed on after all steps
        [InlineData(0, 259, 0, 0, "1,2,3")]        // pnputil: no matching device is fine
        [InlineData(0, 5, 0, 5, "1,2")]            // a failing step stops the script with its code
        [InlineData(1603, 0, 0, 1603, "1")]        // failing setup: nothing else runs
        public void Generated_script_handles_exit_codes(int setupCode, int driverCode, int msiCode, int expected, string ran)
        {
            using (var dir = new TempDir())
            {
                // Fake steps record that they ran and return the given exit code.
                File.WriteAllText(dir.File("setup.cmd"), "@>>\"%~dp0ran.txt\" echo 1\r\n@exit /b " + setupCode);
                File.WriteAllText(dir.File("drivers.cmd"), "@>>\"%~dp0ran.txt\" echo 2\r\n@exit /b " + driverCode);
                File.WriteAllText(dir.File("extra.cmd"), "@>>\"%~dp0ran.txt\" echo 3\r\n@exit /b " + msiCode);
                var items = new List<PackageItem>
                {
                    new PackageItem { SourcePath = dir.File("drivers.cmd"), Action = PackageItemAction.InstallInfDrivers, Command = "call \"drivers.cmd\"" },
                    new PackageItem { SourcePath = dir.File("extra.cmd"), Action = PackageItemAction.RunInstaller, Command = "call \"extra.cmd\"" },
                };
                string app = "IWTest " + Guid.NewGuid().ToString("N").Substring(0, 6);
                string script = InstallScript.Build(app, "call \"setup.cmd\"", items);
                File.WriteAllText(dir.File("install.cmd"), script, System.Text.Encoding.Default);
                string log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "IntuneWrapper", "Logs", app.Replace(' ', '_') + "-install.log");
                try
                {
                    int? exit = SandboxAgent.RunCommand("install.cmd", dir.Path, TimeSpan.FromSeconds(60));
                    Assert.Equal(expected, exit);
                    string steps = string.Join(",", File.ReadAllLines(dir.File("ran.txt")).Select(l => l.Trim()));
                    Assert.Equal(ran, steps);
                    Assert.Contains("Step 1 exit code " + setupCode, File.ReadAllText(log));
                }
                finally
                {
                    if (File.Exists(log)) File.Delete(log);
                }
            }
        }

        [Fact]
        public void Prepare_items_copies_folders_and_writes_or_removes_the_script()
        {
            using (var dir = new TempDir())
            {
                string drivers = Path.Combine(dir.Path, "src", "Drivers");
                Directory.CreateDirectory(Path.Combine(drivers, "x64"));
                File.WriteAllText(Path.Combine(drivers, "x64", "device.inf"), "[Version]");
                string config = dir.File("settings.xml");
                File.WriteAllText(config, "<x/>");
                var layout = new PackagingLayout(Path.Combine(dir.Path, "Packaging"));
                var items = new List<PackageItem> { PackageItem.Create(drivers), PackageItem.Create(config) };

                layout.PrepareItems("App", items, InstallScript.Build("App", "\"setup.exe\" /S", items));
                string input = layout.InputFolder("App");
                Assert.True(File.Exists(Path.Combine(input, @"Drivers\x64\device.inf")));
                Assert.True(File.Exists(Path.Combine(input, "settings.xml")));
                Assert.True(File.Exists(Path.Combine(input, "install.cmd")));

                // No script needed any more: our generated install.cmd is removed, a user's own is kept.
                layout.PrepareItems("App", items.Take(0), null);
                Assert.False(File.Exists(Path.Combine(input, "install.cmd")));
                File.WriteAllText(Path.Combine(input, "install.cmd"), "@echo my own script");
                layout.PrepareItems("App", items.Take(0), null);
                Assert.True(File.Exists(Path.Combine(input, "install.cmd")));

                HashSet<string> expected = PackagingLayout.ExpectedInputNames("setup.exe", items, true);
                Assert.Contains("Drivers", expected);
                Assert.Contains("install.cmd", expected);
                Assert.Contains("SETUP.EXE", expected); // case-insensitive
            }
        }

        [Fact]
        public void Items_survive_history_and_show_in_copy_all()
        {
            using (var dir = new TempDir())
            {
                var c = new PackageCommands
                {
                    AppName = new CommandField("App", Confidence.Detected),
                    Install = new CommandField("\"setup.exe\" /S", Confidence.Suggested),
                };
                c.Items.Add(new PackageItem { SourcePath = @"C:\src\Drivers", Action = PackageItemAction.InstallInfDrivers, Command = "pnputil /add-driver \"Drivers\\*.inf\" /subdirs /install" });
                string text = Summary.Format("App", "setup.intunewin", c);
                Assert.Contains("Install command:              install.cmd", text);
                Assert.Contains("[Package contents]", text);
                Assert.Contains("Install INF drivers (pnputil)", text);

                var store = new HistoryStore(new AppPaths(dir.Path));
                store.Add(HistoryEntry.Create(new InstallerInfo { Path = @"C:\src\setup.exe" }, c, null));
                PackageCommands back = store.Load().Single().ToCommands(InstallContext.System);
                Assert.Single(back.Items);
                Assert.Equal(PackageItemAction.InstallInfDrivers, back.Items[0].Action);
                Assert.Equal("install.cmd", back.IntuneInstallCommand);
            }
        }

        private static int CountOf(string text, string part)
        {
            int count = 0, i = 0;
            while ((i = text.IndexOf(part, i, StringComparison.Ordinal)) >= 0) { count++; i += part.Length; }
            return count;
        }
    }
}
