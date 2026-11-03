namespace chris7197.DevDeck.Core;

/// <summary>Opens a source line in an open, live Visual Studio instance owning the solution.</summary>
public sealed class VisualStudioSourceNavigator(VisualStudioComDispatcher? dispatcher = null)
{
    private readonly VisualStudioComDispatcher _dispatcher = dispatcher ?? VisualStudioComDispatcher.Shared;

    public Task OpenAtLineAndOffsetAsync(string solutionPath, string sourcePath, int line, int offset,
        CancellationToken cancellationToken = default) =>
        _dispatcher.InvokeAsync(() =>
        {
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("The source file is no longer available.", sourcePath);
            bool found = VisualStudioRot.Visit((dte, pid, hwnd, opened) =>
            {
                if (!PathHelper.SamePath(opened, solutionPath)) return false;
                if (!VisualStudioRot.IsValidWindow(hwnd))
                    throw new InvalidOperationException("The Visual Studio window closed before source navigation.");
                dte.ItemOperations.OpenFile(sourcePath);
                dynamic document = dte.ActiveDocument;
                if (document is null)
                    throw new InvalidOperationException("Visual Studio could not activate the source document.");
                dynamic selection = document.Selection;
                if (selection is null)
                    throw new InvalidOperationException("The opened source document has no selection.");
                selection.MoveToLineAndOffset(Math.Max(1, line), Math.Max(1, offset), false);
                if (VisualStudioRot.IsValidWindow(hwnd)) dte.MainWindow.Activate();
                return true;
            });
            if (!found)
                throw new InvalidOperationException("The solution is no longer open in a usable Visual Studio window.");
            return true;
        }, mayModifyVisualStudio: true, cancellationToken: cancellationToken);
}
