using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using IntuneWrapper.Core;

namespace IntuneWrapper.UI
{
    /// <summary>FR-30: list earlier conversions, restore their commands, delete entries.</summary>
    internal sealed class HistoryForm : Form
    {
        private readonly HistoryStore _store;
        private readonly ListView _list;
        private readonly TextBox _details;
        private readonly Button _btnRestore, _btnDelete;
        private List<HistoryEntry> _entries = new List<HistoryEntry>();

        public HistoryForm(HistoryStore store)
        {
            _store = store;
            Ui.ApplyFormDefaults(this);
            Text = "Conversion history";
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MinimumSize = new Size(700, 480);
            Size = new Size(860, 600);
            var tip = new ToolTip();

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12) };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
            _list.Columns.Add("Date", 140);
            _list.Columns.Add("File", 230);
            _list.Columns.Add("Product", 230);
            _list.Columns.Add("Result", 90);
            _list.SelectedIndexChanged += (s, e) => ShowDetails();
            _list.DoubleClick += (s, e) => Restore();
            _list.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Delete) DeleteSelected();
                if (e.KeyCode == Keys.Enter) Restore();
            };

            _details = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = Ui.MonoFont };

            _btnRestore = Ui.Button("&Restore", tip, "Load the selected entry's commands into the main window.", (s, e) => Restore());
            _btnDelete = Ui.Button("&Delete", tip, "Remove the selected entry from the history.", (s, e) => DeleteSelected());
            var close = Ui.Button("Close", tip, "Close the history.", (s, e) => Close());
            CancelButton = close;
            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
            buttons.Controls.AddRange(new Control[] { close, _btnDelete, _btnRestore });

            layout.Controls.Add(_list);
            layout.Controls.Add(_details);
            layout.Controls.Add(buttons);
            Controls.Add(layout);
            Reload();
        }

        public HistoryEntry Selected => _list.SelectedItems.Count == 1 ? (HistoryEntry)_list.SelectedItems[0].Tag : null;

        private void Reload()
        {
            _entries = _store.Load();
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (HistoryEntry e in _entries)
            {
                string product = string.Join(" ", new[] { e.ProductName, e.ProductVersion }.Where(x => !string.IsNullOrWhiteSpace(x)));
                _list.Items.Add(new ListViewItem(new[]
                {
                    e.Timestamp.ToString("yyyy-MM-dd HH:mm"),
                    e.FileName ?? "",
                    product,
                    e.Success ? "Succeeded" : "Failed",
                }) { Tag = e });
            }
            _list.EndUpdate();
            if (_list.Items.Count > 0) _list.Items[0].Selected = true;
            if (_store.LastWarning != null) _details.Text = _store.LastWarning;
            else if (_entries.Count == 0) _details.Text = "No conversions yet. Converted packages appear here (last " + HistoryStore.MaxEntries + ").";
            ShowDetails();
        }

        private void ShowDetails()
        {
            HistoryEntry e = Selected;
            _btnRestore.Enabled = _btnDelete.Enabled = e != null;
            if (e == null) return;
            _details.Text = string.Join("\r\n", new[]
            {
                "File:       " + e.FileName,
                "Source:     " + e.SourcePath,
                "SHA-256:    " + e.Sha256,
                "Output:     " + (e.OutputPath ?? "(none)"),
                "Install:    " + e.Install,
                "Uninstall:  " + e.Uninstall,
                "Detection:  " + e.Detection,
                "Context:    " + e.InstallContext + "    Restart: " + e.Restart,
            });
        }

        private void Restore()
        {
            if (Selected == null) return;
            DialogResult = DialogResult.OK;
        }

        private void DeleteSelected()
        {
            HistoryEntry e = Selected;
            if (e == null) return;
            if (!Ui.Confirm(this, "Delete the history entry for " + e.FileName + " (" + e.Timestamp.ToString("yyyy-MM-dd HH:mm") + ")?")) return;
            try
            {
                _store.Delete(e.Id);
            }
            catch (Exception ex)
            {
                Ui.Error(this, "The history could not be saved: " + ex.Message);
            }
            Reload();
        }
    }
}
