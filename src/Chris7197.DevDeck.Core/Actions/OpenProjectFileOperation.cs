namespace chris7197.DevDeck.Core;

public sealed class OpenProjectFileOperation(DeckSettings settings, VisualStudioTargetService visualStudio) : IDevDeckOperation
{
    public const string ActionId = "com.chris7197.devdeck.openprojectfile";
    public string Name => "Open Project File";
    public string PluginActionId => ActionId;
    public string? IconName => DevDeckIcons.OpenProjectFile;
    public IReadOnlyList<string> CliCommands => [];
    public string CliUsage => "";
    public bool TryParseCliArguments(IReadOnlyList<string> args, out string? target) { target = null; return false; }

    public Task<DevDeckButtonState> GetStateAsync(DeveloperContext? context, CancellationToken cancellationToken = default) =>
        GetConfiguredStateAsync(context, null, cancellationToken);

    public Task<DevDeckButtonState> GetConfiguredStateAsync(DeveloperContext? context, DeckActionSettings? options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string label = VisualStudioTargetResolver.Label(null, "Open File", options);
        if (string.IsNullOrWhiteSpace(context?.SolutionPath))
            return Task.FromResult(new DevDeckButtonState(label, "Focus VS", DeckStatus.Unavailable,
                "Focus Visual Studio with an open solution.", IconName));
        try
        {
            var target = VisualStudioTargetResolver.ResolveFileTarget(settings.OpenProjectFileTargets, context.SolutionPath, options);
            if (target is null)
                return Task.FromResult(new DevDeckButtonState(label, options?.ProjectFileUseCustom == true ? "Wrong solution" : "Not configured",
                    DeckStatus.Unavailable, "No target matches this solution.", IconName));
            label = VisualStudioTargetResolver.Label(target, "Open File", options);
            string path = VisualStudioTargetResolver.ResolvePath(context.RequireSolutionPath(), target.FilePath);
            return Task.FromResult(new DevDeckButtonState(label, "Ready", DeckStatus.Neutral,
                $"Open {path} in {target.Editor ?? "the default editor"}.", IconName));
        }
        catch (FileNotFoundException ex)
        {
            return Task.FromResult(new DevDeckButtonState(label, "File missing", DeckStatus.Unavailable, ex.Message, IconName));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return Task.FromResult(new DevDeckButtonState(label, "Check settings", DeckStatus.Unavailable, ex.Message, IconName));
        }
    }

    public async Task<DevDeckOperationResult> ExecuteAsync(DevDeckOperationContext context)
    {
        string solution = context.Developer.RequireSolutionPath();
        var target = VisualStudioTargetResolver.ResolveFileTarget(settings.OpenProjectFileTargets, solution, context.ActionSettings);
        if (target is null) return new(false, $"No configured target matches '{solution}'.");
        string path = VisualStudioTargetResolver.ResolvePath(solution, target.FilePath);
        await context.ProgressAsync("Opening...");
        await visualStudio.OpenFileAsync(context.Developer, path, target.Editor, context.CancellationToken);
        return new(true, $"Visual Studio open-file command completed for '{path}' (editor: {target.Editor ?? "default"}).");
    }
}
