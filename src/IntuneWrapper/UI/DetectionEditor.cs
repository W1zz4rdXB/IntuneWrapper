using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using IntuneWrapper.Core;

namespace IntuneWrapper.UI
{
    /// <summary>
    /// Detection rule editor with the same fields, labels and options as the Intune "Detection rules"
    /// step: rule type MSI / File / Registry, then the fields for that type. Fields that Intune hides
    /// for the chosen detection method (e.g. Operator for "File or folder exists") are hidden here too.
    /// </summary>
    internal sealed class DetectionEditor : UserControl
    {
        private sealed class Item<T>
        {
            public Item(T value, string text) { Value = value; Text = text; }
            public T Value { get; }
            public string Text { get; }
            public override string ToString() => Text;
        }

        private sealed class Row
        {
            public Label Label;
            public Control Content;
            public Func<bool> IsVisible;
        }

        private readonly ToolTip _tip;
        private readonly Action<string, string> _copy;
        private readonly List<Row> _rows = new List<Row>();
        private readonly TableLayoutPanel _grid;
        private readonly Badge _badge = new Badge();

        private readonly ComboBox _type;
        private readonly TextBox _msiCode, _msiVersion;
        private readonly ComboBox _msiCheck, _msiOp;
        private readonly TextBox _filePath, _fileName, _fileValue;
        private readonly ComboBox _fileMethod, _fileOp, _file32;
        private readonly TextBox _regKey, _regValueName, _regValue;
        private readonly ComboBox _regMethod, _regOp, _reg32;

        private DetectionRule _rule;
        private bool _updating;

