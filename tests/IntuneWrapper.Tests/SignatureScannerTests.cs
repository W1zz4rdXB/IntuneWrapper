using System.IO;
using System.Text;
using IntuneWrapper.Core;
using Xunit;

namespace IntuneWrapper.Tests
{
    public class SignatureScannerTests
    {
        [Fact]
        public void Plain_wix_or_burn_text_is_not_a_burn_bundle()
        {
            Assert.Equal(InstallerFramework.Unknown, SignatureScanner.Detect(Fixtures.FakeExe("Built with WiX. Burn after reading.")));
        }

        [Fact]
        public void Utf16_version_resource_marker_is_found()
        {
            Assert.Equal(InstallerFramework.InnoSetup,
                SignatureScanner.Detect(Fixtures.FakeExe("This installation was built with Inno Setup.", utf16: true)));
        }

        [Fact]
        public void Marker_split_across_chunk_boundary_is_found()
        {
            // Place the marker so it straddles the 1 MB chunk boundary.
            byte[] data = Fixtures.FakeExe("Nullsoft Install System", prefix: 1024 * 1024 - 2 - 10);
            Assert.Equal(InstallerFramework.Nsis, SignatureScanner.Detect(data));
        }

        [Fact]
        public void Priority_order_prefers_inno_over_others()
        {
            byte[] data = Fixtures.FakeExe("InstallShield .wixburn Inno Setup");
            Assert.Equal(InstallerFramework.InnoSetup, SignatureScanner.Detect(data));
        }

        [Fact]
        public void Markers_beyond_scan_limit_are_ignored()
        {
            using (var ms = new MemoryStream())
            {
                ms.Write(new byte[100], 0, 100);
                byte[] marker = Encoding.ASCII.GetBytes("Inno Setup");
                ms.Write(marker, 0, marker.Length);
                ms.Position = 0;
                Assert.Empty(SignatureScanner.FindMarkers(ms, 50));
            }
        }

        [Fact]
        public void IndexOf_handles_edges()
        {
            byte[] hay = Encoding.ASCII.GetBytes("abcabd");
            Assert.Equal(3, SignatureScanner.IndexOf(hay, hay.Length, Encoding.ASCII.GetBytes("abd")));
            Assert.Equal(-1, SignatureScanner.IndexOf(hay, 5, Encoding.ASCII.GetBytes("abd")));
            Assert.Equal(0, SignatureScanner.IndexOf(hay, hay.Length, Encoding.ASCII.GetBytes("abcabd")));
        }
    }
}
