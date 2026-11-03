using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace chris7197.DevDeck.Core;

/// <summary>
/// All in-process EnvDTE/ROT work goes through ONE dedicated STA. A timed-out COM call cannot be
/// interrupted safely, so new calls are suspended while that call remains in flight. Once the
/// original call actually returns, the same STA can accept work again without a plugin restart.
/// A permanently hung call still needs out-of-process COM isolation for full recovery.
/// Do not let COM objects escape a callback into the caller's apartment.
/// </summary>
public sealed class VisualStudioComDispatcher : IDisposable
{
    public static VisualStudioComDispatcher Shared { get; } = new();

    private readonly BlockingCollection<IRequest> _queue;
    private readonly Thread _worker;
    // Protects the currently running STA request and suspension state against races
    // between the waiting caller and the worker's return from a late COM call.
    private readonly object _stateLock = new();
    private IRequest? _running;
    private int _unavailable;
    private int _disposed;
    private readonly TimeSpan _readTimeout;
    private readonly TimeSpan _writeTimeout;

    public VisualStudioComDispatcher(int maximumQueued = 16, TimeSpan? readTimeout = null, TimeSpan? writeTimeout = null)
    {
        if (maximumQueued < 1) throw new ArgumentOutOfRangeException(nameof(maximumQueued));
        _readTimeout = readTimeout ?? TimeSpan.FromSeconds(8);
        _writeTimeout = writeTimeout ?? TimeSpan.FromSeconds(20);
        if (_readTimeout <= TimeSpan.Zero || _writeTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(readTimeout), "COM deadlines must be positive.");
        _queue = new BlockingCollection<IRequest>(maximumQueued);
        _worker = new Thread(WorkLoop) { Name = "DevDeck VS COM STA", IsBackground = true };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    public async Task<T> InvokeAsync<T>(Func<T> callback, bool mayModifyVisualStudio = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(VisualStudioComDispatcher));
        if (Volatile.Read(ref _unavailable) != 0) throw new VisualStudioAutomationUnavailableException();
        var request = new Request<T>(callback, !mayModifyVisualStudio, cancellationToken);
        try
        {
            if (!_queue.TryAdd(request))
                throw new VisualStudioAutomationUnavailableException("Visual Studio automation queue is full; try again shortly.");
        }
        catch (InvalidOperationException) when (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(VisualStudioComDispatcher));
        }

        try
        {
            return await request.Task.WaitAsync(mayModifyVisualStudio ? _writeTimeout : _readTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException) when (!request.Task.IsCompleted)
        {
            request.Abandon();
            bool mayHaveRun = request.HasStarted;
            bool suspended = SuspendIfRunning(request, () => request.Task.IsCompleted);
            // The original modifying request is NEVER retried; its outcome may be unknown.
            throw new VisualStudioAutomationTimeoutException(
                mayModifyVisualStudio && mayHaveRun, suspended);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            request.Abandon();
            // Cancellation does not stop an in-flight COM call. Keep the single STA
            // suspended until the callback really returns, then automatically recover.
            SuspendIfRunning(request, () => request.Task.IsCompleted);
            throw;
        }
    }

    private bool SuspendIfRunning(IRequest request, Func<bool> hasCompleted)
    {
        lock (_stateLock)
        {
            // If the request has just finished, the worker has already made it safe
            // to reuse the STA. Do not poison the dispatcher after that point.
            if (Volatile.Read(ref _disposed) != 0 ||
                !ReferenceEquals(_running, request) || hasCompleted())
                return false;

            Volatile.Write(ref _unavailable, 1);
            return true;
        }
    }

    private void WorkLoop()
    {
        foreach (IRequest request in _queue.GetConsumingEnumerable())
        {
            bool blocked;
            lock (_stateLock)
            {
                blocked = Volatile.Read(ref _unavailable) != 0;
                if (!blocked) _running = request;
            }
            if (blocked)
            {
                request.Fail(new VisualStudioAutomationUnavailableException());
                continue;
            }

            try { request.Run(); }
            finally
            {
                lock (_stateLock)
                {
                    _running = null;
                    // Only the original STA thread finishing its callback is allowed
                    // to reset a suspension. Never spawn another STA to 'recover'.
                    if (Volatile.Read(ref _disposed) == 0)
                        Volatile.Write(ref _unavailable, 0);
                }
            }
        }
    }

    private interface IRequest
    {
        void Run();
        void Fail(Exception exception);
    }

    private sealed class Request<T>(Func<T> callback, bool retryReadOnly, CancellationToken cancellationToken) : IRequest
    {
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;
        private int _abandoned;
        public Task<T> Task => _completion.Task;
        public bool HasStarted => Volatile.Read(ref _started) != 0;
        public void Abandon() => Interlocked.Exchange(ref _abandoned, 1);
        public void Fail(Exception exception) => _completion.TrySetException(exception);

        public void Run()
        {
            if (Volatile.Read(ref _abandoned) != 0 || cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled();
                return;
            }
            Volatile.Write(ref _started, 1);
            // A caller may have timed out while this request was waiting in the queue.
            if (Volatile.Read(ref _abandoned) != 0 || cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled();
                return;
            }
            try
            {
                int attempts = retryReadOnly ? 3 : 1;
                for (int i = 0; i < attempts; i++)
                {
                    try
                    {
                        _completion.TrySetResult(callback());
                        return;
                    }
                    catch (COMException error) when (retryReadOnly && i < attempts - 1 &&
                        (error.HResult == unchecked((int)0x80010001) ||
                         error.HResult == unchecked((int)0x8001010A)))
                    {
                        if (Volatile.Read(ref _abandoned) != 0 || cancellationToken.IsCancellationRequested)
                        {
                            _completion.TrySetCanceled();
                            return;
                        }
                        Thread.Sleep(150 * (i + 1));
                    }
                }
            }
            catch (Exception error) { _completion.TrySetException(error); }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Exchange(ref _unavailable, 1);
        _queue.CompleteAdding();
        // Never wait for a blocked STA thread, especially during SOOMFON shutdown.
        // It is a background thread and the OS will release it when the process exits.
    }
}

public sealed class VisualStudioAutomationTimeoutException(
    bool operationMayHaveExecuted, bool dispatcherUnhealthy)
    : TimeoutException(operationMayHaveExecuted
        ? "Visual Studio timed out during a modifying COM operation. Its outcome is UNKNOWN. " +
          "DevDeck will not retry it; inspect Visual Studio before repeating this action. " +
          "Further COM work will resume automatically if the original call returns."
        : dispatcherUnhealthy
            ? "Visual Studio automation is still running after a timeout. " +
              "DevDeck will automatically resume COM operations when that call returns."
            : "Visual Studio automation was too busy to start this request in time. Please retry.")
{
    public bool OperationMayHaveExecuted { get; } = operationMayHaveExecuted;
    public bool DispatcherUnhealthy { get; } = dispatcherUnhealthy;
}

public sealed class VisualStudioAutomationUnavailableException(string? message = null)
    : InvalidOperationException(message ??
        "Visual Studio automation is temporarily suspended while an earlier COM call finishes. " +
        "Try again shortly. If Visual Studio never returns, a process-isolated COM worker is needed.");
