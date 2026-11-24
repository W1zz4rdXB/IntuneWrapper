using System;
using System.Collections.Generic;
using IntuneWrapper.Core;
using Xunit;

namespace IntuneWrapper.Tests
{
    public class CommandGeneratorTests
    {
        private const string ProductCode = "{12345678-ABCD-4EF0-9876-0123456789AB}";

        /// <summary>TARGETDIR > programFiles > Contoso > My App (INSTALLDIR) > bin.</summary>
        private static List<string[]> StandardDirectories(string programFiles)
        {
            return new List<string[]>
            {
                new[] { "TARGETDIR", "", "SourceDir" },
                new[] { programFiles, "TARGETDIR", "." },
                new[] { "VendorDir", programFiles, "Contoso" },
                new[] { "INSTALLDIR", "VendorDir", "MYAPP~1|My App" },
                new[] { "BIN", "INSTALLDIR", "bin:srcbin" },
            };
        }

        [Fact]
        public void Msi_detection_is_a_file_rule_on_the_main_exe()
        {
            using (var dir = new TempDir())
            {
                string msi = Fixtures.CreateMsi(dir.File("x86.msi"), new Dictionary<string, string>
                {
                    { "ProductCode", ProductCode }, { "ProductName", "Tool" },
                }, StandardDirectories("ProgramFilesFolder"), new[]
                {
                    new[] { "tool.exe", "BIN", "", "1000" },
                    new[] { "uninstall.exe", "BIN", "1.0", "5000" },
                });
                DetectionRule rule = CommandGenerator.Generate(InstallerInspector.Inspect(msi), InstallContext.System).Detection;
                Assert.Equal(DetectionRuleType.File, rule.Type);
                Assert.Equal(@"%ProgramFiles%\Contoso\My App\bin", rule.FilePath);
                Assert.Equal("tool.exe", rule.FileOrFolder);
                Assert.Equal(FileDetectionMethod.Exists, rule.FileMethod); // no file version in the MSI
                Assert.True(rule.File32Bit); // ProgramFilesFolder = 32-bit Program Files on 64-bit clients
                Assert.True(rule.IsComplete);
            }
        }

        [Fact]
        public void Msi_without_file_tables_gets_file_rule_placeholders()
        {
            using (var dir = new TempDir())
            {
                string msi = Fixtures.CreateMsi(dir.File("bare.msi"), new Dictionary<string, string> { { "ProductCode", ProductCode } });
                PackageCommands c = CommandGenerator.Generate(InstallerInspector.Inspect(msi), InstallContext.System);
                Assert.Equal(DetectionRuleType.File, c.Detection.Type);
                Assert.Contains("Detection rules: Path", c.MissingRequired());
                Assert.Contains("Detection rules: File or folder", c.MissingRequired());
            }
        }

        [Fact]
        public void Msi_reads_product_code_and_builds_msiexec_commands()
        {
            using (var dir = new TempDir())
            {
                string msi = Fixtures.CreateMsi(dir.File("My App (x64).msi"), new Dictionary<string, string>
                {
                    { "ProductCode", ProductCode },
                    { "ProductName", "My App" },
                    { "ProductVersion", "2.3.4" },
                    { "Manufacturer", "Contoso" },
                }, StandardDirectories("ProgramFiles64Folder"), new[]
                {
                    new[] { "unins000.exe", "INSTALLDIR", "1.0.0.0", "900000" },
                    new[] { "MYAPP~1.EXE|MyApp.exe", "INSTALLDIR", "2.3.4.0", "5000" },
                    new[] { "helper.exe", "BIN", "2.3.4.0", "800000" },
                    new[] { "readme.txt", "INSTALLDIR", "", "100" },
                });

                InstallerInfo info = InstallerInspector.Inspect(msi);
                Assert.Equal(InstallerType.Msi, info.Type);
                Assert.Equal(ProductCode, info.ProductCode);
                Assert.Equal("My App", info.ProductName);
                Assert.Equal("2.3.4", info.ProductVersion);
                Assert.Equal("Contoso", info.Publisher);
                Assert.Equal(64, info.Sha256.Length);

                PackageCommands c = CommandGenerator.Generate(info, InstallContext.System);
                Assert.Equal("msiexec /i \"My App (x64).msi\" /qn /norestart", c.Install.Value);
                Assert.Equal("msiexec /x " + ProductCode + " /qn /norestart", c.Uninstall.Value);
                Assert.Equal(Confidence.Detected, c.Install.Confidence);
                Assert.Equal(Confidence.Detected, c.Uninstall.Confidence);
                Assert.Equal(Confidence.Suggested, c.Detection.Confidence);
                Assert.Equal(
                    @"Rule type: File | Path: %ProgramFiles%\Contoso\My App | File or folder: MyApp.exe | " +
                    "Detection method: String (version) | Operator: Greater than or equal to | Value: 2.3.4.0 | " +
                    "Associated with a 32-bit app on 64-bit clients: No",
                    c.Detection.Summary(" | "));
                Assert.Equal(ProductCode, c.Detection.MsiProductCode);
                Assert.Equal(InstallContext.System, c.InstallContext);
                Assert.Equal(RestartBehavior.Suppress, c.Restart);

                // App information for the Intune portal
                Assert.Equal("My App", c.AppName.Value);
                Assert.Equal("Contoso", c.Publisher.Value);
                Assert.Equal("2.3.4", c.AppVersion.Value);
                Assert.Equal("My App", c.Description.Value);
                Assert.Equal(Confidence.Suggested, c.Description.Confidence);
                Assert.Equal(60, c.InstallTimeMinutes);
                Assert.True(c.AllowAvailableUninstall);
                Assert.Equal(Portal.DefaultMinimumOs, c.MinimumOs);
                Assert.Empty(c.MissingRequired());
                Assert.False(c.NeedsAttention);
            }
        }

