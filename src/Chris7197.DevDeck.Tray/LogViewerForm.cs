using chris7197.DevDeck.Core;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace chris7197.DevDeck.Tray;

/// <summary>Designer-editable log viewer; all UI controls and layout live in LogViewerForm.Designer.cs.</summary>
internal partial class LogViewerForm : Form
{
    private readonly List<DevDeckLogEntry> _allEntries = [];
    private readonly string _logPath;
    private const int MaxItems = 300;

    // Required by the Visual Studio WinForms designer.
    public LogViewerForm() : this(new DeckLog().FilePath) { }

    public LogViewerForm(string logPath)
    {
        _logPath = logPath;
        InitializeComponent();
    }

    /// <summary>
    /// Raised only after the user confirms deletion. The tray controller owns the reader,
    /// pending notifications and filesystem operation; the form never truncates by itself.
    /// </summary>
    public event EventHandler? ClearLogRequested;

    public void SetEntries(IEnumerable<DevDeckLogEntry> entries)
    {
        _allEntries.Clear();
        _allEntries.AddRange(entries.TakeLast(MaxItems));
        Rebuild();
    }

    public void AddEntries(IEnumerable<DevDeckLogEntry> entries)
    {
        _allEntries.AddRange(entries);
        if (_allEntries.Count > MaxItems) _allEntries.RemoveRange(0, _allEntries.Count - MaxItems);
        Rebuild();
    }

    public void ShowViewer(bool errorsOnly = false)
    {
        chkErrorsOnly.Checked = errorsOnly;
        // When the tray has enabled tracing, show it immediately on first opening.
        if (!Visible && new DeckLog(_logPath).TraceEnabled) chkShowTrace.Checked = true;
        if (!Visible) Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void Rebuild()
    {
        DevDeckLogEntry? selected = lvEntries.SelectedItems.Count > 0
            ? lvEntries.SelectedItems[0].Tag as DevDeckLogEntry : null;
        lvEntries.BeginUpdate();
        try
        {
            lvEntries.Items.Clear();
            foreach (DevDeckLogEntry entry in _allEntries.Where(x =>
                         (!chkErrorsOnly.Checked || x.Severity is DevDeckLogSeverity.Warning or DevDeckLogSeverity.Error) &&
                         (chkShowTrace.Checked || x.Severity != DevDeckLogSeverity.Trace)).Reverse())
            {
                var row = new ListViewItem(entry.Timestamp.ToLocalTime().ToString("dd MMM HH:mm:ss"));
                row.SubItems.Add(entry.Severity.ToString());
                row.SubItems.Add(entry.Summary.Replace('\r', ' ').Replace('\n', ' '));
                row.Tag = entry;
                row.ForeColor = entry.Severity switch
                {
                    DevDeckLogSeverity.Error => Color.FromArgb(255, 137, 148),
                    DevDeckLogSeverity.Warning => Color.FromArgb(255, 209, 133),
                    DevDeckLogSeverity.Trace => Color.FromArgb(150, 174, 213),
                    _ => Color.FromArgb(228, 232, 242)
                };
                lvEntries.Items.Add(row);
                if (ReferenceEquals(selected, entry)) row.Selected = true;
            }
            if (lvEntries.SelectedItems.Count == 0 && lvEntries.Items.Count > 0)
                lvEntries.Items[0].Selected = true;
            if (lvEntries.Items.Count == 0) txtDetails.Clear();
            lblSummary.Text = $"{_allEntries.Count} recent entries";
        }
        finally { lvEntries.EndUpdate(); }
    }

    private void chkErrorsOnly_CheckedChanged(object? sender, EventArgs e) => Rebuild();
    private void chkShowTrace_CheckedChanged(object? sender, EventArgs e) => Rebuild();

    private void lvEntries_SelectedIndexChanged(object? sender, EventArgs e) =>
        txtDetails.Text = lvEntries.SelectedItems.Count == 0 ? "" :
            ((DevDeckLogEntry)lvEntries.SelectedItems[0].Tag!).Details;

    private void lvEntries_DoubleClick(object? sender, EventArgs e)
    {
        if (lvEntries.SelectedItems.Count > 0) CopyDetails();
    }

    private void btnOpenLog_Click(object? sender, EventArgs e) => OpenLog();
    private void btnCopyDetails_Click(object? sender, EventArgs e) => CopyDetails();

    private void btnClearLog_Click(object? sender, EventArgs e)
    {
        if (MessageBox.Show(this,
                "Clear the entire DevDeck log file? This cannot be undone.\n\n" +
                "The tray will continue monitoring new entries afterwards.",
                "Clear DevDeck log", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        ClearLogRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CopyDetails()
    {
        if (txtDetails.Text.Length == 0) return;
        try { Clipboard.SetText(txtDetails.Text); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or
            System.Threading.ThreadStateException)
        {
            MessageBox.Show(this, "Couldn't access the clipboard. Try again. " + ex.Message,
                "DevDeck", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenLog()
    {
        if (!File.Exists(_logPath))
        {
            MessageBox.Show(this, "No DevDeck log file exists yet.", "DevDeck",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try { Process.Start(new ProcessStartInfo(_logPath) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cannot open log", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // Closing hides the viewer; the tray process keeps monitoring.
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }
}
