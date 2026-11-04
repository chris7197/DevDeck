using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.CSharp.RuntimeBinder;

namespace chris7197.DevDeck.Core;

/// <summary>
/// One house-style formatter across SSMS, Visual Studio and the clipboard.
/// SSMS uses UI Automation for the active query buffer when supported, with a guarded
/// Select All/Copy/Paste fallback (Ctrl+Z to undo).
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
            "indented predicates and trailing AND. SSMS: whole query (UI Automation first); VS: selection or whole document. " +
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
                    return await SsmsSqlEditorFormatter.FormatAsync(formatter, stripDbo, log, context.CancellationToken);
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
/// SSMS has no dependable external DTE editor API. Use Windows UI Automation to
/// read the active buffer and select its entire document when available, then paste.
/// If SSMS lacks the necessary UIA provider, fall back to verified SendInput
/// Ctrl+A/C/V. Ctrl+Z undoes the replacement.
/// </summary>
internal static class SsmsSqlEditorFormatter
{
    private const ushort Ctrl = 0x11, KeyA = 0x41, KeyC = 0x43, KeyV = 0x56, KeyRight = 0x27;
    private const uint InputKeyboard = 1;
    private const uint KeyEventFKeyUp = 0x0002;

    public static Task<DevDeckOperationResult> FormatAsync(chris7197SqlFormatter formatter,
        bool stripDbo, DeckLog log, CancellationToken token) =>
        ClipboardSqlWriter.OnSta(() => Format(formatter, stripDbo, log, token), token);

    private static DevDeckOperationResult Format(chris7197SqlFormatter formatter, bool stripDbo,
        DeckLog log, CancellationToken token)
    {
        IntPtr hwnd = GetForegroundWindow();
        if (!IsSsmsForeground(hwnd)) return new(false, "Focus the SSMS SQL editor first.");

        // UIA reads directly from the unsaved text buffer; no Ctrl+A/C and no
        // clipboard involvement. Fall back only when SSMS cannot expose a safe,
        // selectable active document. Never paste into an unverified selection.
        if (SsmsDocumentService.TryCapture(hwnd, out SsmsDocumentSnapshot? document, out string reason)
            && document is not null)
        {
            log.Write("SSMS SQL: " + reason);
            DevDeckOperationResult? result = TryFormatUsingUia(hwnd, document, formatter, stripDbo, log, token);
            if (result is not null) return result;
            // Only reached if UIA cannot reliably select the text. The legacy
            // copy path re-reads and verifies the editor contents before editing.
        }
        else log.WriteWarning("SSMS SQL UIA unavailable: " + reason);

        log.Write("SSMS SQL: falling back to verified Ctrl+A/C copy.");
        return FormatUsingClipboard(hwnd, formatter, stripDbo, token);
    }

    /// <summary>
    /// A null result means the accessibility provider can read but cannot safely
    /// select its document, so the caller may attempt the legacy fallback.
    /// </summary>
    private static DevDeckOperationResult? TryFormatUsingUia(IntPtr hwnd,
        SsmsDocumentSnapshot document, chris7197SqlFormatter formatter,
        bool stripDbo, DeckLog log, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(document.Text))
            return new(false, "The active SSMS query editor is empty.");

        // Validation must happen *before* we change the editor selection.
        token.ThrowIfCancellationRequested();
        string updated = formatter.Format(document.Text, stripDbo);
        if (string.Equals(document.Text, updated, StringComparison.Ordinal))
            return new(true, "SSMS SQL is already formatted (read using UI Automation).");

        if (!StillFocused(hwnd) || !document.StillFocused())
            return new(false, "The SSMS SQL editor lost focus. Nothing was changed.");

        if (!document.TrySelectWholeDocument(out string failureReason))
        {
            log.WriteWarning("SSMS SQL UIA selection unavailable: " + failureReason);
            return null;
        }

