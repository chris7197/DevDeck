using System.Drawing.Drawing2D;
using chris7197.DevDeck.Core;

namespace chris7197.DevDeck.Overlay;

/// <summary>Compact, borderless, keyboard-first Windows picker.</summary>
internal sealed class PromptForm : Form
{
    private readonly DevDeckPromptRequest _request;
    private readonly Dictionary<Keys, string> _keys = new();
    public string? SelectedChoiceId { get; private set; }

    public PromptForm(DevDeckPromptRequest request)
    {
        _request = request;
        Text = request.Title;
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Color.FromArgb(24, 29, 40);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        Padding = new Padding(20);
        Width = 480;
        Rectangle workArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        Height = Math.Min(168 + request.Choices.Count * 62, workArea.Height - 40);
        MinimumSize = new Size(400, Math.Min(240, workArea.Height - 40));

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = BackColor
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = BackColor,
            ColumnCount = 2, RowCount = 1 };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
        var close = new Button
        {
            Text = "×", Dock = DockStyle.Right, Width = 34,
            BackColor = BackColor, ForeColor = Color.FromArgb(176, 191, 213),
            FlatStyle = FlatStyle.Flat, TabStop = false,
            Font = new Font("Segoe UI", 15), Cursor = Cursors.Hand
        };
        close.FlatAppearance.BorderSize = 0;
        close.Click += (_, _) => Close();
        header.Controls.Add(new Label
        {
            Text = request.Title, Dock = DockStyle.Fill, AutoEllipsis = true,
            Font = new Font("Segoe UI Semibold", 15, FontStyle.Bold),
            ForeColor = Color.White, TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        header.Controls.Add(close, 1, 0);
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = request.Description, Dock = DockStyle.Fill,
            AutoEllipsis = true, ForeColor = Color.FromArgb(170, 186, 206),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 1);

        // A list can contain nine numbered solutions plus Next/Previous navigation.
        // Keep its height bounded on smaller screens; never hide the last choices.
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = BackColor };
        var choices = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1,
            RowCount = request.Choices.Count + 1, BackColor = BackColor };
        choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < request.Choices.Count; i++)
        {
            choices.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            var choice = request.Choices[i];
            _keys[(Keys)char.ToUpperInvariant(choice.Shortcut)] = choice.Id;
            if (choice.Shortcut is >= '1' and <= '9')
                _keys[(Keys)((int)Keys.NumPad1 + (choice.Shortcut - '1'))] = choice.Id;
            var button = new Button
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 3, 0, 4),
                Text = $"  {char.ToUpperInvariant(choice.Shortcut)}    {choice.Label}" +
                    (string.IsNullOrWhiteSpace(choice.Description) ? "" : $"    ·    {choice.Description}"),
                TextAlign = ContentAlignment.MiddleLeft,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 49, 67),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 11, FontStyle.Regular),
                Cursor = Cursors.Hand,
                TabStop = true
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(78, 104, 143);
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(54, 77, 109);
            button.Click += (_, _) => Choose(choice.Id);
            choices.Controls.Add(button, 0, i);
        }
        choices.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        choices.Controls.Add(new Label
        {
            Text = "Choose by mouse or key  •  Esc to cancel", Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(149, 165, 185), TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 8.5f)
        }, 0, request.Choices.Count);
        scroll.Controls.Add(choices);
        layout.Controls.Add(scroll, 0, 2);
        Controls.Add(layout);

        // Centre on whichever screen currently contains the mouse pointer.
        Location = new Point(workArea.Left + (workArea.Width - Width) / 2,
            workArea.Top + (workArea.Height - Height) / 2);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        BringToFront();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;
        if (key == Keys.Escape) { Close(); return true; }
        if ((keyData & Keys.Modifiers) == Keys.None && _keys.TryGetValue(key, out var id))
        { Choose(id); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void Choose(string id) { SelectedChoiceId = id; Close(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.FromArgb(81, 111, 153), 1f);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}
