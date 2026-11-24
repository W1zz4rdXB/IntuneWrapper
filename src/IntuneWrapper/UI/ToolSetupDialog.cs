using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using IntuneWrapper.Core;

namespace IntuneWrapper.UI
{
    /// <summary>
    /// FR-6/FR-7: tool missing -> Browse, Download latest from GitHub, or Cancel. Also used for updates.
    /// Download failures keep the dialog open with the reason and a link to the releases page.
    /// </summary>
    internal sealed class ToolSetupDialog : Form
    {
        private readonly string _destination;
        private readonly ToolRelease _knownRelease;
        private readonly bool _downloadNow;
        private readonly Label _message;
        private readonly Label _status;
        private readonly ProgressBar _progress;
        private readonly LinkLabel _releasesLink;
        private readonly Button _btnBrowse, _btnDownload, _btnCancel;
        private CancellationTokenSource _cts;
        private bool _closeRequested;

        public ToolSetupDialog(string destination, string currentPath, ToolRelease knownRelease, bool downloadNow)
        {
            _destination = destination;
            _knownRelease = knownRelease;
            _downloadNow = downloadNow;

            Ui.ApplyFormDefaults(this);
            Text = "IntuneWinAppUtil.exe";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            var tip = new ToolTip();

            var layout = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                Padding = new Padding(14),
                MinimumSize = new Size(560, 0),
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            bool present = ToolManager.IsPresent(currentPath);
            _message = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(520, 0),
                Text = present
                    ? "Intune Wrapper uses Microsoft's IntuneWinAppUtil.exe (Win32 Content Prep Tool) to create packages.\n\nCurrent tool: " + currentPath
                    : "Intune Wrapper needs Microsoft's IntuneWinAppUtil.exe (Win32 Content Prep Tool) to create .intunewin packages." +
                      (string.IsNullOrWhiteSpace(currentPath) ? "" : "\n\nIt was not found at: " + currentPath) +
                      "\n\nBrowse to a copy you already have, or download the latest release from GitHub. Either way it is placed in the packaging folder:\n" + destination,
            };
            _status = new Label { AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(3, 10, 3, 3) };
            _progress = new ProgressBar { Width = 520, Height = 18, Visible = false };
            _releasesLink = new LinkLabel { Text = "Open the releases page on GitHub", AutoSize = true, Visible = false };
            _releasesLink.LinkClicked += (s, e) => Ui.OpenUrl(ToolManager.ReleasesUrl);

            _btnBrowse = Ui.Button("&Browse...", tip, "Locate an existing IntuneWinAppUtil.exe.", (s, e) => Browse());
            _btnDownload = Ui.Button("&Download latest", tip, "Download the latest release from GitHub to " + destination, (s, e) => Download());
            _btnCancel = Ui.Button("Cancel", tip, "Close without changing the tool. Convert stays unavailable until the tool is set up.",
                (s, e) => { if (_cts != null) _cts.Cancel(); else Close(); });
            CancelButton = null; // Esc is handled below so a running download is cancelled, not abandoned.

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
            buttons.Controls.AddRange(new Control[] { _btnCancel, _btnDownload, _btnBrowse });

            layout.Controls.Add(_message);
            layout.Controls.Add(_status);
            layout.Controls.Add(_progress);
            layout.Controls.Add(_releasesLink);
            buttons.Margin = new Padding(0, 12, 0, 0);
            layout.Controls.Add(buttons);
            Controls.Add(layout);

            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Escape) return;
                if (_cts != null) _cts.Cancel(); else Close();
            };
        }

        public string ToolPath { get; private set; }

        /// <summary>Release tag when the tool was downloaded; null when the user browsed to a file.</summary>
        public string ReleaseTag { get; private set; }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_downloadNow) Download();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _closeRequested = true;
                e.Cancel = true; // Closed by Download() once the download has stopped.
            }
            base.OnFormClosing(e);
        }

        private void Browse()
        {
            string path = Ui.PickTool(this, null);
            if (path == null) return;
            ToolPath = path;
            DialogResult = DialogResult.OK;
        }

        private async void Download()
        {
            _cts = new CancellationTokenSource();
            _btnBrowse.Enabled = _btnDownload.Enabled = false;
            _releasesLink.Visible = false;
            _status.ForeColor = SystemColors.ControlText;
            _status.Text = "Looking up the latest release on GitHub...";
            _progress.Visible = true;
            _progress.Style = ProgressBarStyle.Marquee;
            bool closeAfter = false;

            try
            {
                ToolRelease release = _knownRelease ?? await ToolManager.GetLatestReleaseAsync(_cts.Token);
                _status.Text = "Downloading " + ToolManager.ToolFileName + " " + release.Tag + "...";
                var progress = new Progress<Tuple<long, long>>(p =>
                {
                    if (p.Item2 > 0)
                    {
                        _progress.Style = ProgressBarStyle.Continuous;
                        _progress.Value = (int)Math.Min(100, p.Item1 * 100 / p.Item2);
                        _status.Text = "Downloading " + release.Tag + ": " + InstallerInspector.FormatSize(p.Item1) +
                                       " of " + InstallerInspector.FormatSize(p.Item2);
                    }
                });
                await ToolManager.DownloadAsync(release, _destination, progress, _cts.Token);
                ToolPath = _destination;
                ReleaseTag = release.Tag;
                closeAfter = true;
            }
            catch (OperationCanceledException)
            {
                _status.Text = "Download cancelled.";
            }
            catch (ToolDownloadException ex)
            {
                ShowFailure(ex.Message);
            }
            catch (Exception ex)
            {
                Logger.Error("Tool download failed", ex);
                ShowFailure("The download failed: " + ex.Message);
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
                _progress.Visible = false;
                _btnBrowse.Enabled = _btnDownload.Enabled = true;
            }

            if (closeAfter)
            {
                DialogResult = DialogResult.OK;
            }
            else if (_closeRequested)
            {
                Close();
            }
        }

        private void ShowFailure(string message)
        {
            Logger.Warn("Tool download failed: " + message);
            _status.ForeColor = Ui.ErrorText;
            _status.Text = message + "\nYou can download IntuneWinAppUtil.exe manually and use Browse.";
            _releasesLink.Visible = true;
        }
    }
}
