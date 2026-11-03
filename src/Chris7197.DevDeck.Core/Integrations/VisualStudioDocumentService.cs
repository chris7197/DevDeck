using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;

namespace chris7197.DevDeck.Core;

/// <summary>Snapshot a document in the exact selected Visual Studio instance (never another instance).</summary>
public sealed record VisualStudioActiveDocument(string FilePath, int Line, int Column, bool IsSaved);

public sealed class VisualStudioDocumentService(VisualStudioComDispatcher? dispatcher = null)
{
    private readonly VisualStudioComDispatcher _dispatcher = dispatcher ?? VisualStudioComDispatcher.Shared;

    public Task<VisualStudioActiveDocument?> GetActiveDocumentAsync(uint processId, string expectedSolutionPath,
        CancellationToken cancellationToken = default) => _dispatcher.InvokeAsync(() =>
    {
        VisualStudioActiveDocument? result = null;
        bool found = VisualStudioRot.Visit((dte, pid, hwnd, path) =>
        {
            if (pid != processId) return false;
            if (!PathHelper.SamePath(path, expectedSolutionPath))
                throw new InvalidOperationException("Visual Studio changed solutions after selection.");
            if (!VisualStudioRot.IsValidWindow(hwnd))
                throw new InvalidOperationException("The selected Visual Studio window has closed.");

            dynamic document = dte.ActiveDocument;
            if (document is null) return true; // No editor open; the caller may open the solution folder.
            string? filePath;
            try { filePath = (string?)document.FullName; }
            catch (Exception ex) when (ex is COMException or RuntimeBinderException)
            {
                return true; // Some designers expose a document without a normal file path.
            }
            if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathFullyQualified(filePath) || !File.Exists(filePath))
                return true;

            int line = 1, column = 1;
            try
            {
                dynamic selection = document.Selection;
                if (selection is not null)
                {
                    dynamic point = selection.ActivePoint;
                    line = Math.Max(1, (int)point.Line);
                    column = Math.Max(1, (int)point.LineCharOffset);
                }
            }
            catch (Exception ex) when (ex is COMException or RuntimeBinderException)
            {
                // Non-text documents may not expose cursor coordinates.
            }

            // Saved is a DTE document property, not an inferred file timestamp.
            // If it cannot be read, fail closed for external formatting.
            bool saved = false;
            try { saved = (bool)document.Saved; }
            catch (Exception ex) when (ex is COMException or RuntimeBinderException) { }
            result = new VisualStudioActiveDocument(Path.GetFullPath(filePath), line, column, saved);
            return true;
        });
        if (!found)
            throw new InvalidOperationException("The selected Visual Studio instance is no longer available.");
        return result;
    }, cancellationToken: cancellationToken);
}