        public DetectionEditor(ToolTip tip, Action<string, string> copy)
        {
            _tip = tip;
            _copy = copy;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            _grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(_grid);

            // Team standard: detection is always a File rule, so the type is fixed.
            _type = Combo(new[] { DetectionRuleType.File }.Select(t => new Item<DetectionRuleType>(t, Portal.Label(t))), 160);
            _type.Enabled = false;
            var typeRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            typeRow.Controls.Add(_type);
            typeRow.Controls.Add(_badge);
            tip.SetToolTip(typeRow, "Detection always uses a File rule: in Intune choose Rule type File.");
            AddRow("Rule type *", typeRow, () => true);

            // MSI
            _msiCode = Text();
            AddRow("MSI product code *", WithCopy(_msiCode, "MSI product code"), IsType(DetectionRuleType.Msi));
            _msiCheck = YesNo();
            AddRow("MSI product version check", _msiCheck, IsType(DetectionRuleType.Msi));
            _msiOp = Operators();
            AddRow("Operator", _msiOp, () => IsType(DetectionRuleType.Msi)() && SelectedYes(_msiCheck));
            _msiVersion = Text();
            AddRow("Value", WithCopy(_msiVersion, "MSI product version"), () => IsType(DetectionRuleType.Msi)() && SelectedYes(_msiCheck));

            // File
            _filePath = Text();
            tip.SetToolTip(_filePath, "Folder that contains the file or folder to detect, e.g. %ProgramFiles%\\App. " +
                                      "Pasting a full file path splits it into Path and File or folder.");
            _filePath.Leave += (s, e) => SplitFullPath(_filePath);
            AddRow("Path *", WithCopy(_filePath, "Path", "Path"), IsType(DetectionRuleType.File));
            _fileName = Text();
            tip.SetToolTip(_fileName, "Name of the file or folder inside Path, e.g. app.exe. Name only, no folder.");
            _fileName.Leave += (s, e) => SplitFullPath(_fileName);
            Button browse = Ui.Button("Browse...", tip,
                "Pick the app's installed exe (e.g. on a test machine). Fills Path, File or folder and the version check.",
                (s, e) => BrowseInstalledFile());
            AddRow("File or folder *", WithCopy(_fileName, "File or folder", "File or folder", browse), IsType(DetectionRuleType.File));
            _fileMethod = Combo(Enum.GetValues(typeof(FileDetectionMethod)).Cast<FileDetectionMethod>()
                .Select(m => new Item<FileDetectionMethod>(m, Portal.Label(m))), 260);
            AddRow("Detection method *", _fileMethod, IsType(DetectionRuleType.File));
            _fileOp = Operators();
            AddRow("Operator *", _fileOp, () => IsType(DetectionRuleType.File)() && Portal.NeedsOperator(Selected<FileDetectionMethod>(_fileMethod)));
            _fileValue = Text();
            AddRow("Value *", WithCopy(_fileValue, "Value", "Value"), () => IsType(DetectionRuleType.File)() && Portal.NeedsOperator(Selected<FileDetectionMethod>(_fileMethod)));
            _file32 = YesNo();
            tip.SetToolTip(_file32, "Yes expands %ProgramFiles% to the 32-bit Program Files (x86) folder on 64-bit clients.");
            AddRow("Associated with a 32-bit app on 64-bit clients", _file32, IsType(DetectionRuleType.File));

            // Registry
            _regKey = Text();
            tip.SetToolTip(_regKey, "Full key path, starting with HKEY_LOCAL_MACHINE or HKEY_CURRENT_USER.");
            AddRow("Key path *", WithCopy(_regKey, "Key path"), IsType(DetectionRuleType.Registry));
            _regValueName = Text();
            AddRow("Value name", WithCopy(_regValueName, "Value name"),
                () => IsType(DetectionRuleType.Registry)() && Portal.NeedsValueName(Selected<RegistryDetectionMethod>(_regMethod)));
            _regMethod = Combo(Enum.GetValues(typeof(RegistryDetectionMethod)).Cast<RegistryDetectionMethod>()
                .Select(m => new Item<RegistryDetectionMethod>(m, Portal.Label(m))), 260);
            AddRow("Detection method *", _regMethod, IsType(DetectionRuleType.Registry));
            _regOp = Operators();
            AddRow("Operator *", _regOp, () => IsType(DetectionRuleType.Registry)() && Portal.NeedsOperator(Selected<RegistryDetectionMethod>(_regMethod)));
            _regValue = Text();
            AddRow("Value *", WithCopy(_regValue, "Value"), () => IsType(DetectionRuleType.Registry)() && Portal.NeedsOperator(Selected<RegistryDetectionMethod>(_regMethod)));
            _reg32 = YesNo();
            tip.SetToolTip(_reg32, "Yes reads the 32-bit registry view (WOW6432Node) on 64-bit clients.");
            AddRow("Associated with a 32-bit app on 64-bit clients", _reg32, IsType(DetectionRuleType.Registry));

            Rule = null;
        }

        public event EventHandler RuleChanged;

        /// <summary>The rule being edited (edited in place). Null clears and disables the editor.</summary>
        public DetectionRule Rule
        {
            get { return _rule; }
            set
            {
                _rule = value;
                _updating = true;
                try
                {
                    DetectionRule r = value ?? DetectionRule.Unknown();
                    Select(_type, r.Type);
                    _msiCode.Text = r.MsiProductCode ?? "";
                    Select(_msiCheck, r.MsiVersionCheck);
                    Select(_msiOp, r.MsiOperator);
                    _msiVersion.Text = r.MsiVersion ?? "";
                    _filePath.Text = r.FilePath ?? "";
                    _fileName.Text = r.FileOrFolder ?? "";
                    Select(_fileMethod, r.FileMethod);
                    Select(_fileOp, r.FileOperator);
                    _fileValue.Text = r.FileValue ?? "";
                    Select(_file32, r.File32Bit);
                    _regKey.Text = r.RegistryKeyPath ?? "";
                    _regValueName.Text = r.RegistryValueName ?? "";
                    Select(_regMethod, r.RegistryMethod);
                    Select(_regOp, r.RegistryOperator);
                    _regValue.Text = r.RegistryValue ?? "";
                    Select(_reg32, r.Registry32Bit);
                }
                finally
                {
                    _updating = false;
                }
                if (value == null) _badge.Set("", Color.Empty, Color.Empty);
                else _badge.SetConfidence(value.Confidence, _tip);
                UpdateVisibility();
                UpdateErrors();
            }
        }

