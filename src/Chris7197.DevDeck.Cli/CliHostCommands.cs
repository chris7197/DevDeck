using chris7197.DevDeck.Core;

internal static class CliHostCommands
{
    public static IReadOnlyList<DevDeckCliCommand> Commands() =>
    [
        Command("--list-plugins", 1, 1, ListPluginsAsync),
        new("--run", "--run <actionId> [solution.sln]", RunActionAsync),
        new("--run-slot", "--run-slot <profile> <slot 1-15> [solution.sln]", RunSlotAsync),
        Command("--choose-solution", 1, 1, ChooseSolutionAsync),
        Command("--overlay-demo", 1, 1, OverlayDemoAsync),
        Command("--list-actions", 1, 1, ListActionsAsync),
        Command("--test-profile-rules", 1, 1, TestProfileRulesAsync),
        Command("--show-profile", 2, 3, ShowProfileAsync),
        Command("--show-active-solution", 1, 1, ShowActiveSolutionAsync),
        Command("--show-context", 1, 2, ShowContextAsync),
        Command("--show-status", 1, 2, ShowStatusAsync),
        Command("--watch-status", 1, 2, WatchStatusAsync),
    ];

    private static DevDeckCliCommand Command(string name, int minArgs, int maxArgs,
        Func<DevDeckCliContext, Task<int>> handler) => new(name, name,
            cli => cli.Arguments.Count >= minArgs && cli.Arguments.Count <= maxArgs
                ? handler(cli) : cli.InvalidArgumentsAsync(name));

    private static async Task<int> RunActionAsync(DevDeckCliContext cli)
    {
        if (cli.Arguments.Count is < 2 or > 3)
            return await cli.InvalidArgumentsAsync("--run <actionId> [solution.sln]");
        IDevDeckOperation? action = cli.Runtime.FindAction(cli.Arguments[1]);
        if (action is null)
        {
            cli.Error.WriteLine($"Unknown or unavailable action '{cli.Arguments[1]}'. Use --list-actions / --list-plugins.");
            return 1;
        }
        return await ExecuteAsync(cli, action, null, cli.Arguments.Count == 3 ? cli.Arguments[2] : null);
    }

    private static async Task<int> RunSlotAsync(DevDeckCliContext cli)
    {
        if (cli.Arguments.Count is < 3 or > 4 || !int.TryParse(cli.Arguments[2], out int slot) || slot is < 1 or > 15)
            return await cli.InvalidArgumentsAsync("--run-slot <profile> <slot 1-15> [solution.sln]");
        string profile = cli.Arguments[1];
        cli.Runtime.Profiles.SetManualProfile(profile);
        IDevDeckOperation? action = cli.Runtime.ResolveProfileAction(DevDeckProfiles.SlotId(slot));
        if (action is null)
        {
            cli.Error.WriteLine($"No loaded action in profile '{profile}', slot {slot}.");
            return 1;
        }
        cli.Runtime.Settings.ButtonPresets.TryGetValue($"{profile}/{slot}", out DeckActionSettings? settings);
        return await ExecuteAsync(cli, action, settings, cli.Arguments.Count == 4 ? cli.Arguments[3] : null);
    }

    private static async Task<int> ExecuteAsync(DevDeckCliContext cli, IDevDeckOperation action,
        DeckActionSettings? settings, string? path)
    {
        DeveloperContext? developer = path is null && !action.RequiresSolutionFor(settings)
            ? new DeveloperContext(Environment.CurrentDirectory, null, null)
            : await cli.ResolveContextAsync(path, action.Name, action.SaveAllBeforeExecute);
        if (developer is null) { cli.Output.WriteLine("Cancelled."); return 0; }
        // Explicitly running an action is intentional; honour the plugin's confirmation warning.
        string? warning = action.ConfirmationText(developer);
        if (!string.IsNullOrWhiteSpace(warning)) cli.Output.WriteLine("WARNING: " + warning);
        return await cli.RunOperationAsync(action, developer, settings);
    }

    private static Task<int> ListPluginsAsync(DevDeckCliContext cli)
    {
        DevDeckRuntime runtime = cli.Runtime;

        foreach (IDevDeckOperation operation in runtime.Operations)
        {
            Console.WriteLine(
                $"{operation.Name} ({operation.PluginActionId})");

            Console.WriteLine(
                $"  Commands: {string.Join(", ", operation.CliCommands)}");

            Console.WriteLine(
                $"  Usage: {operation.CliUsage}");
        }

        return Task.FromResult(0);
    }

    private static async Task<int> ChooseSolutionAsync(DevDeckCliContext cli)
    {
        DevDeckRuntime runtime = cli.Runtime;
        string[] args = cli.Arguments.ToArray();
        DeveloperContext? chosen = await runtime.SolutionSelector.ResolveAsync("Solution selection demo");
        Console.WriteLine(chosen?.SolutionPath ?? "(cancelled)");
        return 0;
    }

    private static async Task<int> OverlayDemoAsync(DevDeckCliContext cli)
    {
        DevDeckRuntime runtime = cli.Runtime;
        string[] args = cli.Arguments.ToArray();
        string? selected = await runtime.Prompts.ChooseAsync(new DevDeckPromptRequest(
            "Investigate clipboard", "000615",
            [new("product", "Product", 'P', "Search product"),
             new("accessory", "Accessory", 'A', "Search accessory")]));
        Console.WriteLine("Overlay selection: " + (selected ?? "(cancelled)"));
        return 0;
    }

