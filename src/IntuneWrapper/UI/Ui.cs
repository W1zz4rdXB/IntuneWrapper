using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using IntuneWrapper.Core;

namespace IntuneWrapper.UI
{
    /// <summary>Shared fonts, colours and small helpers for all forms.</summary>
    internal static class Ui
    {
        public static readonly Font BaseFont = new Font("Segoe UI", 10f);
        public static readonly Font BoldFont = new Font("Segoe UI", 10f, FontStyle.Bold);
        public static readonly Font HeadingFont = new Font("Segoe UI Semibold", 12f);
        public static readonly Font SmallFont = new Font("Segoe UI", 9f);
        public static readonly Font MonoFont = new Font("Consolas", 10f);

        public static readonly Color Accent = Color.FromArgb(0, 103, 184);
        public static readonly Color MutedText = Color.FromArgb(96, 96, 96);
        public static readonly Color WarningBack = Color.FromArgb(255, 244, 206);
        public static readonly Color WarningText = Color.FromArgb(138, 83, 0);
        public static readonly Color ErrorBack = Color.FromArgb(253, 231, 233);
        public static readonly Color ErrorText = Color.FromArgb(168, 0, 0);
        public static readonly Color SuccessBack = Color.FromArgb(223, 246, 221);
        public static readonly Color SuccessText = Color.FromArgb(16, 124, 16);
        public static readonly Color InfoBack = Color.FromArgb(222, 236, 249);
        public static readonly Color InfoText = Color.FromArgb(0, 78, 140);

        private static Icon _appIcon;

        public static Icon AppIcon
        {
            get
            {
                if (_appIcon == null)
                {
                    try
                    {
                        using (Stream s = typeof(Ui).Assembly.GetManifestResourceStream("IntuneWrapper.icon.ico"))
                        {
                            _appIcon = s != null ? new Icon(s) : SystemIcons.Application;
                        }
                    }
                    catch
                    {
                        _appIcon = SystemIcons.Application;
                    }
                }
                return _appIcon;
            }
        }

        public static void ApplyFormDefaults(Form form)
        {
            form.Font = BaseFont;
            form.AutoScaleMode = AutoScaleMode.Dpi;
            form.AutoScaleDimensions = new SizeF(96f, 96f);
            form.Icon = AppIcon;
        }