        private void OnEdited(object sender, EventArgs e)
        {
            UpdateVisibility();
            if (_updating || _rule == null) return;
            _rule.Type = Selected<DetectionRuleType>(_type);
            _rule.MsiProductCode = _msiCode.Text.Trim();
            _rule.MsiVersionCheck = SelectedYes(_msiCheck);
            _rule.MsiOperator = Selected<ComparisonOperator>(_msiOp);
            _rule.MsiVersion = _msiVersion.Text.Trim();
            // Quotes around a pasted path are never valid in Intune; drop them.
            _rule.FilePath = _filePath.Text.Trim().Trim('"', '“', '”');
            _rule.FileOrFolder = _fileName.Text.Trim().Trim('"', '“', '”');
            _rule.FileMethod = Selected<FileDetectionMethod>(_fileMethod);
            _rule.FileOperator = Selected<ComparisonOperator>(_fileOp);
            _rule.FileValue = _fileValue.Text.Trim();
            _rule.File32Bit = SelectedYes(_file32);
            _rule.RegistryKeyPath = _regKey.Text.Trim();
            _rule.RegistryValueName = _regValueName.Text.Trim();
            _rule.RegistryMethod = Selected<RegistryDetectionMethod>(_regMethod);
            _rule.RegistryOperator = Selected<ComparisonOperator>(_regOp);
            _rule.RegistryValue = _regValue.Text.Trim();
            _rule.Registry32Bit = SelectedYes(_reg32);
            _rule.Confidence = _rule.IsComplete ? Confidence.Edited : Confidence.Unknown;
            _badge.SetConfidence(_rule.Confidence, _tip);
            UpdateErrors();
            RuleChanged?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateVisibility()
        {
            _grid.SuspendLayout();
            foreach (Row row in _rows)
            {
                bool visible = row.IsVisible();
                row.Label.Visible = visible;
                row.Content.Visible = visible;
            }
            _grid.ResumeLayout(true);
        }

        // ------------------------------------------------------------------ builders

        private void AddRow(string label, Control content, Func<bool> visible)
        {
            var l = new Label
            {
                Text = label,
                AutoSize = true,
                MaximumSize = new Size(230, 0),
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 7, 8, 3),
            };
            content.Margin = new Padding(3, 3, 3, 3);
            _grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _grid.Controls.Add(l, 0, _rows.Count);
            _grid.Controls.Add(content, 1, _rows.Count);
            _rows.Add(new Row { Label = l, Content = content, IsVisible = visible });
        }

        private Func<bool> IsType(DetectionRuleType type)
        {
            return () => Selected<DetectionRuleType>(_type) == type;
        }

        private TextBox Text()
        {
            var t = new TextBox { Dock = DockStyle.Fill, Font = Ui.MonoFont };
            t.TextChanged += OnEdited;
            return t;
        }

        /// <summary>
        /// Text box with Copy (and optional extra buttons). When <paramref name="field"/> is given, the
        /// field is validated like Intune does: the reason is shown in red below it and Copy is refused.
        /// </summary>
        private Control WithCopy(TextBox box, string what, string field = null, params Button[] extra)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            box.Margin = new Padding(0, 4, 3, 3);
            row.Controls.Add(box, 0, 0);
            Button copy = Ui.Button("Copy", _tip, "Copy " + what + " to the clipboard.", (s, e) =>
            {
                string error = field != null ? CurrentError(field) : null;
                if (error != null)
                {
                    Ui.Warn(FindForm(), what + " was not copied, because Intune would reject it:\n\n" + error, "Not ready for Intune");
                    return;
                }
                _copy(box.Text.Trim(), what);
            });
            copy.Margin = new Padding(3, 0, 0, 0);
            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            buttons.Controls.Add(copy);
            buttons.Controls.AddRange(extra);
            row.Controls.Add(buttons, 1, 0);

