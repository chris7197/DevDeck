namespace chris7197.DevDeck.Core;

/// <summary>The same invocation context is supplied by the device host and the CLI.</summary>
public sealed record DevDeckOperationContext(
    DeveloperContext Developer,
    Func<string, Task>? ReportProgress = null,
    CancellationToken CancellationToken = default,
    DeckActionSettings? ActionSettings = null)
{
    public Task ProgressAsync(string text) => ReportProgress?.Invoke(text) ?? Task.CompletedTask;
}

public sealed record DevDeckOperationResult(bool Success, string Message, bool HasConflicts = false, bool Cancelled = false, bool NoChanges = false);

public interface IDevDeckOperation
{
    string Name { get; }
    string PluginActionId { get; }
    IReadOnlyList<string> CliCommands { get; }
    string CliUsage { get; }
    bool SaveAllBeforeExecute => false;
    string? IconName => null;
    bool RequiresSolutionContext => true;
    // Per-key settings can change whether an action requires a solution.
    // For example, DB Flight only needs one when a named DB target is not set.
    bool RequiresSolutionFor(DeckActionSettings? settings) => RequiresSolutionContext;
    bool TryParseCliArguments(IReadOnlyList<string> args, out string? target);
    Task<DevDeckOperationResult> ExecuteAsync(DevDeckOperationContext context);
    Task<DevDeckButtonState> GetConfiguredStateAsync(DeveloperContext? context,
        DeckActionSettings? actionSettings, CancellationToken cancellationToken = default)
        => GetStateAsync(context, cancellationToken);

    // The host may call this when a key appears or during a future refresh cycle.
    Task<DevDeckButtonState> GetStateAsync(DeveloperContext? context, CancellationToken cancellationToken = default)
        => Task.FromResult(new DevDeckButtonState(Name, IconName: IconName));
    string? ConfirmationText(DeveloperContext context) => null;
}
