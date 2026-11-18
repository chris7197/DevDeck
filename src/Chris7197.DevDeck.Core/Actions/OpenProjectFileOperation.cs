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

    public Task<DevDeckButtonState> GetStateAsync(DeveloperContext? context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = VisualStudioTargetResolver.Match(settings.OpenProjectFileTargets, context?.SolutionPath);
        if (target is null)
            return Task.FromResult(new DevDeckButtonState(Name, "Not configured", DeckStatus.Unavailable,
                "No OpenProjectFileTargets rule matches this solution.", IconName));
        string label = VisualStudioTargetResolver.Label(target, "Open File");
        try
        {
            string path = VisualStudioTargetResolver.ResolvePath(context!.RequireSolutionPath(), target.FilePath);
            return Task.FromResult(new DevDeckButtonState(label, "Ready", DeckStatus.Neutral,
                $"Open {path} in {target.Editor ?? "the default editor"}.", IconName));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return Task.FromResult(new DevDeckButtonState(label, "File missing", DeckStatus.Unavailable, ex.Message, IconName));
        }
    }

    public async Task<DevDeckOperationResult> ExecuteAsync(DevDeckOperationContext context)
    {
        string solution = context.Developer.RequireSolutionPath();
        var target = VisualStudioTargetResolver.Match(settings.OpenProjectFileTargets, solution);
        if (target is null) return new(false, $"No OpenProjectFileTargets rule matches '{solution}'.");
        string path = VisualStudioTargetResolver.ResolvePath(solution, target.FilePath);
        await context.ProgressAsync("Opening...");
        await visualStudio.OpenFileAsync(context.Developer, path, target.Editor, context.CancellationToken);
        return new(true, $"Visual Studio open-file command completed for '{path}' (editor: {target.Editor ?? "default"}).");
    }
}
