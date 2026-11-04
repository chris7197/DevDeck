using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;

namespace chris7197.DevDeck.Core;

/// <summary>
/// Opens Visual Studio's INTEGRATED terminal for the project containing the active editor.
/// Uses the IDE's native "Open in Terminal" project command, which chooses the project
/// directory, rather than launching a separate process or opening at the solution root.
/// Set Developer PowerShell as Visual Studio's default terminal profile.
/// </summary>
public sealed class OpenProjectDeveloperPromptOperation(VisualStudioComDispatcher? dispatcher = null) : IDevDeckOperation
{
    private readonly VisualStudioComDispatcher _dispatcher = dispatcher ?? VisualStudioComDispatcher.Shared;

    // Deliberately reuse the existing Stream Dock action ID, icon and profile assignment.
    public const string ActionId = "com.chris7197.devdeck.projectdevcmd";
    public string Name => "Project Developer PowerShell";
    public string PluginActionId => ActionId;
    public string? IconName => DevDeckIcons.ProjectDevCmd;
    public IReadOnlyList<string> CliCommands => [];
    public string CliUsage => "";
    public bool RequiresSolutionContext => true;

    public bool TryParseCliArguments(IReadOnlyList<string> args, out string? target)
    {
        target = null;
        return false;
    }

    public Task<DevDeckButtonState> GetStateAsync(DeveloperContext? context,
        CancellationToken cancellationToken = default) => Task.FromResult(new DevDeckButtonState(
            "Dev PS", context?.SolutionName ?? "Focus VS",
            context is null ? DeckStatus.Unavailable : DeckStatus.Neutral,
            "Open Visual Studio's docked terminal in the active document's project folder. " +
            "Set the default terminal profile to Developer PowerShell in Visual Studio options.",
            IconName: IconName));

    public async Task<DevDeckOperationResult> ExecuteAsync(DevDeckOperationContext context)
    {
        uint pid = context.Developer.VisualStudioProcessId ?? throw new InvalidOperationException(
            "Select a running Visual Studio instance with a project file open.");
        string expectedSolution = context.Developer.RequireSolutionPath();

        // Resolve the document, select the owning project and execute the native terminal
        // command in ONE STA operation. Never carry COM objects across apartments.
        (string resolvedProjectName, string resolvedDirectory) = await _dispatcher.InvokeAsync(() =>
        {
            (string project, string directory)? opened = null;
            bool found = VisualStudioRot.Visit((dte, currentPid, hwnd, solution) =>
            {
                if (currentPid != pid) return false;
                if (!PathHelper.SamePath(solution, expectedSolution))
                    throw new InvalidOperationException("The selected Visual Studio instance changed solutions.");
                if (!VisualStudioRot.IsValidWindow(hwnd))
                    throw new InvalidOperationException("The Visual Studio window has closed.");

                dynamic document = dte.ActiveDocument;
                if (document is null)
                    throw new InvalidOperationException("Open a file in a loaded Visual Studio project first.");

                dynamic project = document.ProjectItem?.ContainingProject;
                if (project is null)
                    throw new InvalidOperationException("The active document is not part of a loaded project.");

                string projectFile = (string)project.FullName;
                if (string.IsNullOrWhiteSpace(projectFile) || !File.Exists(projectFile))
                    throw new InvalidOperationException("Could not determine the active file's project directory.");

                string projectName = (string)project.Name;
                string directory = Path.GetDirectoryName(Path.GetFullPath(projectFile))!;
                // DTE objects obtained from the Running Object Table are exposed as COM
                // automation objects. ToolWindows is DTE2-only and is not always
                // late-bound; the standard DTE Windows collection is supported here.
                // Solution Explorer's well-known window GUID (vsWindowKindSolutionExplorer).
                dynamic explorerWindow = dte.Windows.Item("{3AE79031-E1BC-11D0-8F78-00A0C9110057}");
                dynamic explorer = explorerWindow.Object;
                dynamic? node = FindProjectNode(explorer.UIHierarchyItems, projectFile, depth: 0);
                if (node is null)
                    throw new InvalidOperationException(
                        $"Could not find '{projectName}' in Solution Explorer. Select Solution view (not Folder view).");

                // A contextual command must be invoked on the project node; opening
                // View.Terminal alone starts at the solution root, not this directory.
                explorerWindow.Activate();
                node.Select(1); // EnvDTE.vsUISelectionType.vsUISelectionTypeSelect
                string? command = FindProjectTerminalCommand(dte);
                if (command is null)
                    throw new InvalidOperationException(
                        "Visual Studio did not expose an available 'Open in Terminal' project command to automation. " +
                        "Try right-clicking the project in Solution Explorer > Open in Terminal. " +
                        "This action deliberately does not open an external shell or a terminal in the wrong directory.");

                dte.ExecuteCommand(command);
                opened = (projectName, directory);
                return true;
            });
            if (!found)
                throw new InvalidOperationException("The selected Visual Studio instance is no longer available.");
            return opened ?? throw new InvalidOperationException("Visual Studio did not open a terminal.");
        }, mayModifyVisualStudio: true, cancellationToken: context.CancellationToken);

        // We cannot inspect which terminal profile the user selected in VS options.
        return new(true, $"Requested Visual Studio's integrated terminal for {resolvedProjectName} in {resolvedDirectory}. " +
            "For Developer PowerShell, set it as the default in Tools > Options > Environment > Terminal.");
    }