        public static Button Button(string text, ToolTip tip, string tooltip, EventHandler onClick)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(88, 30),
                Padding = new Padding(6, 0, 6, 0),
                Margin = new Padding(3),
                UseVisualStyleBackColor = true,
            };
            if (tip != null && tooltip != null) tip.SetToolTip(b, tooltip);
            if (onClick != null) b.Click += onClick;
            return b;
        }

        public static Label Label(string text, bool autoSize = true)
        {
            return new Label
            {
                Text = text,
                AutoSize = autoSize,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 6, 3, 3),
            };
        }

        public static void Error(IWin32Window owner, string message, string title = "Intune Wrapper")
        {
            MessageBox.Show(owner, message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        public static void Warn(IWin32Window owner, string message, string title = "Intune Wrapper")
        {
            MessageBox.Show(owner, message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        public static void Info(IWin32Window owner, string message, string title = "Intune Wrapper")
        {
            MessageBox.Show(owner, message, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>Yes/No question with No as the default, so Enter never confirms by accident.</summary>
        public static bool Confirm(IWin32Window owner, string message, string title = "Intune Wrapper")
        {
            return MessageBox.Show(owner, message, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        /// <summary>The clipboard can be briefly locked by other apps; retry a few times.</summary>
        public static bool SetClipboard(string text)
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    Clipboard.SetDataObject(string.IsNullOrEmpty(text) ? " " : text, true, 3, 100);
                    return true;
                }
                catch (ExternalException)
                {
                    Thread.Sleep(100);
                }
            }
            return false;
        }

        public static void OpenFolder(string folder, string selectFile = null)
        {
            try
            {
                if (selectFile != null && File.Exists(selectFile))
                {
                    Process.Start("explorer.exe", "/select," + CommandLine.Quote(selectFile));
                }
                else
                {
                    Directory.CreateDirectory(folder);
                    Process.Start("explorer.exe", CommandLine.Quote(folder));
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("Could not open folder " + folder + ": " + ex.Message);
            }
        }

        public static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.Warn("Could not open " + url + ": " + ex.Message);
            }
        }

        /// <summary>Folder picker that starts in <paramref name="initial"/> when it exists.</summary>
        public static string PickFolder(IWin32Window owner, string description, string initial)
        {
            using (var dlg = new FolderBrowserDialog { Description = description, ShowNewFolderButton = true })
            {
                if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial)) dlg.SelectedPath = initial;
                return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.SelectedPath : null;
            }
        }

        public static string PickTool(IWin32Window owner, string initial)
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "Locate IntuneWinAppUtil.exe",
                Filter = "IntuneWinAppUtil.exe|IntuneWinAppUtil.exe|Programs (*.exe)|*.exe",
                CheckFileExists = true,
            })
            {
                if (!string.IsNullOrWhiteSpace(initial) && File.Exists(initial))
                {
                    dlg.InitialDirectory = Path.GetDirectoryName(initial);
                }
                return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.FileName : null;
            }
        }
    }

    /// <summary>A coloured text badge. Colour is never the only signal: the text says it too (SPEC 6.1).</summary>
    internal sealed class Badge : Label
    {
        public Badge()
        {
            AutoSize = true;
            Padding = new Padding(6, 3, 6, 3);
            Margin = new Padding(6, 4, 3, 3);
            Font = Ui.SmallFont;
            Anchor = AnchorStyles.Left;
            BorderStyle = BorderStyle.None;
        }

        public void Set(string text, Color back, Color fore)
        {
            Text = text;
            BackColor = back;
            ForeColor = fore;
            Visible = !string.IsNullOrEmpty(text);
        }

        public void SetConfidence(Confidence confidence, ToolTip tip)
        {
            switch (confidence)
            {
                case Confidence.Detected:
                    Set("Detected", Ui.SuccessBack, Ui.SuccessText);
                    tip.SetToolTip(this, "Detected: read directly from the file or the registry.");
                    break;
                case Confidence.Suggested:
                    Set("Suggested - verify", Ui.WarningBack, Ui.WarningText);
                    tip.SetToolTip(this, "Suggested: inferred from the installer framework. Verify it on a test machine.");
                    break;
                case Confidence.Edited:
                    Set("Edited", Ui.InfoBack, Ui.InfoText);
                    tip.SetToolTip(this, "Edited: you changed this value. Your version is used for copy, export and history.");
                    break;
                default:
                    Set("Unknown - fill in", Ui.ErrorBack, Ui.ErrorText);
                    tip.SetToolTip(this, "Unknown: this is a placeholder or a guess. Fill it in or use Capture uninstall.");
                    break;
            }
        }
    }

    /// <summary>A small dialog with a message and custom buttons. Returns the clicked index or -1.</summary>
    internal sealed class ChoiceDialog : Form
    {
        private ChoiceDialog(string title, string message, string[] buttons, int cancelIndex)
        {
            Ui.ApplyFormDefaults(this);
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);

            var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
            var text = new Label { Text = message, AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(3, 3, 3, 12) };
            layout.Controls.Add(text);
            var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
            for (int i = buttons.Length - 1; i >= 0; i--)
            {
                int index = i;
                Button b = Ui.Button(buttons[i], null, null, (s, e) => { Choice = index; Close(); });
                row.Controls.Add(b);
                if (i == cancelIndex) CancelButton = b;
            }
            layout.Controls.Add(row);
            Controls.Add(layout);
        }

        public int Choice { get; private set; } = -1;

        public static int Show(IWin32Window owner, string title, string message, string[] buttons, int cancelIndex)
        {
            using (var dlg = new ChoiceDialog(title, message, buttons, cancelIndex))
            {
                dlg.ShowDialog(owner);
                return dlg.Choice;
            }
        }
    }
}
