using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using IntuneWrapper.Core;

namespace IntuneWrapper.UI
{
    internal sealed class MainForm : Form
    {
        private readonly AppPaths _paths;
        private readonly Settings _settings;
        private readonly HistoryStore _history;
        private readonly ToolTip _tip = new ToolTip { AutoPopDelay = 15000, InitialDelay = 400 };
        private readonly string[] _initialFiles;

        private InstallerInfo _info;
        private PackageCommands _commands;
        private bool _sourceMissing;
        private bool _loading;
        private bool _updating;
        private CancellationTokenSource _convertCts;
        private string _lastPackagePath;

        // Drop zone and summary
        private DropZone _drop;
        private Label _valFile, _valSize, _valProduct, _valVersion, _valPublisher;
        private Badge _typeBadge;

        // Intune portal fields, one tab per wizard step
        private sealed class FieldBinding
        {
            public TextBox Box;
            public Badge Badge;
            public Func<PackageCommands, CommandField> Get;
            public string Name;
            public bool Optional;

            public void ShowBadge(CommandField field, ToolTip tip)
            {
                if (Optional && string.IsNullOrWhiteSpace(field.Value))
                {
                    Badge.Set("Optional", Color.FromArgb(240, 240, 240), Ui.MutedText);
                    tip.SetToolTip(Badge, "Optional in Intune. Leave empty or fill in.");
                }
                else
                {
                    Badge.SetConfidence(field.Confidence, tip);
                }
            }
        }

        private readonly List<FieldBinding> _fields = new List<FieldBinding>();
        private Label _warning;
        private TabControl _tabs;
        private TabPage _tabContents;
        private ListView _lstItems;
        private ComboBox _cmbItemAction;
        private TextBox _txtItemCommand, _txtIntuneCommand;
        private Label _lblSetupItem, _lblScriptInfo, _lblIntuneCommand, _lblSetupCommand;
        private Button _btnAddFiles, _btnAddFolder, _btnRemoveItem;
        private Control _itemEditor;
        private TableLayoutPanel _rowIntuneCommand;
        private TabPage _tabApp, _tabProgram, _tabRequirements, _tabDetection;
        private TextBox _txtUninstall, _txtNotes;
        private Button _btnCapture, _btnAutoDetect, _btnAutoDetect2;
        private ToolStripMenuItem _miAutoDetect;
        private NumericUpDown _numInstallTime;
        private ComboBox _cmbAllowUninstall, _cmbContext, _cmbRestart, _cmbMinOs;
        private CheckBox _chkX86, _chkX64, _chkArm64;
        private Badge _bArchitecture;
        private DetectionEditor _detection;
        private readonly List<Control> _portalInputs = new List<Control>();
        private readonly List<Button> _copyButtons = new List<Button>();

        // Output, actions, result, log
        private TextBox _txtOutput;      // packaging folder
        private Label _lblAppFolders;
        private Button _btnOutputBrowse, _btnConvert, _btnCancel, _btnCopyAll, _btnExport, _btnOpenOutput, _btnHistory;
        private ProgressBar _progress;
        private TableLayoutPanel _resultPanel;
        private Label _lblResult;
        private CheckBox _chkLog;
        private TextBox _txtLog;

        // Status bar and menu
        private ToolStripStatusLabel _stState, _stTool, _stOutput;
        private ToolStripMenuItem _miCapture, _miExport;

        public MainForm(AppPaths paths, Settings settings, string settingsWarning, string[] initialFiles = null)
        {
            _initialFiles = initialFiles ?? new string[0];
            _paths = paths;
            _settings = settings;
            _history = new HistoryStore(paths);

            Ui.ApplyFormDefaults(this);
            Text = AppInfo.Name + " " + AppInfo.Version;
            MinimumSize = new Size(760, 640);
            // Tall enough for the Intune tabs without scrolling, but never taller than the screen.
            Size = new Size(980, Math.Max(MinimumSize.Height, Math.Min(980, Screen.PrimaryScreen.WorkingArea.Height - 20)));
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            AllowDrop = true;

            SuspendLayout();
            BuildMenu();
            BuildStatusBar();
            BuildBody();
            ResumeLayout(true);

            WireDragDrop(this);
            _txtOutput.Text = _settings.PackagingFolder;
            _chkLog.Checked = _settings.ShowLogByDefault;
            ToggleLog();
            ClearCommands();
            UpdateToolStatus();
            UpdateUiState();
            SetStatus(settingsWarning ?? "Ready. Drop an installer to begin.", settingsWarning != null);

            Logger.LineWritten += OnLoggerLine;
        }

        // ------------------------------------------------------------------ layout

        private void BuildMenu()
        {
            var menu = new MenuStrip { Font = Ui.BaseFont };

            var file = new ToolStripMenuItem("&File");
            file.DropDownItems.Add(new ToolStripMenuItem("&Open installer...", null, (s, e) => BrowseInstaller(), Keys.Control | Keys.O));
            _miExport = new ToolStripMenuItem("&Export commands to text file", null, (s, e) => ExportCommands(), Keys.Control | Keys.E);
            file.DropDownItems.Add(_miExport);
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(new ToolStripMenuItem("&Settings...", null, (s, e) => ShowSettings()));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, (s, e) => Close()));

            var tools = new ToolStripMenuItem("&Tools");
            _miCapture = new ToolStripMenuItem("&Capture uninstall...", null, (s, e) => StartCapture());
            _miAutoDetect = new ToolStripMenuItem("&Auto-detect in Windows Sandbox...", null, (s, e) => StartAutoDetect(), Keys.Control | Keys.D);
            tools.DropDownItems.Add(_miAutoDetect);
            tools.DropDownItems.Add(_miCapture);
            tools.DropDownItems.Add(new ToolStripSeparator());
            tools.DropDownItems.Add(new ToolStripMenuItem("&Locate IntuneWinAppUtil.exe...", null, (s, e) => LocateTool()));
            tools.DropDownItems.Add(new ToolStripMenuItem("&Download IntuneWinAppUtil.exe...", null, (s, e) => ShowToolSetup(true)));
            tools.DropDownItems.Add(new ToolStripMenuItem("Check for tool &update", null, (s, e) => CheckToolUpdate()));

            var view = new ToolStripMenuItem("&View");
            view.DropDownItems.Add(new ToolStripMenuItem("&History...", null, (s, e) => ShowHistory(), Keys.Control | Keys.H));
            view.DropDownItems.Add(new ToolStripMenuItem("Show &log pane", null, (s, e) => _chkLog.Checked = !_chkLog.Checked, Keys.Control | Keys.L));
            view.DropDownItems.Add(new ToolStripMenuItem("Open log &folder", null, (s, e) => Ui.OpenFolder(_paths.LogsDir)));

            var help = new ToolStripMenuItem("&Help");
            help.DropDownItems.Add(new ToolStripMenuItem("&About Intune Wrapper", null, (s, e) => ShowAbout(), Keys.F1));

