using System.Collections.Concurrent;

namespace chris7197.DevDeck.Core;

/// <summary>
/// Tracks visible key instances and publishes only changed states. Used unchanged by the
/// physical Stream Dock plugin and the WinForms simulator. Never executes button actions.
/// </summary>
public sealed class DevDeckStatusMonitor(
    DevDeckRuntime runtime,
    ActiveDeveloperContext activeContext,
    DevDeckProfiles profiles,
    Func<string, DevDeckButtonState, Task> publish,
    TimeSpan interval) : IAsyncDisposable
{
    private sealed record Key(string ActionId, bool Busy, DeckActionSettings? Settings = null);
    private readonly ConcurrentDictionary<string, Key> _visible = new(StringComparer.Ordinal);
    private sealed record Published(string? ActionId, DevDeckButtonState State);
    private readonly ConcurrentDictionary<string, Published> _lastSent = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    // Timed-out providers are reused until completion, not restarted every tick.
    // In particular this avoids multiplying blocked Visual Studio/Teams calls.
    private readonly ConcurrentDictionary<string, Lazy<Task<DevDeckButtonState>>> _providerTasks =
        new(StringComparer.Ordinal);
    private static readonly TimeSpan ProviderDeadline = TimeSpan.FromSeconds(4);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public void Start()
    {
        if (_loop is not null) throw new InvalidOperationException("Status monitor already started.");
        _loop = Task.Run(LoopAsync);
    }

    public void Show(string keyId, string actionId, DeckActionSettings? settings = null)
    {
        if (runtime.FindAction(actionId) is null && !DevDeckProfiles.TrySlotNumber(actionId, out _)) return;
        _visible[keyId] = new Key(actionId, false, settings);
        _lastSent.TryRemove(keyId, out _);
    }

    public void UpdateSettings(string keyId, DeckActionSettings? settings)
    {
        if (_visible.TryGetValue(keyId, out var key))
        {
            _visible[keyId] = key with { Settings = settings };
            _lastSent.TryRemove(keyId, out _);
        }
    }

    public void Hide(string keyId)
    {
        _visible.TryRemove(keyId, out _);
        _lastSent.TryRemove(keyId, out _);
    }

    public void SetBusy(string keyId, bool busy)
    {
        if (_visible.TryGetValue(keyId, out Key? key))
        {
            _visible[keyId] = key with { Busy = busy };
            _lastSent.TryRemove(keyId, out _); // Always repaint after an action completes.
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!await _refreshGate.WaitAsync(0, cancellationToken)) return;
        try
        {
            if (_visible.IsEmpty) return;
            // Remove only completed flights. Timed-out requests remain cached until done,
            // limiting runaway external calls without retaining a completed status forever.
            foreach (var flight in _providerTasks.ToArray())
                if (flight.Value.IsValueCreated && flight.Value.Value.IsCompleted)
                    ((ICollection<KeyValuePair<string, Lazy<Task<DevDeckButtonState>>>>)_providerTasks)
                        .Remove(flight);
            profiles.Refresh();
            DeveloperContext? context = await activeContext.PollAsync(cancellationToken);
            // Cache by action AND key settings. Two Teams keys may show different channels.
            var states = new Dictionary<string, DevDeckButtonState>(StringComparer.Ordinal);
            foreach (var pair in _visible.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                string keyId = pair.Key;
                Key key = pair.Value;
                if (key.Busy || !_visible.TryGetValue(keyId, out Key? current) || current != key) continue;
                string? effectiveId = profiles.Resolve(key.ActionId);
                if (effectiveId is null)
                {
                    DevDeckButtonState empty = new("", profiles.CurrentProfile, DeckStatus.Unavailable,
                        $"Unassigned profile slot — {profiles.CurrentProfile}");
                    if (_lastSent.TryGetValue(keyId, out var oldEmpty) &&
                        oldEmpty.ActionId is null && oldEmpty.State == empty) continue;
                    try
                    {
                        await publish(keyId, empty);
                        _lastSent[keyId] = new Published(null, empty);
                    }
                    catch (Exception ex) { runtime.Log.Write($"Empty profile slot update failed: {ex}"); }
                    continue;
                }
                DeckActionSettings? options = key.Settings;
                if (DevDeckProfiles.TrySlotNumber(key.ActionId, out int slot) &&
                    runtime.Settings.ButtonPresets.TryGetValue($"{profiles.CurrentProfile}/{slot}", out var preset))
                    options = Merge(preset, options);
                string stateKey = effectiveId + ":" + (context?.SolutionPath ?? "") + ":" +
                    System.Text.Json.JsonSerializer.Serialize(options);
                if (!states.TryGetValue(stateKey, out DevDeckButtonState? state))
                {
                    var operation = runtime.FindAction(effectiveId);
                    if (operation is null) continue;
                    try
                    {
                        Lazy<Task<DevDeckButtonState>> flight = _providerTasks.GetOrAdd(stateKey, _ =>
                            new Lazy<Task<DevDeckButtonState>>(
                                () => QueryStateAsync(operation, context, options)));
                        state = await flight.Value.WaitAsync(ProviderDeadline, cancellationToken);
                    }
                    catch (TimeoutException)
                    {
                        state = new DevDeckButtonState(operation.Name, "Checking...", DeckStatus.Warning,
                            "Status provider is taking longer than expected; retrying when it completes.",
                            IconName: operation.IconName);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        runtime.Log.WriteWarning($"Status provider failed for {effectiveId}: {ex}");
                        state = new DevDeckButtonState(operation.Name, "Unavailable", DeckStatus.Unavailable,
                            ex.Message, IconName: operation.IconName);
                    }
                    states[stateKey] = state;
                }
                if (_lastSent.TryGetValue(keyId, out var old) && old.ActionId == effectiveId && old.State == state) continue;
                if (!_visible.TryGetValue(keyId, out Key? next) || next.Busy || next.ActionId != key.ActionId) continue;
                try
                {
                    await publish(keyId, state);
                    _lastSent[keyId] = new Published(effectiveId, state);
                }
                catch (Exception ex)
                {
                    runtime.Log.Write($"Status display update failed for {key.ActionId}: {ex}");
                }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    runtime.Log.Write($"Status refresh failed for key {pair.Key}: {ex}");
                    _lastSent.TryRemove(pair.Key, out _);
                }
            }
        }
        finally { _refreshGate.Release(); }
    }

    public DeckActionSettings? SettingsFor(string keyId)
    {
        if (!_visible.TryGetValue(keyId, out var key)) return null;
        DeckActionSettings? options = key.Settings;
        if (DevDeckProfiles.TrySlotNumber(key.ActionId, out int slot) &&
            runtime.Settings.ButtonPresets.TryGetValue($"{profiles.CurrentProfile}/{slot}", out var preset))
            options = Merge(preset, options);
        return options;
    }

    private static DeckActionSettings Merge(DeckActionSettings defaults, DeckActionSettings? overrides) =>
        overrides is null ? defaults : defaults with
        {
            Label = string.IsNullOrWhiteSpace(overrides.Label) ? defaults.Label : overrides.Label,
            DbTarget = string.IsNullOrWhiteSpace(overrides.DbTarget) ? defaults.DbTarget : overrides.DbTarget,
            TeamId = string.IsNullOrWhiteSpace(overrides.TeamId) ? defaults.TeamId : overrides.TeamId,
            ChannelId = string.IsNullOrWhiteSpace(overrides.ChannelId) ? defaults.ChannelId : overrides.ChannelId,
            ChannelUrl = string.IsNullOrWhiteSpace(overrides.ChannelUrl) ? defaults.ChannelUrl : overrides.ChannelUrl,
            SolutionPath = string.IsNullOrWhiteSpace(overrides.SolutionPath) ? defaults.SolutionPath : overrides.SolutionPath,
            FocusChat = overrides.FocusChat ?? defaults.FocusChat
        };

    private async Task<DevDeckButtonState> QueryStateAsync(IDevDeckOperation operation,
        DeveloperContext? context, DeckActionSettings? options) =>
        await runtime.States.GetConfiguredStateAsync(operation, context, options, _stop.Token);

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(interval);
        do
        {
            try { await RefreshAsync(_stop.Token); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex) { runtime.Log.WriteWarning("Status refresh cycle failed (will retry): " + ex); }
            try
            {
                if (!await timer.WaitForNextTickAsync(_stop.Token)) break;
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
        } while (!_stop.IsCancellationRequested);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null) await _loop;
        await _refreshGate.WaitAsync();
        _refreshGate.Release();
        _refreshGate.Dispose();
        _stop.Dispose();
    }
}
