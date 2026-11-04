using chris7197.DevDeck.Core;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace chris7197.DevDeck.Tray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly DevDeckLogReader _reader;
    private readonly string _logPath;
    private readonly NotifyIcon _notificationIcon;
    private readonly Icon _normalIcon;
    private readonly Icon _attentionIcon;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly TrayPreferences _preferences = TrayPreferences.Load();
    private readonly LogViewerForm _viewer;
    private readonly ToolStripMenuItem _notificationsMenu;
    private readonly ToolStripMenuItem _traceMenu;
    private readonly DeckLog _log;
    private readonly ToolStripMenuItem _warningsMenu;
    private readonly ToolStripMenuItem _detailsMenu;
    private readonly ToolStripMenuItem _startupMenu;
    private readonly Dictionary<string, DateTime> _lastAlertByMessage = new(StringComparer.Ordinal);
    private readonly List<DevDeckLogEntry> _pendingAlerts = [];
    private DateTime _lastAlertUtc = DateTime.MinValue;
    private int _unreadErrors;
    private bool _disposed;

    public TrayApplicationContext()
    {
        _log = new DeckLog();
        _logPath = _log.FilePath;
        _reader = new DevDeckLogReader(_logPath);
        IReadOnlyList<DevDeckLogEntry> existing = _reader.Initialize();
        _viewer = new LogViewerForm(_logPath);
        _viewer.SetEntries(existing);
        _viewer.ClearLogRequested += (_, _) => ClearLog();

        _normalIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? (Icon)SystemIcons.Application.Clone();
        _attentionIcon = WithAlertDot(_normalIcon);
        _traceMenu = new ToolStripMenuItem("Diagnostic tracing (more detail)")
        {
            Checked = _log.TraceEnabled,
            CheckOnClick = false
        };
        _traceMenu.Click += (_, _) => ToggleTracing();
        _notificationsMenu = new ToolStripMenuItem("Show error notifications") { Checked = _preferences.NotificationsEnabled, CheckOnClick = true };
        _notificationsMenu.CheckedChanged += (_, _) =>
        {
            _preferences.NotificationsEnabled = _notificationsMenu.Checked;
            if (!_preferences.NotificationsEnabled) _pendingAlerts.Clear();
            _preferences.Save();
        };
        _warningsMenu = new ToolStripMenuItem("Notify on warnings too") { Checked = _preferences.NotifyWarnings, CheckOnClick = true };
        _warningsMenu.CheckedChanged += (_, _) => { _preferences.NotifyWarnings = _warningsMenu.Checked; _preferences.Save(); };
        _detailsMenu = new ToolStripMenuItem("Show error details in notifications")
            { Checked = _preferences.ShowDetailsInNotifications, CheckOnClick = true };
        _detailsMenu.CheckedChanged += (_, _) =>
        {
            _preferences.ShowDetailsInNotifications = _detailsMenu.Checked;
            _preferences.Save();
        };
        _startupMenu = new ToolStripMenuItem("Start with Windows") { CheckOnClick = false };
        try { _startupMenu.Checked = TrayPreferences.IsStartupEnabled(); }
        catch (Exception) { _startupMenu.Enabled = false; }
        _startupMenu.Click += (_, _) => ToggleStartup();

        var menu = new ContextMenuStrip();
        menu.Items.Add("View recent log entries", null, (_, _) => OpenViewer());
        menu.Items.Add("Show errors / warnings", null, (_, _) => OpenViewer(errorsOnly: true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_traceMenu);
        menu.Items.Add(_notificationsMenu);
        menu.Items.Add(_warningsMenu);
        menu.Items.Add(_detailsMenu);
        menu.Items.Add(_startupMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Test error notification", null, (_, _) => _log.WriteError("Test error notification requested from DevDeck Tray."));
        menu.Items.Add("Exit DevDeck Tray", null, (_, _) => ExitThread());

        _notificationIcon = new NotifyIcon
        {
            Icon = _normalIcon,
            Text = "chris7197 DevDeck — monitoring errors",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notificationIcon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenViewer(); };
        _notificationIcon.BalloonTipClicked += (_, _) => OpenViewer(errorsOnly: true);

        _pollTimer = new System.Windows.Forms.Timer { Interval = 1600 };
        _pollTimer.Tick += (_, _) => ReadUpdates();
        _pollTimer.Start();
    }

    private void ReadUpdates()
    {
        try
        {
            DevDeckLogPoll update = _reader.Poll();
            if (update.Reinitialized)
            {
                _viewer.SetEntries(update.Entries);
                _pendingAlerts.Clear();
                return; // never alert on historical entries after a log rotation
            }
            if (update.Entries.Count > 0)
            {
                _viewer.AddEntries(update.Entries);
                foreach (DevDeckLogEntry entry in update.Entries)
                {
                    if (entry.Severity == DevDeckLogSeverity.Error)
                    {
                        _unreadErrors++;
                        if (_preferences.NotificationsEnabled && _pendingAlerts.Count < 100) _pendingAlerts.Add(entry);
                    }
                    else if (entry.Severity == DevDeckLogSeverity.Warning && _preferences.NotifyWarnings)
                        if (_preferences.NotificationsEnabled && _pendingAlerts.Count < 100) _pendingAlerts.Add(entry);
                }
                UpdateIcon();
            }
            ShowPendingAlert();
        }
        catch (Exception ex)
        {
            // The tray must stay alive even if another process rotates or locks the logfile.
            System.Diagnostics.Debug.WriteLine("DevDeck tray poll failed: " + ex);
        }
    }

    private void ShowPendingAlert()
    {
        if (!_preferences.NotificationsEnabled || _pendingAlerts.Count == 0) return;
        DateTime now = DateTime.UtcNow;
        if ((now - _lastAlertUtc) < TimeSpan.FromSeconds(5)) return;

        DevDeckLogEntry entry = _pendingAlerts[^1];
        int count = _pendingAlerts.Count;
        _pendingAlerts.Clear();
        string signature = entry.Severity + ":" + entry.Summary;
        // Avoid repetitive polling errors generating a toast every few seconds.
        if (_lastAlertByMessage.TryGetValue(signature, out DateTime last) &&
            now - last < TimeSpan.FromMinutes(2)) return;
        _lastAlertByMessage[signature] = now;
        foreach (string key in _lastAlertByMessage.Where(pair => now - pair.Value > TimeSpan.FromMinutes(3))
                     .Select(pair => pair.Key).ToArray()) _lastAlertByMessage.Remove(key);

        string message = _preferences.ShowDetailsInNotifications
            ? entry.Summary.Replace('\r', ' ').Replace('\n', ' ').Trim()
            : "An operation failed. Click to see the log entry.";
        if (message.Length > 175) message = message[..172] + "...";
        if (count > 1) message += $" ({count} new alerts)";
        _notificationIcon.BalloonTipTitle = entry.Severity == DevDeckLogSeverity.Error ? "DevDeck error" : "DevDeck warning";
        _notificationIcon.BalloonTipText = message.Length == 0 ? "See the DevDeck log for details." : message;
        _notificationIcon.BalloonTipIcon = entry.Severity == DevDeckLogSeverity.Error
            ? ToolTipIcon.Error : ToolTipIcon.Warning;
        _lastAlertUtc = now;
        _notificationIcon.ShowBalloonTip(7000);
    }

    private void UpdateIcon()
    {
        _notificationIcon.Icon = _unreadErrors > 0 ? _attentionIcon : _normalIcon;
        _notificationIcon.Text = _unreadErrors > 0
            ? $"DevDeck — {_unreadErrors} new error(s)"
            : "chris7197 DevDeck — monitoring errors";
    }

    private void OpenViewer(bool errorsOnly = false)
    {
        _unreadErrors = 0;
        UpdateIcon();
        _viewer.ShowViewer(errorsOnly);
    }

    private void ClearLog()
    {
        // This runs on the tray's UI thread; the polling timer cannot race our cursor reset.
        try
        {
            new DeckLog(_logPath).Clear();
            _reader.Initialize(); // Critical: reset the incremental EOF cursor after truncation.
            _viewer.SetEntries([]);
            _pendingAlerts.Clear();
            _lastAlertByMessage.Clear();
            _unreadErrors = 0;
            UpdateIcon();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(_viewer,
                "DevDeck could not clear the log (it may be open in another program).\n\n" + ex.Message,
                "Cannot clear log", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ToggleTracing()
    {
        try
        {
            bool enabled = !_log.TraceEnabled;
            _log.SetTraceEnabled(enabled);
            _traceMenu.Checked = enabled;
            _log.Write(enabled
                ? "Diagnostic tracing enabled from DevDeck Tray (SQL text and clipboard contents are not recorded)."
                : "Diagnostic tracing disabled from DevDeck Tray.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("Could not change diagnostic tracing: " + ex.Message, "DevDeck Tray",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ToggleStartup()
    {
        try
        {
            bool next = !_startupMenu.Checked;
            TrayPreferences.SetStartup(next);
            _startupMenu.Checked = next;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not update Windows startup: {ex.Message}", "DevDeck Tray",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static Icon WithAlertDot(Icon original)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.DrawIcon(original, new Rectangle(0, 0, 32, 32));
        using var brush = new SolidBrush(Color.FromArgb(245, 70, 80));
        using var border = new Pen(Color.White, 2);
        graphics.FillEllipse(brush, 20, 19, 11, 11);
        graphics.DrawEllipse(border, 20, 19, 11, 11);
        IntPtr handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr icon);

    protected override void ExitThreadCore()
    {
        _pollTimer.Stop();
        _notificationIcon.Visible = false;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
            _pollTimer.Dispose();
            _notificationIcon.ContextMenuStrip?.Dispose();
            _notificationIcon.Dispose();
            _normalIcon.Dispose();
            _attentionIcon.Dispose();
            _viewer.Dispose();
        }
        base.Dispose(disposing);
    }
}
