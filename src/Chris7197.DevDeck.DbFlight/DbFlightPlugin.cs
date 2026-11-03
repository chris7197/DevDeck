using System.Text.Json;
using chris7197.DevDeck.Core;

namespace chris7197.DevDeck.DbFlight;

public sealed class DbFlightPlugin : IDevDeckPlugin
{
    public string Id => "com.chris7197.devdeck.dbflight.plugin";
    public string Name => "DB Flight Recorder";

    public IReadOnlyList<IDevDeckOperation> CreateOperations(DevDeckPluginContext context)
    {
        var host = context.HostServices ?? throw new InvalidOperationException("DB Flight requires Phase 5 host services.");
        var config = context.Configuration.Deserialize<DbFlightPluginSettings>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        // Null means inherit settings.json; an explicitly empty array removes inherited entries.
        var targets = config.Targets ?? host.Settings.DbFlightRecorderTargets;
        var mappings = config.ConfigMappings ?? host.Settings.DbFlightRecorderConfigMappings;
        if (targets.Any(t => t is null || string.IsNullOrWhiteSpace(t.Name) ||
                string.IsNullOrWhiteSpace(t.Server) || string.IsNullOrWhiteSpace(t.Database)) ||
            targets.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count ||
            mappings.Any(m => m is null || string.IsNullOrWhiteSpace(m.PathFilter) ||
                string.IsNullOrWhiteSpace(m.ConfigPath) || string.IsNullOrWhiteSpace(m.ConnectionStringName)))
            throw new InvalidOperationException("Invalid DB Flight targets or config mappings.");
        var effective = new DeckSettings { DbFlightRecorderTargets = targets,
            DbFlightRecorderConfigMappings = mappings };
        return [new DbFlightRecorderOperation(new DbFlightRecorderService(effective))];
    }
}

public sealed class DbFlightPluginSettings
{
    public List<DbFlightRecorderTarget>? Targets { get; set; }
    public List<DbFlightRecorderConfigMapping>? ConfigMappings { get; set; }
}
