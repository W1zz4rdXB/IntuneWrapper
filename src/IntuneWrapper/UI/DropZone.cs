using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace IntuneWrapper.UI
{
    internal enum DropState
    {
        Idle,
        Valid,
        Invalid
    }

    /// <summary>Large dashed panel that changes colour while a file is dragged over the window (SPEC 6.1).</summary>
    internal sealed class DropZone : Panel
    {
        private DropState _state;

        public DropZone()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
            Padding = new Padding(16, 12, 16, 12);

            Prompt = new Label
            {
                Text = "Drop an installer here (.exe or .msi)",
                Font = Ui.HeadingFont,
                AutoSize = true,
                Anchor = AnchorStyles.None,
                BackColor = Color.Transparent,
            };
            Hint = new Label
            {
                Text = "or",
                AutoSize = true,
                Anchor = AnchorStyles.None,
                ForeColor = Ui.MutedText,
                BackColor = Color.Transparent,
                Margin = new Padding(12, 8, 6, 3),
            };
            BrowseButton = new Button
            {
                Text = "&Browse...",
                AutoSize = true,
                MinimumSize = new Size(96, 30),
                Anchor = AnchorStyles.None,
                UseVisualStyleBackColor = true,
            };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 5,
                RowCount = 1,
                BackColor = Color.Transparent,
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.Controls.Add(Prompt, 1, 0);
            layout.Controls.Add(Hint, 2, 0);
            layout.Controls.Add(BrowseButton, 3, 0);
            Controls.Add(layout);
            SetState(DropState.Idle, null);
        }

        public Label Prompt { get; }
        public Label Hint { get; }
        public Button BrowseButton { get; }

        public void SetState(DropState state, string message)
        {
            _state = state;
            switch (state)
            {
                case DropState.Valid:
                    BackColor = Ui.SuccessBack;
                    Prompt.Text = message ?? "Release to load the installer";
                    break;
                case DropState.Invalid:
                    BackColor = Ui.ErrorBack;
                    Prompt.Text = message ?? "This file type is not supported";
                    break;
                default:
                    BackColor = Color.FromArgb(247, 249, 252);
                    Prompt.Text = "Drop an installer here (.exe or .msi)";
                    break;
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Color color = _state == DropState.Valid ? Ui.SuccessText : _state == DropState.Invalid ? Ui.ErrorText : Ui.Accent;
            using (var pen = new Pen(color, 2f) { DashStyle = DashStyle.Dash })
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
            }
        }
    }
}
