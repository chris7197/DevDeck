namespace chris7197.DevDeck.Core;

/// <summary>
/// Non-blocking reservations for controller actions. A key cannot be pressed twice;
/// operations can overlap within the configured limit, except repository/project
/// mutations (Git sync and migration creation), which run exclusively.
/// </summary>
public sealed class DevDeckActionCoordinator(int maxConcurrent = 3)
{
    private readonly object _sync = new();
    private readonly HashSet<string> _busyKeys = new(StringComparer.Ordinal);
    private int _running;
    private bool _mutationRunning;

    public IDisposable? TryBegin(string keyContext, string operationId)
    {
        bool mutation = operationId is "com.chris7197.devdeck.syncmain" or
            "com.chris7197.devdeck.newmigration" or
            "com.chris7197.devdeck.csharpier.format" or
            "com.chris7197.devdeck.closeothers";
        lock (_sync)
        {
            if (_busyKeys.Contains(keyContext) || _running >= maxConcurrent ||
                (mutation && _mutationRunning)) return null;
            _busyKeys.Add(keyContext);
            _running++;
            if (mutation) _mutationRunning = true;
        }
        return new Lease(this, keyContext, mutation);
    }

    private void End(string keyContext, bool mutation)
    {
        lock (_sync)
        {
            if (!_busyKeys.Remove(keyContext)) return;
            _running--;
            if (mutation) _mutationRunning = false;
        }
    }

    private sealed class Lease(DevDeckActionCoordinator owner, string keyContext, bool mutation) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.End(keyContext, mutation);
        }
    }
}
