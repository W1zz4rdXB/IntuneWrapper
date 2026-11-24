using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using IntuneWrapper.Core;

namespace IntuneWrapper.UI
{
    /// <summary>
    /// Manual capture on this PC, for when the silent install fails (FR-17..FR-20 plus an uninstall check):
    /// 1. snapshot, 2. the user installs the app, 3. pick the new entry (the File rule is checked against
    /// the installed files), 4. the user uninstalls (or runs the found command) and Verify checks that the
    /// entry and the program file are gone. Cancel is available at every step.
    /// </summary>
    internal sealed class CaptureWizard : Form
    {
        private const int Steps = 4;

        private readonly InstallerInfo _info;
        private readonly string _productName;
        private readonly ToolTip _tip = new ToolTip();
        private readonly Label _title, _body, _warning;
        private readonly Panel _content;
        private readonly Button _btnBack, _btnNext, _btnCancel, _btnLaunch, _btnRunUninstall, _btnVerify, _btnAppsSettings;
        private readonly ListView _list;
        private readonly CheckBox _chkChanged;
        private readonly TextBox _preview, _checkLog;

        private int _step = 1;
        private bool _busy;
        private UninstallSnapshot _before;
        private SnapshotDiff _diff;
        private UninstallEntry _selected;
        private PackageCommands _selectedValues;

        public CaptureWizard(InstallerInfo info, string productName)
        {
            _info = info;
            _productName = string.IsNullOrWhiteSpace(productName) ? info.ProductName : productName;
            Ui.ApplyFormDefaults(this);
            Text = "Capture install and uninstall on this PC";
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MinimumSize = new Size(720, 540);
            Size = new Size(840, 620);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _title = new Label { AutoSize = true, Font = Ui.HeadingFont, Margin = new Padding(3, 0, 3, 8) };
            _body = new Label { AutoSize = true, MaximumSize = new Size(780, 0), Margin = new Padding(3, 0, 3, 8) };
            _warning = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(780, 0),
                BackColor = Ui.WarningBack,
                ForeColor = Ui.WarningText,
                Padding = new Padding(6, 4, 6, 4),
                Margin = new Padding(3, 0, 3, 8),
            };
            _content = new Panel { Dock = DockStyle.Fill };

            // Step 3 controls
            _list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
            _list.Columns.Add("Name", 230);
            _list.Columns.Add("Version", 90);
            _list.Columns.Add("Publisher", 140);
            _list.Columns.Add("Change", 70);
            _list.Columns.Add("Registry key", 260);
            _list.SelectedIndexChanged += (s, e) => UpdatePreview();
            _list.DoubleClick += (s, e) => { if (ListSelection != null) Next(); };
            _chkChanged = new CheckBox { Text = "Also show &changed entries (upgrades, repaired apps)", AutoSize = true };
            _chkChanged.CheckedChanged += (s, e) => FillList();
            _preview = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, Font = Ui.MonoFont, ScrollBars = ScrollBars.Vertical };

