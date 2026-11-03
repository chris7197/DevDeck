using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.CSharp.RuntimeBinder;

namespace chris7197.DevDeck.Core;

/// <summary>
/// One house-style formatter across SSMS, Visual Studio and the clipboard.
/// SSMS edits use a single guarded Copy/Format/Paste sequence (Ctrl+Z to undo).
/// Visual Studio edits use its native undoable TextSelection API on the COM STA.
/// </summary>
public sealed class FormatSqlOperation(
    IForegroundWindowReader foreground,
    IVisualStudioSolutionDiscovery visualStudio,
    VisualStudioComDispatcher dispatcher,
    chris7197SqlFormatter formatter,
    DeckSettings settings) : IDevDeckOperation
{
    public const string ActionId = "com.chris7197.devdeck.sql.format";
    public string Name => "Format SQL";
    public string PluginActionId => ActionId;
    public string? IconName => DevDeckIcons.FormatSql;
    public IReadOnlyList<string> CliCommands => [];
    public string CliUsage => "";
    public bool RequiresSolutionContext => false;
    public bool TryParseCliArguments(IReadOnlyList<string> args, out string? target)
    {
        target = null;
        return false;
    }

    public Task<DevDeckButtonState> GetStateAsync(DeveloperContext? context, CancellationToken cancellationToken = default)
    {
        string app = ForegroundApp() switch
        {
            "devenv" => "Visual Studio",
            "ssms" => "SSMS editor",
            _ => "Clipboard"
        };
        return Task.FromResult(new DevDeckButtonState("Format SQL", app, DeckStatus.Neutral,
            "Restyle SQL using chris7197 conventions: tabs, uppercase keywords, separated FROM/JOIN, " +
            "indented predicates and trailing AND. SSMS / VS: selection, or whole SQL document. " +
            "Elsewhere: clipboard only. Invalid SQL is never replaced.", IconName: IconName));
    }

    public async Task<DevDeckOperationResult> ExecuteAsync(DevDeckOperationContext context)
    {
        bool stripDbo = context.ActionSettings?.SqlRemoveDboPrefixes ?? settings.SqlFormatting.RemoveDboPrefixes;
        try
        {
            switch (ForegroundApp())
            {
                case "devenv":
                    return await FormatVisualStudioAsync(context, stripDbo);
                case "ssms":
                    return await SsmsSqlEditorFormatter.FormatAsync(formatter, stripDbo, context.CancellationToken);
                default:
                    return await FormatClipboardAsync(stripDbo, context.CancellationToken);
            }
        }
        catch (SqlFormattingException ex) { return new(false, ex.Message); }
        catch (Exception ex) when (ex is COMException or RuntimeBinderException or ExternalException)
        { return new(false, $"Editor automation failed: {ex.Message}"); }
    }

    private string ForegroundApp() => foreground.Read()?.ProcessName.ToLowerInvariant() switch
    {
        "devenv" or "devenv.exe" => "devenv",
        "ssms" or "ssms.exe" => "ssms",
        _ => "clipboard"
    };

    private async Task<DevDeckOperationResult> FormatVisualStudioAsync(
        DevDeckOperationContext context, bool stripDbo)
    {
        VisualStudioSolutionInstance? selected = await visualStudio.GetFocusedSolutionAsync(context.CancellationToken);
        if (selected is null)
            return new(false, "Select an open Visual Studio solution containing a SQL document.");

        return await dispatcher.InvokeAsync(() =>
        {
            DevDeckOperationResult? result = null;
            bool found = VisualStudioRot.Visit((dte, pid, hwnd, path) =>
            {
                if (pid != selected.ProcessId) return false;
                if (!PathHelper.SamePath(path, selected.SolutionPath) || !VisualStudioRot.IsValidWindow(hwnd))
                    throw new InvalidOperationException("Visual Studio changed solutions. No SQL was replaced.");
                dynamic document = dte.ActiveDocument;
                if (document is null ||
                    !((string)document.Name).EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                {
                    result = new(false, "Open an editable .sql document in Visual Studio first.");
                    return true;
                }
                dynamic selection = document.Selection;
                if (selection is null)
                {
                    result = new(false, "The active Visual Studio document is not a text editor.");
                    return true;
                }
                // Keep the selection when present; otherwise select the entire SQL file.
                // If formatting is declined, restore the caret rather than leaving Select All active.
                bool selectedAll = (bool)selection.IsEmpty;
                int originalLine = 1, originalColumn = 1;
                if (selectedAll)
                {
                    originalLine = (int)selection.ActivePoint.Line;
                    originalColumn = (int)selection.ActivePoint.LineCharOffset;
                }
                bool inserted = false;
                try
                {
                    if (selectedAll) selection.SelectAll();
                    string source = (string)selection.Text;
                    string updated = formatter.Format(source, stripDbo);
                    if (string.Equals(source, updated, StringComparison.Ordinal))
                    {
                        result = new(true, "SQL is already formatted.");
                        return true;
                    }
                    // One native undo step; no external file overwrite or save required.
                    bool opened = false;
                    try
                    {
                        if (!(bool)dte.UndoContext.IsOpen)
                        {
                            dte.UndoContext.Open("Format SQL");
                            opened = true;
                        }
                        selection.Insert(updated, 0);
                        inserted = true;
                    }
                    finally
                    {
                        if (opened) dte.UndoContext.Close();
                    }
                    result = new(true, "Formatted active SQL in Visual Studio. Ctrl+Z undoes the edit.");
                    return true;
                }
                finally
                {
                    if (selectedAll && !inserted)
                    {
                        try { selection.MoveToLineAndOffset(originalLine, originalColumn, false); }
                        catch (Exception) { /* Never mask a parse/formatting failure. */ }
                    }
                }
            });
            return found ? result ?? new(false, "Unable to access the SQL editor.") :
                new(false, "Visual Studio closed before the SQL could be formatted.");
        }, mayModifyVisualStudio: true, cancellationToken: context.CancellationToken);
    }

    private async Task<DevDeckOperationResult> FormatClipboardAsync(bool stripDbo, CancellationToken cancellationToken)
    {
        string source = await SmartInvestigationService.ReadClipboardAsync();
        string updated = formatter.Format(source, stripDbo);
        if (string.Equals(source, updated, StringComparison.Ordinal))
            return new(true, "Clipboard SQL is already formatted.");
        await ClipboardSqlWriter.WriteAsync(updated, cancellationToken);
        return new(true, "Formatted SQL copied to clipboard. Paste it where needed.");
    }
}

/// <summary>Clipboard writes require STA, independently of the device controller thread.</summary>
internal static class ClipboardSqlWriter
{
    public static Task WriteAsync(string text, CancellationToken token) => OnSta(() =>
    {
        Clipboard.SetText(text, TextDataFormat.UnicodeText);
        return true;
    }, token);

    internal static Task<T> OnSta<T>(Func<T> callback, CancellationToken token)
    {
        var promise = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                if (token.IsCancellationRequested) { promise.TrySetCanceled(token); return; }
                promise.TrySetResult(callback());
            }
            catch (Exception ex) { promise.TrySetException(ex); }
        }) { IsBackground = true, Name = "DevDeck SQL clipboard STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return promise.Task;
    }
}

