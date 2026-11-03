using System.Text.Json;
using chris7197.DevDeck.Core;

namespace chris7197.DevDeck.Diagnostics;

public sealed class DiagnosticsPlugin : IDevDeckPlugin
{
    public string Id => "com.chris7197.devdeck.diagnostics";
    public string Name => "Log Diagnostics";

    public IReadOnlyList<IDevDeckOperation> CreateOperations(DevDeckPluginContext context)
    {
        var host = context.HostServices ?? throw new InvalidOperationException("Diagnostics requires Phase 5 host services.");
        var config = context.Configuration.Deserialize<DiagnosticsPluginSettings>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        var baseSettings = host.Settings;
        var effective = new DeckSettings
        {
            LogFilePattern = config.LogFilePattern ?? baseSettings.LogFilePattern,
            LastErrorMaxReadBytes = config.LastErrorMaxReadBytes ?? baseSettings.LastErrorMaxReadBytes,
            LastErrorSearchFileCount = config.LastErrorSearchFileCount ?? baseSettings.LastErrorSearchFileCount
        };
        if (string.IsNullOrWhiteSpace(effective.LogFilePattern) ||
            effective.LogFilePattern.IndexOfAny(['/', '\\']) >= 0 ||
            effective.LastErrorMaxReadBytes is < 65536 or > 33554432 ||
            effective.LastErrorSearchFileCount is < 1 or > 100)
            throw new InvalidOperationException("Diagnostics configuration has invalid file pattern or read limits.");
        var latest = new LatestErrorLogService(new LogFileResolver(effective), context.Log);
        var errors = new LastErrorService(latest, effective);
        var statusProviders = new DevDeckStatusProviders(latest, errors,
            new MigrationProjectResolver(host.Settings));
        if (host.Navigator is null)
            throw new InvalidOperationException("Host navigation service is unavailable.");
        return [new LatestErrorLogOperation(latest, statusProviders),
                new LastErrorOperation(errors, host.Navigator, context.Log, statusProviders)];
    }
}

public sealed class DiagnosticsPluginSettings
{
    public string? LogFilePattern { get; set; }
    public int? LastErrorMaxReadBytes { get; set; }
    public int? LastErrorSearchFileCount { get; set; }
}
