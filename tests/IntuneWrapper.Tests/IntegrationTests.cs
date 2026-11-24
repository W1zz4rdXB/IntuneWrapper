using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IntuneWrapper.Core;
using Xunit;

namespace IntuneWrapper.Tests
{
    /// <summary>
    /// End-to-end with the real tool from GitHub. Opt-in (needs network):
    ///   set INTUNEWRAPPER_INTEGRATION=1 before running dotnet test.
    /// </summary>
    public class IntegrationTests
    {
        private static bool Enabled => Environment.GetEnvironmentVariable("INTUNEWRAPPER_INTEGRATION") == "1";

        [Fact]
        public async Task Download_tool_and_package_file_with_spaces_and_overwrite()
        {
            if (!Enabled) return;

            using (var dir = new TempDir())
            {
                var layout = new PackagingLayout(Path.Combine(dir.Path, "Packaging"));
                ToolRelease release = await ToolManager.GetLatestReleaseAsync(CancellationToken.None);
                await ToolManager.DownloadAsync(release, layout.ToolPath, null, CancellationToken.None);
                Assert.NotNull(ToolManager.GetLocalVersion(layout.ToolPath));

                ToolUpdateCheck same = await ToolManager.CheckForUpdateAsync(layout.ToolPath, dir.Path, CancellationToken.None);
                Assert.False(same.UpdateAvailable);
                ToolUpdateCheck missing = await ToolManager.CheckForUpdateAsync(dir.File("none.exe"), dir.Path, CancellationToken.None);
                Assert.True(missing.UpdateAvailable);

                string source = Path.Combine(dir.Path, "Source (x64)", "o'neil app");
                Directory.CreateDirectory(source);
                File.WriteAllBytes(Path.Combine(source, "My Setup (1.0).exe"), Fixtures.FakeExe("Inno Setup"));

                var request = new PackagingRequest
                {
                    ToolPath = layout.ToolPath,
                    SourceFolder = source + "\\",
                    SetupFile = "My Setup (1.0).exe",
                    OutputFolder = Path.Combine(dir.Path, "Intune Output"),
                };

                PackagingResult first = await Packager.RunAsync(request, null, CancellationToken.None);
                Assert.True(first.Success, first.Message + "\n" + string.Join("\n", first.Log));
                Assert.Equal(request.ExpectedOutputPath, first.OutputPath);
                Assert.True(first.OutputSize > 0);

                // Without confirmation an existing package is never touched.
                await Assert.ThrowsAsync<PackagingException>(() => Packager.RunAsync(request, null, CancellationToken.None));

                request.Overwrite = true;
                PackagingResult second = await Packager.RunAsync(request, null, CancellationToken.None);
                Assert.True(second.Success, second.Message);
                Assert.False(File.Exists(request.ExpectedOutputPath + ".previous"));
            }
        }
    }
}
