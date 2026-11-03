using System.Data;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

using chris7197.DevDeck.Core;

namespace chris7197.DevDeck.DbFlight;

public sealed class DbFlightRecorderOperation(DbFlightRecorderService service) : IDevDeckOperation, IDevDeckDbFlightCommands, IDevDeckCliCommandSource
{
    public DbFlightPreview Preview(string? targetName = null, string? solutionPath = null) =>
        service.Preview(targetName, solutionPath);

    public Task<DbFlightResult> CaptureAsync(string? targetName = null, string? solutionPath = null,
        CancellationToken cancellationToken = default) => service.CaptureAsync(targetName, solutionPath, cancellationToken);

    public IReadOnlyList<DevDeckCliCommand> GetCliCommands() =>
    [
        new("--db-flight-preview", "--db-flight-preview <targetName>", RunCliAsync),
        new("--db-flight-capture", "--db-flight-capture <targetName>", RunCliAsync),
        new("--db-flight-preview-solution", "--db-flight-preview-solution [solution.sln]", RunCliAsync),
        new("--db-flight-capture-solution", "--db-flight-capture-solution [solution.sln]", RunCliAsync)
    ];

    private async Task<int> RunCliAsync(DevDeckCliContext cli)
    {
        string command = cli.Arguments[0];
        bool solution = command.EndsWith("-solution", StringComparison.Ordinal);
        bool capture = command.Contains("-capture", StringComparison.Ordinal);
        bool invalidArguments = solution
            ? cli.Arguments.Count < 1 || cli.Arguments.Count > 2
            : cli.Arguments.Count != 2;
        if (invalidArguments)
            return await cli.InvalidArgumentsAsync(command + (solution ? " [solution.sln]" : " <targetName>"));
        DeveloperContext? developer = solution
            ? await cli.ResolveContextAsync(cli.Arguments.Count == 2 ? cli.Arguments[1] : null, Name)
            : null;
        if (solution && developer is null) return 0;
        string? targetName = solution ? null : cli.Arguments[1];
        string? path = developer?.RequireSolutionPath();
        DbFlightPreview target = Preview(targetName, path);
        if (capture)
        {
            cli.Output.WriteLine($"Starting read-only capture of {target.Server} / {target.Database}...");
            DbFlightResult result = await CaptureAsync(targetName, path);
            cli.Output.WriteLine($"Sections captured: {result.SectionsSucceeded}; failed: {result.SectionsFailed}");
            cli.Output.WriteLine("Report: " + result.SummaryFile);
            return result.SectionsSucceeded > 0 && result.SectionsFailed == 0 ? 0 : 1;
        }
        cli.Output.WriteLine($"{target.TargetName}: {target.Server} / {target.Database}");
        if (solution) cli.Output.WriteLine("Config source: " + target.Source);
        cli.Output.WriteLine($"SQL text: {target.SqlText}; plans: {target.Plans}");
        cli.Output.WriteLine("Preview only — no SQL connection, and no passwords printed.");
        return 0;
    }

    public string Name => "DB Flight";
    public string PluginActionId => "com.chris7197.devdeck.dbflight";
    public string? IconName => DevDeckIcons.DbFlight;
    public bool RequiresSolutionContext => false;
    public bool RequiresSolutionFor(DeckActionSettings? settings) => string.IsNullOrWhiteSpace(settings?.DbTarget);
    public Task<DevDeckButtonState> GetConfiguredStateAsync(DeveloperContext? context, DeckActionSettings? settings,
        CancellationToken ct = default)
    {
        try
        {
            DbFlightPreview preview = service.Preview(settings?.DbTarget, context?.SolutionPath);
            string title = settings?.Label ?? "DB Flight";
            return Task.FromResult(new DevDeckButtonState(title, preview.TargetName, DeckStatus.Neutral,
                $"{preview.Server} / {preview.Database}\nSource: {preview.Source}\nOnly captures when pressed.", IconName: IconName));
        }
        catch (Exception error) when (error is InvalidOperationException or FileNotFoundException or NotSupportedException
            or System.Xml.XmlException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return Task.FromResult(new DevDeckButtonState(settings?.Label ?? "DB Flight", "Unconfigured",
                DeckStatus.Unavailable, error.Message, IconName: IconName));
        }
    }

    public async Task<DevDeckOperationResult> ExecuteAsync(DevDeckOperationContext context)
    {
        await context.ProgressAsync("Recording...");
        DbFlightResult result = await service.CaptureAsync(
            context.ActionSettings?.DbTarget, context.Developer.SolutionPath, context.CancellationToken);
        DeveloperReportStore.Open(result.SummaryFile);
        return new(result.SectionsSucceeded > 0 && result.SectionsFailed == 0,
            $"DB Flight captured {result.SectionsSucceeded} sections; {result.SectionsFailed} errors. Report: {result.SummaryFile}");
    }
}
