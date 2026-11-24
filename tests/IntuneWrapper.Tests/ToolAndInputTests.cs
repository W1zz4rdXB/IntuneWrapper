using System;
using System.IO;
using IntuneWrapper.Core;
using Xunit;

namespace IntuneWrapper.Tests
{
    public class ToolAndInputTests
    {
        [Fact]
        public void Release_without_assets_falls_back_to_raw_file_at_tag()
        {
            ToolRelease r = ToolManager.ParseRelease(
                "{\"tag_name\":\"v1.8.7\",\"html_url\":\"https://github.com/x/releases/tag/v1.8.7\",\"assets\":[],\"body\":\"x\"}");
            Assert.Equal("v1.8.7", r.Tag);
            Assert.Equal(new Version(1, 8, 7), r.Version);
            Assert.Equal("https://raw.githubusercontent.com/microsoft/Microsoft-Win32-Content-Prep-Tool/v1.8.7/IntuneWinAppUtil.exe",
                r.DownloadUrl);
        }

        [Fact]
        public void Release_asset_is_preferred()
        {
            ToolRelease r = ToolManager.ParseRelease(
                "{\"tag_name\":\"v2.0.0\",\"assets\":[{\"name\":\"intunewinapputil.exe\",\"browser_download_url\":\"https://dl/x.exe\"}]}");
            Assert.Equal("https://dl/x.exe", r.DownloadUrl);
        }

        [Fact]
        public void Bad_release_json_is_a_download_error()
        {
            Assert.Throws<ToolDownloadException>(() => ToolManager.ParseRelease("{\"message\":\"Not Found\"}"));
            Assert.Throws<ToolDownloadException>(() => ToolManager.ParseRelease("<html>"));
        }

        [Fact]
        public void Version_comparison()
        {
            Assert.Equal(new Version(1, 8, 6), ToolManager.ParseVersion("1.8.6.0"));
            Assert.Equal(new Version(1, 8, 7), ToolManager.ParseVersion("v1.8.7"));
            Assert.Null(ToolManager.ParseVersion("latest"));
        }

        [Fact]
        public void Downloaded_file_must_be_an_executable()
        {
            using (var dir = new TempDir())
            {
                string html = dir.File("x.exe");
                File.WriteAllText(html, "<html>proxy login</html>" + new string(' ', 20000));
                Assert.Throws<ToolDownloadException>(() => ToolManager.ValidateExecutable(html));
            }
        }

        [Fact]
        public void Input_validation_messages()
        {
            using (var dir = new TempDir())
            {
                Assert.Contains("Folders are not supported", InstallerInspector.Validate(dir.Path));
                string txt = dir.File("readme.txt");
                File.WriteAllText(txt, "x");
                Assert.Contains("Unsupported file type", InstallerInspector.Validate(txt));
                Assert.Contains("does not exist", InstallerInspector.Validate(dir.File("missing.exe")));
                Assert.Null(InstallerInspector.Validate(Fixtures.WriteFakeExe(dir, "SETUP.EXE", null)));
                Assert.Throws<InstallerInputException>(() => InstallerInspector.Inspect(txt));
            }
        }

        [Fact]
        public void Locked_file_gives_friendly_error()
        {
            using (var dir = new TempDir())
            {
                string exe = Fixtures.WriteFakeExe(dir, "locked.exe", null);
                using (new FileStream(exe, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var ex = Assert.Throws<InstallerInputException>(() => InstallerInspector.Inspect(exe));
                    Assert.Contains("could not be read", ex.Message);
                }
            }
        }

        [Fact]
        public void Other_files_in_source_folder_are_listed()
        {
            using (var dir = new TempDir())
            {
                string exe = Fixtures.WriteFakeExe(dir, "setup.exe", null);
                Assert.Empty(InstallerInspector.OtherEntriesInFolder(exe));
                File.WriteAllText(dir.File("other.txt"), "");
                Directory.CreateDirectory(dir.File("sub"));
                Assert.Equal(2, InstallerInspector.OtherEntriesInFolder(exe).Count);
            }
        }

        [Fact]
        public void Packaging_validation_rejects_bad_requests()
        {
            using (var dir = new TempDir())
            {
                string exe = Fixtures.WriteFakeExe(dir, "setup.exe", null);
                string tool = Fixtures.WriteFakeExe(dir, "IntuneWinAppUtil.exe", null);
                var request = new PackagingRequest
                {
                    ToolPath = tool, SourceFolder = dir.Path, SetupFile = "setup.exe", OutputFolder = dir.Path + "\\"
                };
                Assert.Contains("same as the installer's folder",
                    Assert.Throws<PackagingException>(() => Packager.Validate(request)).Message);

                request.OutputFolder = Path.Combine(dir.Path, "out");
                Packager.Validate(request);
                Assert.True(Directory.Exists(request.OutputFolder));

                File.WriteAllText(request.ExpectedOutputPath, "old");
                Assert.Throws<PackagingException>(() => Packager.Validate(request));
                request.Overwrite = true;
                Packager.Validate(request);

                request.ToolPath = dir.File("nope.exe");
                Assert.Throws<PackagingException>(() => Packager.Validate(request));
            }
        }

        [Fact]
        public void Exit_code_zero_without_file_is_explained_as_failure()
        {
            string message = Packager.ExplainFailure(0, false, new string[0]);
            Assert.Contains("no .intunewin file was created", message);
            Assert.Contains("exit code 5", Packager.ExplainFailure(5, false, new[] { "Access is denied." }));
        }
    }
}
