using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using chris7197.DevDeck.Core;

namespace chris7197.DevDeck.Simulator;

/// <summary>
/// Captures EVENT_SYSTEM_FOREGROUND immediately, even when the simulator is not focused.
/// Polling GetForegroundWindow alone misses quick switches back to the simulator.
/// Read() deliberately retains the last non-simulator foreground application while
/// the simulator has focus, so the displayed layout remains useful.
/// </summary>
internal sealed class ForegroundWindowTracker : IForegroundWindowReader, IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0;
    private readonly WindowsForegroundWindowReader _reader = new();
    private readonly WinEventCallback _callback;
    private readonly object _gate = new();
    private readonly uint _ownProcessId = checked((uint)Environment.ProcessId);
    private ForegroundWindowInfo? _lastExternalWindow;
    private IntPtr _hook;
    private bool _disposed;

    public ForegroundWindowTracker() => _callback = WindowChanged;

    public event Action<ObservedForegroundWindow>? ApplicationActivated;

    public ForegroundWindowInfo? LastExternalWindow
    {
        get { lock (_gate) return _lastExternalWindow; }
    }

    public ForegroundWindowInfo? Read()
    {
        // If a foreground transition has just happened before the hook was installed,
        // sampling here still works. Crucially, NEVER replace a real app with ourselves.
        IntPtr window = GetForegroundWindow();
        if (window != IntPtr.Zero)
        {
            GetWindowThreadProcessId(window, out uint pid);
            if (pid != 0 && pid != _ownProcessId)
            {
                ForegroundWindowInfo? info = _reader.ReadWindow(window);
                if (info is not null)
                {
                    lock (_gate) _lastExternalWindow = info;
                    return info;
                }
            }
        }
        return LastExternalWindow;
    }

    /// <summary>Install on the WinForms UI thread (which has a running message pump).</summary>
    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ForegroundWindowTracker));
        if (_hook != IntPtr.Zero) return;
        _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero,
            _callback, 0, 0, WinEventOutOfContext);
        if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),
            "Could not install foreground window watcher.");
        // No prior event may have been seen, e.g. if the simulator started focused.
        _ = Read();
    }

    private void WindowChanged(IntPtr hook, uint eventType, IntPtr hwnd,
        int objectId, int childId, uint eventThreadId, uint eventTime)
    {
        if (_disposed || hwnd == IntPtr.Zero || objectId != 0 || childId != 0) return;
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == _ownProcessId) return;
            ForegroundWindowInfo? info = _reader.ReadWindow(hwnd);
            if (info is null) return;
            lock (_gate) _lastExternalWindow = info;
            ApplicationActivated?.Invoke(new ObservedForegroundWindow(pid, info));
        }
        catch (Exception ex)
        {
            Debug.WriteLine("DevDeck foreground watcher: " + ex);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private delegate void WinEventCallback(IntPtr hook, uint eventType, IntPtr hwnd,
        int objectId, int childId, uint eventThreadId, uint eventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
        WinEventCallback callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}

internal sealed record ObservedForegroundWindow(uint ProcessId, ForegroundWindowInfo Window);
