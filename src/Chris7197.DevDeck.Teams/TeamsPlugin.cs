using System.Text.Json;
using chris7197.DevDeck.Core;

namespace chris7197.DevDeck.Teams;

/// <summary>Owns Microsoft Teams channel operations and their auth/poll configuration.</summary>
public sealed class TeamsPlugin : IDevDeckPlugin
{
    public string Id => "com.chris7197.devdeck.teams";
    public string Name => "Teams";

    public IReadOnlyList<IDevDeckOperation> CreateOperations(DevDeckPluginContext context)
    {
        var host = context.HostServices ?? throw new InvalidOperationException("Teams requires Phase 5 host services.");
        var options = context.Configuration.Deserialize<TeamsPluginSettings>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        var original = host.Settings.Teams;
        var settings = new TeamsAuthSettings
        {
            TenantId = options.TenantId ?? original.TenantId,
            ClientId = options.ClientId ?? original.ClientId,
            PollSeconds = options.PollSeconds ?? original.PollSeconds
        };
        if (settings.PollSeconds is < 15 or > 3600)
            throw new InvalidOperationException("Teams PollSeconds must be 15–3600.");
        return [new TeamsChannelOperation(new TeamsChannelService(settings))];
    }
}

public sealed class TeamsPluginSettings
{
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public int? PollSeconds { get; set; }
}
