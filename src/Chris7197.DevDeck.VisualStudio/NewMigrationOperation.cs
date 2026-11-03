using chris7197.DevDeck.Core;
namespace chris7197.DevDeck.VisualStudio;

public sealed class NewMigrationOperation(NewMigrationService migrations, DevDeckStatusProviders statuses) : IDevDeckOperation, IDevDeckCliCommandSource
{
    public IReadOnlyList<DevDeckCliCommand> GetCliCommands() =>
    [
        new("--new-migration", "--new-migration [solution.sln]", CreateCliAsync),
        new("--preview-migration", "--preview-migration [solution.sln]", PreviewCliAsync),
        new("--create-migration-no-open", "--create-migration-no-open [solution.sln]", CreateCliAsync)
    ];

    private async Task<DeveloperContext?> GetCliContextAsync(DevDeckCliContext cli)
    {
        if (cli.Arguments.Count is < 1 or > 2)
            return null;
        return await cli.ResolveContextAsync(cli.Arguments.Count == 2 ? cli.Arguments[1] : null, Name);
    }

    private async Task<int> PreviewCliAsync(DevDeckCliContext cli)
    {
        if (cli.Arguments.Count is < 1 or > 2)
            return await cli.InvalidArgumentsAsync("--preview-migration [solution.sln]");
        DeveloperContext? context = await GetCliContextAsync(cli);
        if (context is null) return 0;
        MigrationPreview preview = migrations.Preview(context.RequireSolutionPath());
        cli.Output.WriteLine("Project: " + preview.ProjectFile);
        cli.Output.WriteLine("Migration: " + preview.MigrationFile);
        cli.Output.WriteLine("Namespace: " + preview.MigrationNamespace);
        cli.Output.WriteLine("Version: " + preview.Version);
        return 0;
    }

    private async Task<int> CreateCliAsync(DevDeckCliContext cli)
    {
        if (cli.Arguments.Count is < 1 or > 2)
            return await cli.InvalidArgumentsAsync(cli.Arguments[0] + " [solution.sln]");
        DeveloperContext? context = await GetCliContextAsync(cli);
        if (context is null) return 0;
        if (cli.Arguments[0] == "--new-migration") return await cli.RunOperationAsync(this, context);
        MigrationCreation created = await migrations.CreateAsync(context.RequireSolutionPath(), openEditor: false);
        cli.Output.WriteLine("Created: " + created.Preview.MigrationFile);
        cli.Output.WriteLine("Version: " + created.Preview.Version);
        cli.Output.WriteLine("Project inclusion: " + created.IncludeMethod);
        return 0;
    }

    public string Name => "New\nMigration";
    public string PluginActionId => "com.chris7197.devdeck.newmigration";
    public DevDeckExecutionKind ExecutionKind => DevDeckExecutionKind.ProjectMutation;
    public string? IconName => DevDeckIcons.NewMigration;

    public Task<DevDeckButtonState> GetConfiguredStateAsync(DeveloperContext? context,
        DeckActionSettings? settings, CancellationToken cancellationToken = default) =>
        context is null ? Task.FromResult(DevDeckStatusService.MissingSolution(this)) :
        Task.FromResult(statuses.MigrationState(context));

    public async Task<DevDeckOperationResult> ExecuteAsync(DevDeckOperationContext context)
    {
        await context.ProgressAsync("Creating\nMigration...");
        MigrationCreation generated = await migrations.CreateAsync(context.Developer.RequireSolutionPath());
        await context.ProgressAsync("Migration\nCreated");
        string message = $"Created [{generated.Preview.Version}] {generated.Preview.MigrationFile}";
        message += Environment.NewLine + $"Project inclusion: {generated.IncludeMethod}.";
        return new DevDeckOperationResult(true, message);
    }
}
