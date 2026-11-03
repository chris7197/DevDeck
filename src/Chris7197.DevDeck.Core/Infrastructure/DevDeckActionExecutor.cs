namespace chris7197.DevDeck.Core;

/// <summary>
/// Shared execution policy for the controller and simulator. The host supplies only
/// context selection, confirmation and visual feedback; concurrency, cancellation,
/// result logging and lease cleanup are consistent in both hosts.
/// </summary>
public sealed class DevDeckActionExecutor(DevDeckActionCoordinator coordinator, DeckLog log)
{
    public async Task<DevDeckExecutionOutcome> ExecuteAsync(DevDeckExecutionRequest request)
    {
        // Acquire synchronously before the first await: two fast button presses cannot
        // both be admitted while the first is waiting for a solution overlay.
        IDisposable? lease = coordinator.TryBegin(request.KeyId, request.Operation.PluginActionId);
        if (lease is null) return new(DevDeckExecutionStatus.Busy);

        try
        {
            request.SetBusy?.Invoke(true);
            request.CancellationToken.ThrowIfCancellationRequested();
            var operation = request.Operation;
            await (request.ReportProgress?.Invoke("Working...") ?? Task.CompletedTask);
            bool needsSolution = operation.RequiresSolutionFor(request.Settings);
            DeveloperContext? selected = needsSolution
                ? await request.SelectSolutionAsync(operation, request.CancellationToken)
                : await request.GetCurrentContextAsync(request.CancellationToken);

            if (selected is null && needsSolution)
            {
                log.Write($"Solution selection cancelled for {DisplayName(operation)}");
                return new(DevDeckExecutionStatus.Cancelled);
            }

            DeveloperContext context = selected ?? new DeveloperContext(Environment.CurrentDirectory, null, null);

            string? confirmation = request.Operation.ConfirmationText(context);

            if (confirmation is not null && request.ConfirmAsync is not null &&
                !await request.ConfirmAsync(confirmation, request.CancellationToken))
                return new(DevDeckExecutionStatus.Cancelled);

            request.CancellationToken.ThrowIfCancellationRequested();
            DevDeckOperationResult result = await operation.ExecuteAsync(new DevDeckOperationContext(
                context, request.ReportProgress, request.CancellationToken, request.Settings));
            if (result.Cancelled)
            {
                log.Write($"{DisplayName(operation)}: Cancelled");
                return new(DevDeckExecutionStatus.Cancelled, result);
            }
            if (result.Success) log.Write($"{DisplayName(operation)}: {result.Message}");
            else log.WriteError($"{DisplayName(operation)} failed: {result.Message}");
            return new(result.Success ? DevDeckExecutionStatus.Completed : DevDeckExecutionStatus.Failed, result);
        }
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
        {
            log.WriteWarning($"Action {DisplayName(request.Operation)} cancelled during shutdown. " +
                "Check any in-progress external operation before retrying.");
            return new(DevDeckExecutionStatus.Cancelled);
        }
        catch (Exception ex)
        {
            log.WriteError($"Action failed ({request.Operation.PluginActionId}): {ex}");
            return new(DevDeckExecutionStatus.Failed, Error: ex);
        }
        finally
        {
            lease.Dispose();
            // Host callbacks should be trivial synchronous state changes; never let a
            // UI disposal race mask the actual action outcome or leak a reservation.
            try { request.SetBusy?.Invoke(false); }
            catch (Exception ex) { log.WriteWarning("Cannot clear busy indicator: " + ex.Message); }
        }
    }

    private static string DisplayName(IDevDeckOperation operation) => operation.Name.Replace('\n', ' ');
}

public enum DevDeckExecutionStatus { Busy, Cancelled, Completed, Failed }

public sealed record DevDeckExecutionOutcome(
    DevDeckExecutionStatus Status,
    DevDeckOperationResult? Result = null,
    Exception? Error = null);

public sealed record DevDeckExecutionRequest(
    string KeyId,
    IDevDeckOperation Operation,
    DeckActionSettings? Settings,
    Func<IDevDeckOperation, CancellationToken, Task<DeveloperContext?>> SelectSolutionAsync,
    Func<CancellationToken, Task<DeveloperContext?>> GetCurrentContextAsync,
    Func<string, Task>? ReportProgress = null,
    Func<string, CancellationToken, Task<bool>>? ConfirmAsync = null,
    Action<bool>? SetBusy = null,
    CancellationToken CancellationToken = default);
