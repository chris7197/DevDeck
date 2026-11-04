using System.Diagnostics;
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
    DeckSettings settings,
    DeckLog log) : IDevDeckOperation
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
        string traceId = Guid.NewGuid().ToString("N")[..8];
        string app = ForegroundApp();
        var clock = Stopwatch.StartNew();
        log.WriteTrace($"SQL[{traceId}] Start target={app}, removeDboPrefixes={stripDbo}.");
        try
        {
            DevDeckOperationResult result = app switch
            {
                "devenv" => await FormatVisualStudioAsync(context, stripDbo),
                "ssms" => await SsmsSqlEditorFormatter.FormatAsync(formatter, stripDbo,
                    context.CancellationToken, log, traceId),
                _ => await FormatClipboardAsync(stripDbo, context.CancellationToken)
            };
            log.WriteTrace($"SQL[{traceId}] Finished success={result.Success}, cancelled={result.Cancelled}, " +
                $"durationMs={clock.ElapsedMilliseconds}.");
            return result;
        }
        catch (SqlFormattingException ex)
        {
            log.WriteTrace($"SQL[{traceId}] Formatting rejected: {ex.GetType().Name}; " +
                $"durationMs={clock.ElapsedMilliseconds}.");
            return new(false, ex.Message);
        }
        catch (Exception ex) when (ex is COMException or RuntimeBinderException or ExternalException)
        {
            log.WriteTrace($"SQL[{traceId}] Editor automation failed: {ex.GetType().Name}; " +
                $"durationMs={clock.ElapsedMilliseconds}.");
            return new(false, $"Editor automation failed: {ex.Message}");
        }
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
    private const ushort Ctrl = 0x11, KeyA = 0x41, KeyC = 0x43, KeyV = 0x56, KeyRight = 0x27;
    private const uint KeyUp = 0x0002;
    private const uint InputKeyboard = 1;

    public static Task<DevDeckOperationResult> FormatAsync(chris7197SqlFormatter formatter,
        bool stripDbo, CancellationToken token, DeckLog log, string traceId) =>
        ClipboardSqlWriter.OnSta(() => Format(formatter, stripDbo, token, log, traceId), token);

    private static DevDeckOperationResult Format(chris7197SqlFormatter formatter, bool stripDbo,
        CancellationToken token, DeckLog log, string traceId)
    {
        void Trace(string stage) => log.WriteTrace($"SQL[{traceId}] SSMS {stage}");
        IntPtr hwnd = GetForegroundWindow();
        Trace($"foreground window detected={hwnd != IntPtr.Zero}, isSsms={IsSsmsForeground(hwnd)}.");
        if (!IsSsmsForeground(hwnd)) return new(false, "Focus the SSMS SQL editor first.");

        IDataObject? oldClipboard = null;
        try { oldClipboard = Clipboard.GetDataObject(); }
        catch (ExternalException) { Trace("original clipboard temporarily inaccessible."); }

        bool changedClipboard = false;
        bool pasted = false;
        bool selectedAll = false;
        try
        {
            // Use a unique marker for each attempt. An unchanged marker means that
            // Ctrl+C did not reach a text selection or SSMS did not update the clipboard.
            changedClipboard = true;
            Trace("attempting selected-text copy.");
            string? sql = TryCopySql(hwnd, token, log, traceId);
            if (sql is null)
            {
                Trace("selected-text copy yielded no text; trying Select All fallback.");
                // No selection copied: try the whole query. In particular, do not
                // interpret a failed Ctrl+C as proof that the query tab is missing.
                if (!StillFocused(hwnd)) return new(false, "SSMS lost focus. Nothing was edited.");
                SendCtrl(KeyA);
                selectedAll = true;
                Thread.Sleep(80); // Allow SSMS to process Select All before Copy.
                sql = TryCopySql(hwnd, token, log, traceId);
            }
            if (sql is null || string.IsNullOrWhiteSpace(sql))
            {
                Trace("copy unsuccessful or text empty after fallback. No paste attempted.");
                return new(false, "SSMS is active, but DevDeck couldn't copy SQL. Click inside the query text " +
                    "(not just its tab) and retry. If Ctrl+A / Ctrl+C work manually, check whether SSMS " +
                    "is running as administrator while DevDeck is not. No text was replaced.");
            }

            Trace($"copy succeeded, characters={sql.Length}, entireDocument={selectedAll}; formatting.");
            token.ThrowIfCancellationRequested();
            string updated = formatter.Format(sql, stripDbo); // Throws before any paste on parse/equivalence failure.
            if (string.Equals(sql, updated, StringComparison.Ordinal))
            {
                Trace("formatter output unchanged, no paste required.");
                return new(true, "SQL is already formatted.");
            }
            Trace($"formatting complete, outputCharacters={updated.Length}.");
            if (!StillFocused(hwnd))
            {
                Trace("focus lost before clipboard update; paste skipped.");
                return new(false, "SSMS focus changed before paste. Nothing was replaced.");
            }

            Clipboard.SetText(updated, TextDataFormat.UnicodeText);
            if (!StillFocused(hwnd))
            {
                Trace("focus lost after clipboard update; paste skipped.");
                return new(false, "SSMS lost focus before paste. Nothing was replaced.");
            }
            Trace("sending Paste shortcut.");
            SendCtrl(KeyV);
            pasted = true;
            Trace("paste shortcut sent successfully.");
            // Leave formatted SQL in the clipboard; Ctrl+Z in SSMS undoes the editor change.
            return new(true, "Formatted the selected SQL (or entire query) in SSMS. Ctrl+Z undoes the edit.");
        }
        finally
        {
            // Don't leave Ctrl+A selected on a parse failure: the next keystroke could
            // overwrite the entire SQL window even though DevDeck made no edit.
            if (selectedAll && !pasted && StillFocused(hwnd))
            {
                try { SendKey(KeyRight); Trace("cleared Select All after unsuccessful operation."); }
                catch (Exception) { Trace("could not clear Select All after unsuccessful operation."); }
            }
            if (changedClipboard && !pasted && oldClipboard is not null)
            {
                try { Clipboard.SetDataObject(oldClipboard, true); Trace("restored previous clipboard data."); }
                catch (ExternalException) { Trace("could not restore previous clipboard data."); }
            }
        }
    }

    private static string? TryCopySql(IntPtr hwnd, CancellationToken token, DeckLog log, string traceId)
    {
        // SSMS can be slow to process synthetic input, particularly on a busy UI
        // thread. Retry without modifying its document. Each attempt gets a new
        // clipboard marker, so a previous unsuccessful copy cannot appear valid.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (!StillFocused(hwnd))
            {
                log.WriteTrace($"SQL[{traceId}] SSMS focus changed before copy attempt {attempt + 1}.");
                return null;
            }
            string marker = "DevDeck-SQL-" + Guid.NewGuid().ToString("N");
            Clipboard.SetText(marker, TextDataFormat.UnicodeText);
            if (!StillFocused(hwnd))
            {
                log.WriteTrace($"SQL[{traceId}] SSMS focus changed after clipboard marker, before Ctrl+C.");
                return null;
            }
            log.WriteTrace($"SQL[{traceId}] SSMS sending Ctrl+C (attempt {attempt + 1}/2).");
            SendCtrl(KeyC);
            string? text = ReadCopiedText(marker, token);
            if (text is not null)
            {
                log.WriteTrace($"SQL[{traceId}] SSMS clipboard copy succeeded (attempt {attempt + 1}/2).");
                return text;
            }
            log.WriteTrace($"SQL[{traceId}] SSMS copy timed out (attempt {attempt + 1}/2).");
            Thread.Sleep(90);
        }
        return null;
    }

    private static string? ReadCopiedText(string marker, CancellationToken token)
    {
        // Copy crosses process boundaries. Look for changed clipboard content
        // rather than assuming it is ready immediately after the shortcut.
        for (int i = 0; i < 25; i++)
        {
            token.ThrowIfCancellationRequested();
            Thread.Sleep(40);
            try
            {
                string text = Clipboard.ContainsText(TextDataFormat.UnicodeText)
                    ? Clipboard.GetText(TextDataFormat.UnicodeText)
                    : Clipboard.ContainsText(TextDataFormat.Text)
                        ? Clipboard.GetText(TextDataFormat.Text)
                        : marker;
                if (text != marker) return text;
            }
            catch (ExternalException) { /* Clipboard is temporarily locked; retry. */ }
        }
        return null;
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

    private static void SendCtrl(ushort key)
    {
        // Send one uninterrupted input batch rather than four independent
        // keybd_event calls. Check the result: injection can be blocked when SSMS
        // is elevated above DevDeck (Windows UIPI), even with SSMS foreground.
        SendKeys(InputFor(Ctrl), InputFor(key), InputFor(key, KeyUp), InputFor(Ctrl, KeyUp));
    }

    private static void SendKey(ushort key) => SendKeys(InputFor(key), InputFor(key, KeyUp));

    private static KeyboardEvent InputFor(ushort key, uint flags = 0) => new()
    {
        Type = InputKeyboard,
        Data = new InputData { Keyboard = new KeyboardInput { VirtualKey = key, Flags = flags } }
    };

    private static void SendKeys(params KeyboardEvent[] inputs)
    {
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<KeyboardEvent>());
        if (sent != inputs.Length)
            throw new SqlFormattingException("Windows could not send the keyboard shortcut to SSMS. " +
                "If SSMS is running as administrator, run it normally or use a DevDeck instance " +
                "with matching permissions. No paste was attempted.");
    }

    // INPUT includes a union with MOUSEINPUT (larger than KEYBDINPUT), so use
    // the real native layout on both x86 and x64 rather than a truncated struct.
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardEvent
    {
        public uint Type;
        public InputData Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey, ScanCode;
        public uint Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Message;
        public ushort ParameterLow, ParameterHigh;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, [In] KeyboardEvent[] inputs, int inputSize);
}
