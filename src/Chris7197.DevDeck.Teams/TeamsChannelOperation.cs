using System.Diagnostics;
using System.Runtime.InteropServices;

using chris7197.DevDeck.Core;

namespace chris7197.DevDeck.Teams;

public sealed class TeamsChannelOperation(TeamsChannelService teams) : IDevDeckOperation, IDevDeckTeamsCommands, IDevDeckCliCommandSource
{
    public Task SignInAsync(Action<string> display, CancellationToken cancellationToken = default) =>
        teams.SignInAsync(display, cancellationToken);
    public IReadOnlyList<DevDeckCliCommand> GetCliCommands() =>
    [
        new("--teams-login", "--teams-login", RunCliAsync),
        new("--teams-status", "--teams-status <teamId> <channelId> [label]", RunCliAsync)
    ];

    private async Task<int> RunCliAsync(DevDeckCliContext cli)
    {
        if (cli.Arguments[0] == "--teams-login")
        {
            if (cli.Arguments.Count != 1) return await cli.InvalidArgumentsAsync("--teams-login");
            await SignInAsync(cli.Output.WriteLine);
            return 0;
        }
        if (cli.Arguments.Count is < 3 or > 4)
            return await cli.InvalidArgumentsAsync("--teams-status <teamId> <channelId> [label]");
        var channel = new DeckActionSettings
        {
            TeamId = cli.Arguments[1], ChannelId = cli.Arguments[2],
            Label = cli.Arguments.Count == 4 ? cli.Arguments[3] : "Teams"
        };
        DevDeckButtonState state = await cli.Runtime.States.GetConfiguredStateAsync(this, null, channel);
        cli.Output.WriteLine($"{state.Title}: {state.Subtitle} — {state.Description}");
        return state.Status == DeckStatus.Unavailable ? 1 : 0;
    }

    public string Name => "Teams Channel";
    public string PluginActionId => "com.chris7197.devdeck.teamschannel";
    public string? IconName => DevDeckIcons.TeamsChannel;
    public bool RequiresSolutionContext => false;

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
