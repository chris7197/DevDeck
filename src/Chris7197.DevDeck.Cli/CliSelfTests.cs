using chris7197.DevDeck.Core;

internal static class CliSelfTests
{
    public static IReadOnlyList<DevDeckCliCommand> Commands() =>
    [
        Command("--selftest-solution-selection", SelftestSolutionSelectionAsync),
        Command("--selftest-overlay-contracts", SelftestOverlayContractsAsync),
        Command("--selftest-investigation", SelftestInvestigationAsync),
        Command("--selftest-db-flight-config", SelftestDbFlightConfigAsync),
        Command("--selftest-extra-actions", SelftestExtraActionsAsync),
    ];

    private static DevDeckCliCommand Command(string name, Func<DevDeckCliContext, Task<int>> run)
        => new(name, name, cli => cli.Arguments.Count == 1 ? run(cli) : cli.InvalidArgumentsAsync(name));

    private static async Task<int> SelftestSolutionSelectionAsync(DevDeckCliContext cli)
    {
        DevDeckRuntime runtime = cli.Runtime;
        string[] args = cli.Arguments.ToArray();
        return await SolutionSelectionSelfTest.RunAsync();
    }

    private static async Task<int> SelftestOverlayContractsAsync(DevDeckCliContext cli)
    {
        DevDeckRuntime runtime = cli.Runtime;
        string[] args = cli.Arguments.ToArray();
        var request = new DevDeckPromptRequest("Investigate", "000615",
            [new("product", "Product", 'P'), new("accessory", "Accessory", 'A')]);
        DevDeckPromptValidation.Validate(request);
        string json = System.Text.Json.JsonSerializer.Serialize(request);
        var restored = System.Text.Json.JsonSerializer.Deserialize<DevDeckPromptRequest>(json)!;
        bool matches = restored.Choices.Count == 2 && restored.Choices[0].Shortcut == 'P' &&
            SmartInvestigationService.Classify("000615") == InvestigationKind.ManufacturingCode &&
            runtime.FindAction("com.chris7197.devdeck.investigate") is IDevDeckInvestigationCommands;
        try { DevDeckPromptValidation.Validate(request with { Choices =
            [new("product", "Product", 'P'), new("accessory", "Accessory", 'P')] }); matches = false; }
        catch (ArgumentException) { }
        // Exercise the two distinct investigation routes without launching a UI or SQL server.
        var testContext = new DeveloperContext(Path.GetTempPath(), null, null);
        InvestigationResult product = await GetPluginCommand<IDevDeckInvestigationCommands>(runtime, "com.chris7197.devdeck.investigate").InvestigateRawAsync(
            "000615", testContext, selectedKind: InvestigationKind.ProductCode);
        InvestigationResult accessory = await GetPluginCommand<IDevDeckInvestigationCommands>(runtime, "com.chris7197.devdeck.investigate").InvestigateRawAsync(
            "000615", testContext, selectedKind: InvestigationKind.AccessoryCode);
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

    private static async Task<int> SelftestInvestigationAsync(DevDeckCliContext cli)
    {
        DevDeckRuntime runtime = cli.Runtime;
        string[] args = cli.Arguments.ToArray();
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

    private static async Task<int> SelftestDbFlightConfigAsync(DevDeckCliContext cli)
    {
        DevDeckRuntime runtime = cli.Runtime;
        string[] args = cli.Arguments.ToArray();
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

    private static async Task<int> SelftestExtraActionsAsync(DevDeckCliContext cli)
    {
        DevDeckRuntime runtime = cli.Runtime;
        string[] args = cli.Arguments.ToArray();
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

    private static T GetPluginCommand<T>(DevDeckRuntime runtime, string actionId) where T : class =>
        runtime.FindAction(actionId) as T ?? throw new InvalidOperationException(
            $"Action plugin '{actionId}' is unavailable or lacks requested test capabilities.");

}