            // Step 4 controls
            _checkLog = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, Font = Ui.MonoFont, ScrollBars = ScrollBars.Both, WordWrap = false };
            _btnRunUninstall = Ui.Button("&Run uninstall command", _tip,
                "Run the uninstall command that was found (this also tests it). Machine-wide apps ask for administrator approval.",
                (s, e) => RunUninstall());
            _btnAppsSettings = Ui.Button("Open &Installed apps", _tip, "Open Settings > Apps > Installed apps to uninstall it yourself.",
                (s, e) => Ui.OpenUrl("ms-settings:appsfeatures"));
            _btnVerify = Ui.Button("&Verify removal", _tip, "Check that the uninstall entry and the program file are gone.", (s, e) => Verify());

            _btnLaunch = Ui.Button("&Start installer", _tip, "Run " + info.FileName + " normally (interactive) so you can install it now.", (s, e) => LaunchInstaller());
            _btnBack = Ui.Button("< &Back", _tip, "Go back to the previous step.", (s, e) => ShowStep(_step - 1));
            _btnNext = Ui.Button("&Next >", _tip, null, (s, e) => Next());
            _btnCancel = Ui.Button("Cancel", _tip, "Close without changing anything.", (s, e) => Close());
            CancelButton = _btnCancel;

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
            buttons.Controls.AddRange(new Control[] { _btnCancel, _btnNext, _btnBack });

            layout.Controls.Add(_title);
            layout.Controls.Add(_body);
            layout.Controls.Add(_warning);
            layout.Controls.Add(_content);
            layout.Controls.Add(buttons);
            Controls.Add(layout);

            ShowStep(1);
        }

        /// <summary>The chosen entry (after step 3).</summary>
        public UninstallEntry SelectedEntry => _selected;

        /// <summary>The File detection rule found the app after the install.</summary>
        public bool DetectionFoundAfterInstall { get; private set; }

        /// <summary>Verify ran and the uninstall entry was gone.</summary>
        public bool EntryRemovedAfterUninstall { get; private set; }

        /// <summary>Verify ran and the detection file was gone.</summary>
        public bool DetectionGoneAfterUninstall { get; private set; }

        /// <summary>The found uninstall command ran successfully (exit code 0/3010/1641).</summary>
        public bool UninstallCommandRan { get; private set; }

        private UninstallEntry ListSelection =>
            _list.SelectedItems.Count == 1 ? (UninstallEntry)_list.SelectedItems[0].Tag : null;

        private void ShowStep(int step)
        {
            _step = step;
            _content.Controls.Clear();
            _btnBack.Visible = step > 1;
            _warning.Visible = false;

            if (step == 1)
            {
                _title.Text = "Step 1 of " + Steps + ": Take a snapshot before installing";
                _body.Text =
                    "You install and uninstall the app yourself on this PC; Intune Wrapper records what changes:\n" +
                    "the uninstall entry, the uninstall command and the program file for the File detection rule.\n\n" +
                    "Make sure " + _productName + " is NOT installed yet, then click Take snapshot.";
                _warning.Text = "This installs the app on THIS PC (and you uninstall it again in step 4). Other software that is " +
                                "installed or updated in the meantime also shows up, so close other installers.";
                if (!UninstallRegistry.IsAdministrator())
                {
                    _warning.Text += "\n\nYou are running as a standard user. Machine-wide installs and uninstalls will ask for " +
                                     "administrator approval.";
                }
                _warning.Visible = true;
                _btnNext.Text = "&Take snapshot";
                _tip.SetToolTip(_btnNext, "Read the uninstall registry keys now.");
            }
            else if (step == 2)
            {
                _title.Text = "Step 2 of " + Steps + ": Install the application";
                _body.Text =
                    "Snapshot taken at " + _before.TakenAt.ToString("HH:mm:ss") + " (" + _before.Entries.Count + " entries).\n\n" +
                    "Now install " + _info.FileName + " on this PC, for example with Start installer below, using the normal " +
                    "installer screens. Wait until the installation has completely finished, then click Finish install capture.";
                var flow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
                flow.Controls.Add(_btnLaunch);
                _content.Controls.Add(flow);
                ShowAccessWarning(_before);
                _btnNext.Text = "&Finish install capture";
                _tip.SetToolTip(_btnNext, "Take the second snapshot and compare.");
            }
            else if (step == 3)
            {
                _title.Text = "Step 3 of " + Steps + ": Choose the application's entry";
                _body.Text = "These uninstall entries appeared after the first snapshot. Select the one for " + _productName + ".";
                var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
                t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                t.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
                t.Controls.Add(_chkChanged);
                t.Controls.Add(_list);
                t.Controls.Add(new Label { Text = "Result preview:", AutoSize = true, Margin = new Padding(3, 8, 3, 2) });
                t.Controls.Add(_preview);
                _content.Controls.Add(t);
                FillList();
                _btnNext.Text = "&Next: uninstall check >";
                _tip.SetToolTip(_btnNext, "Continue with the uninstall check.");
            }
            else
            {
                _title.Text = "Step 4 of " + Steps + ": Uninstall and verify";
                _body.Text =
                    "Now uninstall " + _selected.Label + ", so the uninstall command and detection rule are checked and this PC " +
                    "is clean again:\n" +
                    "- Run uninstall command runs the command that will go to Intune (best: it tests it), or\n" +
                    "- uninstall it yourself in Settings > Apps > Installed apps.\n" +
                    "Then click Verify removal. You can also skip this check with Use these values.";
                var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
                t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                var flow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
                flow.Controls.AddRange(new Control[] { _btnRunUninstall, _btnAppsSettings, _btnVerify });
                t.Controls.Add(flow);
                t.Controls.Add(_checkLog);
                _content.Controls.Add(t);
                _btnRunUninstall.Enabled = !_selectedValues.Uninstall.IsPlaceholder;
                _btnNext.Text = "&Use these values";
                _tip.SetToolTip(_btnNext, "Fill in the uninstall command and File detection rule.");
            }
            UpdateButtons();
        }

        private void ShowAccessWarning(UninstallSnapshot snapshot)
        {
            if (snapshot.AccessErrors.Count == 0) return;
            _warning.Text = snapshot.AccessErrors.Count + " registry key(s) could not be read (access denied), for example:\n" +
                            snapshot.AccessErrors.First() +
                            "\nRestart Intune Wrapper as administrator (right-click > Run as administrator) for a complete capture.";
            _warning.Visible = true;
            Logger.Warn("Capture: " + snapshot.AccessErrors.Count + " keys not readable, e.g. " + snapshot.AccessErrors.First());
        }

        private void FillList()
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            if (_diff != null)
            {
                IEnumerable<Tuple<UninstallEntry, string>> rows = _diff.New.Select(e => Tuple.Create(e, "New"));
                if (_chkChanged.Checked) rows = rows.Concat(_diff.Changed.Select(e => Tuple.Create(e, "Changed")));
                UninstallEntry best = SandboxAgent.SelectBest(_diff, _productName);
                foreach (Tuple<UninstallEntry, string> row in rows)
                {
                    UninstallEntry e = row.Item1;
                    var item = new ListViewItem(new[]
                    {
                        (string.IsNullOrWhiteSpace(e.DisplayName) ? "(" + e.KeyName + ")" : e.DisplayName) + (e.SystemComponent ? " [hidden]" : ""),
                        e.DisplayVersion ?? "",
                        e.Publisher ?? "",
                        row.Item2,
                        e.KeyPath,
                    }) { Tag = e };
                    _list.Items.Add(item);
                    if (e == best) item.Selected = true;
                }
                if (_list.SelectedItems.Count == 0 && _list.Items.Count > 0) _list.Items[0].Selected = true;
            }
            _list.EndUpdate();

            if (_diff != null && _list.Items.Count == 0)
            {
                _preview.Text = _diff.Changed.Count > 0 && !_chkChanged.Checked
                    ? "No new entries, but " + _diff.Changed.Count + " changed entr(y/ies). Tick \"Also show changed entries\" to see them."
                    : "No new uninstall entries were found.\r\n\r\nWas the installation completely finished? Was the app already " +
                      "installed before step 1? Click Back to take the second snapshot again.";
            }
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            UninstallEntry e = ListSelection;
            if (e != null)
            {
                PackageCommands preview = Preview(e);
                bool found = SandboxAgent.DetectionPresent(preview.Detection);
                _preview.Text = "Uninstall: " + preview.Uninstall.Value + "   [" + preview.Uninstall.Confidence + "]\r\n" +
                                "Detection: " + preview.Detection.Summary(" | ") + "\r\n" +
                                "           " + (found ? "OK - the rule finds the installed app on this PC" :
                                    preview.Detection.IsComplete ? "NOT FOUND on this PC - check the rule" : "incomplete - fill in Path / File or folder later") + "\r\n" +
                                (string.IsNullOrWhiteSpace(e.InstallLocation) ? "" : "InstallLocation: " + e.InstallLocation + "\r\n") +
                                "UninstallString: " + (e.UninstallString ?? "(none)");
            }
            UpdateButtons();
        }

        private PackageCommands Preview(UninstallEntry e)
        {
            var preview = new PackageCommands();
            CommandGenerator.ApplyCapture(preview, e, _info.Framework);
            return preview;
        }

        private void UpdateButtons()
        {
            _btnNext.Enabled = !_busy && (_step != 3 || ListSelection != null);
            _btnBack.Enabled = !_busy;
            _btnLaunch.Enabled = !_busy;
            _btnRunUninstall.Enabled = !_busy && _selectedValues != null && !_selectedValues.Uninstall.IsPlaceholder;
            _btnVerify.Enabled = !_busy;
        }

        private async void Next()
        {
            if (_step == 1)
            {
                UninstallSnapshot snapshot = await Scan("Taking snapshot...");
                if (snapshot == null) return;
                if (!ConfirmNotInstalled(snapshot)) return;
                _before = snapshot;
                Logger.Info("Capture: first snapshot with " + snapshot.Entries.Count + " entries.");
                ShowStep(2);
            }
            else if (_step == 2)
            {
                UninstallSnapshot after = await Scan("Comparing...");
                if (after == null) return;
                _diff = UninstallRegistry.Compare(_before.Entries, after.Entries);
                Logger.Info("Capture: second snapshot with " + after.Entries.Count + " entries; " + _diff.New.Count + " new, " +
                            _diff.Changed.Count + " changed.");
                ShowStep(3);
                ShowAccessWarning(after);
            }
            else if (_step == 3)
            {
                if (ListSelection == null) return;
                _selected = ListSelection;
                _selectedValues = Preview(_selected);
                DetectionFoundAfterInstall = SandboxAgent.DetectionPresent(_selectedValues.Detection);
                EntryRemovedAfterUninstall = DetectionGoneAfterUninstall = UninstallCommandRan = false;
                _checkLog.Clear();
                Log("Selected: " + _selected.Label + "  (" + _selected.KeyPath + ")");
                Log("Uninstall command: " + _selectedValues.Uninstall.Value);
                Log("Detection: " + _selectedValues.Detection.Summary(" | "));
                Log((DetectionFoundAfterInstall ? "OK    " : "FAIL  ") + "Detection rule finds the installed app");
                ShowStep(4);
            }
            else
            {
                DialogResult = DialogResult.OK;
            }
        }

        /// <summary>Warns when the app already seems to be installed: the capture would then find nothing new.</summary>
        private bool ConfirmNotInstalled(UninstallSnapshot snapshot)
        {
            string product = MsiFileLocator.Normalize(_productName);
            if (product.Length < 3) return true;
            UninstallEntry existing = snapshot.Entries.FirstOrDefault(e => MsiFileLocator.Normalize(e.DisplayName).Contains(product));
            if (existing == null) return true;
            return Ui.Confirm(this,
                _productName + " seems to be installed on this PC already:\n\n" + existing.Label + "\n" + existing.KeyPath +
                "\n\nUninstall it first, otherwise the capture may find nothing new. Continue anyway?", "Already installed?");
        }

        private async void RunUninstall()
        {
            string command = _selectedValues.Uninstall.Value;
            bool elevate = _selected.Hive == "HKLM" && !UninstallRegistry.IsAdministrator();
            _busy = true;
            UpdateButtons();
            UseWaitCursor = true;
            Log("Running" + (elevate ? " (as administrator)" : "") + ": " + command);
            try
            {
                int? exit = await Task.Run(() => elevate ? RunElevated(command) : SandboxAgent.RunCommand(command, System.IO.Path.GetTempPath(), TimeSpan.FromMinutes(15)));
                UninstallCommandRan = exit == 0 || exit == 3010 || exit == 1641;
                Log(exit == null ? "FAIL  The uninstall command did not finish within 15 minutes (probably not silent)."
                    : (UninstallCommandRan ? "OK    " : "FAIL  ") + "Uninstall command exit code " + exit + ".");
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Log("Cancelled: administrator approval was declined.");
            }
            catch (Exception ex)
            {
                Log("FAIL  " + ex.Message);
            }
            finally
            {
                UseWaitCursor = false;
                _busy = false;
                UpdateButtons();
            }
            // Uninstallers often hand off to a copy of themselves and return early: give them a moment.
            await VerifyAsync(waitSeconds: 60);
        }

        private static int? RunElevated(string command)
        {
            var psi = new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /s /c \"" + command + "\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = System.IO.Path.GetTempPath(),
            };
            using (Process p = Process.Start(psi))
            {
                if (p == null) return null;
                return p.WaitForExit(15 * 60 * 1000) ? p.ExitCode : (int?)null;
            }
        }

        private async void Verify()
        {
            await VerifyAsync(waitSeconds: 0);
        }

        private async Task VerifyAsync(int waitSeconds)
        {
            _busy = true;
            UpdateButtons();
            UseWaitCursor = true;
            try
            {
                string key = _selected.KeyPath;
                DetectionRule rule = _selectedValues.Detection;
                for (int waited = 0; ; waited += 5)
                {
                    UninstallSnapshot now = await Task.Run(() => UninstallRegistry.TakeSnapshot());
                    EntryRemovedAfterUninstall = !now.Entries.Any(e => e.KeyPath == key);
                    DetectionGoneAfterUninstall = !SandboxAgent.DetectionPresent(rule);
                    if ((EntryRemovedAfterUninstall && DetectionGoneAfterUninstall) || waited >= waitSeconds) break;
                    await Task.Delay(5000);
                }
                Log((EntryRemovedAfterUninstall ? "OK    " : "FAIL  ") + "Uninstall entry " + (EntryRemovedAfterUninstall ? "removed" : "is still present"));
                Log((DetectionGoneAfterUninstall ? "OK    " : "FAIL  ") + "Detection file " + (DetectionGoneAfterUninstall ? "gone" : "is still present"));
                if (EntryRemovedAfterUninstall && DetectionGoneAfterUninstall)
                    Log("Uninstall verified. Click Use these values.");
            }
            finally
            {
                UseWaitCursor = false;
                _busy = false;
                UpdateButtons();
            }
        }

        private void Log(string line)
        {
            _checkLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + line + Environment.NewLine);
        }

        private async Task<UninstallSnapshot> Scan(string message)
        {
            _busy = true;
            UpdateButtons();
            string oldTitle = _title.Text;
            _title.Text = message;
            UseWaitCursor = true;
            try
            {
                return await Task.Run(() => UninstallRegistry.TakeSnapshot());
            }
            catch (Exception ex)
            {
                Logger.Error("Registry snapshot failed", ex);
                Ui.Error(this, "The registry could not be read: " + ex.Message +
                               "\n\nRestart Intune Wrapper as administrator and try again.");
                return null;
            }
            finally
            {
                UseWaitCursor = false;
                _title.Text = oldTitle;
                _busy = false;
                UpdateButtons();
            }
        }

        private void LaunchInstaller()
        {
            try
            {
                Process.Start(new ProcessStartInfo(_info.Path)
                {
                    UseShellExecute = true,
                    WorkingDirectory = _info.SourceFolder,
                });
            }
            catch (Exception ex)
            {
                Ui.Error(this, "The installer could not be started: " + ex.Message);
            }
        }
    }
}
