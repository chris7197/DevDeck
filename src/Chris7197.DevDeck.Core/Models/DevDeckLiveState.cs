namespace chris7197.DevDeck.Core;

/// <summary>Visual severity; both the SOOMFON plugin and the simulator render this consistently.</summary>
public enum DeckStatus { Neutral, Success, Warning, Error, Busy, Unavailable }

/// <summary>Dynamic key content. The description is shown as a tooltip by the simulator.</summary>
public sealed record DevDeckButtonState(
    string Title,
    string Subtitle = "",
    DeckStatus Status = DeckStatus.Neutral,
    string Description = "",
    string? IconName = null);

/// <summary>Holds the last observed focused VS solution while the user is in another program.</summary>
public sealed class ActiveDeveloperContext(DeveloperContextProvider provider, DeveloperContext? initial = null)
{
    private DeveloperContext? _last = initial;
    private volatile bool _pinned = initial is not null;
    private long _observationVersion;
    public DeveloperContext? Last => Volatile.Read(ref _last);

    public async Task<DeveloperContext?> PollAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_pinned) return Last;
        long version = Interlocked.Read(ref _observationVersion);
        try
        {
            // Do not SaveAll or change the user's files during a status refresh.
            DeveloperContext detected = await provider.ResolveAsync(null, saveAll: false);
            // Do not overwrite a newer window-event observation with a slow COM poll.
            if (!_pinned && version == Interlocked.Read(ref _observationVersion))
                Volatile.Write(ref _last, detected);
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException or
                                   ArgumentException or System.ComponentModel.Win32Exception)
        {
            // Not focused on VS (or COM momentarily busy): keep the last valid solution.
        }
        return Last;
    }

    /// <summary>Update from a previously observed Visual Studio window, unless a solution is pinned.</summary>
    public void SetObserved(DeveloperContext value)
    {
        if (!_pinned)
        {
            Interlocked.Increment(ref _observationVersion);
            Volatile.Write(ref _last, value);
        }
    }

    public void SetExplicit(DeveloperContext value)
    {
        _pinned = true;
        Interlocked.Increment(ref _observationVersion);
        Volatile.Write(ref _last, value);
    }

    public void UseAutomaticDetection()
    {
        _pinned = false;
        Interlocked.Increment(ref _observationVersion);
    }
}