            menu.Items.AddRange(new ToolStripItem[] { file, tools, view, help });
            MainMenuStrip = menu;
            Controls.Add(menu);
        }

        private void BuildStatusBar()
        {
            var status = new StatusStrip { Font = Ui.SmallFont, SizingGrip = true };
            _stState = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            _stTool = new ToolStripStatusLabel { BorderSides = ToolStripStatusLabelBorderSides.Left, IsLink = true };
            _stTool.Click += (s, e) => ShowToolSetup(false);
            _stOutput = new ToolStripStatusLabel { BorderSides = ToolStripStatusLabelBorderSides.Left, IsLink = true };
            _stOutput.Click += (s, e) => Ui.OpenFolder(_txtOutput.Text.Trim());
            status.Items.AddRange(new ToolStripItem[] { _stState, _stTool, _stOutput });
            status.ShowItemToolTips = true;
            _stTool.ToolTipText = "IntuneWinAppUtil.exe version. Click to locate or download the tool.";
            _stOutput.ToolTipText = "Output folder. Click to open it.";
            Controls.Add(status);
        }

        private void BuildBody()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                Padding = new Padding(10, 6, 10, 4),
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));   // drop zone
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // summary
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // Intune portal fields
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // output folder
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // actions
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // result
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // log

            _drop = new DropZone { Dock = DockStyle.Fill, Margin = new Padding(3, 3, 3, 6) };
            _drop.BrowseButton.Click += (s, e) => BrowseInstaller();
            _tip.SetToolTip(_drop.BrowseButton, "Choose an .exe or .msi installer (Ctrl+O).");
            _tip.SetToolTip(_drop, "Drag one .exe or .msi file here, or anywhere on this window.");
            root.Controls.Add(_drop);

            root.Controls.Add(BuildSummary());
            root.Controls.Add(BuildPortal());
            root.Controls.Add(BuildOutputRow());
            root.Controls.Add(BuildActions());
            root.Controls.Add(BuildResult());
            root.Controls.Add(BuildLog());

            Controls.Add(root);
            root.BringToFront();
        }

        private Control BuildSummary()
        {
            var group = new GroupBox { Text = "Installer", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8, 4, 8, 6) };
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, RowCount = 3 };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));

            Func<Label> value = () => new Label
            {
                AutoSize = false,
                AutoEllipsis = true,
                Dock = DockStyle.Fill,
                Height = 22,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = Ui.BoldFont,
            };
            _valFile = value();
            _valProduct = value();
            _valVersion = value();
            _valPublisher = value();
            _valSize = value();
            _typeBadge = new Badge();
            _tip.SetToolTip(_typeBadge, "Installer type and the framework detected from the file's contents.");

            t.Controls.Add(Ui.Label("File:"), 0, 0);
            t.Controls.Add(_valFile, 1, 0);
            t.Controls.Add(Ui.Label("Type:"), 2, 0);
            t.Controls.Add(_typeBadge, 3, 0);
            t.Controls.Add(Ui.Label("Product:"), 0, 1);
            t.Controls.Add(_valProduct, 1, 1);
            t.Controls.Add(Ui.Label("Version:"), 2, 1);
            t.Controls.Add(_valVersion, 3, 1);
            t.Controls.Add(Ui.Label("Publisher:"), 0, 2);
            t.Controls.Add(_valPublisher, 1, 2);
            t.Controls.Add(Ui.Label("Size:"), 2, 2);
            t.Controls.Add(_valSize, 3, 2);
            group.Controls.Add(t);
            return group;
        }

        /// <summary>
        /// The fields of the Intune "Add Win32 app" wizard, one tab per step, with the portal's labels.
        /// Required fields are marked with * as in the portal.
        /// </summary>
        private Control BuildPortal()
        {
            var group = new GroupBox { Text = "Intune app settings (Apps > Windows > Add > Windows app (Win32))", Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 8) };

            var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // warning
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // tabs
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));   // notes

            _warning = new Label
            {
                AutoSize = true,
                BackColor = Ui.WarningBack,
                ForeColor = Ui.WarningText,
                Padding = new Padding(6, 4, 6, 4),
                Margin = new Padding(3, 3, 3, 6),
                Visible = false,
            };
            outer.Resize += (s, e) => _warning.MaximumSize = new Size(Math.Max(200, outer.ClientSize.Width - 8), 0);
            outer.Controls.Add(_warning, 0, 0);

            _tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 4) };
            _tabApp = new TabPage("1. App information") { AutoScroll = true, Padding = new Padding(6), UseVisualStyleBackColor = true };
            _tabProgram = new TabPage("2. Program") { AutoScroll = true, Padding = new Padding(6), UseVisualStyleBackColor = true };
            _tabRequirements = new TabPage("3. Requirements") { AutoScroll = true, Padding = new Padding(6), UseVisualStyleBackColor = true };
            _tabDetection = new TabPage("4. Detection rules") { AutoScroll = true, Padding = new Padding(6), UseVisualStyleBackColor = true };
            _tabContents = new TabPage("Package contents") { AutoScroll = true, Padding = new Padding(6), UseVisualStyleBackColor = true };
            _tabs.TabPages.AddRange(new[] { _tabContents, _tabApp, _tabProgram, _tabRequirements, _tabDetection });
            BuildContentsTab();
            _tip.SetToolTip(_tabs, "Fill in the Intune wizard step by step with the values on these tabs.");
            outer.Controls.Add(_tabs, 0, 1);

            BuildAppTab();
            BuildProgramTab();
            BuildRequirementsTab();
            BuildDetectionTab();

            _txtNotes = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = SystemColors.Window,
                Font = Ui.SmallFont,
                Margin = new Padding(3, 6, 3, 0),
            };
            _tip.SetToolTip(_txtNotes, "Notes: how the values were found, confidence and warnings.");
            outer.Controls.Add(_txtNotes, 0, 2);

            group.Controls.Add(outer);
            return group;
        }

        private static TableLayoutPanel NewGrid(TabPage page)
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.Controls.Add(t);
            return t;
        }

        private static void AddRow(TableLayoutPanel grid, string label, Control content)
        {
            int row = grid.RowCount;
            grid.RowCount = row + 1;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var l = Ui.Label(label);
            l.Margin = new Padding(3, 7, 10, 3);
            l.MaximumSize = new Size(240, 0);
            if (content is TextBox && ((TextBox)content).Multiline) l.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            grid.Controls.Add(l, 0, row);
            grid.Controls.Add(content, 1, row);
        }

        /// <summary>A text field bound to a CommandField: text box (stretches), confidence badge, Copy and extra buttons.</summary>
        private Control BoundField(string name, Func<PackageCommands, CommandField> get, string tooltip, bool multiline,
            params Button[] extra)
        {
            var box = new TextBox { Dock = DockStyle.Fill, Font = Ui.MonoFont, Margin = new Padding(0, 4, 3, 3) };
            if (multiline)
            {
                box.Multiline = true;
                box.ScrollBars = ScrollBars.Vertical;
                box.Height = 52;
                box.Font = Ui.BaseFont;
            }
            _tip.SetToolTip(box, tooltip);
            var badge = new Badge();
            var binding = new FieldBinding { Box = box, Badge = badge, Get = get, Name = name };
            _fields.Add(binding);
            box.TextChanged += (s, e) => OnFieldEdited(binding);
            _portalInputs.Add(box);

            Button copy = Ui.Button("Copy", _tip, "Copy " + name + " to the clipboard.", (s, e) => CopyText(box.Text, name));
            _copyButtons.Add(copy);

            var row = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Margin = new Padding(0) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.Controls.Add(box, 0, 0);
            row.Controls.Add(badge, 1, 0);
            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            buttons.Controls.Add(copy);
            buttons.Controls.AddRange(extra);
            row.Controls.Add(buttons, 2, 0);
            return row;
        }

        private ComboBox NewCombo(int width, string tooltip, params object[] items)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width, Anchor = AnchorStyles.Left };
            c.Items.AddRange(items);
            _tip.SetToolTip(c, tooltip);
            _portalInputs.Add(c);
            return c;
        }

        private void BuildAppTab()
        {
            TableLayoutPanel t = NewGrid(_tabApp);
            AddRow(t, "Name *", BoundField("Name", c => c.AppName, "App name shown in Intune and Company Portal.", false));
            AddRow(t, "Description *", BoundField("Description", c => c.Description,
                "Description shown to users in Company Portal. Required by Intune.", true));
            AddRow(t, "Publisher *", BoundField("Publisher", c => c.Publisher, "Publisher (vendor) of the app. Required by Intune.", false));
            AddRow(t, "App version", BoundField("App version", c => c.AppVersion, "Optional version shown in Intune.", false));
            _fields.Last().Optional = true;
        }

        private void BuildProgramTab()
        {
            TableLayoutPanel t = NewGrid(_tabProgram);

            // Shown only when the package installs extra items: Intune then runs install.cmd.
            _txtIntuneCommand = new TextBox { Dock = DockStyle.Fill, Font = Ui.MonoFont, ReadOnly = true, Text = InstallScript.IntuneCommand, Margin = new Padding(0, 4, 3, 3) };
            Button copyIntune = Ui.Button("Copy", _tip, "Copy the Intune install command.", (s, e) => CopyText(_txtIntuneCommand.Text, "Install command"));
            _copyButtons.Add(copyIntune);
            _rowIntuneCommand = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Margin = new Padding(0) };
            _rowIntuneCommand.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _rowIntuneCommand.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            _rowIntuneCommand.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _rowIntuneCommand.Controls.Add(_txtIntuneCommand, 0, 0);
            var scriptBadge = new Badge();
            scriptBadge.Set("Generated script", Ui.InfoBack, Ui.InfoText);
            _tip.SetToolTip(scriptBadge, "install.cmd runs the setup, then the drivers and installers from the Package contents tab.");
            _rowIntuneCommand.Controls.Add(scriptBadge, 1, 0);
            _rowIntuneCommand.Controls.Add(copyIntune, 2, 0);
            AddRow(t, "Install command *", _rowIntuneCommand);
            _lblIntuneCommand = (Label)t.GetControlFromPosition(0, t.RowCount - 1);

            AddRow(t, "Install command *", BoundField("Install command", c => c.Install,
                "Silent install command. Runs from the package root, so use the file name only.", false));
            _lblSetupCommand = (Label)t.GetControlFromPosition(0, t.RowCount - 1);

            _btnCapture = Ui.Button("Ca&pture...", _tip,
                "Find the real uninstall command and detection rule by comparing the registry before and after installing the app on a test machine.",
                (s, e) => StartCapture());
            _btnAutoDetect = Ui.Button("A&uto-detect...", _tip,
                "Install, detect and uninstall the app automatically in Windows Sandbox, and fill in the verified uninstall command and File detection rule.",
                (s, e) => StartAutoDetect());
            Control uninstall = BoundField("Uninstall command", c => c.Uninstall,
                "Silent uninstall command. Never leave a placeholder here when uploading to Intune.", false, _btnAutoDetect, _btnCapture);
            _txtUninstall = _fields.Last().Box;
            _txtUninstall.Leave += (s, e) => EnsureUninstallNotEmpty();
            AddRow(t, "Uninstall command *", uninstall);

            _numInstallTime = new NumericUpDown { Minimum = 1, Maximum = 1440, Value = Portal.DefaultInstallTimeMinutes, Width = 90, Anchor = AnchorStyles.Left };
            _numInstallTime.ValueChanged += (s, e) => { if (!_updating && _commands != null) _commands.InstallTimeMinutes = (int)_numInstallTime.Value; };
            _tip.SetToolTip(_numInstallTime, "Maximum time Intune waits for the install to finish. The Intune default is 60.");
            _portalInputs.Add(_numInstallTime);
            AddRow(t, "Installation time required (mins) *", _numInstallTime);

            _cmbAllowUninstall = NewCombo(90, "Lets users uninstall the app from Company Portal when it is assigned as Available.", "Yes", "No");
            _cmbAllowUninstall.SelectedIndexChanged += (s, e) =>
            {
                if (!_updating && _commands != null) _commands.AllowAvailableUninstall = _cmbAllowUninstall.SelectedIndex == 0;
            };
            AddRow(t, "Allow available uninstall *", _cmbAllowUninstall);

            _cmbContext = NewCombo(110, "Install behavior: System (all users, runs as SYSTEM) or User (signed-in user).",
                InstallContext.System, InstallContext.User);
            _cmbContext.SelectedIndexChanged += (s, e) =>
            {
                if (_commands != null && !_updating && _cmbContext.SelectedItem != null)
                    _commands.InstallContext = (InstallContext)_cmbContext.SelectedItem;
            };
            AddRow(t, "Install behavior *", _cmbContext);

            _cmbRestart = NewCombo(340, "Device restart behavior in Intune. The commands already include /norestart where supported.");
            foreach (RestartBehavior r in Enum.GetValues(typeof(RestartBehavior))) _cmbRestart.Items.Add(new RestartItem(r));
            _cmbRestart.SelectedIndexChanged += (s, e) =>
            {
                if (_commands != null && !_updating && _cmbRestart.SelectedItem != null)
                    _commands.Restart = ((RestartItem)_cmbRestart.SelectedItem).Value;
            };
            AddRow(t, "Device restart behavior *", _cmbRestart);

            var codes = new Label
            {
                Text = Portal.ReturnCodes + "\nIntune fills these in by default. Keep them unless the vendor documents other codes.",
                AutoSize = true,
                ForeColor = Ui.MutedText,
                Margin = new Padding(3, 7, 3, 3),
            };
            AddRow(t, "Return codes", codes);
        }

        /// <summary>
        /// What goes into the package besides the setup: files and folders (e.g. Drivers) with an action each.
        /// Files can be dropped onto the list.
        /// </summary>
        private void BuildContentsTab()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // setup
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // buttons
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // list
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // item editor
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // script info

            _lblSetupItem = new Label { AutoSize = true, Margin = new Padding(3, 4, 3, 6) };
            t.Controls.Add(_lblSetupItem);

            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            _btnAddFiles = Ui.Button("Add &files...", _tip, "Add files to the package (e.g. a driver installer or a config file).", (s, e) => AddItemsFromDialog(false));
            _btnAddFolder = Ui.Button("Add f&older...", _tip, "Add a folder with everything in it (e.g. a Drivers folder with .inf files).", (s, e) => AddItemsFromDialog(true));
            _btnRemoveItem = Ui.Button("&Remove", _tip, "Remove the selected item from the package.", (s, e) => RemoveSelectedItem());
            buttons.Controls.AddRange(new Control[] { _btnAddFiles, _btnAddFolder, _btnRemoveItem });
            buttons.Controls.Add(new Label { Text = "or drop files and folders on the list", AutoSize = true, ForeColor = Ui.MutedText, Margin = new Padding(8, 9, 3, 3) });
            t.Controls.Add(buttons);

            _lstItems = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, MinimumSize = new Size(0, 80) };
            _lstItems.Columns.Add("Name", 200);
            _lstItems.Columns.Add("Type", 70);
            _lstItems.Columns.Add("Action", 200);
            _lstItems.Columns.Add("Command in install.cmd", 380);
            _lstItems.SelectedIndexChanged += (s, e) => ShowSelectedItem();
            _lstItems.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) RemoveSelectedItem(); };
            _tip.SetToolTip(_lstItems, "Additional files and folders in the package. Drop files or folders here to add them.");
            t.Controls.Add(_lstItems);

            var editor = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, Margin = new Padding(0, 4, 0, 0) };
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _cmbItemAction = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230, Anchor = AnchorStyles.Left };
            foreach (PackageItemAction a in Enum.GetValues(typeof(PackageItemAction))) _cmbItemAction.Items.Add(PackageItem.Label(a));
            _cmbItemAction.SelectedIndexChanged += (s, e) => OnItemEdited(actionChanged: true);
            _tip.SetToolTip(_cmbItemAction, "Include only: just copied. INF drivers: installed with pnputil. Installer: run silently after the setup.");
            _txtItemCommand = new TextBox { Dock = DockStyle.Fill, Font = Ui.MonoFont, Margin = new Padding(3, 4, 3, 3) };
            _txtItemCommand.TextChanged += (s, e) => OnItemEdited(actionChanged: false);
            _tip.SetToolTip(_txtItemCommand, "Command run by install.cmd from the package folder. Edit the silent switches if needed.");
            editor.Controls.Add(Ui.Label("Action:"), 0, 0);
            editor.Controls.Add(_cmbItemAction, 1, 0);
            editor.Controls.Add(_txtItemCommand, 2, 0);
            editor.Controls.Add(Ui.Button("Default", _tip, "Reset the command to the suggested one.", (s, e) =>
            {
                PackageItem item = SelectedItem;
                if (item == null) return;
                _txtItemCommand.Text = PackageItem.DefaultCommand(item) ?? "";
            }), 3, 0);
            _itemEditor = editor;
            t.Controls.Add(editor);

            _lblScriptInfo = new Label { AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
            t.Resize += (s, e) => _lblScriptInfo.MaximumSize = new Size(Math.Max(200, t.ClientSize.Width - 10), 0);
            t.Controls.Add(_lblScriptInfo);

            _tabContents.Controls.Add(t);

            // Drops on the list add items (elsewhere on the window a drop loads a new installer).
            _lstItems.AllowDrop = true;
            _lstItems.DragEnter += (s, e) => e.Effect = _commands != null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            _lstItems.DragOver += (s, e) => e.Effect = _commands != null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            _lstItems.DragDrop += (s, e) =>
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null) BeginInvoke(new Action(() => AddItems(files)));
            };
        }

        private PackageItem SelectedItem =>
            _lstItems.SelectedItems.Count == 1 ? (PackageItem)_lstItems.SelectedItems[0].Tag : null;

        private void AddItemsFromDialog(bool folder)
        {
            if (_commands == null) return;
            if (folder)
            {
                string path = Ui.PickFolder(this, "Choose a folder to add to the package (e.g. Drivers)", _info?.SourceFolder);
                if (path != null) AddItems(new[] { path });
            }
            else
            {
                using (var dlg = new OpenFileDialog { Title = "Add files to the package", Multiselect = true, CheckFileExists = true })
                {
                    if (_info != null && Directory.Exists(_info.SourceFolder)) dlg.InitialDirectory = _info.SourceFolder;
                    if (dlg.ShowDialog(this) == DialogResult.OK) AddItems(dlg.FileNames);
                }
            }
        }

        private void AddItems(IEnumerable<string> paths)
        {
            if (_commands == null || _info == null) return;
            var problems = new List<string>();
            PackageItem last = null;
            foreach (string raw in paths)
            {
                string path = raw.TrimEnd('\\');
                string name = Path.GetFileName(path);
                if (!File.Exists(path) && !Directory.Exists(path)) { problems.Add(name + ": not found"); continue; }
                if (string.Equals(Path.GetFullPath(path), _info.Path, StringComparison.OrdinalIgnoreCase)) { problems.Add(name + ": this is the setup itself"); continue; }
                if (string.Equals(name, _info.FileName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, InstallScript.FileName, StringComparison.OrdinalIgnoreCase) ||
                    _commands.Items.Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    problems.Add(name + ": an item with this name is already in the package");
                    continue;
                }
                last = PackageItem.Create(path);
                _commands.Items.Add(last);
                Logger.Info("Package item added: " + last.SourcePath + " (" + last.ActionLabel + ")");
            }
            RefreshContents(last);
            UpdateWarnings();
            if (problems.Count > 0) Ui.Warn(this, "Not added:\n\n" + string.Join("\n", problems));
            if (last != null)
            {
                _tabs.SelectedTab = _tabContents;
                SetStatus("Added to the package. " + (_commands.UsesInstallScript
                    ? "The Intune install command is now install.cmd."
                    : "These files are only included; the install command is unchanged."));
            }
        }

        private void RemoveSelectedItem()
        {
            PackageItem item = SelectedItem;
            if (item == null || _commands == null) return;
            _commands.Items.Remove(item);
            RefreshContents(null);
            UpdateWarnings();
            SetStatus(item.Name + " removed from the package. (Files already copied to the input folder stay there until you delete them.)");
        }

        private void ShowSelectedItem()
        {
            PackageItem item = SelectedItem;
            _updating = true;
            try
            {
                _itemEditor.Enabled = item != null && !IsBusy;
                _btnRemoveItem.Enabled = item != null && !IsBusy;
                _cmbItemAction.SelectedIndex = item != null ? (int)item.Action : -1;
                _txtItemCommand.Text = item?.Command ?? "";
                _txtItemCommand.Enabled = item != null && item.Action != PackageItemAction.Include;
            }
            finally
            {
                _updating = false;
            }
        }

        private void OnItemEdited(bool actionChanged)
        {
            PackageItem item = SelectedItem;
            if (_updating || item == null || _cmbItemAction.SelectedIndex < 0) return;
            var action = (PackageItemAction)_cmbItemAction.SelectedIndex;
            if (actionChanged)
            {
                if (action == PackageItemAction.RunInstaller && (item.IsFolder ||
                    !new[] { ".exe", ".msi" }.Contains(Path.GetExtension(item.Name).ToLowerInvariant())))
                {
                    Ui.Warn(this, "\"Run installer\" needs an .exe or .msi file. For a folder with an installer inside, add the " +
                                  "folder as \"Include only\" and add its setup file separately, or edit the command of another item.");
                    ShowSelectedItem();
                    return;
                }
                item.Action = action;
                item.Command = PackageItem.DefaultCommand(item);
            }
            else
            {
                // Typing: update only this row, so the text box keeps its cursor.
                item.Command = _txtItemCommand.Text.Trim();
                if (_lstItems.SelectedItems.Count == 1) _lstItems.SelectedItems[0].SubItems[3].Text = item.Command;
                return;
            }
            RefreshContents(item);
            UpdateWarnings();
        }

        /// <summary>Redraws the Package contents tab and the install command rows on the Program tab.</summary>
        private void RefreshContents(PackageItem select)
        {
            if (_lstItems == null) return;
            bool has = _commands != null;
            _lblSetupItem.Text = has
                ? "Setup (always included):  " + _info.FileName + "   -   run with:  " + _commands.Install.Value
                : "Load an installer first. Then add drivers, extra installers or other files here.";
            _lstItems.BeginUpdate();
            _lstItems.Items.Clear();
            if (has)
            {
                foreach (PackageItem item in _commands.Items)
                {
                    var row = new ListViewItem(new[]
                    {
                        item.Name,
                        item.IsFolder ? "Folder" : File.Exists(item.SourcePath) ? "File" : "MISSING",
                        item.ActionLabel,
                        item.Command ?? "",
                    }) { Tag = item };
                    _lstItems.Items.Add(row);
                    if (item == select) row.Selected = true;
                }
            }
            _lstItems.EndUpdate();
            ShowSelectedItem();

            bool script = has && _commands.UsesInstallScript;
            _lblScriptInfo.Text = !has ? "" : script
                ? "Intune install command: install.cmd. It is written to the input folder at Convert and runs the setup, then " +
                  "the drivers/installers above in order, from the package folder. Log: %ProgramData%\\IntuneWrapper\\Logs. " +
                  "Exit codes 0, 3010 and 1641 count as success; any other code stops the script and is reported to Intune."
                : "Intune install command: the setup command (no script needed). Items set to \"Include only\" are just copied into the package.";
            _lblScriptInfo.ForeColor = script ? Ui.InfoText : Ui.MutedText;

            // Program tab: with a script, Intune runs install.cmd and the setup command moves inside it.
            if (_rowIntuneCommand != null)
            {
                _rowIntuneCommand.Visible = _lblIntuneCommand.Visible = script;
                _lblSetupCommand.Text = script ? "Setup command (runs in install.cmd) *" : "Install command *";
            }
            _btnAddFiles.Enabled = _btnAddFolder.Enabled = has && !IsBusy;
            _tabContents.Text = "Package contents" + (has && _commands.Items.Count > 0 ? " (" + _commands.Items.Count + ")" : "");
        }

        private void BuildRequirementsTab()
        {
            TableLayoutPanel t = NewGrid(_tabRequirements);
            _chkX64 = new CheckBox { Text = "x64", AutoSize = true, Margin = new Padding(3, 6, 12, 3) };
            _chkX86 = new CheckBox { Text = "x86", AutoSize = true, Margin = new Padding(3, 6, 12, 3) };
            _chkArm64 = new CheckBox { Text = "ARM64", AutoSize = true, Margin = new Padding(3, 6, 12, 3) };
            _bArchitecture = new Badge();
            var arch = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            foreach (CheckBox c in new[] { _chkX64, _chkX86, _chkArm64 })
            {
                c.CheckedChanged += (s, e) => OnArchitectureEdited();
                _portalInputs.Add(c);
                arch.Controls.Add(c);
            }
            arch.Controls.Add(_bArchitecture);
            _tip.SetToolTip(arch, "Check operating system architecture: Yes, and tick the same architectures in Intune.");
            AddRow(t, "Operating system architecture *", arch);

            _cmbMinOs = NewCombo(200, "Minimum operating system, as listed in Intune.", Portal.MinimumOsOptions.Cast<object>().ToArray());
            _cmbMinOs.SelectedIndexChanged += (s, e) =>
            {
                if (!_updating && _commands != null && _cmbMinOs.SelectedItem != null) _commands.MinimumOs = (string)_cmbMinOs.SelectedItem;
            };
            AddRow(t, "Minimum operating system *", _cmbMinOs);

            var hint = new Label
            {
                Text = "Disk space, memory, processors and additional requirement rules are optional in Intune.",
                AutoSize = true,
                ForeColor = Ui.MutedText,
                Margin = new Padding(3, 10, 3, 3),
            };
            AddRow(t, "", hint);
        }

        private void BuildDetectionTab()
        {
            TableLayoutPanel t = NewGrid(_tabDetection);
            var format = new Label { Text = "Manually configure detection rules  >  + Add", AutoSize = true, Margin = new Padding(3, 7, 3, 3) };
            AddRow(t, "Rules format *", format);

            _btnAutoDetect2 = Ui.Button("Auto-detect in Windows Sandbox...", _tip,
                "Install the app in Windows Sandbox, find its main program file and check the rule, then uninstall to verify.",
                (s, e) => StartAutoDetect());
            AddRow(t, "", _btnAutoDetect2);

            _detection = new DetectionEditor(_tip, CopyText) { Dock = DockStyle.Top };
            _detection.RuleChanged += (s, e) => UpdateWarnings();
            _portalInputs.Add(_detection);
            var page = new Panel { Dock = DockStyle.Top, AutoSize = true };
            page.Controls.Add(_detection);
            _tabDetection.Controls.Add(page);
            page.BringToFront();
        }

        private Control BuildOutputRow()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Margin = new Padding(3, 6, 3, 0) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _txtOutput = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(3, 5, 3, 3) };
            _txtOutput.TextChanged += (s, e) =>
            {
                // Short form in the status bar so the status message keeps its room; full path in the tooltip.
                string full = _txtOutput.Text.Trim();
                _stOutput.Text = "Packaging: " + full;
                _stOutput.ToolTipText = "Packaging folder: " + full + ". Click to open it.";
                UpdateAppFolders();
            };
            _txtOutput.Leave += (s, e) =>
            {
                if (_txtOutput.Text.Trim().Length > 0 && _txtOutput.Text.Trim() != _settings.PackagingFolder)
                {
                    _settings.PackagingFolder = _txtOutput.Text.Trim();
                    SaveSettings();
                }
            };
            _tip.SetToolTip(_txtOutput,
                "Packaging folder. Holds IntuneWinAppUtil.exe and one folder per program, each with an input folder " +
                "(copy of the installer) and an output folder (.intunewin package).");
            _btnOutputBrowse = Ui.Button("Br&owse...", _tip, "Choose the packaging folder.", (s, e) => BrowseOutput());
            t.Controls.Add(Ui.Label("Packaging fol&der:"), 0, 0);
            t.Controls.Add(_txtOutput, 1, 0);
            t.Controls.Add(_btnOutputBrowse, 2, 0);

            _lblAppFolders = new Label { AutoSize = true, ForeColor = Ui.MutedText, Font = Ui.SmallFont, Margin = new Padding(3, 0, 3, 3) };
            _tip.SetToolTip(_lblAppFolders, "Folders used for this program. They are created when you convert.");
            t.Controls.Add(_lblAppFolders, 1, 1);
            return t;
        }

        private PackagingLayout Layout =>
            new PackagingLayout(_txtOutput.Text.Trim().Length > 0 ? _txtOutput.Text.Trim() : AppPaths.DefaultPackagingFolder);

        /// <summary>The program's folder name, or null while the Name is still missing.</summary>
        private string CurrentAppName =>
            _commands != null && !_commands.AppName.IsPlaceholder ? _commands.AppName.Value.Trim() : null;

        private void UpdateAppFolders()
        {
            if (_lblAppFolders == null) return;
            try
            {
                if (CurrentAppName == null)
                {
                    _lblAppFolders.Text = "Each program gets <packaging folder>\\<Name>\\input and \\output.";
                    return;
                }
                PackagingLayout layout = Layout;
                _lblAppFolders.Text = "This program:  " + layout.InputFolder(CurrentAppName) + "   >   " + layout.OutputFolder(CurrentAppName);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException
                                       || ex is PackagingException)
            {
                _lblAppFolders.Text = "The packaging folder path is not valid.";
            }
        }

        private Control BuildActions()
        {
            var row = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 4, 0, 0) };
            _btnConvert = Ui.Button("&Convert", _tip, "Create the .intunewin package with IntuneWinAppUtil.exe.", (s, e) => Convert());
            _btnConvert.Font = Ui.BoldFont;
            _btnConvert.MinimumSize = new Size(110, 34);
            _btnCancel = Ui.Button("Cancel", _tip, "Stop the running conversion.", (s, e) => _convertCts?.Cancel());
            _btnCopyAll = Ui.Button("Copy &all", _tip, "Copy all Intune settings, step by step, to the clipboard.", (s, e) => CopyAll());
            _btnExport = Ui.Button("&Export txt", _tip, "Save all Intune settings and notes as <name>-commands.txt in the program's output folder.", (s, e) => ExportCommands());
            _btnOpenOutput = Ui.Button("Open &folder", _tip, "Open this program's packaging folder (or the packaging folder) in Explorer.", (s, e) => OpenAppFolder());
            _btnHistory = Ui.Button("&History...", _tip, "Show earlier conversions and restore their settings (Ctrl+H).", (s, e) => ShowHistory());
            _progress = new ProgressBar { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 0, Width = 140, Height = 22, Margin = new Padding(12, 9, 3, 3), Visible = false };
            row.Controls.AddRange(new Control[] { _btnConvert, _btnCancel, _progress, _btnCopyAll, _btnExport, _btnOpenOutput, _btnHistory });
            return row;
        }

        private Control BuildResult()
        {
            _resultPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 3,
                BackColor = Ui.SuccessBack,
                Padding = new Padding(4),
                Margin = new Padding(3, 6, 3, 0),
                Visible = false,
            };
            _resultPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _resultPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _resultPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _lblResult = new Label { AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Ui.SuccessText, Margin = new Padding(3, 8, 3, 3) };
            var open = Ui.Button("Open folder", _tip, "Show the package in Explorer.", (s, e) =>
            {
                if (_lastPackagePath != null) Ui.OpenFolder(Path.GetDirectoryName(_lastPackagePath), _lastPackagePath);
            });
            var copy = Ui.Button("Copy path", _tip, "Copy the full path of the .intunewin file.", (s, e) =>
            {
                if (_lastPackagePath != null && Ui.SetClipboard(_lastPackagePath)) SetStatus("Package path copied.");
            });
            _resultPanel.Controls.Add(_lblResult, 0, 0);
            _resultPanel.Controls.Add(open, 1, 0);
            _resultPanel.Controls.Add(copy, 2, 0);
            return _resultPanel;
        }

        private Control BuildLog()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, Margin = new Padding(0, 4, 0, 0) };
            _chkLog = new CheckBox { Text = "Show lo&g", AutoSize = true, Margin = new Padding(3, 3, 3, 0) };
            _chkLog.CheckedChanged += (s, e) => ToggleLog();
            _tip.SetToolTip(_chkLog, "Show or hide the tool output and application log (Ctrl+L).");
            _txtLog = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font("Consolas", 9f),
                Height = 150,
                BackColor = SystemColors.Window,
            };
            t.Controls.Add(_chkLog);
            t.Controls.Add(_txtLog);
            return t;
        }

        private sealed class RestartItem
        {
            public RestartItem(RestartBehavior value) { Value = value; }
            public RestartBehavior Value { get; }
            public override string ToString() => CommandGenerator.RestartLabel(Value);
        }

        // ------------------------------------------------------------------ startup and closing

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // After DPI scaling: keep the window on the screen.
            Rectangle area = Screen.FromControl(this).WorkingArea;
            if (Height > area.Height) Height = area.Height;
            if (Width > area.Width) Width = area.Width;
            if (Top < area.Top || Bottom > area.Bottom) Top = area.Top;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Logger.Info("Tool path: " + (_settings.ToolPath ?? "(not set)") + ", version " +
                        (ToolManager.GetLocalVersion(_settings.ToolPath)?.ToString() ?? "n/a"));

            // FR-6: the tool lives in the packaging folder. An existing copy elsewhere is copied in silently.
            if (!BringToolIntoPackagingFolder())
            {
                ShowToolSetup(false);
            }
            UpdateToolStatus();
            _history.Load();
            if (_history.LastWarning != null) SetStatus(_history.LastWarning, true);
            // Command line: the first file is the installer, the rest are added to the package contents.
            if (_initialFiles.Length > 0) LoadInstaller(_initialFiles[0], _initialFiles.Skip(1).ToList(), askForExtras: false);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_convertCts != null)
            {
                if (!Ui.Confirm(this, "A conversion is running. Cancel it and close Intune Wrapper?"))
                {
                    e.Cancel = true;
                    return;
                }
                _convertCts.Cancel();
            }
            Logger.LineWritten -= OnLoggerLine;
            base.OnFormClosing(e);
        }

        // ------------------------------------------------------------------ drag and drop

        private void WireDragDrop(Control control)
        {
            // Text boxes keep their own text drag-and-drop; everything else accepts files.
            if (control is TextBoxBase || control is ComboBox || control is NumericUpDown) return;
            if (control == _lstItems) return; // drops there add package items (see BuildContentsTab)
            control.AllowDrop = true;
            control.DragEnter += OnDragEnter;
            control.DragOver += OnDragOver;
            control.DragLeave += (s, e) => _drop.SetState(DropState.Idle, null);
            control.DragDrop += OnDragDrop;
            foreach (Control child in control.Controls) WireDragDrop(child);
        }

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0)
            {
                e.Effect = DragDropEffects.None;
                return;
            }
            // Always accept file drops so an unsupported item gets an explanation instead of a "no" cursor.
            e.Effect = DragDropEffects.Copy;
            if (IsBusy)
            {
                _drop.SetState(DropState.Invalid, "Busy - wait for the current task to finish");
                return;
            }
            string error = InstallerInspector.Validate(files[0]);
            if (error == null)
            {
                _drop.SetState(DropState.Valid, files.Length > 1
                    ? "Release to load " + Path.GetFileName(files[0]) + " (only the first file is used)"
                    : "Release to load " + Path.GetFileName(files[0]));
            }
            else
            {
                _drop.SetState(DropState.Invalid, Directory.Exists(files[0]) ? "Folders are not supported" : "Only .exe and .msi files are supported");
            }
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            _drop.SetState(DropState.Idle, null);
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;
            // Let the drag source (Explorer) return before showing dialogs.
            BeginInvoke(new Action(() => LoadInstaller(files[0], files.Skip(1).ToList(), askForExtras: true)));
        }

        // ------------------------------------------------------------------ loading

        private bool IsBusy => _loading || _convertCts != null;

        private void BrowseInstaller()
        {
            if (IsBusy) return;
            using (var dlg = new OpenFileDialog
            {
                Title = "Choose an installer",
                Filter = "Installers (*.exe;*.msi)|*.exe;*.msi",
                CheckFileExists = true,
                Multiselect = false,
            })
            {
                if (_info != null && Directory.Exists(_info.SourceFolder)) dlg.InitialDirectory = _info.SourceFolder;
                if (dlg.ShowDialog(this) == DialogResult.OK) LoadInstaller(dlg.FileName, new List<string>(), askForExtras: false);
            }
        }

        /// <summary>
        /// Loads an installer. <paramref name="extras"/> (other dropped or command-line items) are added to the
        /// package contents: after asking when dropped, directly when given on the command line.
        /// </summary>
        private async void LoadInstaller(string path, IList<string> extras, bool askForExtras)
        {
            bool loaded = false;
            if (IsBusy)
            {
                SetStatus("Busy: wait for the current task to finish before loading another installer.", true);
                return;
            }
            string error = InstallerInspector.Validate(path);
            if (error != null)
            {
                Ui.Error(this, error, "Cannot load this item");
                SetStatus(error, true);
                return;
            }

            _loading = true;
            UpdateUiState();
            SetStatus("Analyzing " + Path.GetFileName(path) + "...");
            UseWaitCursor = true;
            try
            {
                InstallerInfo info = await Task.Run(() => InstallerInspector.Inspect(path));
                _info = info;
                _sourceMissing = false;
                _commands = CommandGenerator.Generate(info, _settings.DefaultInstallContext, _settings.DefaultMinimumOs);
                ShowInfo();
                ShowCommands();
                _resultPanel.Visible = false;
                _tabs.SelectedTab = _tabApp;

                Logger.Info("Loaded " + info.FileName + " (" + info.Size + " bytes, SHA-256 " + info.Sha256 + ", " + info.TypeLabel + ")");
                Logger.Info("Generated install: " + _commands.Install.Value + " [" + _commands.Install.Confidence + "]");
                Logger.Info("Generated uninstall: " + _commands.Uninstall.Value + " [" + _commands.Uninstall.Confidence + "]");
                Logger.Info("Generated detection: " + _commands.Detection.Summary(" | ") + " [" + _commands.Detection.Confidence + "]");

                string message = "Loaded " + info.FileName + ".";
                if (_commands.NeedsAttention) message += " Warning: some values need checking (see the yellow banner).";
                SetStatus(message, _commands.NeedsAttention);
                loaded = true;
            }
            catch (InstallerInputException ex)
            {
                Logger.Warn("Load failed: " + ex.Message);
                Ui.Error(this, ex.Message, "Cannot read installer");
                SetStatus("Could not load " + Path.GetFileName(path) + ".", true);
            }
            catch (Exception ex)
            {
                Logger.Error("Unexpected error analyzing " + path, ex);
                Ui.Error(this, "Something went wrong while analyzing the installer. Details are in the log.\n\n" + ex.Message);
                SetStatus("Could not load " + Path.GetFileName(path) + ".", true);
            }
            finally
            {
                UseWaitCursor = false;
                _loading = false;
                UpdateUiState();
            }

            if (loaded && extras != null && extras.Count > 0 &&
                (!askForExtras || Ui.Confirm(this, "You dropped " + extras.Count + " more item(s) with the installer:\n\n" +
                    string.Join("\n", extras.Take(8).Select(x => "   " + Path.GetFileName(x.TrimEnd('\\')))) +
                    (extras.Count > 8 ? "\n   ..." : "") +
                    "\n\nAdd them to this package (Package contents tab), e.g. as drivers or extra installers?", "Add to package?")))
            {
                AddItems(extras);
            }
        }

        private void ShowInfo()
        {
            if (_info == null)
            {
                _valFile.Text = _valProduct.Text = _valVersion.Text = _valPublisher.Text = _valSize.Text = "-";
                _typeBadge.Set("", Color.Empty, Color.Empty);
                return;
            }
            _valFile.Text = _info.FileName + (_sourceMissing ? "  (file not found)" : "");
            _tip.SetToolTip(_valFile, _info.Path);
            _valProduct.Text = string.IsNullOrWhiteSpace(_info.ProductName) ? Path.GetFileNameWithoutExtension(_info.Path) : _info.ProductName;
            _valVersion.Text = _info.ProductVersion ?? "-";
            _valPublisher.Text = _info.Publisher ?? "-";
            _valSize.Text = _info.Size > 0 ? InstallerInspector.FormatSize(_info.Size) : "-";

            if (_info.Type == InstallerType.Msi)
                _typeBadge.Set("MSI" + (_info.MsiTemplate != null ? " (" + _info.MsiTemplate.Split(';')[0] + ")" : ""), Ui.InfoBack, Ui.InfoText);
            else if (_info.Framework == InstallerFramework.Unknown)
                _typeBadge.Set(_info.TypeLabel + " (switches guessed)", Ui.WarningBack, Ui.WarningText);
            else
                _typeBadge.Set(_info.TypeLabel, Ui.SuccessBack, Ui.SuccessText);
        }

        private void ShowCommands()
        {
            _updating = true;
            try
            {
                foreach (FieldBinding f in _fields)
                {
                    CommandField field = f.Get(_commands);
                    f.Box.Text = field.Value;
                    f.ShowBadge(field, _tip);
                }
                _numInstallTime.Value = Math.Max(_numInstallTime.Minimum, Math.Min(_numInstallTime.Maximum, _commands.InstallTimeMinutes));
                _cmbAllowUninstall.SelectedIndex = _commands.AllowAvailableUninstall ? 0 : 1;
                _cmbContext.SelectedItem = _commands.InstallContext;
                _cmbRestart.SelectedIndex = (int)_commands.Restart;
                _chkX64.Checked = (_commands.Architecture & OsArchitecture.X64) != 0;
                _chkX86.Checked = (_commands.Architecture & OsArchitecture.X86) != 0;
                _chkArm64.Checked = (_commands.Architecture & OsArchitecture.Arm64) != 0;
                _bArchitecture.SetConfidence(_commands.ArchitectureConfidence, _tip);
                int os = Array.IndexOf(Portal.MinimumOsOptions, _commands.MinimumOs);
                _cmbMinOs.SelectedIndex = os >= 0 ? os : 0;
                _detection.Rule = _commands.Detection;
                _txtNotes.Text = string.Join(Environment.NewLine, _commands.Notes.Select(n => "- " + n));
            }
            finally
            {
                _updating = false;
            }
            RefreshContents(null);
            UpdateWarnings();
        }

        private void ClearCommands()
        {
            _updating = true;
            foreach (FieldBinding f in _fields)
            {
                f.Box.Text = "";
                f.Badge.Set("", Color.Empty, Color.Empty);
            }
            _txtNotes.Text = "";
            _numInstallTime.Value = Portal.DefaultInstallTimeMinutes;
            _cmbAllowUninstall.SelectedIndex = 0;
            _cmbContext.SelectedItem = _settings.DefaultInstallContext;
            _cmbRestart.SelectedIndex = 0;
            _chkX64.Checked = true;
            _chkX86.Checked = _chkArm64.Checked = false;
            _bArchitecture.Set("", Color.Empty, Color.Empty);
            int os = Array.IndexOf(Portal.MinimumOsOptions, _settings.DefaultMinimumOs);
            _cmbMinOs.SelectedIndex = os >= 0 ? os : 0;
            _detection.Rule = null;
            _updating = false;
            ShowInfo();
            RefreshContents(null);
            _warning.Visible = false;
        }

        // ------------------------------------------------------------------ editing

        private void OnFieldEdited(FieldBinding binding)
        {
            if (_updating || _commands == null) return;
            CommandField f = binding.Get(_commands);
            f.Value = binding.Box.Text;
            f.Confidence = CommandGenerator.ContainsPlaceholder(binding.Box.Text) ? Confidence.Unknown : Confidence.Edited;
            binding.ShowBadge(f, _tip);
            if (binding.Name == "Install command" && _lblSetupItem != null)
                _lblSetupItem.Text = "Setup (always included):  " + _info.FileName + "   -   run with:  " + f.Value;
            UpdateWarnings();
        }

        private void OnArchitectureEdited()
        {
            if (_updating || _commands == null) return;
            OsArchitecture arch = OsArchitecture.None;
            if (_chkX86.Checked) arch |= OsArchitecture.X86;
            if (_chkX64.Checked) arch |= OsArchitecture.X64;
            if (_chkArm64.Checked) arch |= OsArchitecture.Arm64;
            _commands.Architecture = arch;
            _commands.ArchitectureConfidence = arch == OsArchitecture.None ? Confidence.Unknown : Confidence.Edited;
            _bArchitecture.SetConfidence(_commands.ArchitectureConfidence, _tip);
            UpdateWarnings();
        }

        /// <summary>FR-16: the uninstall command is never left empty.</summary>
        private void EnsureUninstallNotEmpty()
        {
            if (_commands == null || !string.IsNullOrWhiteSpace(_txtUninstall.Text)) return;
            _commands.Uninstall = new CommandField(CommandGenerator.UninstallPlaceholder, Confidence.Unknown);
            ShowCommands();
        }

        private void UpdateWarnings()
        {
            if (_commands == null)
            {
                _warning.Visible = false;
                SetTabMarks(null);
                return;
            }
            UpdateAppFolders();
            List<string> missing = _commands.MissingRequired();
            var items = new List<string>();
            if (missing.Count > 0)
                items.Add("Still required for Intune: " + string.Join("; ", missing) + ".");
            if (_commands.Uninstall.IsPlaceholder)
                items.Add("Use Capture... to find the uninstall command and detection rule.");
            else if (_commands.Uninstall.Confidence == Confidence.Unknown)
                items.Add("The uninstall command has no known silent switch. Verify it.");
            if (_commands.Install.Confidence == Confidence.Unknown)
                items.Add("The install switches are a guess. Check the vendor documentation.");
            if (_sourceMissing)
                items.Add("The installer file was not found, so Convert is unavailable. Drop it again to convert.");

            _warning.Text = items.Count == 0 ? "" : "Warning: " + string.Join("  ", items);
            _warning.Visible = items.Count > 0;
            _tip.SetToolTip(_warning, _warning.Text);
            SetTabMarks(missing);
        }

        /// <summary>Marks wizard tabs that still have missing required fields with "(!)".</summary>
        private void SetTabMarks(List<string> missing)
        {
            var tabs = new[]
            {
                Tuple.Create(_tabApp, "1. App information", "App information:"),
                Tuple.Create(_tabProgram, "2. Program", "Program:"),
                Tuple.Create(_tabRequirements, "3. Requirements", "Requirements:"),
                Tuple.Create(_tabDetection, "4. Detection rules", "Detection rules:"),
            };
            foreach (var tab in tabs)
            {
                bool flagged = missing != null && missing.Any(m => m.StartsWith(tab.Item3, StringComparison.Ordinal));
                string text = tab.Item2 + (flagged ? " (!)" : "");
                if (tab.Item1.Text != text) tab.Item1.Text = text;
                tab.Item1.ToolTipText = flagged ? "Required fields are missing on this step." : "";
            }
            _tabs.ShowToolTips = true;
        }

        // ------------------------------------------------------------------ copy and export

        private void CopyText(string text, string what)
        {
            if (_commands == null) return;
            // Placeholders like <PATH: use Capture mode> must never end up in the Intune portal.
            if (CommandGenerator.ContainsPlaceholder(text))
            {
                string reason = string.IsNullOrWhiteSpace(text) ? " is empty." : " is still a placeholder.";
                SetStatus(what + " was not copied: it" + reason, true);
                Ui.Warn(this, what + " was not copied, because it" + reason + "\n\n" +
                              "Fill it in first (for EXE installers, Capture... finds the real values).", "Not ready for Intune");
                return;
            }
            if (Ui.SetClipboard(text))
                SetStatus(what + " copied.");
            else
                SetStatus("The clipboard is in use by another program. Try again.", true);
        }

        private string AppName => _commands != null && !_commands.AppName.IsPlaceholder
            ? (_commands.AppName.Value + (string.IsNullOrWhiteSpace(_commands.AppVersion.Value) ? "" : " " + _commands.AppVersion.Value)).Trim()
            : _info?.DisplayName ?? "";

        private void CopyAll()
        {
            if (_commands == null) return;
            List<string> missing = _commands.MissingRequired();
            if (missing.Count > 0 && !Ui.Confirm(this,
                    "These fields are not ready for Intune yet, and the portal would reject them:\n\n- " +
                    string.Join("\n- ", missing) + "\n\nCopy anyway? The text will be marked NOT READY.", "Not ready for Intune"))
            {
                SetStatus("Nothing copied. Fill in the missing fields first.", true);
                return;
            }
            string text = Summary.Format(AppName, Summary.PackageFileName(_info.FileName), _commands);
            if (missing.Count > 0)
                text = "NOT READY FOR INTUNE - fix first: " + string.Join("; ", missing) + "\r\n\r\n" + text;
            if (Ui.SetClipboard(text))
                SetStatus("All Intune settings copied." + (missing.Count > 0 ? " Warning: marked NOT READY." : ""), missing.Count > 0);
            else
                SetStatus("The clipboard is in use by another program. Try again.", true);
        }

        private void ExportCommands()
        {
            if (_commands == null) return;
            try
            {
                if (CurrentAppName == null) throw new PackagingException(
                    "Fill in the Name on the App information tab first; it is used as the program's folder name.");
                string folder = Layout.OutputFolder(CurrentAppName);
                Packager.EnsureOutputWritable(folder);
                string path = Path.Combine(folder, Summary.ExportFileName(AppName));
                if (File.Exists(path) && !Ui.Confirm(this, "This file already exists:\n" + path + "\n\nOverwrite it?"))
                {
                    return;
                }
                File.WriteAllText(path, Summary.FormatExport(AppName, Summary.PackageFileName(_info.FileName), _commands),
                    new System.Text.UTF8Encoding(true));
                SetStatus("Exported to " + path);
                Logger.Info("Exported commands to " + path);
            }
            catch (Exception ex) when (ex is PackagingException || ex is IOException || ex is UnauthorizedAccessException
                                       || ex is ArgumentException || ex is NotSupportedException)
            {
                Ui.Error(this, ex.Message, "Export failed");
            }
        }

        // ------------------------------------------------------------------ packaging folder

        private void BrowseOutput()
        {
            string folder = Ui.PickFolder(this,
                "Choose the packaging folder. It holds IntuneWinAppUtil.exe and one folder per program (input and output).",
                _txtOutput.Text.Trim());
            if (folder == null) return;
            _txtOutput.Text = folder;
            _settings.PackagingFolder = folder;
            SaveSettings();
            if (BringToolIntoPackagingFolder()) UpdateToolStatus();
        }

        private void OpenAppFolder()
        {
            try
            {
                string folder = CurrentAppName != null && Directory.Exists(Layout.AppFolder(CurrentAppName))
                    ? Layout.AppFolder(CurrentAppName)
                    : Layout.Root;
                Ui.OpenFolder(folder);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PackagingException)
            {
                Ui.Error(this, "The packaging folder path is not valid: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ conversion

        private async void Convert()
        {
            if (_info == null || IsBusy) return;
            if (_sourceMissing || !File.Exists(_info.Path))
            {
                Ui.Error(this, "The installer no longer exists:\n" + _info.Path + "\n\nDrop it again to convert.");
                return;
            }
            if (CurrentAppName == null)
            {
                _tabs.SelectedTab = _tabApp;
                Ui.Error(this, "Fill in the Name on the App information tab first. It is used as the program's folder name.");
                return;
            }
            if (!EnsureTool()) return;

            // <packaging>\<Name>\input gets a copy of the installer; the package goes to <packaging>\<Name>\output.
            PackagingLayout layout;
            string appName = CurrentAppName;
            try
            {
                layout = Layout;
                layout.AppFolder(appName);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException
                                       || ex is PackagingException)
            {
                Ui.Error(this, "The packaging folder or program name is not a valid path: " + ex.Message, "Cannot convert");
                return;
            }

            var request = new PackagingRequest
            {
                ToolPath = layout.ToolPath,
                SourceFolder = layout.InputFolder(appName),
                SetupFile = _info.FileName,
                OutputFolder = layout.OutputFolder(appName),
            };

            try
            {
                Packager.EnsureOutputWritable(request.OutputFolder);

                // FR-11: ask before overwriting; No keeps the old file.
                if (File.Exists(request.ExpectedOutputPath))
                {
                    if (!Ui.Confirm(this, "A package with this name already exists:\n" + request.ExpectedOutputPath +
                                          "\n\nOverwrite it?", "Overwrite package?"))
                    {
                        SetStatus("Conversion cancelled. The existing package was kept.");
                        return;
                    }
                    request.Overwrite = true;
                }

                // Setup, additional files/drivers and install.cmd into the input folder.
                if (!await PrepareInputFolderAsync(layout, appName, "Conversion")) return;

                Packager.Validate(request);
            }
            catch (PackagingException ex)
            {
                Ui.Error(this, ex.Message, "Cannot convert");
                return;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Logger.Error("Preparing the packaging folders failed", ex);
                Ui.Error(this, "The packaging folders could not be prepared:\n" + ex.Message, "Cannot convert");
                return;
            }

            _convertCts = new CancellationTokenSource();
            _resultPanel.Visible = false;
            _progress.Visible = true;
            _progress.MarqueeAnimationSpeed = 30;
            UpdateUiState();
            AppendLog("---- Converting " + _info.FileName + " ----");
            AppendLog("\"" + request.ToolPath + "\" " + Packager.BuildArguments(request));
            SetStatus("Converting " + _info.FileName + "...");

            PackagingResult result = null;
            try
            {
                result = await Packager.RunAsync(request, AppendLog, _convertCts.Token);
            }
            catch (PackagingException ex)
            {
                Ui.Error(this, ex.Message, "Cannot convert");
            }
            catch (Exception ex)
            {
                Logger.Error("Unexpected conversion error", ex);
                Ui.Error(this, "The conversion failed unexpectedly. Details are in the log.\n\n" + ex.Message);
            }
            finally
            {
                _convertCts.Dispose();
                _convertCts = null;
                _progress.MarqueeAnimationSpeed = 0;
                _progress.Visible = false;
                UpdateUiState();
            }

            if (result == null) return;
            if (!result.Cancelled) AddHistory(result);

            if (result.Success)
            {
                _lastPackagePath = result.OutputPath;
                _lblResult.Text = "Package created: " + result.OutputPath + "  (" + InstallerInspector.FormatSize(result.OutputSize) + ")";
                _resultPanel.Visible = true;
                AppendLog("Package created: " + result.OutputPath);
                SetStatus("Conversion succeeded." + (_commands.NeedsAttention ? " Warning: some Intune settings still need checking." : ""),
                    _commands.NeedsAttention);
            }
            else if (result.Cancelled)
            {
                AppendLog("Conversion cancelled.");
                SetStatus("Conversion cancelled." + (request.Overwrite ? " The previous package was restored." : ""));
            }
            else
            {
                AppendLog(result.Message);
                _chkLog.Checked = true;
                SetStatus("Conversion failed" + (result.ExitCode.HasValue ? " (exit code " + result.ExitCode + ")" : "") + ".", true);
                string tail = string.Join("\n", result.LastLogLines(20));
                Ui.Error(this, result.Message + (tail.Length > 0 ? "\n\nLast lines of the log:\n" + tail : ""), "Conversion failed");
            }
        }

        /// <summary>
        /// Fills &lt;packaging&gt;\&lt;Name&gt;\input with what goes into the package: the setup, the additional files and
        /// folders, and install.cmd when needed. Warns about other files there (FR-26). Returns false when the
        /// user cancelled. Throws PackagingException / IOException for the caller to report.
        /// </summary>
        private async Task<bool> PrepareInputFolderAsync(PackagingLayout layout, string appName, string what)
        {
            string input = layout.InputFolder(appName);
            InputState state = await Task.Run(() => layout.CheckInput(_info.Path, appName));
            bool replace = false;
            if (state == InputState.Different)
            {
                replace = Ui.Confirm(this, "The input folder already contains a different file with this name:\n" +
                                           Path.Combine(input, _info.FileName) +
                                           "\n\nReplace it with the installer you loaded?", "Replace installer?");
                if (!replace)
                {
                    SetStatus(what + " cancelled. The file in the input folder was kept.");
                    return false;
                }
            }

            SetStatus("Preparing " + input + "...");
            UseWaitCursor = true;
            _loading = true;
            UpdateUiState();
            try
            {
                string source = _info.Path;
                List<PackageItem> items = _commands.Items.ToList();
                string script = _commands.UsesInstallScript ? InstallScript.Build(appName, _commands.Install.Value, items) : null;
                await Task.Run(() =>
                {
                    layout.PrepareInput(source, appName, replace);
                    layout.PrepareItems(appName, items, script);
                });
                if (state == InputState.Missing || state == InputState.Different) AppendLog("Copied " + source + " to " + input);
                if (items.Count > 0) AppendLog("Copied " + items.Count + " additional item(s) to " + input);
                if (script != null) AppendLog("Wrote " + Path.Combine(input, InstallScript.FileName));
            }
            finally
            {
                _loading = false;
                UseWaitCursor = false;
                UpdateUiState();
            }

            // FR-26: everything in the input folder ends up in the package. Items on the Package contents tab are expected.
            HashSet<string> expected = PackagingLayout.ExpectedInputNames(_info.FileName, _commands.Items, _commands.UsesInstallScript);
            List<string> others = InstallerInspector.OtherEntriesInFolder(Path.Combine(input, _info.FileName))
                .Where(p => !expected.Contains(Path.GetFileName(p))).ToList();
            if (others.Count > 0)
            {
                string sample = string.Join("\n", others.Take(6).Select(p => "   " + Path.GetFileName(p)))
                                + (others.Count > 6 ? "\n   ... and " + (others.Count - 6) + " more" : "");
                int choice = ChoiceDialog.Show(this, "Other files will be packaged",
                    "The input folder contains " + others.Count + " item(s) that are not on the Package contents tab:\n\n" +
                    input + "\n" + sample +
                    "\n\nThey would be packaged too. Remove them (for example older installers), or add them on the " +
                    "Package contents tab if they belong to this package.",
                    new[] { "&Package all", "&Open folder", "Cancel" }, 2);
                if (choice == 1) Ui.OpenFolder(input);
                if (choice != 0)
                {
                    SetStatus(what + " not started.");
                    return false;
                }
            }
            return true;
        }

        private bool EnsureTool()
        {
            if (BringToolIntoPackagingFolder()) return true;
            ShowToolSetup(false);
            return ToolManager.IsPresent(Layout.ToolPath);
        }

        /// <summary>
        /// IntuneWinAppUtil.exe lives in the packaging folder. When it is not there yet, a copy found at the
        /// saved tool path or the old %APPDATA% location is copied in. Returns true when the tool is in place.
        /// </summary>
        private bool BringToolIntoPackagingFolder()
        {
            string target;
            try
            {
                target = Layout.ToolPath;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException)
            {
                return false;
            }

            if (File.Exists(target))
            {
                if (!string.Equals(_settings.ToolPath, target, StringComparison.OrdinalIgnoreCase))
                    SetToolPath(target, SameFile(target, _settings.ToolSha256) ? _settings.ToolReleaseTag : null);
                return true;
            }

            string source = new[] { _settings.ToolPath, _paths.LegacyToolPath }.FirstOrDefault(ToolManager.IsPresent);
            if (source == null) return false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(source, target, false);
                Logger.Info("Copied IntuneWinAppUtil.exe from " + source + " to " + target);
                SetToolPath(target, SameFile(target, _settings.ToolSha256) ? _settings.ToolReleaseTag : null);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Logger.Warn("Could not copy IntuneWinAppUtil.exe to " + target + ": " + ex.Message);
                return false;
            }
        }

        private static bool SameFile(string path, string sha256)
        {
            if (sha256 == null) return false;
            try
            {
                return string.Equals(InstallerInspector.ComputeSha256(path), sha256, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Copies a tool the user browsed to into the packaging folder and uses that copy.</summary>
        private void AdoptTool(string chosen, string releaseTag)
        {
            string target = Layout.ToolPath;
            try
            {
                if (!string.Equals(Path.GetFullPath(chosen), target, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(chosen, target, true);
                    Logger.Info("Copied IntuneWinAppUtil.exe from " + chosen + " to " + target);
                }
                SetToolPath(target, releaseTag);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Ui.Error(this, "IntuneWinAppUtil.exe could not be copied to the packaging folder:\n" + target + "\n\n" + ex.Message);
            }
        }

        // ------------------------------------------------------------------ history

        private void AddHistory(PackagingResult result)
        {
            try
            {
                _history.Add(HistoryEntry.Create(_info, _commands, result));
            }
            catch (Exception ex)
            {
                Logger.Warn("Could not save history: " + ex.Message);
            }
        }

        private void ShowHistory()
        {
            if (IsBusy) return;
            using (var form = new HistoryForm(_history))
            {
                if (form.ShowDialog(this) == DialogResult.OK && form.Selected != null)
                {
                    RestoreHistory(form.Selected);
                }
            }
        }

        private async void RestoreHistory(HistoryEntry entry)
        {
            InstallerInfo info = null;
            string note = "Restored from history (" + entry.Timestamp.ToString("yyyy-MM-dd HH:mm") + ").";
            if (!string.IsNullOrEmpty(entry.SourcePath) && File.Exists(entry.SourcePath))
            {
                _loading = true;
                UpdateUiState();
                try
                {
                    info = await Task.Run(() => InstallerInspector.Inspect(entry.SourcePath));
                    if (!string.Equals(info.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                        note += " WARNING: the installer file has changed since then (different SHA-256).";
                }
                catch (Exception ex)
                {
                    Logger.Warn("Could not re-read " + entry.SourcePath + ": " + ex.Message);
                    info = null;
                }
                finally
                {
                    _loading = false;
                }
            }

            _sourceMissing = info == null;
            if (info == null)
            {
                InstallerType type;
                InstallerFramework framework;
                info = new InstallerInfo
                {
                    Path = entry.SourcePath ?? entry.FileName,
                    Type = Enum.TryParse(entry.InstallerType, out type) ? type : InstallerType.Exe,
                    Framework = Enum.TryParse(entry.Framework, out framework) ? framework : InstallerFramework.Unknown,
                    ProductName = entry.ProductName,
                    ProductVersion = entry.ProductVersion,
                    Publisher = entry.Publisher,
                    Sha256 = entry.Sha256,
                };
            }

            _info = info;
            _commands = entry.ToCommands(_settings.DefaultInstallContext);
            _commands.Notes.Insert(0, note);
            if (entry.Notes != null) _commands.Notes.AddRange(entry.Notes.Where(n => !n.StartsWith("Restored from history")));

            _resultPanel.Visible = false;
            ShowInfo();
            ShowCommands();
            UpdateUiState();
            SetStatus(note + (_sourceMissing ? " The installer file was not found." : ""), _sourceMissing);
        }

        // ------------------------------------------------------------------ capture

        /// <summary>Install / capture / uninstall in Windows Sandbox, then fill in the verified values.</summary>
        private async void StartAutoDetect()
        {
            if (_info == null || _commands == null || IsBusy) return;
            if (CurrentAppName == null)
            {
                _tabs.SelectedTab = _tabApp;
                Ui.Error(this, "Fill in the Name on the App information tab first. It is used as the program's folder name.");
                return;
            }
            if (!SandboxLauncher.IsAvailable || SandboxLauncher.IsRunning || _commands.Install.IsPlaceholder)
            {
                // The dialog explains these cases (and offers to turn on Windows Sandbox).
                ShowAutoDetectDialog(null);
                return;
            }

            // The Sandbox tests exactly what will be packaged: the prepared input folder.
            string installerInInput;
            try
            {
                PackagingLayout layout = Layout;
                if (!await PrepareInputFolderAsync(layout, CurrentAppName, "Auto-detect")) return;
                installerInInput = Path.Combine(layout.InputFolder(CurrentAppName), _info.FileName);
            }
            catch (Exception ex) when (ex is PackagingException || ex is IOException || ex is UnauthorizedAccessException
                                       || ex is ArgumentException || ex is NotSupportedException)
            {
                Ui.Error(this, ex.Message, "Cannot prepare the package");
                return;
            }
            ShowAutoDetectDialog(installerInInput);
        }

        private void ShowAutoDetectDialog(string installerInInput)
        {
            using (var dlg = new AutoDetectDialog(_info, _commands, installerInInput))
            {
                DialogResult answer = dlg.ShowDialog(this);
                if (answer == DialogResult.Retry)
                {
                    // "Capture on this PC instead": the manual install/uninstall capture.
                    BeginInvoke(new Action(StartCapture));
                    return;
                }
                if (answer != DialogResult.OK || dlg.Result == null || dlg.Result.Selected == null) return;
                ApplySandboxResult(dlg.Result);
            }
        }

        private void ApplySandboxResult(SandboxResult r)
        {
            UninstallEntry entry = r.Selected;
            CommandGenerator.ApplyCapture(_commands, entry, _info.Framework);
            if (r.Detection != null && r.Detection.IsComplete) _commands.Detection = r.Detection.Clone();

            bool installOk = !r.InstallTimedOut && (r.InstallExitCode == 0 || r.InstallExitCode == 1707 || r.InstallExitCode == 3010 || r.InstallExitCode == 1641);
            bool uninstallOk = r.UninstallRan && !r.UninstallTimedOut && r.EntryRemovedAfterUninstall;
            bool detectionOk = r.DetectionFoundAfterInstall && r.DetectionGoneAfterUninstall && _commands.Detection.IsComplete;
            if (installOk && !_commands.Install.IsPlaceholder) _commands.Install.Confidence = Confidence.Detected;
            if (uninstallOk && !_commands.Uninstall.IsPlaceholder) _commands.Uninstall.Confidence = Confidence.Detected;
            _commands.Detection.Confidence = detectionOk ? Confidence.Detected : (_commands.Detection.IsComplete ? Confidence.Suggested : Confidence.Unknown);

            _commands.Notes.RemoveAll(n => n.StartsWith("Sandbox:", StringComparison.Ordinal));
            _commands.Notes.Insert(0, "Sandbox: verified in Windows Sandbox on " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") +
                ". Install " + (installOk ? "OK" : "FAILED") + ", detection " + (detectionOk ? "OK" : "NOT verified") +
                ", uninstall " + (uninstallOk ? "OK" : "NOT verified") + ".");
            Logger.Info("Sandbox values applied: uninstall " + _commands.Uninstall.Value + "; detection " + _commands.Detection.Summary(" | "));
            ShowCommands();
            _tabs.SelectedTab = _tabDetection;
            SetStatus(r.FullyVerified
                ? "Uninstall command and File detection rule verified in Windows Sandbox."
                : "Values filled in from Windows Sandbox, but not everything was verified (see Notes).", !r.FullyVerified);
        }

        private void StartCapture()
        {
            if (_info == null || _commands == null || IsBusy) return;
            using (var wizard = new CaptureWizard(_info, CurrentAppName))
            {
                if (wizard.ShowDialog(this) != DialogResult.OK || wizard.SelectedEntry == null) return;
                UninstallEntry entry = wizard.SelectedEntry;
                CommandGenerator.ApplyCapture(_commands, entry, _info.Framework);

                // What the user's own install/uninstall proved.
                bool uninstallOk = wizard.UninstallCommandRan && wizard.EntryRemovedAfterUninstall;
                bool detectionOk = wizard.DetectionFoundAfterInstall && wizard.DetectionGoneAfterUninstall && _commands.Detection.IsComplete;
                if (uninstallOk && !_commands.Uninstall.IsPlaceholder) _commands.Uninstall.Confidence = Confidence.Detected;
                if (detectionOk) _commands.Detection.Confidence = Confidence.Detected;
                _commands.Notes.RemoveAll(n => n.StartsWith("Capture on this PC:", StringComparison.Ordinal));
                _commands.Notes.Insert(0, "Capture on this PC (" + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "): detection " +
                    (detectionOk ? "verified (found after install, gone after uninstall)" : wizard.DetectionFoundAfterInstall ? "found after install, removal not verified" : "NOT verified") +
                    "; uninstall command " + (uninstallOk ? "verified" : wizard.EntryRemovedAfterUninstall ? "not tested (app removed another way)" : "NOT verified") + ".");
                if (_commands.Install.Confidence == Confidence.Unknown)
                    _commands.Notes.Insert(1, "WARNING: the app was installed by hand, so the silent install command is still unverified. " +
                                              "Check the vendor's silent switches.");

                Logger.Info("Capture applied from " + entry.KeyPath + ": uninstall " + _commands.Uninstall.Value +
                            "; detection " + _commands.Detection.Summary(" | ") + "; verified uninstall=" + uninstallOk + ", detection=" + detectionOk);
                ShowCommands();
                _tabs.SelectedTab = _tabDetection;
                SetStatus("Uninstall command and detection rule filled in from " + entry.Label +
                          (uninstallOk && detectionOk ? " (verified)." : "."), _commands.NeedsAttention);
            }
        }

        // ------------------------------------------------------------------ tool

        private void UpdateToolStatus()
        {
            if (ToolManager.IsPresent(_settings.ToolPath))
            {
                // Show the GitHub release tag when the file is still the one we downloaded; the
                // file version (e.g. 6.2509.50) does not match the release numbering.
                string tag = null;
                if (_settings.ToolReleaseTag != null && _settings.ToolSha256 != null)
                {
                    try
                    {
                        if (string.Equals(InstallerInspector.ComputeSha256(_settings.ToolPath), _settings.ToolSha256,
                                StringComparison.OrdinalIgnoreCase))
                            tag = _settings.ToolReleaseTag;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("Could not hash the tool: " + ex.Message);
                    }
                }
                Version v = ToolManager.GetLocalVersion(_settings.ToolPath);
                _stTool.Text = "Tool: " + (tag ?? (v != null ? "file version " + v : "found"));
                _stTool.ToolTipText = _settings.ToolPath + (v != null ? "\nFile version " + v : "") +
                                      (tag != null ? "\nRelease " + tag : "") + "\nClick to locate or download the tool.";
                _stTool.ForeColor = SystemColors.ControlText;
            }
            else
            {
                _stTool.Text = "Tool: not found (click to set up)";
                _stTool.ForeColor = Ui.ErrorText;
            }
        }

        private void ShowToolSetup(bool downloadNow)
        {
            using (var dlg = new ToolSetupDialog(Layout.ToolPath, _settings.ToolPath, null, downloadNow))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && dlg.ToolPath != null)
                {
                    AdoptTool(dlg.ToolPath, dlg.ReleaseTag);
                }
            }
            UpdateToolStatus();
            if (!ToolManager.IsPresent(_settings.ToolPath))
                SetStatus("IntuneWinAppUtil.exe is not set up. You can review commands, but Convert needs the tool.", true);
        }

        private void LocateTool()
        {
            string path = Ui.PickTool(this, _settings.ToolPath);
            if (path == null) return;
            AdoptTool(path, null);
            UpdateToolStatus();
            SetStatus("IntuneWinAppUtil.exe copied to " + Layout.ToolPath);
        }

        /// <summary>Saves the tool path; remembers the release tag and hash when we downloaded it.</summary>
        private void SetToolPath(string path, string releaseTag)
        {
            _settings.ToolPath = path;
            _settings.ToolReleaseTag = releaseTag;
            _settings.ToolSha256 = null;
            if (releaseTag != null)
            {
                try
                {
                    _settings.ToolSha256 = InstallerInspector.ComputeSha256(path);
                }
                catch (Exception ex)
                {
                    Logger.Warn("Could not hash the tool: " + ex.Message);
                }
            }
            SaveSettings();
            Logger.Info("Tool path set to " + path + (releaseTag != null ? " (release " + releaseTag + ")" : ""));
        }

        private async void CheckToolUpdate()
        {
            SetStatus("Checking GitHub for the latest IntuneWinAppUtil release...");
            UseWaitCursor = true;
            ToolUpdateCheck check;
            try
            {
                check = await ToolManager.CheckForUpdateAsync(_settings.ToolPath, Path.GetTempPath(), CancellationToken.None);
            }
            catch (ToolDownloadException ex)
            {
                Logger.Warn("Update check failed: " + ex.Message);
                SetStatus("Update check failed.", true);
                if (ChoiceDialog.Show(this, "Update check failed", ex.Message + "\n\nYou can also download the tool manually from:\n" +
                                                                   ToolManager.ReleasesUrl,
                        new[] { "Open &releases page", "Close" }, 1) == 0)
                    Ui.OpenUrl(ToolManager.ReleasesUrl);
                return;
            }
            catch (Exception ex)
            {
                Logger.Error("Update check failed", ex);
                SetStatus("Update check failed.", true);
                Ui.Error(this, "The update check failed: " + ex.Message);
                return;
            }
            finally
            {
                UseWaitCursor = false;
            }

            try
            {
                string tag = check.Release.Tag;
                if (!check.UpdateAvailable)
                {
                    if (_settings.ToolReleaseTag == null) SetToolPath(_settings.ToolPath, tag); // same file: now we know its tag
                    UpdateToolStatus();
                    SetStatus("IntuneWinAppUtil.exe is up to date (" + tag + ").");
                    Ui.Info(this, "Your IntuneWinAppUtil.exe is identical to the latest release (" + tag + ").");
                    return;
                }

                string question = ToolManager.IsPresent(_settings.ToolPath)
                    ? "The latest IntuneWinAppUtil.exe release is " + tag + ", and your copy is different:\n" + _settings.ToolPath +
                      "\n\nInstall " + tag + " to " + Layout.ToolPath + " and use it from now on?"
                    : "IntuneWinAppUtil.exe " + tag + " is available. Install it to " + Layout.ToolPath + "?";
                if (!Ui.Confirm(this, question, "Tool update")) return;
                ToolManager.Install(check.DownloadedPath, Layout.ToolPath);
                SetToolPath(Layout.ToolPath, tag);
                UpdateToolStatus();
                SetStatus("IntuneWinAppUtil.exe updated to " + tag + ".");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ToolDownloadException)
            {
                Logger.Error("Tool update failed", ex);
                Ui.Error(this, "The tool could not be updated: " + ex.Message);
            }
            finally
            {
                try
                {
                    if (File.Exists(check.DownloadedPath)) File.Delete(check.DownloadedPath);
                }
                catch
                {
                    // Ignore.
                }
            }
        }

        // ------------------------------------------------------------------ settings / about

        private void ShowSettings()
        {
            using (var form = new SettingsForm(_paths, _settings))
            {
                if (form.ShowDialog(this) != DialogResult.OK) return;
                SaveSettings();
                _txtOutput.Text = _settings.PackagingFolder;
                _chkLog.Checked = _settings.ShowLogByDefault;
                if (_commands == null) _cmbContext.SelectedItem = _settings.DefaultInstallContext;
                // A tool picked in Settings is copied into the packaging folder like any other.
                if (ToolManager.IsPresent(_settings.ToolPath) &&
                    !string.Equals(Path.GetFullPath(_settings.ToolPath), Layout.ToolPath, StringComparison.OrdinalIgnoreCase))
                    AdoptTool(_settings.ToolPath, null);
                else
                    BringToolIntoPackagingFolder();
                UpdateToolStatus();
                SetStatus("Settings saved.");
            }
        }

        private void SaveSettings()
        {
            try
            {
                SettingsStore.Save(_paths, _settings);
            }
            catch (Exception ex)
            {
                Logger.Error("Could not save settings", ex);
                SetStatus("Settings could not be saved: " + ex.Message, true);
            }
        }

        private void ShowAbout()
        {
            using (var about = new AboutDialog(_paths, _settings.ToolPath)) about.ShowDialog(this);
        }

        // ------------------------------------------------------------------ status and state

        private void UpdateUiState()
        {
            bool busy = IsBusy;
            bool converting = _convertCts != null;
            bool hasCommands = _commands != null;

            _drop.Enabled = !busy;
            _btnConvert.Enabled = !busy && _info != null && !_sourceMissing;
            _btnCancel.Enabled = converting;
            _btnCancel.Visible = converting;
            _btnCopyAll.Enabled = _btnExport.Enabled = _miExport.Enabled = hasCommands;
            foreach (Button b in _copyButtons) b.Enabled = hasCommands;
            _btnHistory.Enabled = !busy;
            _btnOutputBrowse.Enabled = _txtOutput.Enabled = !converting;

            // Capture (install/uninstall by hand on this PC) is the fallback for any installer type.
            bool canCapture = _info != null && hasCommands && !busy && !_sourceMissing;
            _btnCapture.Visible = _info != null;
            _btnCapture.Enabled = _miCapture.Enabled = canCapture;
            if (_btnAddFiles != null)
            {
                _btnAddFiles.Enabled = _btnAddFolder.Enabled = hasCommands && !busy;
                _lstItems.Enabled = hasCommands && !converting;
                _itemEditor.Enabled = _btnRemoveItem.Enabled = SelectedItem != null && !busy;
            }
            bool canAutoDetect = _info != null && hasCommands && !busy && !_sourceMissing;
            _btnAutoDetect.Enabled = _btnAutoDetect2.Enabled = _miAutoDetect.Enabled = canAutoDetect;

            foreach (Control c in _portalInputs)
            {
                c.Enabled = hasCommands && !converting;
            }
        }

        private void SetStatus(string text, bool warning = false)
        {
            _stState.Text = text;
            _stState.ForeColor = warning ? Ui.WarningText : SystemColors.ControlText;
            _stState.ToolTipText = text;
        }

        private void OnLoggerLine(string line)
        {
            AppendLog("[app] " + line);
        }

        private void AppendLog(string line)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action<string>(AppendLog), line);
                }
                catch (InvalidOperationException)
                {
                    // Form is closing.
                }
                return;
            }
            if (_txtLog.TextLength > 500000) _txtLog.Clear();
            _txtLog.AppendText(line + Environment.NewLine);
        }

        private void ToggleLog()
        {
            _txtLog.Visible = _chkLog.Checked;
        }
    }
}
