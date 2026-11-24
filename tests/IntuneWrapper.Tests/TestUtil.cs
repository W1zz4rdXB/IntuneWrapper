using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using IntuneWrapper.Core;

namespace IntuneWrapper.Tests
{
    /// <summary>A unique temp folder per test, deleted on dispose.</summary>
    public sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "IntuneWrapperTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch
            {
                // Ignore.
            }
        }
    }

    public static class Fixtures
    {
        /// <summary>A fake PE-ish file: "MZ" header, filler, the marker text, more filler.</summary>
        public static byte[] FakeExe(string marker, bool utf16 = false, int prefix = 4096)
        {
            var bytes = new List<byte> { (byte)'M', (byte)'Z' };
            bytes.AddRange(new byte[prefix]);
            if (marker != null)
            {
                bytes.AddRange(utf16 ? Encoding.Unicode.GetBytes(marker) : Encoding.ASCII.GetBytes(marker));
            }
            bytes.AddRange(new byte[1024]);
            return bytes.ToArray();
        }

        public static string WriteFakeExe(TempDir dir, string fileName, string marker, bool utf16 = false)
        {
            string path = dir.File(fileName);
            System.IO.File.WriteAllBytes(path, FakeExe(marker, utf16));
            return path;
        }

        /// <summary>
        /// Creates a minimal MSI database with a Property table and, optionally, Directory/Component/File
        /// tables. <paramref name="directories"/>: {Directory, Parent, DefaultDir}.
        /// <paramref name="files"/>: {FileName, Directory, Version, Size}.
        /// </summary>
        public static string CreateMsi(string path, IDictionary<string, string> properties,
            IList<string[]> directories = null, IList<string[]> files = null)
        {
            IntPtr db;
            Check(MsiReader.NativeMethods.MsiOpenDatabase(path, (IntPtr)3 /* MSIDBOPEN_CREATE */, out db));
            try
            {
                Execute(db, "CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` LONGCHAR NOT NULL " +
                            "LOCALIZABLE PRIMARY KEY `Property`)");
                foreach (KeyValuePair<string, string> kv in properties)
                {
                    Execute(db, "INSERT INTO `Property` (`Property`, `Value`) VALUES ('" + kv.Key + "', '" + kv.Value + "')");
                }
                if (directories != null)
                {
                    Execute(db, "CREATE TABLE `Directory` (`Directory` CHAR(72) NOT NULL, `Directory_Parent` CHAR(72), " +
                                "`DefaultDir` CHAR(255) NOT NULL LOCALIZABLE PRIMARY KEY `Directory`)");
                    Execute(db, "CREATE TABLE `Component` (`Component` CHAR(72) NOT NULL, `Directory_` CHAR(72) NOT NULL " +
                                "PRIMARY KEY `Component`)");
                    Execute(db, "CREATE TABLE `File` (`File` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, " +
                                "`FileName` CHAR(255) NOT NULL LOCALIZABLE, `FileSize` LONG NOT NULL, `Version` CHAR(72) " +
                                "PRIMARY KEY `File`)");
                    foreach (string[] d in directories)
                    {
                        Execute(db, "INSERT INTO `Directory` (`Directory`, `Directory_Parent`, `DefaultDir`) VALUES ('" +
                                    d[0] + "', '" + d[1] + "', '" + d[2] + "')");
                    }
                    int n = 0;
                    foreach (string[] f in files ?? new List<string[]>())
                    {
                        n++;
                        Execute(db, "INSERT INTO `Component` (`Component`, `Directory_`) VALUES ('C" + n + "', '" + f[1] + "')");
                        Execute(db, "INSERT INTO `File` (`File`, `Component_`, `FileName`, `FileSize`, `Version`) VALUES ('F" + n +
                                    "', 'C" + n + "', '" + f[0] + "', " + f[3] + ", '" + f[2] + "')");
                    }
                }
                Check(MsiReader.NativeMethods.MsiDatabaseCommit(db));
            }
            finally
            {
                MsiReader.NativeMethods.MsiCloseHandle(db);
            }
            return path;
        }

        private static void Execute(IntPtr db, string sql)
        {
            IntPtr view;
            Check(MsiReader.NativeMethods.MsiDatabaseOpenView(db, sql, out view));
            try
            {
                Check(MsiReader.NativeMethods.MsiViewExecute(view, IntPtr.Zero));
            }
            finally
            {
                MsiReader.NativeMethods.MsiViewClose(view);
                MsiReader.NativeMethods.MsiCloseHandle(view);
            }
        }

        private static void Check(uint rc)
        {
            if (rc != 0) throw new InvalidOperationException("MSI API error " + rc);
        }

        [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CommandLineToArgvW(string cmdLine, out int numArgs);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr mem);

        /// <summary>Parses a command line the way Windows programs do.</summary>
        public static string[] ParseCommandLine(string commandLine)
        {
            int count;
            IntPtr argv = CommandLineToArgvW("prog.exe " + commandLine, out count);
            try
            {
                var result = new string[count - 1];
                for (int i = 1; i < count; i++)
                {
                    result[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size));
                }
                return result;
            }
            finally
            {
                LocalFree(argv);
            }
        }
    }
}
