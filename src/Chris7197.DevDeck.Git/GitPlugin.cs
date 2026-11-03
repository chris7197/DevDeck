using System.Text.Json;
using chris7197.DevDeck.Core;

namespace chris7197.DevDeck.Git;

/// <summary>Git actions are deployable independently. Null settings inherit existing settings.json values.</summary>
public sealed class GitPlugin : IDevDeckPlugin
{
    public string Id => "com.chris7197.devdeck.git";
    public string Name => "Git";

    public IReadOnlyList<IDevDeckOperation> CreateOperations(DevDeckPluginContext context)
    {
        var host = context.HostServices ?? throw new InvalidOperationException(
            "This Git plugin requires DevDeck host services (Phase 4 or newer).");
        var config = context.Configuration.Deserialize<GitPluginSettings>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        var baseSettings = host.Settings;
        var effective = new DeckSettings
        {
            Remote = string.IsNullOrWhiteSpace(config.Remote) ? baseSettings.Remote : config.Remote.Trim(),
            MainBranch = string.IsNullOrWhiteSpace(config.MainBranch) ? baseSettings.MainBranch : config.MainBranch.Trim(),
            CommitMessage = config.CommitMessage ?? baseSettings.CommitMessage,
            GitCommandTimeoutSeconds = config.CommandTimeoutSeconds ?? baseSettings.GitCommandTimeoutSeconds,
            SaveAllBeforeCommit = config.SaveAllBeforeCommit ?? baseSettings.SaveAllBeforeCommit
        };
        if (string.IsNullOrWhiteSpace(effective.Remote) ||
            string.IsNullOrWhiteSpace(effective.MainBranch) ||
            string.IsNullOrWhiteSpace(effective.CommitMessage) ||
            effective.GitCommandTimeoutSeconds is < 10 or > 3600)
            throw new InvalidOperationException("Invalid Git plugin config.json. Timeout must be 10–3600 seconds.");

        var git = new GitProcess(context.Log, effective.GitCommandTimeoutSeconds);
        var statuses = new GitStatusProvider(effective,
            new GitProcess(context.Log, Math.Min(effective.GitCommandTimeoutSeconds, 15), logCommands: false));
        return [new GitSyncOperation(new GitSyncService(effective, git, context.Log),
            context.Log, effective.SaveAllBeforeCommit, statuses, effective.CommitMessage)];
    }
}

public sealed class GitPluginSettings
{
    public string? Remote { get; set; }
    public string? MainBranch { get; set; }
    public string? CommitMessage { get; set; }
    public int? CommandTimeoutSeconds { get; set; }
    public bool? SaveAllBeforeCommit { get; set; }
}