            if (field != null)
            {
                var error = new Label
                {
                    AutoSize = true,
                    ForeColor = Ui.ErrorText,
                    Font = Ui.SmallFont,
                    Margin = new Padding(0, 0, 3, 3),
                    Visible = false,
                };
                row.Controls.Add(error, 0, 1);
                row.SetColumnSpan(error, 2);
                _errorLabels[field] = error;
            }
            return row;
        }

        private readonly Dictionary<string, Label> _errorLabels = new Dictionary<string, Label>();

        private string CurrentError(string field)
        {
            if (_rule == null) return field + " is not set.";
            string error;
            return _rule.Problems().TryGetValue(field, out error) ? error : null;
        }

        /// <summary>Shows Intune's validation result under each File rule field.</summary>
        private void UpdateErrors()
        {
            Dictionary<string, string> problems = _rule != null ? _rule.Problems() : new Dictionary<string, string>();
            foreach (KeyValuePair<string, Label> kv in _errorLabels)
            {
                string error;
                bool show = _rule != null && problems.TryGetValue(kv.Key, out error);
                kv.Value.Text = show ? "✖ " + problems[kv.Key] : "";
                kv.Value.Visible = show;
            }
        }

        /// <summary>A full file path pasted into Path or File or folder is split into both fields.</summary>
        private void SplitFullPath(TextBox source)
        {
            if (_rule == null) return;
            string folder, name;
            if (!FileRuleValidator.TrySplitFullPath(source.Text, out folder, out name)) return;
            if (source == _filePath && !CommandGenerator.ContainsPlaceholder(_fileName.Text) &&
                !string.Equals(_fileName.Text.Trim(), name, StringComparison.OrdinalIgnoreCase))
                return; // Path is a folder whose last part has a dot, and a file name is already set
            _filePath.Text = Portal.ToIntunePath(folder);
            _fileName.Text = name;
        }

        /// <summary>Fills the File rule from an exe the user picks, e.g. the installed app on a test machine.</summary>
        private void BrowseInstalledFile()
        {
            if (_rule == null) return;
            using (var dlg = new OpenFileDialog
            {
                Title = "Pick the app's installed program file",
                Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
                CheckFileExists = true,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            })
            {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                string version = CommandGenerator.ReadFileVersion(dlg.FileName);
                _updating = true;
                try
                {
                    _filePath.Text = Portal.ToIntunePath(System.IO.Path.GetDirectoryName(dlg.FileName));
                    _fileName.Text = System.IO.Path.GetFileName(dlg.FileName);
                    if (version != null)
                    {
                        Select(_fileMethod, FileDetectionMethod.Version);
                        Select(_fileOp, ComparisonOperator.GreaterThanOrEqualTo);
                        _fileValue.Text = version;
                    }
                    else
                    {
                        Select(_fileMethod, FileDetectionMethod.Exists);
                    }
                    Select(_file32, false);
                }
                finally
                {
                    _updating = false;
                }
                OnEdited(this, EventArgs.Empty);
            }
        }

        private ComboBox Combo<T>(IEnumerable<Item<T>> items, int width)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width, Anchor = AnchorStyles.Left };
            foreach (Item<T> item in items) c.Items.Add(item);
            c.SelectedIndex = 0;
            c.SelectedIndexChanged += OnEdited;
            return c;
        }

        private ComboBox YesNo()
        {
            return Combo(new[] { new Item<bool>(true, "Yes"), new Item<bool>(false, "No") }, 90);
        }

        private ComboBox Operators()
        {
            return Combo(Enum.GetValues(typeof(ComparisonOperator)).Cast<ComparisonOperator>()
                .Select(o => new Item<ComparisonOperator>(o, Portal.Label(o))), 220);
        }

        private static T Selected<T>(ComboBox combo)
        {
            var item = combo.SelectedItem as Item<T>;
            return item != null ? item.Value : default(T);
        }

        private static bool SelectedYes(ComboBox combo) => Selected<bool>(combo);

        private static void Select<T>(ComboBox combo, T value)
        {
            for (int i = 0; i < combo.Items.Count; i++)
            {
                var item = combo.Items[i] as Item<T>;
                if (item != null && EqualityComparer<T>.Default.Equals(item.Value, value))
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
        }
    }
}
