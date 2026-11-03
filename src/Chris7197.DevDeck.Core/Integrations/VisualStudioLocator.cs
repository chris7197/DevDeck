using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace chris7197.DevDeck.Core;

/// <summary>
/// Finds only saved, open solutions hosted by live devenv windows. Read-only discovery
/// skips stale COM objects; changing operations fail if the selected window has closed.
/// </summary>
public sealed class VisualStudioLocator(VisualStudioComDispatcher? dispatcher = null) : IVisualStudioSolutionDiscovery
{
    private readonly VisualStudioComDispatcher _dispatcher = dispatcher ?? VisualStudioComDispatcher.Shared;

    public async Task<string> GetActiveSolutionDirectoryAsync(bool saveAll)
        => Path.GetDirectoryName(await GetActiveSolutionPathAsync(saveAll))!;

    public Task<string> GetActiveSolutionPathAsync(bool saveAll = false)
    {
        uint pid = GetForegroundVisualStudioPid() ?? throw new InvalidOperationException(
            "Focus Visual Studio and press again, or choose an open solution.");
        return _dispatcher.InvokeAsync(() => FindSolutionPathForProcessId(pid, saveAll),
            mayModifyVisualStudio: saveAll);
    }

    public Task<string> GetSolutionPathForProcessIdAsync(uint processId) =>
        _dispatcher.InvokeAsync(() => FindSolutionPathForProcessId(processId, saveAll: false));

    public Task<VisualStudioSolutionInstance?> GetFocusedSolutionAsync(CancellationToken cancellationToken = default)
    {
        // Snapshot focus before queuing the COM call, not after opening an overlay.
        uint? pid = GetForegroundVisualStudioPid();
        if (pid is null) return Task.FromResult<VisualStudioSolutionInstance?>(null);
        return _dispatcher.InvokeAsync(() =>
        {
            VisualStudioSolutionInstance? result = null;
            VisualStudioRot.Visit((dte, runningPid, hwnd, path) =>
            {
                if (runningPid != pid.Value) return false;
                result = new VisualStudioSolutionInstance(path, runningPid, hwnd);
                return true;
            });
            if (result is null)
                throw new InvalidOperationException(
                    "The focused Visual Studio window has closed or does not have a saved solution.");
            return result;
        }, cancellationToken: cancellationToken);
    }

    public Task<IReadOnlyList<VisualStudioSolutionInstance>> GetOpenSolutionsAsync(
        CancellationToken cancellationToken = default) =>
        _dispatcher.InvokeAsync<IReadOnlyList<VisualStudioSolutionInstance>>(() =>
        {
            var result = new List<VisualStudioSolutionInstance>();
            var seen = new HashSet<uint>();
            VisualStudioRot.Visit((dte, pid, hwnd, path) =>
            {
                if (seen.Add(pid)) result.Add(new VisualStudioSolutionInstance(path, pid, hwnd));
                return false;
            });
            return result;
        }, cancellationToken: cancellationToken);

    public Task SaveAllAsync(uint processId, string expectedSolutionPath,
        CancellationToken cancellationToken = default) => _dispatcher.InvokeAsync(() =>
        {
            bool found = VisualStudioRot.Visit((dte, pid, hwnd, path) =>
            {
                if (pid != processId) return false;
                if (!PathHelper.SamePath(path, expectedSolutionPath))
                    throw new InvalidOperationException(
                        "Visual Studio switched solutions after selection. No files were saved.");
                if (!VisualStudioRot.IsValidWindow(hwnd))
                    throw new InvalidOperationException("The selected Visual Studio window is no longer open.");
                // Fail closed: if this throws or times out, we must not continue with Git.
                dte.ExecuteCommand("File.SaveAll");
                return true;
            });
            if (!found)
                throw new InvalidOperationException("The selected Visual Studio instance is no longer available. No files were saved.");
            return true;
        }, mayModifyVisualStudio: true, cancellationToken: cancellationToken);

    public Task<IntPtr> FindOpenSolutionWindowAsync(string solutionPath,
        CancellationToken cancellationToken = default) =>
        _dispatcher.InvokeAsync(() =>
        {
            IntPtr found = IntPtr.Zero;
            VisualStudioRot.Visit((dte, pid, hwnd, path) =>
            {
                if (!PathHelper.SamePath(path, solutionPath)) return false;
                found = hwnd;
                return true;
            });
            // WindowActivator validates this again at activation time (it might close meanwhile).
            return found;
        }, cancellationToken: cancellationToken);

    private static string FindSolutionPathForProcessId(uint selectedPid, bool saveAll)
    {
        if (!VisualStudioRot.IsLiveVisualStudio(selectedPid))
            throw new InvalidOperationException("The selected Visual Studio process is no longer running.");
        string? result = null;
        VisualStudioRot.Visit((dte, pid, hwnd, path) =>
        {
            if (pid != selectedPid) return false;
            if (saveAll)
            {
                if (!VisualStudioRot.IsValidWindow(hwnd))
                    throw new InvalidOperationException("Visual Studio closed before Save All.");
                dte.ExecuteCommand("File.SaveAll");
            }
            result = path;
            return true;
        });
        return result ?? throw new InvalidOperationException(
            "Could not access the Visual Studio solution through DTE. " +
            "The window might have closed, be loading, or have an unavailable automation server.");
    }

    private static uint? GetForegroundVisualStudioPid()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (!VisualStudioRot.IsValidWindow(hwnd)) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return VisualStudioRot.IsLiveVisualStudio(pid) ? pid : null;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
