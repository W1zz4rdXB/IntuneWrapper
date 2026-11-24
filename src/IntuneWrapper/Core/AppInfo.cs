using System.Reflection;

namespace IntuneWrapper.Core
{
    /// <summary>Version comes from Directory.Build.props via the assembly attributes.</summary>
    public static class AppInfo
    {
        public const string Name = "Intune Wrapper";

        public static string Version
        {
            get
            {
                var attr = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                return attr?.InformationalVersion ?? typeof(AppInfo).Assembly.GetName().Version.ToString(3);
            }
        }
    }
}
