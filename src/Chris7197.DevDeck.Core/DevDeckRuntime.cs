namespace chris7197.DevDeck.Core;

/// <summary>
/// Composition root shared by the CLI and Stream Dock plugin.
/// Register new operations once here; both hosts receive the same action instances.
/// </summary>
public sealed class DevDeckRuntime
{
    public DeckSettings Settings { get; }
    public DeckLog Log { get; }
    public VisualStudioComDispatcher VisualStudioCom { get; }
    public DeveloperContextProvider ContextProvider { get; }
    public LatestErrorLogService LatestLogs { get; }
    public LastErrorService LastErrors { get; }
    public NewMigrationService Migrations { get; }
    public TeamsChannelService Teams { get; }
    public SmartInvestigationService Investigations { get; }
    public IDevDeckPromptService Prompts { get; }
    public SolutionSelectionResolver SolutionSelector { get; }
    public DbFlightRecorderService FlightRecorder { get; }
    public IReadOnlyList<IDevDeckOperation> Operations { get; }
    public DevDeckStatusService States { get; }
    public DevDeckProfiles Profiles { get; }
    private readonly IReadOnlyDictionary<string, IDevDeckOperation> _byActionId;

    private DevDeckRuntime(DeckSettings settings, DeckLog log, IForegroundWindowReader? foregroundWindowReader)
    {
        Settings = settings;
        Log = log;
        DeveloperReportStore.ConfigureRetention(settings.ReportRetentionDays, settings.MaxReportsPerKind);
        // Don't delay the controller's WebSocket startup while pruning old reports.
        _ = Task.Run(() =>
        {
            try { DeveloperReportStore.PruneReports(e => log.Write("Report cleanup warning: " + e.Message)); }
            catch (Exception e) { log.Write("Report cleanup unexpectedly failed: " + e); }
        });
        VisualStudioCom = VisualStudioComDispatcher.Shared;
        var visualStudio = new VisualStudioLocator(VisualStudioCom);
        var documents = new VisualStudioDocumentService(VisualStudioCom);
        ContextProvider = new DeveloperContextProvider(visualStudio);
        LatestLogs = new LatestErrorLogService(new LogFileResolver(settings), log);
        LastErrors = new LastErrorService(LatestLogs, settings);
        Teams = new TeamsChannelService(settings.Teams);
        var navigator = new VisualStudioSourceNavigator(VisualStudioCom);
        Investigations = new SmartInvestigationService(new GitProcess(log, 15, logCommands: false), settings);
        Prompts = new SerializedPromptService(new ExternalOverlayPromptService());
        SolutionSelector = new SolutionSelectionResolver(visualStudio, Prompts);
        FlightRecorder = new DbFlightRecorderService(settings);
        Migrations = new NewMigrationService(new MigrationProjectResolver(settings), log, navigator,
            new MigrationProjectItemIncluder(), new VisualStudioProjectItemAdder(VisualStudioCom));

        IForegroundWindowReader foreground = foregroundWindowReader ?? new WindowsForegroundWindowReader();
        Profiles = new DevDeckProfiles(settings, foreground);
        var git = new GitProcess(log, settings.GitCommandTimeoutSeconds);
        Operations =
        [
            new GitSyncOperation(new GitSyncService(settings, git, log), log, settings.SaveAllBeforeCommit),
            new LatestErrorLogOperation(LatestLogs),
            new LastErrorOperation(LastErrors, navigator, log),
            new NewMigrationOperation(Migrations),
            new TeamsChannelOperation(Teams),
            new OpenInVsCodeOperation(settings, documents),
            new CloseOtherTabsOperation(foreground, visualStudio,
                new CloseOtherVisualStudioTabsOperation(VisualStudioCom),
                new ShortcutOperation(CloseOtherTabsOperation.OldSsmsActionId, "Close Others",
                    "CTRL+ALT+SHIFT+W", DevDeckIcons.CloseOthers, "SqlServer", Profiles, foreground)),
            new FormatCSharpOperation(documents),
            new FormatSqlOperation(foreground, visualStudio, VisualStudioCom,
                new chris7197SqlFormatter(), settings),
            new OpenSolutionOperation(visualStudio),
            new SmartInvestigateOperation(Investigations, navigator, Prompts, settings.SmartInvestigateAmbiguousCodePattern),
            new DbFlightRecorderOperation(FlightRecorder),
            new ShortcutOperation("com.chris7197.devdeck.ssms.newquery", "New Query", "CTRL+N", DevDeckIcons.SqlNewQuery, "SqlServer", Profiles, foreground),
            new ShortcutOperation("com.chris7197.devdeck.ssms.execute", "Execute SQL", "F5", DevDeckIcons.SqlExecute, "SqlServer", Profiles, foreground),
            new ShortcutOperation("com.chris7197.devdeck.ssms.plan", "Actual Plan", "CTRL+M", DevDeckIcons.SqlPlan, "SqlServer", Profiles, foreground),
            new ShortcutOperation("com.chris7197.devdeck.browser.refresh", "Refresh BI", "CTRL+R", DevDeckIcons.BrowserRefresh, "BrowserBi", Profiles, foreground),
            new ShortcutOperation("com.chris7197.devdeck.browser.devtools", "DevTools", "F12", DevDeckIcons.BrowserDevTools, "BrowserBi", Profiles, foreground),
            new ShortcutOperation("com.chris7197.devdeck.browser.hardrefresh", "Hard Reload", "CTRL+SHIFT+R", DevDeckIcons.BrowserHardRefresh, "BrowserBi", Profiles, foreground)
        ];
        States = new DevDeckStatusService(settings, new GitProcess(log, Math.Min(settings.GitCommandTimeoutSeconds, 15), logCommands: false),
            LatestLogs, LastErrors, new MigrationProjectResolver(settings));
        var actionsById = Operations.ToDictionary(operation => operation.PluginActionId, StringComparer.Ordinal);
        // Existing installations preserve settings.json. Resolve either previous UUID
        // to the ONE current action without advertising duplicate manifest actions.
        actionsById[CloseOtherTabsOperation.OldVisualStudioActionId] =
            actionsById[CloseOtherTabsOperation.ActionId];
        actionsById[CloseOtherTabsOperation.OldSsmsActionId] =
            actionsById[CloseOtherTabsOperation.ActionId];
        _byActionId = actionsById;
        foreach (var profile in settings.Profiles)
        foreach (string? actionId in profile.Value)
            if (!string.IsNullOrWhiteSpace(actionId) && !_byActionId.ContainsKey(actionId))
                throw new InvalidOperationException($"Profile '{profile.Key}' references unknown action: {actionId}");
    }

    public static DevDeckRuntime Create(string baseDirectory, DeckLog? log = null,
        IForegroundWindowReader? foregroundWindowReader = null) =>
        new(DeckSettings.Load(baseDirectory), log ?? new DeckLog(), foregroundWindowReader);

    public IDevDeckOperation? ResolveProfileAction(string actionOrSlotId) =>
        Profiles.Resolve(actionOrSlotId) is { } resolved ? FindAction(resolved) : null;

    public IDevDeckOperation? FindAction(string actionId) =>
        _byActionId.TryGetValue(actionId, out var operation) ? operation : null;
}
