using IntuneWrapper.Core;
using Xunit;

namespace IntuneWrapper.Tests
{
    /// <summary>The checks the Intune admin center applies to File detection rules.</summary>
    public class FileRuleValidatorTests
    {
        [Theory]
        [InlineData(@"C:\Program Files\REW")]
        [InlineData(@"%ProgramFiles%\REW")]
        [InlineData(@"%ProgramFiles(x86)%\Vendor\App")]
        [InlineData(@"%LocalAppData%\Programs\REW")]
        [InlineData(@"%ProgramData%")]
        [InlineData(@"D:\Apps\My App (x64)\")]
        public void Valid_paths(string path)
        {
            Assert.Null(FileRuleValidator.ValidatePath(path));
        }

        [Theory]
        [InlineData("<PATH: use Capture mode>", "required")]           // the placeholder from the reported portal error
        [InlineData("", "required")]
        [InlineData(@"C:\App\<x>", "cannot contain")]
        [InlineData(@"C:/Program Files/App", "cannot contain")]
        [InlineData("\"C:\\Program Files\\App\"", "cannot contain")]
        [InlineData("\u201CC:\\App\u201D", "cannot contain")]
        [InlineData(@"C:\App|x", "cannot contain")]
        [InlineData(@"C:\App\*", "cannot contain")]
        [InlineData(@"\\server\share\App", "local path")]
        [InlineData(@"Program Files\App", "must start with")]
        [InlineData(@"C:\App\x:y", "':'")]
        [InlineData(@"C:\App\\Sub", "empty folder")]
        public void Invalid_paths(string path, string expected)
        {
            string error = FileRuleValidator.ValidatePath(path);
            Assert.NotNull(error);
            Assert.Contains(expected, error);
        }

        [Theory]
        [InlineData("roomeqwizard.exe")]
        [InlineData("My App")]
        [InlineData("app (x64).exe")]
        public void Valid_names(string name)
        {
            Assert.Null(FileRuleValidator.ValidateFileOrFolder(name));
        }

        [Theory]
        [InlineData("<FILE OR FOLDER: use Capture mode>", "required")] // the placeholder from the reported portal error
        [InlineData(" ", "required")]
        [InlineData(@"bin\app.exe", "name only")]
        [InlineData("C:app.exe", "name only")]
        [InlineData("app?.exe", "cannot contain")]
        [InlineData("app.", "period")]
        public void Invalid_names(string name, string expected)
        {
            string error = FileRuleValidator.ValidateFileOrFolder(name);
            Assert.NotNull(error);
            Assert.Contains(expected, error);
        }

        [Fact]
        public void Names_must_be_shorter_than_255()
        {
            Assert.Null(FileRuleValidator.ValidateFileOrFolder(new string('a', 254)));
            Assert.Contains("255", FileRuleValidator.ValidateFileOrFolder(new string('a', 255)));
        }

        [Theory]
        [InlineData(FileDetectionMethod.Version, "5.31.3.0", true)]
        [InlineData(FileDetectionMethod.Version, "5", true)]
        [InlineData(FileDetectionMethod.Version, "v5.31", false)]
        [InlineData(FileDetectionMethod.Version, "5.31.3.0.1", false)]
        [InlineData(FileDetectionMethod.SizeInMB, "12", true)]
        [InlineData(FileDetectionMethod.SizeInMB, "1.5", false)]
        [InlineData(FileDetectionMethod.DateModified, "2026-11-19", true)]
        [InlineData(FileDetectionMethod.DateModified, "yesterday", false)]
        [InlineData(FileDetectionMethod.Version, "", false)]
        public void Values_match_the_detection_method(FileDetectionMethod method, string value, bool valid)
        {
            Assert.Equal(valid, FileRuleValidator.ValidateValue(method, value) == null);
        }

        [Fact]
        public void Placeholder_rule_is_never_complete_and_reports_both_fields()
        {
            DetectionRule rule = DetectionRule.Unknown();
            var problems = rule.Problems();
            Assert.False(rule.IsComplete);
            Assert.Contains("Path", problems.Keys);
            Assert.Contains("File or folder", problems.Keys);
        }

        [Fact]
        public void Invalid_value_makes_rule_incomplete()
        {
            DetectionRule rule = DetectionRule.Unknown();
            rule.FilePath = @"%ProgramFiles%\REW";
            rule.FileOrFolder = "roomeqwizard.exe";
            Assert.True(rule.IsComplete);
            rule.FileMethod = FileDetectionMethod.Version;
            rule.FileValue = "latest";
            Assert.Equal(new[] { "Value" }, rule.MissingFields());
            rule.FileOrFolder = @"C:\Program Files\REW\roomeqwizard.exe";
            Assert.Contains("File or folder", rule.MissingFields());
        }

        [Theory]
        [InlineData(@"C:\Program Files\REW\roomeqwizard.exe", @"C:\Program Files\REW", "roomeqwizard.exe")]
        [InlineData("\"C:\\Program Files\\REW\\roomeqwizard.exe\"", @"C:\Program Files\REW", "roomeqwizard.exe")]
        [InlineData(@"%ProgramFiles%\REW\roomeqwizard.exe", @"%ProgramFiles%\REW", "roomeqwizard.exe")]
        public void Full_file_paths_are_split(string value, string folder, string name)
        {
            string f, n;
            Assert.True(FileRuleValidator.TrySplitFullPath(value, out f, out n));
            Assert.Equal(folder, f);
            Assert.Equal(name, n);
        }

        [Theory]
        [InlineData(@"C:\Program Files\REW")]
        [InlineData("roomeqwizard.exe")]
        [InlineData("<PATH: use Capture mode>")]
        [InlineData(@"C:\app.exe")]
        public void Folders_names_and_placeholders_are_not_split(string value)
        {
            string f, n;
            Assert.False(FileRuleValidator.TrySplitFullPath(value, out f, out n));
        }
    }
}
