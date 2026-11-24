using System;
using System.Drawing;
using System.Windows.Forms;
using IntuneWrapper.Core;

namespace IntuneWrapper.UI
{
    /// <summary>Version, tool path and a link to the packaging tool's repo (SPEC 3).</summary>
    internal sealed class AboutDialog : Form
    {
        public AboutDialog(AppPaths paths, string toolPath)
        {
            Ui.ApplyFormDefaults(this);
            Text = "About " + AppInfo.Name;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Padding = new Padding(16) };
            var icon = new PictureBox { Image = new Icon(Ui.AppIcon, 48, 48).ToBitmap(), Size = new Size(48, 48), Margin = new Padding(3, 3, 14, 3) };
            t.Controls.Add(icon, 0, 0);
            t.SetRowSpan(icon, 2);
            t.Controls.Add(new Label { Text = AppInfo.Name, Font = Ui.HeadingFont, AutoSize = true }, 1, 0);
            t.Controls.Add(new Label { Text = "Version " + AppInfo.Version, AutoSize = true }, 1, 1);

            Version toolVersion = ToolManager.GetLocalVersion(toolPath);
            string toolText = ToolManager.IsPresent(toolPath)
                ? toolPath + (toolVersion != null ? "  (v" + toolVersion + ")" : "")
                : "not found";
            var details = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(520, 0),
                Margin = new Padding(3, 12, 3, 3),
                Text = "Packages installers for Microsoft Intune with IntuneWinAppUtil.exe and suggests install, uninstall and detection settings.\n\n" +
                       "Open source under the MIT License. Copyright (c) 2026 Intune Wrapper contributors.\n" +
                       "Provided \"as is\", without warranty. Test every package before deploying it.\n\n" +
                       "Packaging tool: " + toolText + "\n" +
                       "IntuneWinAppUtil.exe is Microsoft software under its own license; it is downloaded from Microsoft's GitHub, not included.\n" +
                       "Data folder: " + paths.Root,
            };
            t.Controls.Add(details, 1, 2);

            var repo = new LinkLabel { Text = "Microsoft Win32 Content Prep Tool on GitHub", AutoSize = true, Margin = new Padding(3, 10, 3, 3) };
            repo.LinkClicked += (s, e) => Ui.OpenUrl(ToolManager.RepoUrl);
            t.Controls.Add(repo, 1, 3);
            var logs = new LinkLabel { Text = "Open log folder", AutoSize = true };
            logs.LinkClicked += (s, e) => Ui.OpenFolder(paths.LogsDir);
            t.Controls.Add(logs, 1, 4);

            var ok = Ui.Button("OK", null, null, (s, e) => Close());
            ok.Anchor = AnchorStyles.Right;
            AcceptButton = ok;
            CancelButton = ok;
            t.Controls.Add(ok, 1, 5);
            Controls.Add(t);
        }
    }
}