    private static async Task<int> ListActionsAsync(DevDeckCliContext cli)
    {
        DevDeckRuntime runtime = cli.Runtime;
        string[] args = cli.Arguments.ToArray();
        foreach (IDevDeckOperation action in runtime.Operations)
            Console.WriteLine($"{action.PluginActionId}: {action.Name.Replace('\n', ' ')}");
        return 0;
    }

    private static async Task<int> TestProfileRulesAsync(DevDeckCliContext cli)
    {
        DevDeckRuntime runtime = cli.Runtime;
        string[] args = cli.Arguments.ToArray();
        var profiles = runtime.Profiles;
        var checks = new (ForegroundWindowInfo Foreground, string Expected)[]
        {
            (new("devenv", "SampleApp - Microsoft Visual Studio"), "VisualStudio"),
            (new("ssms", "SQL Server Management Studio"), "SqlServer"),
            (new("msedge", "Analytics Dashboard - Microsoft Edge"), "BrowserBi"),
            (new("chrome", "Analytics Dashboard - Google Chrome"), "BrowserBi"),
            (new("chrome", "General Search - Chrome"), "Default"),
            (new("explorer", "File Explorer"), "Default")
        };
        bool passed = true;
        foreach (var test in checks)
        {
            string actual = profiles.DetectProfile(test.Foreground);
            Console.WriteLine($"{test.Foreground.ProcessName} / {test.Foreground.Title}: {actual}");
            passed &= actual.Equals(test.Expected, StringComparison.OrdinalIgnoreCase);
        }
        return passed ? 0 : 1;
    }

    private static async Task<int> ShowProfileAsync(DevDeckCliContext cli)
    {
        DevDeckRuntime runtime = cli.Runtime;
        string[] args = cli.Arguments.ToArray();
        // Offline preview of the exact actions assigned to each physical key; doesn't
        // change the user's foreground application or execute shortcuts.
        runtime.Profiles.SetManualProfile(args[1]);
        DeveloperContext? context = args.Length == 3
            ? await runtime.ContextProvider.ResolveAsync(args[2]) : null;
        Console.WriteLine($"Profile: {runtime.Profiles.CurrentProfile}");
        for (int slot = 1; slot <= 15; slot++)
        {
            IDevDeckOperation? operation = runtime.ResolveProfileAction(DevDeckProfiles.SlotId(slot));
            if (operation is null) { Console.WriteLine($"{slot:00}: (unassigned)"); continue; }
            runtime.Settings.ButtonPresets.TryGetValue($"{args[1]}/{slot}", out var perSlot);
            DevDeckButtonState state = await runtime.States.GetConfiguredStateAsync(operation, context, perSlot);
            Console.WriteLine($"{slot:00}: {operation.Name.Replace('\n', ' ')} — {state.Subtitle}");
        }
        return 0;
    }

    private static async Task<int> ShowActiveSolutionAsync(DevDeckCliContext cli)
    {
        DevDeckRuntime runtime = cli.Runtime;
        string[] args = cli.Arguments.ToArray();
        DeveloperContext active = await runtime.ContextProvider.ResolveAsync(null);
        Console.WriteLine(active.RequireSolutionPath());
        return 0;
    }

    private static async Task<int> ShowContextAsync(DevDeckCliContext cli)
    {
        DevDeckRuntime runtime = cli.Runtime;
        string[] args = cli.Arguments.ToArray();
        DeveloperContext context = await runtime.ContextProvider.ResolveAsync(args.Length == 2 ? args[1] : null);
        Console.WriteLine("Solution: " + (context.SolutionPath ?? "(none; repository directory supplied)"));
        Console.WriteLine("Solution name: " + (context.SolutionName ?? "(none)"));
        Console.WriteLine("Working directory: " + context.WorkingDirectory);
        return 0;
    }

    private static async Task<int> ShowStatusAsync(DevDeckCliContext cli)
    {
        DevDeckRuntime runtime = cli.Runtime;
        string[] args = cli.Arguments.ToArray();
        DeveloperContext context = await runtime.ContextProvider.ResolveAsync(args.Length == 2 ? args[1] : null);
        Console.WriteLine("Solution: " + (context.SolutionPath ?? context.WorkingDirectory));
        foreach (IDevDeckOperation operation in runtime.Operations)
        {
            DevDeckButtonState state = await runtime.States.GetStateAsync(operation, context);
            Console.WriteLine($"[{state.Status}] {operation.Name.Replace('\n', ' ')}: {state.Subtitle}");
            if (!string.IsNullOrWhiteSpace(state.Description))
                Console.WriteLine("    " + state.Description.Replace('\n', ' '));
        }
        return 0;
    }

    private static async Task<int> WatchStatusAsync(DevDeckCliContext cli)
    {
        DevDeckRuntime runtime = cli.Runtime;
        string[] args = cli.Arguments.ToArray();
        DeveloperContext context = await runtime.ContextProvider.ResolveAsync(args.Length == 2 ? args[1] : null);
        var active = new ActiveDeveloperContext(runtime.ContextProvider, context);
        await using var monitor = new DevDeckStatusMonitor(runtime, active, runtime.Profiles,
            (key, state) =>
            {
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} [{state.Status}] {state.Title}: {state.Subtitle}");
                return Task.CompletedTask;
            }, TimeSpan.FromSeconds(runtime.Settings.LiveRefreshSeconds));
        foreach (IDevDeckOperation op in runtime.Operations) monitor.Show(op.PluginActionId, op.PluginActionId);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            monitor.Start();
            Console.WriteLine("Watching read-only statuses; Ctrl+C to exit (no Git fetch or actions performed).");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { Console.CancelKeyPress -= cancel; }
        return 0;
    }

}