/// <summary>
/// SSMS doesn't expose the same reliable DTE interface as Visual Studio. Use editor Copy/Paste;
/// recheck the foreground HWND before EVERY destructive keyboard action. No focus stealing.
/// </summary>
internal static class SsmsSqlEditorFormatter
{
    private const byte Ctrl = 0x11, KeyA = 0x41, KeyC = 0x43, KeyV = 0x56;
    private const uint KeyUp = 2;

    public static Task<DevDeckOperationResult> FormatAsync(chris7197SqlFormatter formatter,
        bool stripDbo, CancellationToken token) => ClipboardSqlWriter.OnSta(() => Format(formatter, stripDbo, token), token);

    private static DevDeckOperationResult Format(chris7197SqlFormatter formatter, bool stripDbo, CancellationToken token)
    {
        IntPtr hwnd = GetForegroundWindow();
        if (!IsSsmsForeground(hwnd)) return new(false, "Focus the SSMS SQL editor first.");

        IDataObject? oldClipboard = null;
        try { oldClipboard = Clipboard.GetDataObject(); }
        catch (ExternalException) { /* Clipboard may be occupied by another process. */ }

        bool changedClipboard = false;
        bool pasted = false;
        bool selectedAll = false;
        try
        {
            string marker = "DevDeck-SQL-" + Guid.NewGuid().ToString("N");
            Clipboard.SetText(marker, TextDataFormat.UnicodeText);
            changedClipboard = true;
            if (!StillFocused(hwnd)) return new(false, "SSMS lost focus. Nothing was edited.");
            SendCtrl(KeyC);
            string sql = ReadCopiedText(marker);
            if (sql == marker)
            {
                // No selection copied: format the entire current query window.
                if (!StillFocused(hwnd)) return new(false, "SSMS lost focus. Nothing was edited.");
                SendCtrl(KeyA);
                selectedAll = true;
                SendCtrl(KeyC);
                sql = ReadCopiedText(marker);
            }
            if (sql == marker || string.IsNullOrWhiteSpace(sql))
                return new(false, "No SQL could be copied from the SSMS editor. Focus a query tab, not Object Explorer.");

            token.ThrowIfCancellationRequested();
            string updated = formatter.Format(sql, stripDbo); // Throws before any paste on parse/equivalence failure.
            if (string.Equals(sql, updated, StringComparison.Ordinal))
                return new(true, "SQL is already formatted.");
            if (!StillFocused(hwnd)) return new(false, "SSMS focus changed before paste. Nothing was replaced.");

            Clipboard.SetText(updated, TextDataFormat.UnicodeText);
            if (!StillFocused(hwnd)) return new(false, "SSMS lost focus before paste. Nothing was replaced.");
            SendCtrl(KeyV);
            pasted = true;
            // Leave formatted SQL in the clipboard; Ctrl+Z in SSMS undoes the editor change.
            return new(true, "Formatted the selected SQL (or entire query) in SSMS. Ctrl+Z undoes the edit.");
        }
        finally
        {
            // Don't leave Ctrl+A selected on a parse failure: the next keystroke could
            // overwrite the entire SQL window even though DevDeck made no edit.
            if (selectedAll && !pasted && StillFocused(hwnd))
            {
                try { keybd_event(0x27, 0, 0, UIntPtr.Zero); keybd_event(0x27, 0, KeyUp, UIntPtr.Zero); }
                catch (Exception) { /* keep the original error */ }
            }
            if (changedClipboard && !pasted && oldClipboard is not null)
            {
                try { Clipboard.SetDataObject(oldClipboard, true); }
                catch (ExternalException) { /* Don't mask the original failure. */ }
            }
        }
    }

    private static string ReadCopiedText(string marker)
    {
        // Copy is asynchronous across processes: allow SSMS time to populate the clipboard.
        for (int i = 0; i < 15; i++)
        {
            Thread.Sleep(45);
            try
            {
                string text = Clipboard.ContainsText() ? Clipboard.GetText(TextDataFormat.UnicodeText) : marker;
                if (text != marker) return text;
            }
            catch (ExternalException) { /* retry until deadline */ }
        }
        return marker;
    }

    private static bool StillFocused(IntPtr hwnd) => hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd && IsSsmsForeground(hwnd);

    private static bool IsSsmsForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(checked((int)pid));
            return !process.HasExited && process.ProcessName.Equals("ssms", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException)
        { return false; }
    }

    private static void SendCtrl(byte key)
    {
        keybd_event(Ctrl, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, KeyUp, UIntPtr.Zero);
        keybd_event(Ctrl, 0, KeyUp, UIntPtr.Zero);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scanCode, uint flags, UIntPtr extraInfo);
}
