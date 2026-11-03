namespace chris7197.DevDeck.Core;

public sealed class NewMigrationOperation(NewMigrationService migrations) : IDevDeckOperation
{
    public string Name => "New\nMigration";
    public string PluginActionId => "com.chris7197.devdeck.newmigration";
    public string? IconName => DevDeckIcons.NewMigration;
    public IReadOnlyList<string> CliCommands => ["--new-migration"];
    public string CliUsage => "--new-migration [solution.sln]";

    public bool TryParseCliArguments(IReadOnlyList<string> args, out string? target)
    {
        target = args.Count == 2 ? args[1] : null;
        return (args.Count is 1 or 2) && args[0] == "--new-migration";
    }

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