        bool pasteDispatched = false;
        IDataObject? originalClipboard = null;
        bool clipboardChanged = false;
        try
        {
            if (!StillFocused(hwnd) || !document.StillFocused())
                return new(false, "The SSMS editor lost focus after selection. Nothing was pasted.");

            try { originalClipboard = Clipboard.GetDataObject(); }
            catch (ExternalException) { /* Clipboard can be busy. */ }

            Clipboard.SetText(updated, TextDataFormat.UnicodeText);
            clipboardChanged = true;
            if (!StillFocused(hwnd) || !document.StillFocused())
                return new(false, "The SSMS editor lost focus before paste. Nothing was pasted.");

            token.ThrowIfCancellationRequested();
            SendCtrl(KeyV);
            pasteDispatched = true;
            bool confirmed = document.WaitForText(updated, token);
            if (confirmed)
            {
                log.Write("SSMS SQL: UI Automation read/selection and paste verified.");
                return new(true, "Formatted entire SSMS query using UI Automation. Ctrl+Z undoes the edit.");
            }

            log.WriteWarning("SSMS SQL: paste dispatched, but UI Automation could not confirm the new text.");
            return new(false, "Paste was sent, but SSMS did not confirm the replacement. " +
                "Check the query tab before retrying; Ctrl+Z will undo a replacement if it occurred.");
        }
        finally
        {
            if (!pasteDispatched) document.RestoreSelection();
            if (clipboardChanged && !pasteDispatched && originalClipboard is not null)
            {
                try { Clipboard.SetDataObject(originalClipboard, true); }
                catch (ExternalException) { /* Do not hide the editor error. */ }
            }
        }
    }

    private static DevDeckOperationResult FormatUsingClipboard(IntPtr hwnd,
        chris7197SqlFormatter formatter, bool stripDbo, CancellationToken token)
    {

        IDataObject? oldClipboard = null;
        try { oldClipboard = Clipboard.GetDataObject(); }
        catch (ExternalException) { /* Clipboard may temporarily be locked. */ }

        bool changedClipboard = false;
        bool pasted = false;
        bool selectedAll = false;
        try
        {
            token.ThrowIfCancellationRequested();

            // An unselected Ctrl+C in SSMS can copy the current LINE. Always select
            // the whole query so a successful copy unambiguously captures the document.
            // Use a unique marker to prove SSMS, not DevDeck, updated the clipboard.
            string marker = "DevDeck-SQL-" + Guid.NewGuid().ToString("N");
            Clipboard.SetText(marker, TextDataFormat.UnicodeText);
            changedClipboard = true;
            uint markerSequence = GetClipboardSequenceNumber();

            if (!StillFocused(hwnd)) return new(false, "SSMS lost focus. Nothing was edited.");
            SendCtrl(KeyA);
            selectedAll = true;

            // Let SSMS process the selection before copying. Sending both shortcuts
            // back-to-back can race against the editor's asynchronous event handlers.
            Thread.Sleep(100);
            if (!StillFocused(hwnd)) return new(false, "SSMS lost focus before copy. Nothing was edited.");
            SendCtrl(KeyC);
            string? sql = ReadCopiedText(marker, markerSequence, token);
            if (sql is null)
                return new(false, "SSMS did not update the clipboard after Ctrl+A / Ctrl+C. " +
                    "Click inside the SQL query editor, then retry. The simulated keys " +
                    "may be blocked by Windows or another keyboard utility.");
            if (string.IsNullOrWhiteSpace(sql))
                return new(false, "SSMS copied an empty query. Nothing was edited.");

            token.ThrowIfCancellationRequested();
            string updated = formatter.Format(sql, stripDbo);
            if (string.Equals(sql, updated, StringComparison.Ordinal))
                return new(true, "SQL is already formatted.");

            if (!StillFocused(hwnd))
                return new(false, "SSMS focus changed before paste. Nothing was replaced.");
            Clipboard.SetText(updated, TextDataFormat.UnicodeText);
            if (!StillFocused(hwnd))
                return new(false, "SSMS lost focus before paste. Nothing was replaced.");
            SendCtrl(KeyV);
            pasted = true;
            // The clipboard intentionally holds formatted SQL after a successful paste.
            return new(true, "Sent formatted SQL to the SSMS query editor. Ctrl+Z undoes the edit.");
        }
        finally
        {
            if (selectedAll && !pasted && StillFocused(hwnd))
            {
                try { SendKey(KeyRight); } // Clear Select All after a rejected/unchanged query.
                catch (Exception) { /* Never hide the formatting error. */ }
            }
            if (changedClipboard && !pasted && oldClipboard is not null)
            {
                try { Clipboard.SetDataObject(oldClipboard, true); }
                catch (ExternalException) { /* Don't mask the original failure. */ }
            }
        }
    }

    private static string? ReadCopiedText(string marker, uint markerSequence, CancellationToken token)
    {
        // Even when SendInput succeeds, the target may ignore the keystroke. Observe
        // the clipboard until SSMS actually publishes different content, or time out.
        for (int i = 0; i < 30; i++)
        {
            token.ThrowIfCancellationRequested();
            Thread.Sleep(50);
            try
            {
                uint currentSequence = GetClipboardSequenceNumber();
                if (markerSequence != 0 && currentSequence == markerSequence) continue;
                if (!Clipboard.ContainsText()) continue;
                string text = Clipboard.GetText(TextDataFormat.UnicodeText);
                if (text != marker) return text;
            }
            catch (ExternalException) { /* SSMS or another process still owns clipboard. */ }
        }
        return null;
    }

    private static bool StillFocused(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd && IsSsmsForeground(hwnd);

    private static bool IsSsmsForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(checked((int)pid));
            return !process.HasExited && process.ProcessName.Equals("ssms", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
            OverflowException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    private static void SendCtrl(ushort key) => SendKeys(Ctrl, key);
    private static void SendKey(ushort key) => SendKeys(key);

    private static void SendKeys(params ushort[] keys)
    {
        // SendInput is the supported successor to keybd_event. It tells us whether
        // Windows accepted all the injected events; clipboard checking below then
        // tells us whether the target application really performed the copy.
        var inputs = new INPUT[keys.Length * 2];
        for (int i = 0; i < keys.Length; i++)
            inputs[i] = KeyboardInput(keys[i], keyUp: false);
        for (int i = 0; i < keys.Length; i++)
            inputs[keys.Length + i] = KeyboardInput(keys[keys.Length - 1 - i], keyUp: true);

        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            int error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"Windows accepted {sent}/{inputs.Length} simulated key events (Win32 error {error}). " +
                "Check whether DevDeck and SSMS run at different privilege levels, or " +
                "whether a security utility is blocking injected keys.");
        }
    }

    private static INPUT KeyboardInput(ushort key, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Data = new INPUTUNION
        {
            Keyboard = new KEYBDINPUT
            {
                VirtualKey = key,
                Flags = keyUp ? KeyEventFKeyUp : 0
            }
        }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public INPUTUNION Data;
    }

    // INPUT is a tagged union, so including MOUSEINPUT also ensures the native
    // structure size is correct on both x64 and x86 (40 and 28 bytes).
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public HARDWAREINPUT Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint Message;
        public ushort ParamL;
        public ushort ParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, [In] INPUT[] inputs, int inputSize);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
}