    private static dynamic? FindProjectNode(dynamic items, string projectFile, int depth)
    {
        // Normal projects and projects nested under solution folders are supported.
        // We need not traverse the contents of unrelated .csproj projects.
        if (depth > 12) return null;
        foreach (dynamic node in items)
        {
            dynamic? associated = null;
            try { associated = node.Object; }
            catch (Exception ex) when (ex is COMException or RuntimeBinderException) { }

            if (associated is not null)
            {
                string? file = null;
                try { file = (string?)associated.FullName; }
                catch (Exception ex) when (ex is COMException or RuntimeBinderException) { }

                if (!string.IsNullOrWhiteSpace(file) && PathHelper.SamePath(file, projectFile))
                    return node;

                // A different project node can be skipped entirely: its descendants
                // are source folders/files, not other top-level projects.
                if (!string.IsNullOrWhiteSpace(file) &&
                    (file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                     file.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) ||
                     file.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase) ||
                     file.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)))
                    continue;
            }

            dynamic? children = null;
            try { children = node.UIHierarchyItems; }
            catch (Exception ex) when (ex is COMException or RuntimeBinderException) { }
            if (children is null) continue;
            dynamic? matching = FindProjectNode(children, projectFile, depth + 1);
            if (matching is not null) return matching;
        }
        return null;
    }

    private static string? FindProjectTerminalCommand(dynamic dte)
    {
        // Visual Studio's project context command has differed between IDE versions.
        // Query actual command availability at runtime rather than guessing that
        // View.Terminal supports a working-directory argument (it does not).
        string[] knownNames =
        [
            "Project.OpenInTerminal", "File.OpenInTerminal", "Tools.OpenInTerminal",
            "View.OpenInTerminal", "SolutionExplorer.OpenInTerminal"
        ];
        foreach (string name in knownNames)
        {
            try
            {
                dynamic candidate = dte.Commands.Item(name);
                if ((bool)candidate.IsAvailable) return (string)candidate.Name;
            }
            catch (Exception ex) when (ex is COMException or RuntimeBinderException or ArgumentException) { }
        }

        // A fallback for builds that register the menu using a different prefix.
        // Only a command that explicitly opens *in* terminal qualifies: plain
        // View.Terminal is not a substitute because it defaults to solution root.
        foreach (dynamic candidate in dte.Commands)
        {
            string? name;
            try { name = (string?)candidate.Name; }
            catch (Exception ex) when (ex is COMException or RuntimeBinderException) { continue; }
            if (string.IsNullOrWhiteSpace(name) ||
                !name.Contains("OpenInTerminal", StringComparison.OrdinalIgnoreCase)) continue;
            try { if ((bool)candidate.IsAvailable) return name; }
            catch (Exception ex) when (ex is COMException or RuntimeBinderException) { }
        }
        return null;
    }
}
