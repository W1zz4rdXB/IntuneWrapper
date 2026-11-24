using System.Drawing;
using System.IO;
using System.Windows.Forms;
using IntuneWrapper.Core;

namespace IntuneWrapper.UI
{
    /// <summary>FR-32: tool path, default output folder, default install context, log pane default.</summary>
    internal sealed class SettingsForm : Form
    {
        private readonly Settings _settings;
        private readonly TextBox _txtTool, _txtOutput;
        private readonly ComboBox _cmbContext, _cmbMinOs;
        private readonly CheckBox _chkLog;

        public SettingsForm(AppPaths paths, Settings settings)
        {
            _settings = settings;
            Ui.ApplyFormDefaults(this);
            Text = "Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(660, 320);
            var tip = new ToolTip();

            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(12) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            for (int i = 0; i < 6; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _txtTool = new TextBox { Dock = DockStyle.Fill, Text = settings.ToolPath ?? "", Margin = new Padding(3, 5, 3, 3) };
            tip.SetToolTip(_txtTool, "Full path to IntuneWinAppUtil.exe.");
            t.Controls.Add(Ui.Label("&Tool path:"), 0, 0);
            t.Controls.Add(_txtTool, 1, 0);
            t.Controls.Add(Ui.Button("Browse...", tip, "Locate IntuneWinAppUtil.exe.", (s, e) =>
            {
                string p = Ui.PickTool(this, _txtTool.Text);
                if (p != null) _txtTool.Text = p;
            }), 2, 0);

            _txtOutput = new TextBox { Dock = DockStyle.Fill, Text = settings.PackagingFolder ?? "", Margin = new Padding(3, 5, 3, 3) };
            tip.SetToolTip(_txtOutput, "Packaging folder: holds IntuneWinAppUtil.exe and one folder per program with input and output subfolders.");
            t.Controls.Add(Ui.Label("&Packaging folder:"), 0, 1);
            t.Controls.Add(_txtOutput, 1, 1);
            t.Controls.Add(Ui.Button("Browse...", tip, "Choose the packaging folder.", (s, e) =>
            {
                string f = Ui.PickFolder(this, "Packaging folder", _txtOutput.Text);
                if (f != null) _txtOutput.Text = f;
            }), 2, 1);

            _cmbContext = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
            _cmbContext.Items.AddRange(new object[] { InstallContext.System, InstallContext.User });
            _cmbContext.SelectedItem = settings.DefaultInstallContext;
            tip.SetToolTip(_cmbContext, "Install context selected for newly loaded installers.");
            t.Controls.Add(Ui.Label("Default install &context:"), 0, 2);
            t.Controls.Add(_cmbContext, 1, 2);

            _cmbMinOs = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
            _cmbMinOs.Items.AddRange(Portal.MinimumOsOptions);
            int os = System.Array.IndexOf(Portal.MinimumOsOptions, settings.DefaultMinimumOs);
            _cmbMinOs.SelectedIndex = os >= 0 ? os : 0;
            tip.SetToolTip(_cmbMinOs, "Minimum operating system requirement selected for newly loaded installers.");
            t.Controls.Add(Ui.Label("Default &minimum OS:"), 0, 3);
            t.Controls.Add(_cmbMinOs, 1, 3);

            _chkLog = new CheckBox { Text = "Show the &log pane when the app starts", AutoSize = true, Checked = settings.ShowLogByDefault };
            t.Controls.Add(_chkLog, 1, 4);

            var data = new LinkLabel { Text = "Open data folder (" + paths.Root + ")", AutoSize = true, Margin = new Padding(3, 10, 3, 3) };
            data.LinkClicked += (s, e) => Ui.OpenFolder(paths.Root);
            t.Controls.Add(data, 1, 5);
            t.SetColumnSpan(data, 2);

            var ok = Ui.Button("OK", tip, "Save settings.", (s, e) => Save());
            var cancel = Ui.Button("Cancel", tip, "Close without saving.", (s, e) => Close());
            CancelButton = cancel;
            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
            buttons.Controls.AddRange(new Control[] { cancel, ok });
            t.Controls.Add(new Panel(), 0, 6);
            t.Controls.Add(buttons, 0, 7);
            t.SetColumnSpan(buttons, 3);
            Controls.Add(t);
        }

        private void Save()
        {
            string tool = _txtTool.Text.Trim();
            string output = _txtOutput.Text.Trim();
            if (tool.Length > 0 && !File.Exists(tool))
            {
                if (!Ui.Confirm(this, "The tool was not found at:\n" + tool + "\n\nSave this path anyway?")) return;
            }
            if (output.Length == 0)
            {
                Ui.Error(this, "Choose a packaging folder.");
                return;
            }
            try
            {
                Path.GetFullPath(output);
            }
            catch
            {
                Ui.Error(this, "The packaging folder path is not valid.");
                return;
            }
            _settings.ToolPath = tool.Length == 0 ? null : tool;
            _settings.PackagingFolder = output;
            _settings.DefaultInstallContext = (InstallContext)_cmbContext.SelectedItem;
            _settings.ShowLogByDefault = _chkLog.Checked;
            _settings.DefaultMinimumOs = (string)_cmbMinOs.SelectedItem;
            DialogResult = DialogResult.OK;
        }
    }
}
