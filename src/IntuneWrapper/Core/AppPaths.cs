using System;
using System.IO;

namespace IntuneWrapper.Core
{
    /// <summary>
    /// All per-user storage locations. Everything lives under %APPDATA%\IntuneWrapper so the
    /// exe can run from a read-only share (SPEC 3). Tests pass their own root.
    /// </summary>
    public sealed class AppPaths
    {
        public AppPaths() : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IntuneWrapper"))
        {
        }

        public AppPaths(string root)
        {
            Root = root;
        }

        public string Root { get; }
        public string SettingsFile => Path.Combine(Root, "settings.json");
        public string HistoryFile => Path.Combine(Root, "history.json");
        public string LogsDir => Path.Combine(Root, "logs");
        /// <summary>Where 1.0/1.1 downloaded the tool. Now only a source to copy it into the packaging folder.</summary>
        public string LegacyToolPath => Path.Combine(Root, "tools", ToolManager.ToolFileName);

        /// <summary>
        /// D:\Packaging when a local D: drive exists, otherwise Packaging in the user profile.
        /// Never the Desktop: it is often redirected to a file server.
        /// </summary>
        public static string DefaultPackagingFolder
        {
            get
            {
                try
                {
                    var d = new DriveInfo("D");
                    if (d.IsReady && d.DriveType == DriveType.Fixed)
                    {
                        return @"D:\Packaging";
                    }
                }
                catch
                {
                    // No D: drive.
                }
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Packaging");
            }
        }

        public void EnsureRoot()
        {
            Directory.CreateDirectory(Root);
        }
    }
}
