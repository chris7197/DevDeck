using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;

namespace chris7197.DevDeck.Core;

/// <summary>Snapshot a document in the exact selected Visual Studio instance (never another instance).</summary>
public sealed record VisualStudioActiveDocument(string FilePath, int Line, int Column, bool IsSaved);

/// <summary>The project containing the active document, resolved via Visual Studio rather than file ancestry.</summary>
public sealed record VisualStudioActiveProject(
    string FilePath, string ProjectFilePath, string ProjectName, string VisualStudioExecutablePath)
{
    public string ProjectDirectory => Path.GetDirectoryName(ProjectFilePath)!;
}

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

    /// <summary>
    /// Resolve the active document's real containing project in a specific running VS instance.
    /// This handles linked source files and solution folders correctly, unlike searching for a
    /// nearby csproj. A non-project document returns null rather than using the wrong folder.
    /// </summary>
    public Task<VisualStudioActiveProject?> GetActiveProjectAsync(uint processId, string expectedSolutionPath,
        CancellationToken cancellationToken = default) => _dispatcher.InvokeAsync(() =>
    {
        VisualStudioActiveProject? result = null;
        bool found = VisualStudioRot.Visit((dte, pid, hwnd, path) =>
        {
            if (pid != processId) return false;
            if (!PathHelper.SamePath(path, expectedSolutionPath))
                throw new InvalidOperationException("Visual Studio changed solutions after selection.");
            if (!VisualStudioRot.IsValidWindow(hwnd))
                throw new InvalidOperationException("The selected Visual Studio window has closed.");

            try
            {
                dynamic document = dte.ActiveDocument;
                if (document is null) return true;
                string filePath = (string)document.FullName;
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return true;
                dynamic item = document.ProjectItem;
                if (item is null) return true;
                dynamic project = item.ContainingProject;
                if (project is null) return true;
                string projectFile = (string)project.FullName;
                if (string.IsNullOrWhiteSpace(projectFile) || !File.Exists(projectFile)) return true;
                string devenv = (string)dte.FullName;
                string projectName = (string)project.Name;
                result = new VisualStudioActiveProject(
                    Path.GetFullPath(filePath), Path.GetFullPath(projectFile), projectName,
                    Path.GetFullPath(devenv));
            }
            catch (Exception ex) when (ex is COMException or RuntimeBinderException or
                                       ArgumentException or InvalidCastException)
            {
                // Designers, miscellaneous files, unloaded and virtual projects do not
                // necessarily expose ProjectItem/ContainingProject. Do not guess a project.
            }
            return true;
        });
        if (!found)
            throw new InvalidOperationException("The selected Visual Studio instance is no longer available.");
        return result;
    }, cancellationToken: cancellationToken);
}
