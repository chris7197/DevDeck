using chris7197.DevDeck.Core;

try
{
    DevDeckRuntime runtime = DevDeckRuntime.Create(AppContext.BaseDirectory);
    string command = args.FirstOrDefault() ?? string.Empty;

    if (command == "--choose-solution" && args.Length == 1)
    {
        DeveloperContext? chosen = await runtime.SolutionSelector.ResolveAsync("Solution selection demo");
        Console.WriteLine(chosen?.SolutionPath ?? "(cancelled)");
        return 0;
    }

    if (command == "--selftest-solution-selection" && args.Length == 1)
        return await SolutionSelectionSelfTest.RunAsync();

    if (command == "--overlay-demo" && args.Length == 1)
    {
        string? selected = await runtime.Prompts.ChooseAsync(new DevDeckPromptRequest(
            "Investigate clipboard", "000615",
            [new("product", "Product", 'P', "Search product"),
             new("accessory", "Accessory", 'A', "Search accessory")]));
        Console.WriteLine("Overlay selection: " + (selected ?? "(cancelled)"));
        return 0;
    }

    if (command == "--investigate-prompt-text" && args.Length is 2 or 3)
    {
        DeveloperContext context = args.Length == 3
            ? await runtime.ContextProvider.ResolveAsync(args[2])
            : new DeveloperContext(Environment.CurrentDirectory, null, null);
        var action = (SmartInvestigateOperation)runtime.FindAction("com.chris7197.devdeck.investigate")!;
        DevDeckOperationResult result = await action.InvestigateTextAsync(args[1], new DevDeckOperationContext(context));
        Console.WriteLine(result.Message);
        return result.Success || result.Cancelled ? 0 : 1;
    }

    if (command == "--selftest-overlay-contracts" && args.Length == 1)
    {
        var request = new DevDeckPromptRequest("Investigate", "000615",
            [new("product", "Product", 'P'), new("accessory", "Accessory", 'A')]);
        DevDeckPromptValidation.Validate(request);
        string json = System.Text.Json.JsonSerializer.Serialize(request);
        var restored = System.Text.Json.JsonSerializer.Deserialize<DevDeckPromptRequest>(json)!;
        bool matches = restored.Choices.Count == 2 && restored.Choices[0].Shortcut == 'P' &&
            SmartInvestigationService.Classify("000615") == InvestigationKind.ManufacturingCode &&
            runtime.FindAction("com.chris7197.devdeck.investigate") is SmartInvestigateOperation;
        try { DevDeckPromptValidation.Validate(request with { Choices =
            [new("product", "Product", 'P'), new("accessory", "Accessory", 'P')] }); matches = false; }
        catch (ArgumentException) { }
        // Exercise the two distinct investigation routes without launching a UI or SQL server.
        var testContext = new DeveloperContext(Path.GetTempPath(), null, null);
        InvestigationResult product = await runtime.Investigations.InvestigateAsync(
            "000615", testContext, false, selectedKind: InvestigationKind.ProductCode);
        InvestigationResult accessory = await runtime.Investigations.InvestigateAsync(
            "000615", testContext, false, selectedKind: InvestigationKind.AccessoryCode);
        try
        {
            matches &= product.Kind == InvestigationKind.ProductCode &&
                       accessory.Kind == InvestigationKind.AccessoryCode &&
                       File.ReadAllText(product.ReportPath!).Contains("# ProductCode investigation") &&
                       File.ReadAllText(accessory.ReportPath!).Contains("# AccessoryCode investigation");
        }
        finally
        {
            if (product.ReportPath is not null) Directory.Delete(Path.GetDirectoryName(product.ReportPath)!, true);
            if (accessory.ReportPath is not null) Directory.Delete(Path.GetDirectoryName(accessory.ReportPath)!, true);
        }
        Console.WriteLine(matches ? "Overlay contracts and Product/Accessory routing: PASS" : "Overlay routing: FAIL");
        return matches ? 0 : 1;
    }

    if (command == "--selftest-investigation" && args.Length == 1)
    {
        var checks = new Dictionary<string, InvestigationKind>
        {
            ["abcdef12345"] = InvestigationKind.GitCommit,
            ["000615"] = InvestigationKind.ManufacturingCode,
            ["dbo.SampleItems"] = InvestigationKind.SqlObject,
            ["SampleApp.Services.Example"] = InvestigationKind.CSharpSymbol,
            ["System.InvalidOperationException: test"] = InvestigationKind.Exception,
            ["https://example.org/issue/123"] = InvestigationKind.Url,
            ["password-please-do-not-store-this"] = InvestigationKind.Unrecognised
        };
        DbFlightPreview offline = new DbFlightRecorderService(new DeckSettings
        {
            DbFlightRecorderTargets = [new DbFlightRecorderTarget
            { Name = "Offline", Server = "does-not-exist", Database = "OfflineDatabase" }]
        }).Preview("Offline");
        bool ok = offline.Server == "does-not-exist" && offline.Database == "OfflineDatabase" &&
            checks.All(check => SmartInvestigationService.Classify(check.Key) == check.Value) &&
            runtime.FindAction("com.chris7197.devdeck.investigate") is not null &&
            runtime.FindAction("com.chris7197.devdeck.dbflight") is not null;
        foreach (var check in checks) Console.WriteLine($"{check.Value}: {check.Key}");
        Console.WriteLine(ok ? "Investigate / DB registration: PASS" : "Investigate / DB registration: FAIL");
        return ok ? 0 : 1;
    }

    if (command == "--selftest-db-flight-config" && args.Length == 1)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "DevDeck-DbConfig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string sampleJsonSolution = Path.Combine(temporary, "SampleApp.sln");
            string sampleXmlSolution = Path.Combine(temporary, "SampleLegacy.sln");
            File.WriteAllText(sampleJsonSolution, "");
            File.WriteAllText(sampleXmlSolution, "");
            File.WriteAllText(Path.Combine(temporary, "appsettings.json"),
                """
                { "ConnectionStrings": { "MainDb": "Server=LocalJson;Database=JsonDb;Integrated Security=true" } }
                """);
            File.WriteAllText(Path.Combine(temporary, "web.config"),
                """
                <configuration><connectionStrings configSource="connections.config" /></configuration>
                """);
            File.WriteAllText(Path.Combine(temporary, "connections.config"),
                """
                <connectionStrings>
                  <add name="LegacyDb" connectionString="Data Source=LocalXml;Initial Catalog=XmlDb;Integrated Security=true" providerName="System.Data.SqlClient" />
                </connectionStrings>
                """);

            File.WriteAllText(Path.Combine(temporary, "legacy-entity.config"),
                """
                <configuration><connectionStrings>
                <add name="LegacyEf" providerName="System.Data.EntityClient"
                  connectionString="metadata=res://*/;provider=System.Data.SqlClient;provider connection string=&quot;Data Source=EfSql;Initial Catalog=EfDb;Integrated Security=True&quot;" />
                </connectionStrings></configuration>
                """);
            var demoSettings = new DeckSettings
            {
                DbFlightRecorderConfigMappings =
                [
                    new() { PathFilter = "*SampleApp.sln", ConfigPath = "appsettings.json", ConnectionStringName = "MainDb" },
                    new() { PathFilter = "*SampleLegacy.sln", ConfigPath = "web.config", ConnectionStringName = "LegacyDb" }
                ],
                DbFlightRecorderTargets =
                [
                    new() { Name = "Explicit", Server = "ExplicitSql", Database = "ExplicitDb" }
                ]
            };
            var service = new DbFlightRecorderService(demoSettings);
            DbFlightPreview json = service.Preview(null, sampleJsonSolution);
            DbFlightPreview xml = service.Preview(null, sampleXmlSolution);
            DbFlightPreview named = service.Preview("Explicit", sampleJsonSolution);
            var efSettings = new DeckSettings { DbFlightRecorderConfigMappings =
                [new() { PathFilter = "*SampleLegacy.sln", ConfigPath = "legacy-entity.config", ConnectionStringName = "LegacyEf" }] };
            DbFlightPreview ef = new DbFlightRecorderService(efSettings).Preview(null, sampleXmlSolution);
            bool passed = json.Server == "LocalJson" && json.Database == "JsonDb" &&
                xml.Server == "LocalXml" && xml.Database == "XmlDb" &&
                named.Server == "ExplicitSql" && named.Database == "ExplicitDb" &&
                named.Source == "Named target" && ef.Server == "EfSql" && ef.Database == "EfDb";
            Console.WriteLine("JSON mapping: " + json.Server + " / " + json.Database);
            Console.WriteLine("XML configSource: " + xml.Server + " / " + xml.Database);
            Console.WriteLine("Explicit override: " + named.Server + " / " + named.Database);
            Console.WriteLine("Legacy EF EntityClient: " + ef.Server + " / " + ef.Database);
            Console.WriteLine(passed ? "Config mapping resolution: PASS" : "Config mapping resolution: FAIL");
            Console.WriteLine("All checks are offline; no database connections were opened.");
            return passed ? 0 : 1;
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }

    if (command == "--investigate-text" && args.Length is 2 or 3)
    {
        DeveloperContext context = await runtime.ContextProvider.ResolveAsync(args.Length == 3 ? args[2] : null);
        InvestigationResult result = await runtime.Investigations.InvestigateAsync(args[1], context, openReport: false);
        Console.WriteLine($"{result.Kind}: {result.Message}");
        if (result.ReportPath is not null) Console.WriteLine("Report: " + result.ReportPath);
        return result.ReportPath is null ? 1 : 0;
    }

    if (command == "--investigate-clipboard" && args.Length is 1 or 2)
    {
        DeveloperContext context = await runtime.ContextProvider.ResolveAsync(args.Length == 2 ? args[1] : null);
        // Use the same operation as the SOOMFON button, including any ambiguity prompt.
        var action = (SmartInvestigateOperation)runtime.FindAction("com.chris7197.devdeck.investigate")!;
        DevDeckOperationResult result = await action.ExecuteAsync(new DevDeckOperationContext(context));
        Console.WriteLine(result.Message);
        return result.Success || result.Cancelled ? 0 : 1;
    }

    if (command == "--db-flight-preview-solution" && args.Length is 1 or 2)
    {
        DeveloperContext? context = await ChooseOrUseExplicitAsync(runtime, args.Length == 2 ? args[1] : null, "DB Flight Recorder");
        if (context is null) return 0;
        DbFlightPreview target = runtime.FlightRecorder.Preview(null, context.RequireSolutionPath());
        Console.WriteLine($"{target.TargetName}: {target.Server} / {target.Database}");
        Console.WriteLine($"Config source: {target.Source}");
        Console.WriteLine($"SQL text: {target.SqlText}; plans: {target.Plans}");
        Console.WriteLine("Preview only — no SQL connection, and no passwords printed.");
        return 0;
    }

    if (command == "--db-flight-capture-solution" && args.Length is 1 or 2)
    {
        DeveloperContext? context = await ChooseOrUseExplicitAsync(runtime, args.Length == 2 ? args[1] : null, "DB Flight Recorder");
        if (context is null) return 0;
        DbFlightPreview target = runtime.FlightRecorder.Preview(null, context.RequireSolutionPath());
        Console.WriteLine($"Starting read-only capture of {target.Server} / {target.Database}...");
        DbFlightResult result = await runtime.FlightRecorder.CaptureAsync(null, context.RequireSolutionPath());
        Console.WriteLine($"Sections captured: {result.SectionsSucceeded}; failed: {result.SectionsFailed}");
        Console.WriteLine("Report: " + result.SummaryFile);
        return result.SectionsSucceeded > 0 && result.SectionsFailed == 0 ? 0 : 1;
    }

    if (command == "--db-flight-preview" && args.Length == 2)
    {
        DbFlightPreview target = runtime.FlightRecorder.Preview(args[1]);
        Console.WriteLine($"{target.TargetName}: {target.Server} / {target.Database}");
        Console.WriteLine($"SQL text: {target.SqlText}; plans: {target.Plans}");
        Console.WriteLine("Preview only — NO database connection made.");
        return 0;
    }

    if (command == "--db-flight-capture" && args.Length == 2)
    {
        DbFlightPreview target = runtime.FlightRecorder.Preview(args[1]);
        Console.WriteLine($"Starting READ-ONLY capture of {target.Server} / {target.Database}...");
        DbFlightResult result = await runtime.FlightRecorder.CaptureAsync(args[1]);
        Console.WriteLine($"Sections captured: {result.SectionsSucceeded}; failed: {result.SectionsFailed}");
        Console.WriteLine("Report: " + result.SummaryFile);
        return result.SectionsSucceeded > 0 && result.SectionsFailed == 0 ? 0 : 1;
    }

    if (command == "--selftest-extra-actions" && args.Length == 1)
    {
        runtime.Profiles.SetManualProfile("VisualStudio");
        bool passes = TeamsChannelService.Initials("Sarah Jones") == "SJ"
            && TeamsChannelService.Initials("Chris") == "C"
            && TeamsChannelService.Initials("") == ""
            && runtime.FindAction("com.chris7197.devdeck.teamschannel") is not null
            && runtime.FindAction("com.chris7197.devdeck.openvscode") is not null
            && runtime.FindAction("com.chris7197.devdeck.opensolution") is not null
            && runtime.Profiles.Resolve(DevDeckProfiles.SlotId(6)) is not null;
        Console.WriteLine(passes ? "New actions and initials: PASS" : "New actions and initials: FAIL");
        return passes ? 0 : 1;
    }

    if (command == "--teams-login" && args.Length == 1)
    {
        await runtime.Teams.SignInAsync(Console.WriteLine);
        return 0;
    }

    if (command == "--teams-status" && args.Length is 3 or 4)
    {
        var channel = new DeckActionSettings { TeamId = args[1], ChannelId = args[2],
            Label = args.Length == 4 ? args[3] : "Teams" };
        DevDeckButtonState state = await new TeamsChannelOperation(runtime.Teams)
            .GetConfiguredStateAsync(null, channel);
        Console.WriteLine($"{state.Title}: {state.Subtitle} — {state.Description}");
        return state.Status == DeckStatus.Unavailable ? 1 : 0;
    }

    if (command == "--open-solution" && args.Length == 2)
    {
        var action = runtime.FindAction("com.chris7197.devdeck.opensolution")!;
        var result = await action.ExecuteAsync(new DevDeckOperationContext(
            new DeveloperContext(Environment.CurrentDirectory, null, null),
            ActionSettings: new DeckActionSettings { SolutionPath = args[1] }));
        Console.WriteLine(result.Message);
        return result.Success ? 0 : 1;
    }

    if (command == "--open-vscode" && args.Length is 1 or 2)
    {
        var action = runtime.FindAction("com.chris7197.devdeck.openvscode")!;
        DeveloperContext? context = await ChooseOrUseExplicitAsync(runtime, args.Length == 2 ? args[1] : null, "Open in VS Code");
        if (context is null) return 0;
        var result = await action.ExecuteAsync(new DevDeckOperationContext(context));
        Console.WriteLine(result.Message);
        return result.Success ? 0 : 1;
    }

    if (command == "--list-actions" && args.Length == 1)
    {
        foreach (IDevDeckOperation action in runtime.Operations)
            Console.WriteLine($"{action.PluginActionId}: {action.Name.Replace('\n', ' ')}");
        return 0;
    }

    if (command == "--test-profile-rules" && args.Length == 1)
    {
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

    if (command == "--show-profile" && args.Length is >= 2 and <= 3)
    {
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

    if (command == "--show-active-solution" && args.Length == 1)
    {
        DeveloperContext active = await runtime.ContextProvider.ResolveAsync(null);
        Console.WriteLine(active.RequireSolutionPath());
        return 0;
    }

    if (command == "--show-context" && (args.Length is 1 or 2))
    {
        DeveloperContext context = await runtime.ContextProvider.ResolveAsync(args.Length == 2 ? args[1] : null);
        Console.WriteLine("Solution: " + (context.SolutionPath ?? "(none; repository directory supplied)"));
        Console.WriteLine("Solution name: " + (context.SolutionName ?? "(none)"));
        Console.WriteLine("Working directory: " + context.WorkingDirectory);
        return 0;
    }

    if (command == "--find-latest-log" && (args.Length is 1 or 2))
    {
        DeveloperContext? context = await ChooseOrUseExplicitAsync(runtime, args.Length == 2 ? args[1] : null, "Latest Log");
        if (context is null) return 0;
        Console.WriteLine("Solution: " + context.RequireSolutionPath());
        Console.WriteLine("Latest log: " + (runtime.LatestLogs.FindLatest(context.RequireSolutionPath()) ?? "Not Found"));
        return 0;
    }

    if (command == "--find-last-error" && (args.Length is 1 or 2))
    {
        DeveloperContext? context = await ChooseOrUseExplicitAsync(runtime, args.Length == 2 ? args[1] : null, "Last Error");
        if (context is null) return 0;
        LastErrorInfo? result = runtime.LastErrors.FindLast(context.RequireSolutionPath());
        if (result is null) { Console.WriteLine("No log file found."); return 1; }
        Console.WriteLine("Log: " + result.LogPath);
        Console.WriteLine("Error: " + result.Summary);
        Console.WriteLine("Source: " + (result.SourcePath ?? "(not identified)"));
        Console.WriteLine("Line: " + (result.SourceLine?.ToString() ?? "(not identified)"));
        Console.WriteLine("Details:\n" + result.Details);
        return string.IsNullOrWhiteSpace(result.Details) ? 1 : 0;
    }

    if (command == "--show-status" && (args.Length is 1 or 2))
    {
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

    if (command == "--watch-status" && (args.Length is 1 or 2))
    {
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

    if (command == "--preview-migration" && (args.Length is 1 or 2))
    {
        DeveloperContext? context = await ChooseOrUseExplicitAsync(runtime, args.Length == 2 ? args[1] : null, "Preview Migration");
        if (context is null) return 0;
        MigrationPreview preview = runtime.Migrations.Preview(context.RequireSolutionPath());
        Console.WriteLine("Project: " + preview.ProjectFile);
        Console.WriteLine("Migration: " + preview.MigrationFile);
        Console.WriteLine("Namespace: " + preview.MigrationNamespace);
        Console.WriteLine("Version: " + preview.Version);
        return 0;
    }

    if (command == "--create-migration-no-open" && (args.Length is 1 or 2))
    {
        DeveloperContext? context = await ChooseOrUseExplicitAsync(runtime, args.Length == 2 ? args[1] : null, "New Migration");
        if (context is null) return 0;
        MigrationCreation created = await runtime.Migrations.CreateAsync(context.RequireSolutionPath(), openEditor: false);
        Console.WriteLine("Created: " + created.Preview.MigrationFile);
        Console.WriteLine("Version: " + created.Preview.Version);
        Console.WriteLine("Project inclusion: " + created.IncludeMethod);
        return 0;
    }

    foreach (IDevDeckOperation operation in runtime.Operations)
    {
        if (!operation.CliCommands.Contains(command, StringComparer.Ordinal)) continue;
        if (!operation.TryParseCliArguments(args, out string? target)) return PrintUsage(runtime.Operations);

        if (command is "--repo" or "--active-vs")
            Console.WriteLine("WARNING: This stages and commits ALL repo changes, then fetches and merges without confirmation.");

        DeveloperContext? context = target is null && operation.RequiresSolutionContext
            ? await runtime.SolutionSelector.ResolveAsync(operation.Name, operation.SaveAllBeforeExecute)
            : await runtime.ContextProvider.ResolveAsync(target, operation.SaveAllBeforeExecute);
        if (context is null)
        {
            Console.WriteLine("Cancelled.");
            return 0;
        }
        DevDeckOperationResult result = await operation.ExecuteAsync(new DevDeckOperationContext(
            context,
            stage =>
            {
                Console.WriteLine(" > " + stage.Replace('\n', ' '));
                return Task.CompletedTask;
            }));
        Console.WriteLine(result.Message);
        return result.Success ? 0 : result.HasConflicts ? 2 : 1;
    }

    return PrintUsage(runtime.Operations);
}
catch (Exception e)
{
    Console.Error.WriteLine(e);
    return 1;
}

static int PrintUsage(IEnumerable<IDevDeckOperation> operations)
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  DevDeck.Cli --selftest-overlay-contracts (offline overlay + routing checks)");
    Console.WriteLine("  DevDeck.Cli --selftest-solution-selection (offline focused VS / numbered overlay checks)");
    Console.WriteLine("  DevDeck.Cli --choose-solution (read-only discovery and selection preview)");
    Console.WriteLine("  DevDeck.Cli --overlay-demo (show P/A popup, no investigation)");
    Console.WriteLine("  DevDeck.Cli --investigate-prompt-text <text> [solution.sln] (popup if ambiguous)");
    Console.WriteLine("  DevDeck.Cli --selftest-investigation (offline, no changes)");
    Console.WriteLine("  DevDeck.Cli --investigate-text <text> [solution.sln] (local report)");
    Console.WriteLine("  DevDeck.Cli --investigate-clipboard [solution.sln] (local report)");
    Console.WriteLine("  DevDeck.Cli --selftest-db-flight-config (offline config mapping tests)");
    Console.WriteLine("  DevDeck.Cli --db-flight-preview-solution [solution.sln] (offline)");
    Console.WriteLine("  DevDeck.Cli --db-flight-capture-solution [solution.sln] (read-only DMVs)");
    Console.WriteLine("  DevDeck.Cli --db-flight-preview <targetName> (no connection)");
    Console.WriteLine("  DevDeck.Cli --db-flight-capture <targetName> (connects, READ-ONLY DMVs)");
    Console.WriteLine("  DevDeck.Cli --selftest-extra-actions          (no network or changes)");
    Console.WriteLine("  DevDeck.Cli --teams-login                      (Microsoft device-code login)");
    Console.WriteLine("  DevDeck.Cli --teams-status <teamId> <channelId> [label] (live message status)");
    Console.WriteLine("  DevDeck.Cli --open-solution <fullpath.sln>     (focus or open VS)");
    Console.WriteLine("  DevDeck.Cli --open-vscode [solution.sln]       (launch VS Code)");
    Console.WriteLine("  DevDeck.Cli --test-profile-rules              (read-only rule test)");
    Console.WriteLine("  DevDeck.Cli --show-profile <profile> [solution.sln] (read-only profile preview)");
    Console.WriteLine("  DevDeck.Cli --show-active-solution             (no changes)");
    Console.WriteLine("  DevDeck.Cli --show-context [solution.sln]      (no changes)");
    Console.WriteLine("  DevDeck.Cli --list-actions                     (no changes)");
    Console.WriteLine("  DevDeck.Cli --show-status [solution.sln]      (read-only one-shot)");
    Console.WriteLine("  DevDeck.Cli --watch-status [solution.sln]     (read-only live watch)");
    Console.WriteLine("  DevDeck.Cli --find-latest-log [solution.sln]   (no changes)");
    Console.WriteLine("  DevDeck.Cli --find-last-error [solution.sln]   (read-only)");
    Console.WriteLine("  DevDeck.Cli --preview-migration [solution.sln] (read-only)");
    Console.WriteLine("  DevDeck.Cli --create-migration-no-open [solution.sln] (creates .cs, no editor)");
    foreach (IDevDeckOperation operation in operations)
        Console.WriteLine("  DevDeck.Cli " + operation.CliUsage);
    return 64;
}

static async Task<DeveloperContext?> ChooseOrUseExplicitAsync(
    DevDeckRuntime runtime, string? explicitPath, string actionName)
{
    return string.IsNullOrWhiteSpace(explicitPath)
        ? await runtime.SolutionSelector.ResolveAsync(actionName)
        : await runtime.ContextProvider.ResolveAsync(explicitPath);
}