        [Fact]
        public void Msi_handle_is_released_so_file_can_be_deleted()
        {
            using (var dir = new TempDir())
            {
                string msi = Fixtures.CreateMsi(dir.File("a.msi"), new Dictionary<string, string> { { "ProductCode", ProductCode } });
                InstallerInspector.Inspect(msi);
                System.IO.File.Delete(msi);
                Assert.False(System.IO.File.Exists(msi));
            }
        }

        [Fact]
        public void Msi_without_product_code_falls_back_to_unknown_placeholder()
        {
            using (var dir = new TempDir())
            {
                string msi = Fixtures.CreateMsi(dir.File("noid.msi"), new Dictionary<string, string> { { "ProductName", "X" } });
                PackageCommands c = CommandGenerator.Generate(InstallerInspector.Inspect(msi), InstallContext.System);
                Assert.Equal(Confidence.Unknown, c.Uninstall.Confidence);
                Assert.True(c.Uninstall.IsPlaceholder);
                Assert.True(c.NeedsAttention);
            }
        }

        [Fact]
        public void Corrupt_msi_falls_back_to_unknown_instead_of_throwing()
        {
            using (var dir = new TempDir())
            {
                string path = dir.File("broken.msi");
                System.IO.File.WriteAllText(path, "not an msi");
                InstallerInfo info = InstallerInspector.Inspect(path);
                Assert.Null(info.ProductCode);
                Assert.NotNull(info.MsiReadError);
                PackageCommands c = CommandGenerator.Generate(info, InstallContext.System);
                Assert.True(c.Uninstall.IsPlaceholder);
            }
        }

        [Theory]
        [InlineData("Inno Setup Setup Data (6.2.0)", InstallerFramework.InnoSetup, "\"setup.exe\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART", "/VERYSILENT /NORESTART")]
        [InlineData("Nullsoft Install System v3.08", InstallerFramework.Nsis, "\"setup.exe\" /S", "/S")]
        [InlineData("InstallShield", InstallerFramework.InstallShield, "\"setup.exe\" /s /v\"/qn /norestart\"", "/s /v\"/qn\"")]
        [InlineData(".wixburn", InstallerFramework.WixBurn, "\"setup.exe\" /quiet /norestart", null)]
        [InlineData("install4j", InstallerFramework.Install4j, "\"setup.exe\" -q", "-q")]
        public void Exe_frameworks_get_suggested_install_and_placeholder_uninstall(string marker, InstallerFramework expected,
            string install, string uninstallSwitches)
        {
            using (var dir = new TempDir())
            {
                InstallerInfo info = InstallerInspector.Inspect(Fixtures.WriteFakeExe(dir, "setup.exe", marker));
                Assert.Equal(expected, info.Framework);

                PackageCommands c = CommandGenerator.Generate(info, InstallContext.System);
                Assert.Equal(install, c.Install.Value);
                Assert.Equal(Confidence.Suggested, c.Install.Confidence);
                Assert.Equal(Confidence.Unknown, c.Uninstall.Confidence);
                Assert.True(c.Uninstall.IsPlaceholder);
                Assert.Contains("Capture", c.Uninstall.Value);
                if (uninstallSwitches != null)
                {
                    Assert.EndsWith(uninstallSwitches, c.Uninstall.Value);
                }
                Assert.False(c.Detection.IsComplete);
                Assert.Equal(Confidence.Unknown, c.Detection.Confidence);
                Assert.Contains("Detection rules: Path", c.MissingRequired());
                Assert.Contains("Program: Uninstall command", c.MissingRequired());
                Assert.Equal(OsArchitecture.X64, c.Architecture);
                Assert.Equal(Confidence.Suggested, c.ArchitectureConfidence);
                Assert.Contains(c.Notes, n => n.StartsWith("WARNING: The uninstall command is a placeholder"));
            }
        }

