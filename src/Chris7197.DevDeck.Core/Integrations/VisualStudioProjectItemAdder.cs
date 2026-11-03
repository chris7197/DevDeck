using System.Runtime.InteropServices;

namespace chris7197.DevDeck.Core;

/// <summary>
/// Adds a file via an existing Visual Studio project, without editing a loaded legacy csproj.
/// Stale windows, unloaded projects and closed solutions are treated as missing. COM failures
/// during an actual add/save are propagated (the result might be partially committed).
/// </summary>
public sealed class VisualStudioProjectItemAdder(VisualStudioComDispatcher? dispatcher = null)
{
    private readonly VisualStudioComDispatcher _dispatcher = dispatcher ?? VisualStudioComDispatcher.Shared;

    public Task<bool> TryAddAsync(string solutionPath, string projectFile, string sourceFile, DeckLog log,
        CancellationToken cancellationToken = default) =>
        _dispatcher.InvokeAsync(() => TryAdd(solutionPath, projectFile, sourceFile, log),
            mayModifyVisualStudio: true, cancellationToken: cancellationToken);

    private static bool TryAdd(string solutionPath, string projectFile, string sourceFile, DeckLog log)
    {
        if (!File.Exists(sourceFile)) throw new FileNotFoundException("Generated migration is missing.", sourceFile);
        if (!File.Exists(projectFile)) throw new FileNotFoundException("Migration project is missing.", projectFile);
        return VisualStudioRot.Visit((dte, pid, hwnd, opened) =>
        {
            if (!PathHelper.SamePath(opened, solutionPath)) return false;
            if (!VisualStudioRot.IsValidWindow(hwnd)) return false;
            log.Write($"Visual Studio DTE: {opened}");
            // Enumerate before attempting any modification. If the solution unloads
            // mid-enumeration, treat it as unavailable, not as a failed write.
            var projects = new List<object>();
            try
            {
                foreach (object project in dte.Solution.Projects)
                    if (project is not null) projects.Add(project);
            }
            catch (COMException) { return false; }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { return false; }
            foreach (object project in projects)
                if (TryAddInProject(project, projectFile, sourceFile, log)) return true;
            return false;
        });
    }

    private static bool TryAddInProject(dynamic project, string projectFile, string sourceFile, DeckLog log)
    {
        if (project is null) return false;
        string? path = null;
        try { path = (string?)project.FullName; }
        catch (COMException) { /* Solution folder or unloaded project. */ }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }

        if (!string.IsNullOrWhiteSpace(path) && PathHelper.SamePath(path, projectFile))
        {
            // Never swallow exceptions after AddFromFile is attempted. The caller's
            // uncertainty handling must decide whether a fallback is safe.
            log.Write($"Adding {sourceFile} to {projectFile}");
            try
            {
                project.ProjectItems.AddFromFile(sourceFile);
                project.Save(projectFile);
                return true;
            }
            catch (Exception ex)
            {
                // AddFromFile can succeed even when Save fails or the window closes.
                // The caller must NOT apply a competing csproj edit automatically.
                throw new VisualStudioProjectChangeUncertainException(sourceFile, ex);
            }
        }

        // Solution folders and some project types expose missing/broken ProjectItems.
        // Probe child objects separately; a stale child must not invalidate other projects.
        dynamic items;
        try { items = project.ProjectItems; }
        catch (COMException) { return false; }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { return false; }
        if (items is null) return false;
        // Materialise child references while enumerating so an exception from a
        // descendant's AddFromFile is never swallowed as a mere enumeration error.
        var children = new List<object>();
        try
        {
            foreach (dynamic item in items)
            {
                dynamic? child;
                try { child = item.SubProject; }
                catch (COMException) { continue; }
                catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { continue; }
                if (child is not null) children.Add(child);
            }
        }
        catch (COMException) { return false; }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { return false; }
        foreach (object child in children)
            if (TryAddInProject(child, projectFile, sourceFile, log)) return true;
        return false;
    }
}

/// <summary>
/// Visual Studio may already have included the file despite reporting failure.
/// The caller should preserve the file and require inspection, not try a second write.
/// </summary>
public sealed class VisualStudioProjectChangeUncertainException(string file, Exception inner)
    : InvalidOperationException("Visual Studio could not confirm adding " + file +
        ". The project may already have changed; check it before retrying.", inner);
