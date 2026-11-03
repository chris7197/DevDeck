using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace chris7197.DevDeck.Core;

/// <summary>
/// Defensive ROT/DTE enumeration. Must only be called from VisualStudioComDispatcher's STA.
/// Each candidate is screened for a real, live devenv window and saved solution before
/// any caller gets its DTE object. Missing/stale ROT entries are skipped individually.
/// COM objects never escape this call. Errors *inside* a caller's modifying action
/// deliberately propagate: they must not be mistaken for an unsuccessful lookup.
/// </summary>
internal static class VisualStudioRot
{
    internal static bool Visit(Func<dynamic, uint, IntPtr, string, bool> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        IRunningObjectTable? rot = null;
        IBindCtx? bindContext = null;
        IEnumMoniker? enumerator = null;
        try
        {
            int hr = GetRunningObjectTable(0, out rot);
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);
            hr = CreateBindCtx(0, out bindContext);
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);
            rot.EnumRunning(out enumerator);
            IMoniker[] monikers = new IMoniker[1];

            while (true)
            {
                int next = enumerator.Next(1, monikers, IntPtr.Zero);
                if (next == 1) break;
                if (next != 0) Marshal.ThrowExceptionForHR(next);
                IMoniker? moniker = monikers[0];
                monikers[0] = null!;
                if (moniker is null) continue;
                try
                {
                    string name;
                    try { moniker.GetDisplayName(bindContext, null, out name); }
                    catch (COMException) { continue; }
                    if (!name.Contains("VisualStudio.DTE", StringComparison.OrdinalIgnoreCase)) continue;

                    // The DTE moniker normally ends in :<pid>. Before making a potentially
                    // blocking DTE call, reject obvious stale processes if a PID is present.
                    uint? hintedPid = ProcessIdInMoniker(name);
                    if (hintedPid is uint knownPid && !IsLiveVisualStudio(knownPid)) continue;

                    object? automation = null;
                    try
                    {
                        try { rot.GetObject(moniker, out automation); }
                        catch (COMException) { continue; }
                        if (automation is null) continue;
                        dynamic dte = automation;
                        IntPtr hwnd;
                        uint pid;
                        string solutionPath;
                        try
                        {
                            hwnd = new IntPtr((int)dte.MainWindow.HWnd);
                            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) continue;
                            GetWindowThreadProcessId(hwnd, out pid);
                            if (pid == 0 || (hintedPid is uint hinted && hinted != pid) ||
                                !IsLiveVisualStudio(pid)) continue;
                            solutionPath = (string)dte.Solution.FullName;
                            if (string.IsNullOrWhiteSpace(solutionPath) || !File.Exists(solutionPath)) continue;
                            solutionPath = Path.GetFullPath(solutionPath);
                        }
                        catch (Exception ex) when (ex is COMException or InvalidCastException or
                            NullReferenceException or ArgumentException or
                            Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                        {
                            // DTE may disappear between ROT enumeration and the probe.
                            continue;
                        }

                        // Do NOT swallow COM exceptions from a modifying visitor. A SaveAll or
                        // AddFromFile may already have partly run; the caller must see failure.
                        if (visitor(dte, pid, hwnd, solutionPath)) return true;
                    }
                    finally { ReleaseComObject(automation); }
                }
                finally { ReleaseComObject(moniker); }
            }
            return false;
        }
        finally
        {
            ReleaseComObject(enumerator);
            ReleaseComObject(bindContext);
            ReleaseComObject(rot);
        }
    }

    internal static bool IsLiveVisualStudio(uint pid)
    {
        try
        {
            using Process process = Process.GetProcessById(checked((int)pid));
            return !process.HasExited && process.ProcessName.Equals("devenv", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException or
            InvalidOperationException or Win32Exception) { return false; }
    }

    internal static bool IsValidWindow(IntPtr window) => window != IntPtr.Zero && IsWindow(window);

    internal static uint? ProcessIdInMoniker(string name)
    {
        int colon = name.LastIndexOf(':');
        return colon >= 0 && uint.TryParse(name.AsSpan(colon + 1), out uint pid) && pid != 0
            ? pid : null;
    }

    private static void ReleaseComObject(object? obj)
    {
        if (obj is null) return;
        try { if (Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj); }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException or ArgumentException) { }
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("ole32.dll")] private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable table);
    [DllImport("ole32.dll")] private static extern int CreateBindCtx(int reserved, out IBindCtx context);
}
