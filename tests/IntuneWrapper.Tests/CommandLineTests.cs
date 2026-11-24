using System.Collections.Generic;
using IntuneWrapper.Core;
using Xunit;

namespace IntuneWrapper.Tests
{
    public class CommandLineTests
    {
        [Theory]
        [InlineData(@"C:\Installers\My App")]
        [InlineData(@"C:\Users\o'brien\Downloads")]
        [InlineData(@"C:\Program Files (x86)\Vendor (beta)")]
        [InlineData(@"C:\Output\")]
        [InlineData(@"\\server\share\with space\")]
        [InlineData(@"weird ""quoted"" name")]
        [InlineData(@"a\\""b")]
        [InlineData("")]
        [InlineData(@"C:\a & b ^ c % d")]
        public void Quote_round_trips_through_windows_parser(string value)
        {
            string[] parsed = Fixtures.ParseCommandLine(CommandLine.Quote(value));
            Assert.Single(parsed);
            Assert.Equal(value, parsed[0]);
        }

        [Fact]
        public void Paths_are_always_quoted()
        {
            Assert.Equal("\"C:\\simple\"", CommandLine.Quote(@"C:\simple"));
        }

        [Fact]
        public void Packager_arguments_quote_every_path()
        {
            var request = new PackagingRequest
            {
                ToolPath = @"C:\tools\IntuneWinAppUtil.exe",
                SourceFolder = @"C:\Source (x64)\o'neil app\",
                SetupFile = "My Setup (1.0).exe",
                OutputFolder = @"D:\Intune Output",
            };
            string arguments = Packager.BuildArguments(request);
            Assert.Equal("-c \"C:\\Source (x64)\\o'neil app\\\\\" -s \"My Setup (1.0).exe\" -o \"D:\\Intune Output\" -q", arguments);

            string[] parsed = Fixtures.ParseCommandLine(arguments);
            Assert.Equal(new[] { "-c", @"C:\Source (x64)\o'neil app\", "-s", "My Setup (1.0).exe", "-o", @"D:\Intune Output", "-q" }, parsed);
            Assert.Equal(@"D:\Intune Output\My Setup (1.0).intunewin", request.ExpectedOutputPath);
        }

        [Fact]
        public void Join_leaves_switches_bare()
        {
            Assert.Equal("-a \"x y\" -q", CommandLine.Join(new[]
            {
                new KeyValuePair<string, string>("-a", "x y"),
                new KeyValuePair<string, string>("-q", null),
            }));
        }
    }
}
