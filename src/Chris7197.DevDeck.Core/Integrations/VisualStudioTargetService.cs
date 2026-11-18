using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;

namespace chris7197.DevDeck.Core;

public sealed record VisualStudioProjectBuildContext(uint ProcessId, string SolutionPath, string ProjectPath,
    string Configuration, string Platform, string SolutionConfiguration);

/// <summary>Uses the host's single STA. Only managed snapshots leave the COM callbacks.</summary>
public sealed class VisualStudioTargetService(VisualStudioLocator locator, VisualStudioComDispatcher dispatcher)
{
    private const string SolutionExplorerWindow = "{3AE79031-E1BC-11D0-8F78-00A0C9110057}";
    private const string SolutionFolderKind = "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}";
    private const int BuildInProgress = 2; // EnvDTE.vsBuildState.vsBuildStateInProgress
    private const int DesignMode = 1; // EnvDTE.vsIDEMode.vsIDEModeDesign
    private const int SelectOne = 1; // EnvDTE.vsUISelectionType.vsUISelectionTypeSelect

    public async Task<DeveloperContext> BindAsync(DeveloperContext context, CancellationToken ct)
    {
        if (context.VisualStudioProcessId.HasValue) return context;
        string solution = context.RequireSolutionPath();
        var matches = (await locator.GetOpenSolutionsAsync(ct)).Where(s => PathHelper.SamePath(s.SolutionPath, solution)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException("Open the solution in one Visual Studio instance, or select its window explicitly.");
        return context with { VisualStudioProcessId = matches[0].ProcessId };
    }

    public async Task OpenFileAsync(DeveloperContext context, string filePath, string? editor, CancellationToken ct)
    {
        var bound = await BindAsync(context, ct);
        await InInstanceAsync(bound, (dte, hwnd) =>
        {
            if (!File.Exists(filePath)) throw new FileNotFoundException("Configured file no longer exists.", filePath);
            if (string.IsNullOrWhiteSpace(editor))
                dte.ItemOperations.OpenFile(filePath);
            else
            {
                // File.OpenFile supports /e:<editor display name>. Never fall back to the designer.
                if (editor.Contains('"') || editor.Contains('\r') || editor.Contains('\n'))
                    throw new InvalidOperationException("Invalid Visual Studio editor name.");
                dte.ExecuteCommand("File.OpenFile", $"\"{filePath}\" /e:\"{editor}\"");
            }
            WindowActivator.Activate(hwnd);
            return true;
        }, ct);
    }

    public Task<VisualStudioProjectBuildContext> PrepareRunAsync(DeveloperContext context, string projectPath, bool saveAll, CancellationToken ct) =>
        InInstanceAsync<VisualStudioProjectBuildContext>(context, (dte, hwnd) =>
        {
            RequireIdle(dte);
            ProjectNode project = FindProject((object)dte, projectPath);
            if (saveAll) dte.ExecuteCommand("File.SaveAll");
            // Project may have been reloaded by saving; resolve again rather than using a stale COM reference.
            RequireIdle(dte);
            project = FindProject((object)dte, projectPath);
            return Snapshot((object)dte, project.Project, context, projectPath);
        }, ct);

    public Task<bool> StartDebuggingAsync(DeveloperContext context, VisualStudioProjectBuildContext prepared, CancellationToken ct) =>
        InInstanceAsync(context, (dte, hwnd) =>
        {
            RequireIdle(dte);
            if (!File.Exists(prepared.ProjectPath)) throw new FileNotFoundException("Project no longer exists.", prepared.ProjectPath);
            ProjectNode project = FindProject((object)dte, prepared.ProjectPath);
            if (Snapshot((object)dte, project.Project, context, prepared.ProjectPath) != prepared)
                throw new InvalidOperationException("Visual Studio's active configuration changed during the build. No project was launched.");

            // Use DTE.Windows instead of DTE2.ToolWindows: ROT exposes the base DTE interface.
            dynamic window = dte.Windows.Item(SolutionExplorerWindow);
            window.Activate();
            dynamic hierarchy = window.Object;
            string root = (string)hierarchy.UIHierarchyItems.Item(1).Name;
            dynamic node = hierarchy.GetItem(root + "\\" + project.HierarchyPath);
            node.Select(SelectOne);

            // The command acts on the IDE selection. Fail closed if the selection is not the exact project.
            RequireSelectedProject(dte, prepared.ProjectPath);
            string command = GetDebugCommand(dte);
            RequireSelectedProject(dte, prepared.ProjectPath);
            RequireIdle(dte);
            ct.ThrowIfCancellationRequested();
            dte.ExecuteCommand(command);
            return true;
        }, ct);

    private Task<T> InInstanceAsync<T>(DeveloperContext context, Func<dynamic, IntPtr, T> action, CancellationToken ct)
    {
        uint pid = context.VisualStudioProcessId ?? throw new InvalidOperationException("A running Visual Studio instance is required.");
        string solution = context.RequireSolutionPath();
        return dispatcher.InvokeAsync(() =>
        {
            T result = default!;
            bool found = VisualStudioRot.Visit((dte, currentPid, hwnd, path) =>
            {
                if (currentPid != pid) return false;
                if (!PathHelper.SamePath(path, solution) || !VisualStudioRot.IsValidWindow(hwnd))
                    throw new InvalidOperationException("The selected Visual Studio solution or window changed. Action stopped.");
                ct.ThrowIfCancellationRequested();
                result = action((object)dte, hwnd);
                return true;
            });
            if (!found) throw new InvalidOperationException("The selected Visual Studio instance is no longer available.");
            return result;
        }, mayModifyVisualStudio: true, cancellationToken: ct);
    }

    private static void RequireIdle(dynamic dte)
    {
        if ((int)dte.Mode != DesignMode || (int)dte.Solution.SolutionBuild.BuildState == BuildInProgress)
            throw new InvalidOperationException("Stop the current debug session or wait for Visual Studio's build to finish first.");
    }

    private static VisualStudioProjectBuildContext Snapshot(dynamic dte, object projectObject, DeveloperContext context, string path)
    {
        dynamic project = projectObject;
        dynamic config = project.ConfigurationManager.ActiveConfiguration;
        string configuration = (string)config.ConfigurationName;
        string platform = (string)config.PlatformName;
        if (string.IsNullOrWhiteSpace(configuration) || string.IsNullOrWhiteSpace(platform))
            throw new InvalidOperationException("Could not determine the project's active build configuration and platform.");
        if (platform.Equals("Any CPU", StringComparison.OrdinalIgnoreCase)) platform = "AnyCPU";
        dynamic solutionConfig = dte.Solution.SolutionBuild.ActiveConfiguration;
        // Name is present on the base DTE interface; avoid a DTE2-only member.
        string solutionConfiguration = (string)solutionConfig.Name;
        return new(context.VisualStudioProcessId!.Value, context.RequireSolutionPath(), path, configuration, platform, solutionConfiguration);
    }

    private static string GetDebugCommand(dynamic dte)
    {
        string[] candidates = ["ClassViewContextMenus.ClassViewProject.Debug.Startnewinstance",
            "ProjectAndSolutionContextMenus.Project.Debug.Startnewinstance"];
        foreach (string name in candidates)
        {
            try
            {
                dynamic command = dte.Commands.Item(name, 0);
                if ((bool)command.IsAvailable) return name;
            }
            catch (Exception ex) when (ex is COMException or RuntimeBinderException or ArgumentException) { }
        }
        throw new InvalidOperationException("Visual Studio does not currently expose Start New Instance for the selected project. No project was launched.");
    }

    private static void RequireSelectedProject(dynamic dte, string expectedPath)
    {
        dynamic items = dte.SelectedItems;
        if ((int)items.Count != 1)
            throw new InvalidOperationException("Could not select exactly one project in Solution Explorer.");
        dynamic item = items.Item(1);
        dynamic project = item.Project;
        if (project is null || !PathHelper.SamePath((string)project.FullName, expectedPath))
            throw new InvalidOperationException("The Solution Explorer selection is not the configured project. No project was launched.");
    }

    private sealed record ProjectNode(object Project, string HierarchyPath);

    private static ProjectNode FindProject(dynamic dte, string expectedPath)
    {
        // Cast the collection to IEnumerable; the existing integration uses COM enumeration as well.
        foreach (object project in dte.Solution.Projects)
        {
            if (project is null) continue;
            ProjectNode? result = FindInProject(project, expectedPath, "");
            if (result is not null) return result;
        }
        throw new InvalidOperationException($"'{expectedPath}' is not a loaded project in the selected solution.");
    }

    private static ProjectNode? FindInProject(object projectObject, string expectedPath, string parent)
    {
        dynamic project = projectObject;
        string name, kind;
        try { name = (string)project.Name; kind = (string)project.Kind; }
        catch (Exception ex) when (ex is COMException or RuntimeBinderException) { return null; }
        string hierarchyPath = string.IsNullOrEmpty(parent) ? name : parent + "\\" + name;
        if (!kind.Equals(SolutionFolderKind, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                string? path = (string?)project.FullName;
                if (!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && PathHelper.SamePath(path, expectedPath))
                    return new(projectObject, hierarchyPath);
            }
            catch (Exception ex) when (ex is COMException or RuntimeBinderException or ArgumentException) { }
            return null;
        }
        foreach (dynamic item in project.ProjectItems)
        {
            object? child;
            try { child = item.SubProject; }
            catch (Exception ex) when (ex is COMException or RuntimeBinderException) { continue; }
            if (child is null) continue;
            ProjectNode? result = FindInProject(child, expectedPath, hierarchyPath);
            if (result is not null) return result;
        }
        return null;
    }
}
