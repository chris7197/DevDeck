namespace chris7197.DevDeck.Core;

/// <summary>Executes Visual Studio's native command in the selected (PID + solution) instance.</summary>
public sealed class CloseOtherVisualStudioTabsOperation(VisualStudioComDispatcher? dispatcher = null) : IDevDeckOperation
{
    private readonly VisualStudioComDispatcher _dispatcher = dispatcher ?? VisualStudioComDispatcher.Shared;
    public string Name => "Close Other Tabs";
    public string PluginActionId => "com.chris7197.devdeck.vs.closeothers";
    public string? IconName => DevDeckIcons.CloseOthers;
    public IReadOnlyList<string> CliCommands => [];
    public string CliUsage => "";
    public bool RequiresSolutionContext => true;
    public bool TryParseCliArguments(IReadOnlyList<string> args, out string? target) { target = null; return false; }

    public Task<DevDeckButtonState> GetStateAsync(DeveloperContext? context, CancellationToken cancellationToken = default) =>
        Task.FromResult(new DevDeckButtonState("Close Others", context?.SolutionName ?? "Focus VS",
            context is null ? DeckStatus.Unavailable : DeckStatus.Neutral,
            "Keep the active Visual Studio document; close other document tabs. Modified files may prompt to save.", IconName: IconName));

    public async Task<DevDeckOperationResult> ExecuteAsync(DevDeckOperationContext context)
    {
        uint pid = context.Developer.VisualStudioProcessId ?? throw new InvalidOperationException(
            "Choose a running Visual Studio instance; this action cannot target an unbound solution path.");
        string expectedPath = context.Developer.RequireSolutionPath();
        await _dispatcher.InvokeAsync(() =>
        {
            bool found = VisualStudioRot.Visit((dte, currentPid, hwnd, path) =>
            {
                if (currentPid != pid) return false;
                if (!PathHelper.SamePath(path, expectedPath))
                    throw new InvalidOperationException("Visual Studio changed solutions; no tabs were closed.");
                if (!VisualStudioRot.IsValidWindow(hwnd))
                    throw new InvalidOperationException("Visual Studio window is no longer available.");
                if (dte.ActiveDocument is null)
                    throw new InvalidOperationException("Select an open document tab in Visual Studio first.");
                dte.ExecuteCommand("File.CloseAllButThis");
                return true;
            });
            if (!found) throw new InvalidOperationException("The selected Visual Studio instance has closed.");
            return true;
        }, mayModifyVisualStudio: true, cancellationToken: context.CancellationToken);
        return new(true, "Closed other Visual Studio tabs. Any unsaved documents are handled by Visual Studio.");
    }
}
