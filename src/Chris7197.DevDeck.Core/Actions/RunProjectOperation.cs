namespace chris7197.DevDeck.Core;

public sealed class RunProjectOperation(DeckSettings settings, VisualStudioTargetService visualStudio,
    VisualStudioProjectBuilder builder, IDevDeckPromptService prompts) : IDevDeckOperation
{
    public const string ActionId = "com.chris7197.devdeck.runproject";
    public string Name => "Run Project";
    public string PluginActionId => ActionId;
    public string? IconName => DevDeckIcons.RunProject;
    public IReadOnlyList<string> CliCommands => [];
    public string CliUsage => "";
    public bool TryParseCliArguments(IReadOnlyList<string> args, out string? target) { target = null; return false; }

    public Task<DevDeckButtonState> GetStateAsync(DeveloperContext? context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = VisualStudioTargetResolver.Match(settings.RunProjectTargets, context?.SolutionPath);
        if (target is null)
            return Task.FromResult(new DevDeckButtonState(Name, "Not configured", DeckStatus.Unavailable,
                "No RunProjectTargets rule matches this solution.", IconName));
        string label = VisualStudioTargetResolver.Label(target, "Run Project");
        try
        {
            string path = VisualStudioTargetResolver.ResolvePath(context!.RequireSolutionPath(), target.ProjectPath);
            return Task.FromResult(new DevDeckButtonState(label, "Ready", DeckStatus.Neutral,
                $"{target.BuildMode} and debug {path}.", IconName));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return Task.FromResult(new DevDeckButtonState(label, "Project missing", DeckStatus.Unavailable, ex.Message, IconName));
        }
    }

    public async Task<DevDeckOperationResult> ExecuteAsync(DevDeckOperationContext context)
    {
        string solution = context.Developer.RequireSolutionPath();
        var target = VisualStudioTargetResolver.Match(settings.RunProjectTargets, solution);
        if (target is null) return new(false, $"No RunProjectTargets rule matches '{solution}'.");
        string path = VisualStudioTargetResolver.ResolvePath(solution, target.ProjectPath);
        CancellationToken ct = context.CancellationToken;
        // Verify the exact running instance before prompting; a simulator may be pinned to a file only.
        var bound = await visualStudio.BindAsync(context.Developer, ct);
        if (target.Confirm)
        {
            string description = $"{target.BuildMode} and debug {Path.GetFileName(path)} in {Path.GetFileName(solution)}. " +
                "This starts the real project; verify any database connection settings before continuing.";
            string? choice = await prompts.ChooseAsync(new("Run Visual Studio project",
                description.Length > 400 ? description[..397] + "..." : description,
                [new("run", "Run project", 'R'), new("cancel", "Cancel", 'C')]), ct);
            if (choice != "run") return new(false, "Cancelled.", Cancelled: true);
        }

        await context.ProgressAsync("Preparing...");
        var project = await visualStudio.PrepareRunAsync(bound, path, target.SaveAll, ct);
        if (!target.BuildMode.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            await context.ProgressAsync(target.BuildMode.Equals("Rebuild", StringComparison.OrdinalIgnoreCase)
                ? "Rebuilding..." : "Building...");
            bool success = await builder.BuildAsync(project, target.BuildMode, target.BuildTimeoutSeconds, ct);
            if (!success)
                return new(false, $"Build failed for '{path}'. Debugging was not started. See DevDeck log.");
        }

        ct.ThrowIfCancellationRequested();
        await context.ProgressAsync("Starting...");
        await visualStudio.StartDebuggingAsync(bound, project, ct);
        return new(true, $"Requested Visual Studio debugging for '{path}'. Startup projects were not changed.");
    }
}
