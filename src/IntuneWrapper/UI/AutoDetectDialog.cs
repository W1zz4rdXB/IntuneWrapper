using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using IntuneWrapper.Core;

namespace IntuneWrapper.UI
{
    /// <summary>
    /// Runs the install / capture / uninstall check in Windows Sandbox and shows what was verified.
    /// The user's machine is not changed: everything happens in the disposable Sandbox.
    /// </summary>
    internal sealed class AutoDetectDialog : Form
    {
        private readonly InstallerInfo _info;
        private readonly PackageCommands _commands;
        private readonly Label _title, _body;
        private readonly TextBox _log;
        private readonly ProgressBar _progress;
        private readonly Button _btnApply, _btnCancel, _btnEnable, _btnCaptureHere;
        private readonly string _installerInInput;
        private CancellationTokenSource _cts;
        private string _workDir;

        public AutoDetectDialog(InstallerInfo info, PackageCommands commands, string installerInInput)
        {
            _info = info;
            _installerInInput = installerInInput;
            _commands = commands;
            Ui.ApplyFormDefaults(this);
            Text = "Auto-detect in Windows Sandbox";
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MinimumSize = new Size(720, 520);
            Size = new Size(860, 600);
            var tip = new ToolTip();

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _title = new Label { AutoSize = true, Font = Ui.HeadingFont, Margin = new Padding(3, 0, 3, 8) };
            _body = new Label { AutoSize = true, MaximumSize = new Size(800, 0), Margin = new Padding(3, 0, 3, 8) };
            _progress = new ProgressBar { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Dock = DockStyle.Fill, Height = 18 };
            _log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = Ui.MonoFont, BackColor = SystemColors.Window };

            _btnEnable = Ui.Button("&Turn on Windows Sandbox...", tip,
                "Turns on the Windows Sandbox feature (administrator approval and a restart are needed).", (s, e) => EnableSandbox());
            _btnEnable.Visible = false;
            _btnApply = Ui.Button("&Use these values", tip, "Fill in the uninstall command and File detection rule.", (s, e) => DialogResult = DialogResult.OK);
            _btnApply.Visible = false;
            _btnCancel = Ui.Button("Cancel", tip, "Stop waiting and close the Sandbox.", (s, e) => Close());
            _btnCaptureHere = Ui.Button("Capture on this &PC instead...", tip,
                "Install and uninstall the app yourself on this PC while Intune Wrapper records the changes.",
                (s, e) => DialogResult = DialogResult.Retry);
            _btnCaptureHere.Visible = false;
            CancelButton = _btnCancel;
            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
            buttons.Controls.AddRange(new Control[] { _btnCancel, _btnApply, _btnEnable, _btnCaptureHere });

            layout.Controls.Add(_title);
            layout.Controls.Add(_body);
            layout.Controls.Add(_progress);
            layout.Controls.Add(_log);
            layout.Controls.Add(buttons);
            Controls.Add(layout);
        }

