using System.Data;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace chris7197.DevDeck.Core;

public sealed class DbFlightRecorderOperation(DbFlightRecorderService service) : IDevDeckOperation
{
    public string Name => "DB Flight";
    public string PluginActionId => "com.chris7197.devdeck.dbflight";
    public string? IconName => DevDeckIcons.DbFlight;
    public IReadOnlyList<string> CliCommands => [];
    public string CliUsage => "";
    public bool RequiresSolutionContext => false;
    public bool RequiresSolutionFor(DeckActionSettings? settings) => string.IsNullOrWhiteSpace(settings?.DbTarget);
    public bool TryParseCliArguments(IReadOnlyList<string> args, out string? target) { target = null; return false; }
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
