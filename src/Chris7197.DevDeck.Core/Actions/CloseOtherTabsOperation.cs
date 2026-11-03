namespace chris7197.DevDeck.Core;

/// <summary>
/// One Stream Dock action for both IDEs. Dispatch is based on the real foreground
/// process, NOT the remembered controller profile or a stale Visual Studio context.
/// </summary>
public sealed class CloseOtherTabsOperation(
    IForegroundWindowReader foreground,
    IVisualStudioSolutionDiscovery visualStudio,
    IDevDeckOperation visualStudioClose,
    IDevDeckOperation ssmsClose) : IDevDeckOperation
{
    public const string ActionId = "com.chris7197.devdeck.closeothers";
    public const string OldVisualStudioActionId = "com.chris7197.devdeck.vs.closeothers";
    public const string OldSsmsActionId = "com.chris7197.devdeck.ssms.closeothers";

    public string Name => "Close Other Tabs";
    public string PluginActionId => ActionId;
    public string? IconName => DevDeckIcons.CloseOthers;
    public IReadOnlyList<string> CliCommands => [];
    public string CliUsage => "";
    // SSMS needs no solution. Visual Studio gets the *focused* process PID on demand.
    public bool RequiresSolutionContext => false;
    public bool TryParseCliArguments(IReadOnlyList<string> args, out string? target)
    {
        target = null;
        return false;
    }

    public Task<DevDeckButtonState> GetStateAsync(
        DeveloperContext? context, CancellationToken cancellationToken = default)
    {
        string subtitle = GetForegroundTarget(foreground.Read()) switch
        {
            CloseTabsTarget.VisualStudio => "Visual Studio",
            CloseTabsTarget.SqlServer => "SSMS",
            _ => "Focus VS / SSMS"
        };
        return Task.FromResult(new DevDeckButtonState("Close Others", subtitle,
            subtitle == "Focus VS / SSMS" ? DeckStatus.Unavailable : DeckStatus.Neutral,
            "Keeps the current document tab in the foreground IDE. " +
            "VS uses its native command; SSMS requires Ctrl+Alt+Shift+W bound to Close All But This. " +
            "Unsaved changes may prompt, depending on IDE settings.", IconName: IconName));
    }

    public async Task<DevDeckOperationResult> ExecuteAsync(DevDeckOperationContext context)
    {
        // Never use the last remembered profile to choose a destructive operation.
        switch (GetForegroundTarget(foreground.Read()))
        {
            case CloseTabsTarget.VisualStudio:
            {
                // Snapshot focus *before* any COM queueing or prompt can change it.
                VisualStudioSolutionInstance? selected =
                    await visualStudio.GetFocusedSolutionAsync(context.CancellationToken);
                if (selected is null)
                    return new(false, "Focus a Visual Studio window with a saved solution and an active document.");

                string path = Path.GetFullPath(selected.SolutionPath);
                var developer = new DeveloperContext(Path.GetDirectoryName(path)!, path,
                    Path.GetFileNameWithoutExtension(path))
                {
                    VisualStudioProcessId = selected.ProcessId
                };
                // The existing COM action revalidates PID, solution and active document
                // immediately before executing File.CloseAllButThis.
                return await visualStudioClose.ExecuteAsync(context with { Developer = developer });
            }
            case CloseTabsTarget.SqlServer:
                // Reuse the foreground-guarded SSMS shortcut implementation. It never
                // activates a background window or sends keys to a remembered profile.
                return await ssmsClose.ExecuteAsync(context);
            default:
                return new(false, "Focus Visual Studio or SSMS before pressing Close Others. No tabs were closed.");
        }
    }

    private enum CloseTabsTarget { None, VisualStudio, SqlServer }

    private static CloseTabsTarget GetForegroundTarget(ForegroundWindowInfo? window) =>
        window?.ProcessName.ToLowerInvariant() switch
        {
            "devenv" or "devenv.exe" => CloseTabsTarget.VisualStudio,
            "ssms" or "ssms.exe" => CloseTabsTarget.SqlServer,
            _ => CloseTabsTarget.None
        };
}
