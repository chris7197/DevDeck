namespace chris7197.DevDeck.Core;

/// <summary>A saved solution belonging to a particular running Visual Studio process.</summary>
public sealed record VisualStudioSolutionInstance(string SolutionPath, uint ProcessId, IntPtr WindowHandle);

/// <summary>Small boundary so selecting a solution can be tested without running Visual Studio.</summary>
public interface IVisualStudioSolutionDiscovery
{
    Task<VisualStudioSolutionInstance?> GetFocusedSolutionAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VisualStudioSolutionInstance>> GetOpenSolutionsAsync(CancellationToken cancellationToken = default);
    Task SaveAllAsync(uint processId, string expectedSolutionPath, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves solution context for an action: focused Visual Studio wins; otherwise a
/// keyboard-first picker lists open saved solutions. Never silently uses last observed VS
/// for actions that change repository or project files.
/// </summary>
public sealed class SolutionSelectionResolver(
    IVisualStudioSolutionDiscovery discovery,
    IDevDeckPromptService prompts)
{
    private const int PageSize = 9;

    /// <returns>Selected solution or null when the user cancels.</returns>
    public async Task<DeveloperContext?> ResolveAsync(
        string actionName,
        bool saveAllBeforeExecute = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VisualStudioSolutionInstance? selected = await discovery.GetFocusedSolutionAsync(cancellationToken);

        if (selected is null)
        {
            IReadOnlyList<VisualStudioSolutionInstance> open = await discovery.GetOpenSolutionsAsync(cancellationToken);
            var solutions = open
                .Where(x => !string.IsNullOrWhiteSpace(x.SolutionPath) && File.Exists(x.SolutionPath))
                .OrderBy(x => Path.GetFileNameWithoutExtension(x.SolutionPath), StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.SolutionPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.ProcessId)
                .ToArray();

            if (solutions.Length == 0)
                throw new InvalidOperationException(
                    "No saved Visual Studio solutions are open. Open a solution in Visual Studio first.");

            int page = 0;
            while (true)
            {
                var pageItems = solutions.Skip(page * PageSize).Take(PageSize).ToArray();
                var options = pageItems.Select((solution, i) => new DevDeckPromptChoice(
                    Id: i.ToString(),
                    Label: FormatSolutionName(solution, solutions),
                    Shortcut: (char)('1' + i),
                    Description: solution.SolutionPath)).ToList();

                if (page > 0)
                    options.Add(new DevDeckPromptChoice("previous", "Previous solutions", 'B'));
                if ((page + 1) * PageSize < solutions.Length)
                    options.Add(new DevDeckPromptChoice("next", "More solutions", 'N'));

                string? choice = await prompts.ChooseAsync(new DevDeckPromptRequest(
                    "Select Visual Studio solution",
                    $"{actionName.Replace('\n', ' ')} · {solutions.Length} open solution(s) · Page {page + 1}",
                    options), cancellationToken);

                if (choice is null) return null;
                if (choice == "previous") { page--; continue; }
                if (choice == "next") { page++; continue; }
                if (!int.TryParse(choice, out int index) || index < 0 || index >= pageItems.Length)
                    throw new InvalidOperationException("Unexpected solution selection returned by the overlay.");
                selected = pageItems[index];
                break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (saveAllBeforeExecute)
            await discovery.SaveAllAsync(selected.ProcessId, selected.SolutionPath, cancellationToken);

        string fullPath = Path.GetFullPath(selected.SolutionPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The selected Visual Studio solution is no longer available.", fullPath);
        return new DeveloperContext(Path.GetDirectoryName(fullPath)!, fullPath,
            Path.GetFileNameWithoutExtension(fullPath))
        {
            VisualStudioProcessId = selected.ProcessId
        };
    }

    private static string FormatSolutionName(VisualStudioSolutionInstance item,
        IReadOnlyList<VisualStudioSolutionInstance> all)
    {
        string name = Path.GetFileNameWithoutExtension(item.SolutionPath);
        // Distinguish two VS instances that have the same solution open.
        return all.Count(x => Path.GetFileNameWithoutExtension(x.SolutionPath)
            .Equals(name, StringComparison.OrdinalIgnoreCase)) > 1
            ? $"{name} (PID {item.ProcessId})" : name;
    }
}
