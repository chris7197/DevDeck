using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace chris7197.DevDeck.Core;

/// <summary>
/// Reads the *focused* SSMS query editor using the Windows accessibility text provider.
/// No keyboard input, clipboard or on-disk file is involved in reading the document.
/// This is intentionally conservative: never search all document tabs and guess which
/// one is active, and never return a textbox from a toolbar or Object Explorer.
/// </summary>
internal static class SsmsDocumentService
{
    public static bool TryCapture(IntPtr ssmsHwnd, out SsmsDocumentSnapshot? snapshot, out string reason)
    {
        snapshot = null;
        reason = "No active SSMS text editor exposed a UI Automation TextPattern.";

        try
        {
            GetWindowThreadProcessId(ssmsHwnd, out uint expectedPid);
            if (expectedPid == 0)
            {
                reason = "Cannot determine the SSMS process.";
                return false;
            }

            AutomationElement focused = AutomationElement.FocusedElement;
            if (focused is null || focused.Current.ProcessId != checked((int)expectedPid))
            {
                reason = "Windows UI Automation does not report focus inside SSMS.";
                return false;
            }

            // Log only UI element metadata, never the text/SQL or control name.
            string focusedType = focused.Current.ControlType.ProgrammaticName;
            string focusedClass = focused.Current.ClassName;

            AutomationElement? element = focused;
            for (int depth = 0; element is not null && depth < 18; depth++)
            {
                if (element.Current.ProcessId != checked((int)expectedPid)) break;
                if (LooksLikeEditor(element) &&
                    element.TryGetCurrentPattern(TextPattern.Pattern, out object pattern) &&
                    pattern is TextPattern textPattern)
                {
                    TextPatternRange range = textPattern.DocumentRange;
                    string text = range.GetText(-1);
                    snapshot = new SsmsDocumentSnapshot(element, textPattern, range, text);
                    reason = $"UI Automation TextPattern captured {text.Length} characters from the focused editor.";
                    return true;
                }

                element = TreeWalker.RawViewWalker.GetParent(element);
            }

            reason = $"Focused SSMS control ({focusedType}, class {focusedClass}) " +
                "does not expose an editor TextPattern in this version/layout.";
            return false;
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or
            InvalidOperationException or UnauthorizedAccessException)
        {
            reason = $"UI Automation could not read the focused editor ({ex.GetType().Name}: {ex.Message}).";
            return false;
        }
    }

    private static bool LooksLikeEditor(AutomationElement element)
    {
        ControlType type = element.Current.ControlType;
        // Some Visual Studio-shell editors have a Document, Edit, Custom or Pane
        // provider. None is sufficient by itself: a generic search box, results
        // view or properties text box must not be treated as the SQL query buffer.
        if (type != ControlType.Document && type != ControlType.Edit &&
            type != ControlType.Custom && type != ControlType.Pane) return false;

        AutomationElement? current = element;
        for (int depth = 0; current is not null && depth < 4; depth++)
        {
            // Do not inherit a SQL tab title from the entire SSMS window: a
            // toolbar search field may live under the same top-level window.
            if (current.Current.ControlType == ControlType.Window) break;
            string name = current.Current.Name ?? "";
            string className = current.Current.ClassName ?? "";
            if (ContainsEditorHint(name) || ContainsEditorHint(className)) return true;
            current = TreeWalker.RawViewWalker.GetParent(current);
        }
        return false;
    }

    private static bool ContainsEditorHint(string text) =>
        text.Contains("Text Editor", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Query Editor", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("SQLQuery", StringComparison.OrdinalIgnoreCase) ||
        text.Contains(".sql", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("TextView", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("VsTextEditPane", StringComparison.OrdinalIgnoreCase);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}

/// <summary>
/// TextPattern is read-only, but some providers support selecting DocumentRange.
/// We verify the selection before allowing a Ctrl+V and can restore the original
/// caret/selection when the edit is cancelled or fails before paste.
/// </summary>
internal sealed class SsmsDocumentSnapshot(
    AutomationElement editor, TextPattern pattern, TextPatternRange documentRange, string text)
{
    private TextPatternRange[]? _originalSelection;

    public string Text { get; } = text;

    public bool StillFocused()
    {
        try
        {
            AutomationElement? element = AutomationElement.FocusedElement;
            for (int depth = 0; element is not null && depth < 18; depth++)
            {
                if (Automation.Compare(element, editor)) return true;
                element = TreeWalker.RawViewWalker.GetParent(element);
            }
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or InvalidOperationException)
        {
            // The editor was closed or rebuilt, so do not paste into an unknown control.
        }
        return false;
    }

    public bool TrySelectWholeDocument(out string reason)
    {
        reason = "";
        try
        {
            if (pattern.SupportedTextSelection == SupportedTextSelection.None)
            {
                reason = "SSMS exposes text for reading but does not support UIA text selection.";
                return false;
            }

            _originalSelection = pattern.GetSelection().Select(range => range.Clone()).ToArray();
            documentRange.Select();

            // SSMS's provider may report its selection a little after Select().
            for (int attempt = 0; attempt < 4; attempt++)
            {
                TextPatternRange[] selected = pattern.GetSelection();
                bool isEntireDocument = selected.Length == 1 &&
                    selected[0].CompareEndpoints(TextPatternRangeEndpoint.Start,
                        documentRange, TextPatternRangeEndpoint.Start) == 0 &&
                    selected[0].CompareEndpoints(TextPatternRangeEndpoint.End,
                        documentRange, TextPatternRangeEndpoint.End) == 0;
                if (isEntireDocument) return true;
                Thread.Sleep(60);
            }

            reason = "SSMS did not report a full-document selection after TextPatternRange.Select().";
            RestoreSelection();
            return false;
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or InvalidOperationException)
        {
            reason = $"SSMS refused UIA text selection ({ex.GetType().Name}: {ex.Message}).";
            RestoreSelection();
            return false;
        }
    }

    public void RestoreSelection()
    {
        try
        {
            // TextPatternRange.Select accepts a zero-length range as a caret position.
            if (_originalSelection is { Length: > 0 } && StillFocused())
                _originalSelection[0].Select();
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or InvalidOperationException)
        {
            // Best effort; no other key is sent if the target has disappeared.
        }
        finally { _originalSelection = null; }
    }

    public bool WaitForText(string expected, CancellationToken token)
    {
        for (int i = 0; i < 8; i++)
        {
            token.ThrowIfCancellationRequested();
            Thread.Sleep(90);
            try
            {
                if (SameDocumentText(pattern.DocumentRange.GetText(-1), expected)) return true;
            }
            catch (Exception ex) when (ex is COMException or ElementNotAvailableException or InvalidOperationException)
            {
                return false;
            }
        }
        return false;
    }

    private static bool SameDocumentText(string actual, string expected)
    {
        // Some accessibility providers add a final paragraph terminator or expose
        // CRLF as LF. Do not otherwise discard whitespace, which SQL formatting uses.
        static string Normalize(string source) =>
            source.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\r', '\n');
        return string.Equals(Normalize(actual), Normalize(expected), StringComparison.Ordinal);
    }
}