        [Fact]
        public void Squirrel_needs_both_markers()
        {
            using (var dir = new TempDir())
            {
                string both = Fixtures.WriteFakeExe(dir, "a.exe", "Squirrel ... Update.exe");
                string one = Fixtures.WriteFakeExe(dir, "b.exe", "Squirrel");
                Assert.Equal(InstallerFramework.Squirrel, InstallerInspector.Inspect(both).Framework);
                Assert.Equal(InstallerFramework.Unknown, InstallerInspector.Inspect(one).Framework);
                Assert.Equal("\"a.exe\" --silent", CommandGenerator.Generate(InstallerInspector.Inspect(both), InstallContext.User).Install.Value);
            }
        }

        [Fact]
        public void Unknown_exe_gets_unknown_confidence_and_placeholder()
        {
            using (var dir = new TempDir())
            {
                InstallerInfo info = InstallerInspector.Inspect(Fixtures.WriteFakeExe(dir, "tool.exe", "nothing to see"));
                Assert.Equal(InstallerFramework.Unknown, info.Framework);
                Assert.Equal("tool", info.DisplayName);

                PackageCommands c = CommandGenerator.Generate(info, InstallContext.System);
                Assert.Equal("\"tool.exe\" /S", c.Install.Value);
                Assert.Equal(Confidence.Unknown, c.Install.Confidence);
                Assert.Equal(CommandGenerator.UninstallPlaceholder, c.Uninstall.Value);
                Assert.Equal(Confidence.Unknown, c.Uninstall.Confidence);
                Assert.False(string.IsNullOrWhiteSpace(c.Uninstall.Value));
                Assert.True(c.NeedsAttention);

                // No version info in the file: name falls back to the file name, publisher is required.
                Assert.Equal("tool", c.AppName.Value);
                Assert.Equal(Confidence.Suggested, c.AppName.Confidence);
                Assert.True(c.Publisher.IsPlaceholder);
                Assert.Contains("App information: Publisher", c.MissingRequired());
            }
        }

        [Fact]
        public void Context_follows_the_default_setting()
        {
            using (var dir = new TempDir())
            {
                InstallerInfo info = InstallerInspector.Inspect(Fixtures.WriteFakeExe(dir, "x.exe", null));
                Assert.Equal(InstallContext.User, CommandGenerator.Generate(info, InstallContext.User).InstallContext);
            }
        }

        [Fact]
        public void Placeholder_detection_is_specific()
        {
            Assert.True(CommandGenerator.ContainsPlaceholder(CommandGenerator.UninstallPlaceholder));
            Assert.True(CommandGenerator.ContainsPlaceholder(""));
            Assert.True(CommandGenerator.ContainsPlaceholder("   "));
            Assert.False(CommandGenerator.ContainsPlaceholder("\"C:\\a\\unins000.exe\" /VERYSILENT"));
            Assert.False(CommandGenerator.ContainsPlaceholder("cmd /c echo <x> > out.txt"));
        }

