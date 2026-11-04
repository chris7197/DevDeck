using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;

namespace chris7197.DevDeck.Core;

/// <summary>Snapshot only strings from Visual Studio's DTE; never marshal COM objects off the STA.</summary>
internal sealed record ProjectDevCmdActiveProject(
    string FilePath, string ProjectFilePath, string ProjectName, string VisualStudioExecutablePath)
{
    public string ProjectDirectory => Path.GetDirectoryName(ProjectFilePath)!;
}

internal sealed class ProjectDevCmdResolver(VisualStudioComDispatcher dispatcher)
{
    public Task<ProjectDevCmdActiveProject?> ResolveAsync(uint processId, string expectedSolutionPath,
        CancellationToken cancellationToken = default) => dispatcher.InvokeAsync(() =>
    {
        ProjectDevCmdActiveProject? result = null;
        bool found = VisualStudioRot.Visit((dte, pid, hwnd, solutionPath) =>
        {
            if (pid != processId) return false;
            if (!PathHelper.SamePath(solutionPath, expectedSolutionPath))
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
                result = new ProjectDevCmdActiveProject(
                    Path.GetFullPath(filePath), Path.GetFullPath(projectFile),
                    (string)project.Name, Path.GetFullPath((string)dte.FullName));
            }
            catch (Exception ex) when (ex is COMException or RuntimeBinderException or
                                       ArgumentException or InvalidCastException)
            {
                // Miscellaneous files, designers and virtual projects need not have a real csproj.
            }
            return true;
        });
        if (!found)
            throw new InvalidOperationException("The selected Visual Studio instance is no longer available.");
        return result;
    }, cancellationToken: cancellationToken);
}
