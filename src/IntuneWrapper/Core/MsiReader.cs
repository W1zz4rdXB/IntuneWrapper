using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace IntuneWrapper.Core
{
    /// <summary>
    /// Reads the MSI Property table through msi.dll, the same engine the WindowsInstaller.Installer
    /// COM object wraps. The database is opened read-only and every handle is closed in a finally
    /// block (SPEC 5.1 / prototype issue: release objects after reading).
    /// </summary>
    public static class MsiReader
    {
        private const uint ErrorSuccess = 0;
        private const uint ErrorNoMoreItems = 259;
        private const uint ErrorMoreData = 234;
        private static readonly IntPtr MsiDbOpenReadOnly = IntPtr.Zero;

        public static Dictionary<string, string> ReadProperties(string msiPath)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string[] row in ReadRows(msiPath, "SELECT `Property`, `Value` FROM `Property`", 2))
            {
                result[row[0]] = row[1];
            }
            return result;
        }

        /// <summary>Runs a read-only query and returns each row's first <paramref name="columns"/> fields as strings.</summary>
        public static List<string[]> ReadRows(string msiPath, string query, int columns)
        {
            var result = new List<string[]>();
            IntPtr db = IntPtr.Zero, view = IntPtr.Zero;
            try
            {
                Check(NativeMethods.MsiOpenDatabase(msiPath, MsiDbOpenReadOnly, out db), "MsiOpenDatabase");
                Check(NativeMethods.MsiDatabaseOpenView(db, query, out view), "MsiDatabaseOpenView");
                Check(NativeMethods.MsiViewExecute(view, IntPtr.Zero), "MsiViewExecute");
                while (true)
                {
                    IntPtr record;
                    uint rc = NativeMethods.MsiViewFetch(view, out record);
                    if (rc == ErrorNoMoreItems)
                    {
                        break;
                    }
                    Check(rc, "MsiViewFetch");
                    try
                    {
                        var row = new string[columns];
                        for (int i = 0; i < columns; i++) row[i] = GetString(record, (uint)(i + 1));
                        result.Add(row);
                    }
                    finally
                    {
                        NativeMethods.MsiCloseHandle(record);
                    }
                }
            }
            finally
            {
                if (view != IntPtr.Zero)
                {
                    NativeMethods.MsiViewClose(view);
                    NativeMethods.MsiCloseHandle(view);
                }
                if (db != IntPtr.Zero)
                {
                    NativeMethods.MsiCloseHandle(db);
                }
            }
            return result;
        }

        /// <summary>
        /// The Template summary property (platform;languages, e.g. "x64;1033"), or null when it cannot
        /// be read. Used for the Intune architecture requirement.
        /// </summary>
        public static string ReadTemplate(string msiPath)
        {
            IntPtr summary = IntPtr.Zero;
            try
            {
                if (NativeMethods.MsiGetSummaryInformation(IntPtr.Zero, msiPath, 0, out summary) != ErrorSuccess)
                {
                    return null;
                }
                uint dataType;
                int intValue;
                long fileTime;
                uint size = 256;
                var sb = new StringBuilder((int)size);
                uint rc = NativeMethods.MsiSummaryInfoGetProperty(summary, 7 /* PID_TEMPLATE */, out dataType, out intValue,
                    out fileTime, sb, ref size);
                if (rc == ErrorMoreData)
                {
                    size++;
                    sb = new StringBuilder((int)size);
                    rc = NativeMethods.MsiSummaryInfoGetProperty(summary, 7, out dataType, out intValue, out fileTime, sb, ref size);
                }
                return rc == ErrorSuccess ? sb.ToString() : null;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (summary != IntPtr.Zero) NativeMethods.MsiCloseHandle(summary);
            }
        }

        private static string GetString(IntPtr record, uint field)
        {
            uint size = 256;
            var sb = new StringBuilder((int)size);
            uint rc = NativeMethods.MsiRecordGetString(record, field, sb, ref size);
            if (rc == ErrorMoreData)
            {
                size++;
                sb = new StringBuilder((int)size);
                rc = NativeMethods.MsiRecordGetString(record, field, sb, ref size);
            }
            Check(rc, "MsiRecordGetString");
            return sb.ToString();
        }

        private static void Check(uint rc, string call)
        {
            if (rc != ErrorSuccess)
            {
                throw new Win32Exception((int)rc, call + " failed: " + new Win32Exception((int)rc).Message);
            }
        }

        internal static class NativeMethods
        {
            [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "MsiOpenDatabaseW")]
            internal static extern uint MsiOpenDatabase(string path, IntPtr persist, out IntPtr handle);

            [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "MsiDatabaseOpenViewW")]
            internal static extern uint MsiDatabaseOpenView(IntPtr database, string query, out IntPtr view);

            [DllImport("msi.dll", ExactSpelling = true)]
            internal static extern uint MsiViewExecute(IntPtr view, IntPtr record);

            [DllImport("msi.dll", ExactSpelling = true)]
            internal static extern uint MsiViewFetch(IntPtr view, out IntPtr record);

            [DllImport("msi.dll", ExactSpelling = true)]
            internal static extern uint MsiViewClose(IntPtr view);

            [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "MsiRecordGetStringW")]
            internal static extern uint MsiRecordGetString(IntPtr record, uint field, StringBuilder value, ref uint size);

            [DllImport("msi.dll", ExactSpelling = true)]
            internal static extern uint MsiDatabaseCommit(IntPtr database);

            [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "MsiGetSummaryInformationW")]
            internal static extern uint MsiGetSummaryInformation(IntPtr database, string path, uint updateCount, out IntPtr summary);

            [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "MsiSummaryInfoGetPropertyW")]
            internal static extern uint MsiSummaryInfoGetProperty(IntPtr summary, uint property, out uint dataType,
                out int intValue, out long fileTime, StringBuilder value, ref uint size);

            [DllImport("msi.dll", ExactSpelling = true)]
            internal static extern uint MsiCloseHandle(IntPtr handle);
        }
    }
}