        [Fact]
        public void Copy_all_follows_the_intune_wizard_and_uses_edited_values()
        {
            var rule = DetectionRule.Unknown();
            rule.Type = DetectionRuleType.File;
            rule.FilePath = @"%ProgramFiles%\App";
            rule.FileOrFolder = "app.exe";
            rule.FileMethod = FileDetectionMethod.Version;
            rule.FileOperator = ComparisonOperator.GreaterThanOrEqualTo;
            rule.FileValue = "1.0";
            var c = new PackageCommands
            {
                AppName = new CommandField("App", Confidence.Detected),
                Description = new CommandField("Does things", Confidence.Edited),
                Publisher = new CommandField("Contoso", Confidence.Detected),
                AppVersion = new CommandField("1.0", Confidence.Detected),
                Install = new CommandField("\"setup.exe\" /S /D=C:\\App", Confidence.Edited),
                Uninstall = new CommandField("\"C:\\App\\uninst.exe\" /S", Confidence.Detected),
                InstallContext = InstallContext.System,
                Restart = RestartBehavior.Suppress,
                Architecture = OsArchitecture.X64 | OsArchitecture.Arm64,
                MinimumOs = "Windows 10 1809",
                Detection = rule,
            };
            string text = Summary.Format("App 1.0", "setup.intunewin", c);
            Assert.Equal(
                "Application:                  App 1.0\r\n" +
                "Package:                      setup.intunewin\r\n" +
                "\r\n[App information]\r\n" +
                "Name:                         App\r\n" +
                "Description:                  Does things\r\n" +
                "Publisher:                    Contoso\r\n" +
                "App version:                  1.0\r\n" +
                "\r\n[Program]\r\n" +
                "Install command:              \"setup.exe\" /S /D=C:\\App\r\n" +
                "Uninstall command:            \"C:\\App\\uninst.exe\" /S\r\n" +
                "Installation time required (mins): 60\r\n" +
                "Allow available uninstall:    Yes\r\n" +
                "Install behavior:             System\r\n" +
                "Device restart behavior:      No specific action\r\n" +
                "Return codes:                 0 Success, 1707 Success, 3010 Soft reboot, 1641 Hard reboot, 1618 Retry (Intune defaults)\r\n" +
                "\r\n[Requirements]\r\n" +
                "Operating system architecture: x64, ARM64\r\n" +
                "Minimum operating system:     Windows 10 1809\r\n" +
                "\r\n[Detection rules]\r\n" +
                "Rules format:                 Manually configure detection rules\r\n" +
                "Rule type:                    File\r\n" +
                "Path:                         %ProgramFiles%\\App\r\n" +
                "File or folder:               app.exe\r\n" +
                "Detection method:             String (version)\r\n" +
                "Operator:                     Greater than or equal to\r\n" +
                "Value:                        1.0\r\n" +
                "Associated with a 32-bit app on 64-bit clients: No", text);
        }

        [Fact]
        public void Detection_rule_shows_only_fields_intune_shows()
        {
            var rule = DetectionRule.Unknown();
            rule.Type = DetectionRuleType.File;
            rule.FilePath = @"%ProgramFiles%\App";
            rule.FileOrFolder = "app.exe";
            rule.FileMethod = FileDetectionMethod.Exists;
            Assert.DoesNotContain(rule.Fields(), f => f.Key == "Operator" || f.Key == "Value");
            Assert.True(rule.IsComplete);

            rule.FileMethod = FileDetectionMethod.SizeInMB;
            rule.FileValue = "";
            Assert.Equal(new[] { "Value" }, rule.MissingFields());

            rule.Type = DetectionRuleType.Registry;
            rule.RegistryKeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Contoso";
            rule.RegistryMethod = RegistryDetectionMethod.KeyExists;
            Assert.DoesNotContain(rule.Fields(), f => f.Key == "Value name" || f.Key == "Operator");
            Assert.True(rule.IsComplete);

            rule.Type = DetectionRuleType.Msi;
            rule.MsiProductCode = "{12345678-ABCD-4EF0-9876-0123456789AB}";
            rule.MsiVersionCheck = false;
            Assert.Equal(3, rule.Fields().Count);
        }

        [Theory]
        [InlineData("x64;1033", OsArchitecture.X64)]
        [InlineData("Intel;1033", OsArchitecture.X86 | OsArchitecture.X64)]
        [InlineData(";1033", OsArchitecture.X86 | OsArchitecture.X64)]
        [InlineData("Arm64;0", OsArchitecture.Arm64)]
        [InlineData("Intel64;1033", OsArchitecture.None)]
        public void Msi_template_maps_to_architecture(string template, OsArchitecture expected)
        {
            Assert.Equal(expected, Portal.ArchitectureFromMsiTemplate(template));
        }

        [Fact]
        public void Paths_use_intune_environment_variables()
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            Assert.Equal(@"%ProgramFiles%\App", Portal.ToIntunePath(pf + @"\App\"));
            Assert.Equal(@"%ProgramFiles(x86)%\App", Portal.ToIntunePath(pf86 + @"\App"));
            Assert.Equal(@"D:\Apps\X", Portal.ToIntunePath(@"D:\Apps\X"));

            string folder, file;
            Assert.True(Portal.TryParseIconPath("\"" + pf + "\\App\\app.exe\",0", out folder, out file));
            Assert.Equal(pf + @"\App", folder);
            Assert.Equal("app.exe", file);
            Assert.False(Portal.TryParseIconPath(@"C:\App\app.ico", out folder, out file));
        }

        [Fact]
        public void Export_file_name_is_sanitised()
        {
            Assert.Equal("My_App 1.0-commands.txt", Summary.ExportFileName("My/App 1.0"));
            Assert.Equal("installer-commands.txt", Summary.ExportFileName("  "));
        }
    }
}
