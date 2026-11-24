using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace IntuneWrapper.Core
{
    public enum InstallerFramework
    {
        None,
        InnoSetup,
        Nsis,
        InstallShield,
        WixBurn,
        Squirrel,
        Install4j,
        Unknown
    }

    /// <summary>
    /// Detects the EXE installer framework from raw bytes (SPEC 5.2). Only specific marker strings
    /// are used: a plain "WiX|Burn" match flags far too many files (prototype issue). Each marker is
    /// searched as ASCII and UTF-16LE, because version resources store strings as UTF-16.
    /// </summary>
    public static class SignatureScanner
    {
        /// <summary>Markers live in the setup stub, so the scan stops after this many bytes.</summary>
        public const long MaxScanBytes = 48L * 1024 * 1024;

        private const int ChunkSize = 1024 * 1024;

        internal sealed class Marker
        {
            public Marker(string key, string text)
            {
                Key = key;
                Patterns = new[] { Encoding.ASCII.GetBytes(text), Encoding.Unicode.GetBytes(text) };
            }

            public string Key { get; }
            public byte[][] Patterns { get; }
        }

        // Keys combine into frameworks below. ".wixburn" is the PE section name every Burn bundle has.
        internal static readonly Marker[] Markers =
        {
            new Marker("inno", "Inno Setup"),
            new Marker("nsis", "Nullsoft Install System"),
            new Marker("nsis", "NullsoftInst"),
            new Marker("installshield", "InstallShield"),
            new Marker("burn", ".wixburn"),
            new Marker("squirrel", "Squirrel"),
            new Marker("update.exe", "Update.exe"),
            new Marker("install4j", "install4j"),
        };

        public static InstallerFramework Detect(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize))
            {
                return Classify(FindMarkers(fs, MaxScanBytes));
            }
        }

        public static InstallerFramework Detect(byte[] data)
        {
            using (var ms = new MemoryStream(data, false))
            {
                return Classify(FindMarkers(ms, MaxScanBytes));
            }
        }

        /// <summary>Priority order from SPEC 5.2.</summary>
        internal static InstallerFramework Classify(ISet<string> found)
        {
            if (found.Contains("inno")) return InstallerFramework.InnoSetup;
            if (found.Contains("nsis")) return InstallerFramework.Nsis;
            if (found.Contains("installshield")) return InstallerFramework.InstallShield;
            if (found.Contains("burn")) return InstallerFramework.WixBurn;
            if (found.Contains("squirrel") && found.Contains("update.exe")) return InstallerFramework.Squirrel;
            // Extension beyond SPEC 5.2: common for Java desktop apps.
            if (found.Contains("install4j")) return InstallerFramework.Install4j;
            return InstallerFramework.Unknown;
        }

        internal static HashSet<string> FindMarkers(Stream stream, long maxBytes)
        {
            var found = new HashSet<string>();
            int overlap = Markers.SelectMany(m => m.Patterns).Max(p => p.Length) - 1;
            var buffer = new byte[ChunkSize + overlap];
            int carried = 0;
            long total = 0;

            while (total < maxBytes)
            {
                int toRead = (int)Math.Min(ChunkSize, maxBytes - total);
                int read = ReadFully(stream, buffer, carried, toRead);
                if (read == 0)
                {
                    break;
                }
                total += read;
                int length = carried + read;

                foreach (Marker marker in Markers)
                {
                    if (found.Contains(marker.Key))
                    {
                        continue;
                    }
                    foreach (byte[] pattern in marker.Patterns)
                    {
                        if (IndexOf(buffer, length, pattern) >= 0)
                        {
                            found.Add(marker.Key);
                            break;
                        }
                    }
                }

                // Keep the tail so markers split across chunk boundaries are still found.
                carried = Math.Min(overlap, length);
                Buffer.BlockCopy(buffer, length - carried, buffer, 0, carried);
            }
            return found;
        }

        private static int ReadFully(Stream stream, byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = stream.Read(buffer, offset + total, count - total);
                if (n == 0)
                {
                    break;
                }
                total += n;
            }
            return total;
        }

        internal static int IndexOf(byte[] haystack, int length, byte[] needle)
        {
            int last = length - needle.Length;
            byte first = needle[0];
            int i = 0;
            while (i <= last)
            {
                // Array.IndexOf<byte> uses an optimised native scan for the first byte.
                i = Array.IndexOf(haystack, first, i, last - i + 1);
                if (i < 0)
                {
                    return -1;
                }
                int j = 1;
                while (j < needle.Length && haystack[i + j] == needle[j])
                {
                    j++;
                }
                if (j == needle.Length)
                {
                    return i;
                }
                i++;
            }
            return -1;
        }

        public static string DisplayName(InstallerFramework framework)
        {
            switch (framework)
            {
                case InstallerFramework.InnoSetup: return "Inno Setup";
                case InstallerFramework.Nsis: return "NSIS";
                case InstallerFramework.InstallShield: return "InstallShield";
                case InstallerFramework.WixBurn: return "WiX Burn";
                case InstallerFramework.Squirrel: return "Squirrel";
                case InstallerFramework.Install4j: return "install4j";
                case InstallerFramework.None: return "";
                default: return "Unknown";
            }
        }
    }
}
