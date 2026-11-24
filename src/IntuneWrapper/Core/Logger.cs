using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace IntuneWrapper.Core
{
    /// <summary>
    /// Daily log files in %APPDATA%\IntuneWrapper\logs\app-YYYY-MM-DD.log, kept 14 days (SPEC 8).
    /// Never pass file contents or credentials to this class. Logging failures are swallowed:
    /// the log must never be the reason the app crashes.
    /// </summary>
    public static class Logger
    {
        public const int RetentionDays = 14;

        private static readonly object Sync = new object();
        private static string _dir;

        /// <summary>Raised for every line written, so the UI can mirror it in the log pane.</summary>
        public static event Action<string> LineWritten;

        public static string Directory => _dir;

        public static void Initialize(string logsDir)
        {
            lock (Sync)
            {
                _dir = logsDir;
                try
                {
                    System.IO.Directory.CreateDirectory(logsDir);
                    Cleanup(logsDir, DateTime.Now);
                }
                catch
                {
                    // Ignore: logging is best effort.
                }
            }
        }

        public static string CurrentFile =>
            _dir == null ? null : Path.Combine(_dir, "app-" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");

        public static void Info(string message) => Write("INFO ", message);
        public static void Warn(string message) => Write("WARN ", message);

        public static void Error(string message, Exception ex = null)
        {
            Write("ERROR", ex == null ? message : message + Environment.NewLine + ex);
        }

        private static void Write(string level, string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                          + " " + level + " " + message;
            lock (Sync)
            {
                string file = CurrentFile;
                if (file != null)
                {
                    try
                    {
                        File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
                    }
                    catch
                    {
                        // Ignore.
                    }
                }
            }
            try
            {
                LineWritten?.Invoke(line);
            }
            catch
            {
                // A UI subscriber must not break logging.
            }
        }

        /// <summary>Deletes app-*.log files older than the retention window.</summary>
        internal static int Cleanup(string logsDir, DateTime now)
        {
            int deleted = 0;
            foreach (string file in System.IO.Directory.GetFiles(logsDir, "app-*.log"))
            {
                string datePart = Path.GetFileNameWithoutExtension(file).Substring(4);
                DateTime day;
                if (DateTime.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out day) && day < now.Date.AddDays(-(RetentionDays - 1)))
                {
                    try
                    {
                        File.Delete(file);
                        deleted++;
                    }
                    catch
                    {
                        // Ignore locked files.
                    }
                }
            }
            return deleted;
        }
    }
}
