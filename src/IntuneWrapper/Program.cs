using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using IntuneWrapper.Core;
using IntuneWrapper.UI;

namespace IntuneWrapper
{
    internal static class Program
    {
        private static int _reporting;

        /// <summary>STA is required for the clipboard and drag-and-drop (prototype issue).</summary>
        [STAThread]
        private static int Main(string[] commandLine)
        {
            // Inside Windows Sandbox: headless install/capture/uninstall run (see SandboxAutomation).
            if (commandLine.Length >= 3 && commandLine[0] == "--sandbox-capture")
            {
                Logger.Initialize(System.IO.Path.Combine(commandLine[1], "logs"));
                return SandboxAgent.RunInSandbox(commandLine[1], commandLine[2]);
            }

            var paths = new AppPaths();
            try
            {
                paths.EnsureRoot();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Intune Wrapper cannot create its data folder:\n" + paths.Root + "\n\n" + ex.Message,
                    AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            Logger.Initialize(paths.LogsDir);
            Logger.Info("Starting " + AppInfo.Name + " " + AppInfo.Version + " on " + Environment.OSVersion +
                        (Environment.Is64BitProcess ? " (64-bit)" : " (32-bit)"));

            // Every unhandled exception is logged and explained; the app never just disappears (SPEC 7).
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Report(e.Exception, false);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Report(e.ExceptionObject as Exception, e.IsTerminating);
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Logger.Error("Unobserved task exception", e.Exception);
                e.SetObserved();
            };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string warning;
            Settings settings = SettingsStore.Load(paths, out warning);
            // Files passed on the command line (or dropped on the exe icon): the first is the installer,
            // the rest are added to the package contents.
            Application.Run(new MainForm(paths, settings, warning, commandLine));
            Logger.Info("Exit");
            return 0;
        }

        private static void Report(Exception ex, bool terminating)
        {
            Logger.Error("Unhandled exception" + (terminating ? " (terminating)" : ""), ex);
            if (Interlocked.Exchange(ref _reporting, 1) == 1) return; // avoid dialog storms
            try
            {
                MessageBox.Show(
                    "Something went wrong" + (terminating ? " and Intune Wrapper has to close" : "") + ".\n\n" +
                    (ex != null ? ex.Message : "Unknown error") + "\n\n" +
                    "The full details were written to the log:\n" + (Logger.CurrentFile ?? "(log unavailable)"),
                    AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch
            {
                // Nothing else we can do.
            }
            finally
            {
                Interlocked.Exchange(ref _reporting, 0);
            }
        }
    }
}
