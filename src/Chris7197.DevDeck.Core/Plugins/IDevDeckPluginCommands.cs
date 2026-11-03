namespace chris7197.DevDeck.Core;

/// <summary>Optional CLI capabilities implemented by dynamically loaded action plugins.</summary>
public interface IDevDeckInvestigationCommands
{
    Task<DevDeckOperationResult> InvestigateTextAsync(string text, DevDeckOperationContext context);
    Task<InvestigationResult> InvestigateRawAsync(string text, DeveloperContext context,
        InvestigationKind? selectedKind = null, CancellationToken cancellationToken = default);
}

public interface IDevDeckTeamsCommands
{
    Task SignInAsync(Action<string> display, CancellationToken cancellationToken = default);
}

public interface IDevDeckDbFlightCommands
{
    DbFlightPreview Preview(string? targetName = null, string? solutionPath = null);
    Task<DbFlightResult> CaptureAsync(string? targetName = null, string? solutionPath = null,
        CancellationToken cancellationToken = default);
}

public interface IDevDeckLatestLogCommands
{
    string? FindLatest(string solutionPath);
}

public interface IDevDeckLastErrorCommands
{
    LastErrorInfo? FindLast(string solutionPath);
}
