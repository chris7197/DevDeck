using chris7197.DevDeck.Core;
using System.Diagnostics;

namespace chris7197.DevDeck.Plugin;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        var log = new DeckLog();
        try
        {
            Dictionary<string, string> parameters = ParseArguments(args);
            int port = int.Parse(parameters["-port"]);
            string uuid = parameters["-pluginUUID"];
            string registrationEvent = parameters["-registerEvent"];
            DevDeckRuntime runtime = DevDeckRuntime.Create(AppContext.BaseDirectory, log);

            using var lifetime = new CancellationTokenSource();
            await using var deck = new StreamDockClient(e => log.WriteWarning("Bad controller event ignored: " + e));
            // Limit independent actions, but only serialize repository/project mutations.
            var actions = new DevDeckActionCoordinator(runtime.Settings.MaxConcurrentActions);
            var ongoing = new List<Task>();
            var activeContext = new ActiveDeveloperContext(runtime.ContextProvider);
            await using var statusMonitor = new DevDeckStatusMonitor(runtime, activeContext, runtime.Profiles,
                async (keyId, state) =>
                {
                    // Paint the *whole key*, including its live subtitle and severity colour.
                    // Keep the original manifest images as an offline fallback.
                    await deck.SetImageAsync(keyId, DevDeckButtonRenderer.RenderDataUrl(state));
                    await deck.SetTitleAsync(keyId, "");
                }, TimeSpan.FromSeconds(runtime.Settings.LiveRefreshSeconds));

            deck.WillAppear += keyEvent =>
            {
                statusMonitor.Show(keyEvent.Context, keyEvent.Action, keyEvent.Settings);
                // Do not delay the WebSocket event reader on file, COM or Git operations.
                return Task.CompletedTask;
            };
            deck.SettingsChanged += keyEvent =>
            {
                statusMonitor.UpdateSettings(keyEvent.Context, keyEvent.Settings);
                return Task.CompletedTask;
            };
            deck.WillDisappear += keyEvent =>
            {
                statusMonitor.Hide(keyEvent.Context);
                return Task.CompletedTask;
            };
            deck.KeyDown += async keyEvent =>
            {
                if (lifetime.IsCancellationRequested) return;
                // Refresh at press-time so even a window switch between 10s status polls
                // dispatches the action currently shown for the foreground application.
                runtime.Profiles.Refresh();
                IDevDeckOperation? operation = runtime.ResolveProfileAction(keyEvent.Action);
                if (operation is null)
                {
                    log.WriteTrace($"Controller key press ignored: no operation resolved for action={keyEvent.Action}.");
                    return;
                }
                log.WriteTrace($"Controller key press: action={operation.PluginActionId}.");
                IDisposable? lease = actions.TryBegin(keyEvent.Context, operation.PluginActionId);
                if (lease is null)
                {
                    log.WriteTrace($"Controller action busy: {operation.PluginActionId}.");
                    await deck.ShowAlertAsync(keyEvent.Context);
                    return;
                }
                statusMonitor.SetBusy(keyEvent.Context, true);
                // Do not block the WebSocket receive loop during long Git commands.
                Task work = ExecuteActionAsync(operation, keyEvent, lease);
                lock (ongoing) ongoing.Add(work);
                lock (ongoing) ongoing.RemoveAll(t => t.IsCompleted);
            };

            async Task ExecuteActionAsync(IDevDeckOperation operation, StreamDockKeyEvent keyEvent, IDisposable lease)
            {
                string traceId = Guid.NewGuid().ToString("N")[..8];
                var clock = Stopwatch.StartNew();
                log.WriteTrace($"Action[{traceId}] started name={operation.Name.Replace('\n', ' ')} " +
                    $"id={operation.PluginActionId}.");
                try
                {
                    await deck.SetImageAsync(keyEvent.Context, DevDeckButtonRenderer.RenderDataUrl(
                        new DevDeckButtonState(operation.Name, "Working...", DeckStatus.Busy, IconName: operation.IconName)));
                    DeckActionSettings? options = statusMonitor.SettingsFor(keyEvent.Context);
                    bool needsSolution = operation.RequiresSolutionFor(options);
                    log.WriteTrace($"Action[{traceId}] context required={needsSolution}.");
                    DeveloperContext? selectedContext = needsSolution
                        ? await runtime.SolutionSelector.ResolveAsync(operation.Name, operation.SaveAllBeforeExecute,
                            lifetime.Token)
                        : activeContext.Last;
                    // Escape cancels before any operation begins, including Git or migrations.
                    if (selectedContext is null && needsSolution)
                    {
                        log.WriteTrace($"Action[{traceId}] solution selection cancelled.");
                        log.Write("Solution selection cancelled for " + operation.Name.Replace('\n', ' '));
                        return;
                    }
                    DeveloperContext context = selectedContext ??
                        new DeveloperContext(Environment.CurrentDirectory, null, null);
                    log.WriteTrace($"Action[{traceId}] invoking operation; hasContext={selectedContext is not null}.");
                    DevDeckOperationResult result = await operation.ExecuteAsync(new DevDeckOperationContext(
                        context,
                        stage => deck.SetImageAsync(keyEvent.Context, DevDeckButtonRenderer.RenderDataUrl(
                            new DevDeckButtonState(operation.Name, stage.Replace('\n', ' '), DeckStatus.Busy, IconName: operation.IconName))),
                        CancellationToken: lifetime.Token,
                        ActionSettings: options));
                    log.WriteTrace($"Action[{traceId}] result success={result.Success} cancelled={result.Cancelled} " +
                        $"durationMs={clock.ElapsedMilliseconds}.");
                    if (result.Cancelled) log.Write($"{operation.Name.Replace('\n', ' ')}: Cancelled");
                    else if (result.Success) log.Write($"{operation.Name.Replace('\n', ' ')}: {result.Message}");
                    else log.WriteError($"{operation.Name.Replace('\n', ' ')} failed: {result.Message}");
                    if (result.Cancelled) { /* Escape: silent, intentional cancellation. */ }
                    else if (result.NoChanges)
                        await deck.SetImageAsync(keyEvent.Context, DevDeckButtonRenderer.RenderDataUrl(
                            new DevDeckButtonState(operation.Name, "No changes", DeckStatus.Warning,
                                result.Message, IconName: operation.IconName)));
                    else if (result.Success) await deck.ShowOkAsync(keyEvent.Context);
                    else await deck.ShowAlertAsync(keyEvent.Context);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                    log.WriteTrace($"Action[{traceId}] shutdown cancellation after {clock.ElapsedMilliseconds}ms.");
                    log.Write($"Action {operation.Name.Replace('\n', ' ')} canceled on controller shutdown. " +
                        "Check any in-progress Git operation before retrying.");
                }
                catch (Exception e)
                {
                    log.WriteTrace($"Action[{traceId}] exception type={e.GetType().Name}, " +
                        $"durationMs={clock.ElapsedMilliseconds}.");
                    log.WriteError($"Action failed ({keyEvent.Action}): {e}");
                    try
                    {
                        await deck.SetImageAsync(keyEvent.Context, DevDeckButtonRenderer.RenderDataUrl(
                            new DevDeckButtonState(operation.Name, "Failed", DeckStatus.Error, e.Message, IconName: operation.IconName)));
                        await deck.ShowAlertAsync(keyEvent.Context);
                    }
                    catch (Exception socketError) { log.WriteWarning("Cannot update key: " + socketError.Message); }
                }
                finally
                {
                    statusMonitor.SetBusy(keyEvent.Context, false);
                    lease.Dispose();
                    if (!lifetime.IsCancellationRequested)
                        try { await statusMonitor.RefreshAsync(lifetime.Token); }
                        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                }
            }

            statusMonitor.Start();
            log.Write("Connecting to Stream Dock at localhost:" + port);
            try { await deck.RunAsync(port, uuid, registrationEvent, lifetime.Token); }
            finally
            {
                // Cancel overlays, SQL and Git on controller exit; do not hang on an
                // abandoned COM operation while Stream Dock is trying to restart the plugin.
                await lifetime.CancelAsync();
                Task[] pending;
                lock (ongoing) pending = ongoing.ToArray();
                try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(8)); }
                catch (TimeoutException)
                {
                    log.Write($"Shutdown grace period exceeded. {pending.Count(t => !t.IsCompleted)} actions " +
                        "have not completed; check their external processes before retrying.");
                }
            }
        }
        catch (Exception e) { log.WriteError("Plugin startup/connection failure: " + e); }
    }

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        if (args.Length % 2 != 0) throw new ArgumentException("Expected Stream Dock arguments in name/value pairs.");
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i += 2) dict[args[i]] = args[i + 1];
        foreach (string required in new[] { "-port", "-pluginUUID", "-registerEvent" })
            if (!dict.ContainsKey(required)) throw new ArgumentException($"Missing Stream Dock argument: {required}");
        return dict;
    }
}
