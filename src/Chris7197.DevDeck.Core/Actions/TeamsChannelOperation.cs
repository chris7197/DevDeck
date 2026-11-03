using System.Diagnostics;
using System.Runtime.InteropServices;

namespace chris7197.DevDeck.Core;

public sealed class TeamsChannelOperation(TeamsChannelService teams) : IDevDeckOperation
{
    public string Name => "Teams Channel";
    public string PluginActionId => "com.chris7197.devdeck.teamschannel";
    public string? IconName => DevDeckIcons.TeamsChannel;
    public IReadOnlyList<string> CliCommands => [];
    public string CliUsage => "";
    public bool RequiresSolutionContext => false;
    public bool TryParseCliArguments(IReadOnlyList<string> args, out string? target) { target = null; return false; }

    public async Task<DevDeckButtonState> GetConfiguredStateAsync(DeveloperContext? context, DeckActionSettings? options,
        CancellationToken cancellationToken = default)
    {
        if (options is null || string.IsNullOrWhiteSpace(options.ChannelId) || string.IsNullOrWhiteSpace(options.TeamId))
            return new("Teams", "Set channel", DeckStatus.Unavailable, "Set Team ID and Channel ID in this button's properties.", IconName: IconName);
        if (!teams.IsConfigured) return new(options.Label ?? "Teams", "Set auth", DeckStatus.Unavailable,
            "Configure Teams.TenantId and Teams.ClientId, then sign in via --teams-login.", IconName: IconName);
        ChannelSnapshot snapshot = await teams.GetSnapshotAsync(options, cancellationToken);
        return teams.Describe(options, snapshot);
    }

    public async Task<DevDeckOperationResult> ExecuteAsync(DevDeckOperationContext context)
    {
        DeckActionSettings options = context.ActionSettings ?? throw new InvalidOperationException("Configure the Teams channel for this key.");
        string link = await teams.GetChannelUrlAsync(options, context.CancellationToken);
        using var process = Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
        await teams.AcknowledgeAsync(options, context.CancellationToken);
        return new(true, "Opened Teams and cleared the locally tracked new-message count.");
    }
}