        public SandboxResult Result { get; private set; }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!SandboxLauncher.IsAvailable)
            {
                ShowSandboxMissing();
                return;
            }
            if (SandboxLauncher.IsRunning)
            {
                Stop("Windows Sandbox is already running",
                    "Only one Windows Sandbox can run at a time. Close the open Sandbox window and try again.");
                return;
            }
            if (_commands.Install.IsPlaceholder)
            {
                Stop("No install command", "Fill in the install command on the Program tab first.");
                return;
            }
            Run();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_cts != null && DialogResult != DialogResult.OK)
            {
                _cts.Cancel();
                SandboxLauncher.Close();
            }
            base.OnFormClosing(e);
        }

        private async void Run()
        {
            _title.Text = "Installing " + _info.FileName + " in Windows Sandbox...";
            _body.Text =
                "Windows Sandbox opens in a separate window. Don't close it; Intune Wrapper drives it:\n" +
                "1. install silently with:  " + _commands.IntuneInstallCommand +
                (_commands.UsesInstallScript ? "  (setup + " + _commands.Items.Count + " additional item(s))" : "") + "\n" +
                "2. find the uninstall entry and the main program file\n" +
                "3. check the File detection rule, then uninstall and check that the app is gone.\n" +
                "Your own computer is not changed. The Sandbox closes by itself when done.";

            var job = new SandboxJob
            {
                InstallerFileName = _info.FileName,
                InstallCommand = _commands.IntuneInstallCommand,
                Framework = _info.Framework,
                ProductName = _commands.AppName.IsPlaceholder ? _info.ProductName : _commands.AppName.Value,
                InstallTimeoutMinutes = Math.Max(5, _commands.InstallTimeMinutes),
            };
            _workDir = Path.Combine(Path.GetTempPath(), "IntuneWrapper", "sandbox-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            _cts = new CancellationTokenSource();
            try
            {
                string wsb = SandboxLauncher.Prepare(Application.ExecutablePath, _installerInInput, job, _workDir);
                Append("Prepared " + _workDir);
                Logger.Info("Sandbox auto-detect for " + _info.FileName + ", work folder " + _workDir);
                SandboxLauncher.Start(wsb);
                Append("Starting Windows Sandbox (this takes about a minute)...");

                TimeSpan timeout = TimeSpan.FromMinutes(job.InstallTimeoutMinutes + job.UninstallTimeoutMinutes + 15);
                SandboxResult result = await SandboxLauncher.WaitAsync(_workDir, timeout, line => BeginInvoke(new Action(() => Append(line))), _cts.Token);
                _cts = null;
                if (result == null)
                {
                    SandboxLauncher.Close();
                    Stop("No result from Windows Sandbox",
                        "The Sandbox did not report back within " + (int)timeout.TotalMinutes + " minutes. The install command may be waiting " +
                        "for input. Check the Sandbox window, or the log in " + _workDir + ".");
                    return;
                }
                ShowResult(result);
            }
            catch (OperationCanceledException)
            {
                // Closed by the user.
            }
            catch (Exception ex)
            {
                _cts = null;
                Logger.Error("Sandbox auto-detect failed", ex);
                Stop("Auto-detect failed", ex.Message);
            }
        }

        private void ShowResult(SandboxResult r)
        {
            Result = r;
            _progress.Visible = false;
            Logger.Info("Sandbox result: completed=" + r.Completed + ", entry=" + r.SelectedKeyPath + ", verified=" + r.FullyVerified);
            Func<bool, string> mark = ok => ok ? "OK    " : "FAIL  ";
            var lines = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(r.Error)) lines.Add("FAIL  Error: " + r.Error);
            lines.Add(mark(!r.InstallTimedOut && IsSuccess(r.InstallExitCode)) + "Silent install: " +
                      (r.InstallTimedOut ? "did not finish (probably not silent)" : "exit code " + r.InstallExitCode + " after " + r.InstallSeconds + " s"));
            UninstallEntry entry = r.Selected;
            lines.Add(mark(entry != null) + "Uninstall entry: " + (entry != null ? entry.Label + "  (" + entry.KeyPath + ")" : "none found"));
            if (entry != null)
            {
                lines.Add(mark(r.Detection != null && r.Detection.IsComplete && r.DetectionFoundAfterInstall) + "Detection rule finds the app: " +
                          (r.Detection != null ? r.Detection.Summary(" | ") : "-"));
                lines.Add(mark(r.UninstallRan && !r.UninstallTimedOut && IsSuccess(r.UninstallExitCode)) + "Silent uninstall: " +
                          (r.UninstallCommand ?? "-") +
                          (r.UninstallRan ? (r.UninstallTimedOut ? "  (did not finish)" : "  (exit code " + r.UninstallExitCode + ")") : "  (not run)"));
                lines.Add(mark(r.EntryRemovedAfterUninstall && r.DetectionGoneAfterUninstall) + "App removed after uninstall: entry " +
                          (r.EntryRemovedAfterUninstall ? "removed" : "still present") + ", detection file " +
                          (r.DetectionGoneAfterUninstall ? "gone" : "still present"));
            }
            Append("");
            Append("===== Result =====");
            foreach (string l in lines) Append(l);

            _title.Text = r.FullyVerified ? "Verified in Windows Sandbox" : entry != null ? "Partly verified - check the FAIL lines" : "Nothing found";
            _body.Text = r.FullyVerified
                ? "Install, detection and uninstall all worked. Click \"Use these values\" to fill in the Program and Detection rules tabs."
                : entry != null
                    ? "Some checks failed (see FAIL below). You can still use the values, but fix or verify them before uploading."
                    : "No uninstall entry appeared. The install command may not be silent or correct. Fix it and try again, " +
                      "or click \"Capture on this PC instead...\" to install and uninstall the app yourself while Intune Wrapper records it.";
            _btnApply.Visible = entry != null;
            _btnCaptureHere.Visible = !r.FullyVerified;
            _btnCancel.Text = "Close";
            if (entry != null) _btnApply.Focus();
        }

        private static bool IsSuccess(int? exitCode)
        {
            // Intune's default success codes: 0, 1707, 3010 (soft reboot), 1641 (hard reboot).
            return exitCode == 0 || exitCode == 1707 || exitCode == 3010 || exitCode == 1641;
        }

        private void ShowSandboxMissing()
        {
            _progress.Visible = false;
            _title.Text = "Windows Sandbox is not turned on";
            _body.Text =
                "Auto-detect installs the app in Windows Sandbox: a clean, disposable copy of Windows, so your own computer " +
                "is never changed.\n\n" +
                "Windows Sandbox is a free Windows feature (Windows 10/11 Pro or Enterprise, with virtualization enabled in the " +
                "BIOS). Turning it on needs administrator approval and a restart. You only do this once.\n\n" +
                "Click \"Turn on Windows Sandbox...\", approve the prompt, restart, and run Auto-detect again.\n" +
                "Or: Start > \"Turn Windows features on or off\" > tick \"Windows Sandbox\".";
            _log.Visible = false;
            _btnEnable.Visible = true;
            _btnCaptureHere.Visible = true;
            _btnCancel.Text = "Close";
        }

        private void EnableSandbox()
        {
            try
            {
                Process p = Process.Start(new ProcessStartInfo("dism.exe", SandboxLauncher.EnableArguments)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                });
                p?.WaitForExit();
                if (p != null && (p.ExitCode == 0 || p.ExitCode == 3010))
                {
                    Logger.Info("Windows Sandbox feature enabled (dism exit " + p.ExitCode + ").");
                    Ui.Info(this, "Windows Sandbox is turned on. Restart the computer, then run Auto-detect again.");
                    Close();
                }
                else if (p != null)
                {
                    Ui.Error(this, "Turning on Windows Sandbox failed (dism exit code " + p.ExitCode + "). Check that virtualization " +
                                   "is enabled in the BIOS and that this is Windows Pro or Enterprise.");
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // The user declined the administrator prompt.
            }
        }

        private void Stop(string title, string message)
        {
            _progress.Visible = false;
            _btnCaptureHere.Visible = true;
            _title.Text = title;
            _body.Text = message;
            _btnCancel.Text = "Close";
        }

        private void Append(string line)
        {
            _log.AppendText(line + Environment.NewLine);
        }
    }
}
