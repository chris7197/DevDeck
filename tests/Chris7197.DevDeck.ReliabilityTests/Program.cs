using System.Text;
using chris7197.DevDeck.Core;
using chris7197.DevDeck.Plugin;

try
{
    TestLogReaderAndSeverity();
    Console.WriteLine("PASS: tray log parsing, multiline errors, incremental notifications and rotation");
    TestActionCoordinator();
    Console.WriteLine("PASS: concurrent independent keys, one key at a time, exclusive mutations");
    await TestUnifiedCloseOthersAsync();
    Console.WriteLine("PASS: unified Close Others routes only to the actual foreground IDE");
    TestAtomicProjectUpdates();
    Console.WriteLine("PASS: atomic project update, duplicate avoidance, optimistic conflict detection");
    TestReportRetention();
    Console.WriteLine("PASS: safe report retention and recent capture protection");
    await TestComDispatcherAsync();
    Console.WriteLine("PASS: single STA, read retries, write safety, late-COM recovery and cancellation");
    await TestSocketEventIsolationAsync();
    Console.WriteLine("PASS: malformed WebSocket event and throwing subscribers are isolated");
    await TestMonitorRecoversAsync();
    Console.WriteLine("PASS: background status monitor recovers after a failed refresh");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("FAIL: " + ex);
    return 1;
}


static async Task TestUnifiedCloseOthersAsync()
{
    var foreground = new MutableCloseTabsForeground();
    var visualStudio = new FakeCloseTabsVisualStudioDiscovery();
    var vsAction = new FakeCloseTabsAction();
    var ssmsAction = new FakeCloseTabsAction();
    var operation = new CloseOtherTabsOperation(foreground, visualStudio, vsAction, ssmsAction);
    var context = new DevDeckOperationContext(new DeveloperContext(Environment.CurrentDirectory, null, null));

    if (operation.RequiresSolutionContext || operation.PluginActionId != CloseOtherTabsOperation.ActionId)
        throw new InvalidOperationException("Universal action incorrectly requires a solution or uses old UUID.");

    // Sticky profile must not close anything when another app is actually foreground.
    foreground.Window = new ForegroundWindowInfo("explorer", "Desktop");
    var result = await operation.ExecuteAsync(context);
    if (result.Success || vsAction.Calls != 0 || ssmsAction.Calls != 0)
        throw new InvalidOperationException("Closed tabs in a stale/background IDE.");
    var state = await operation.GetStateAsync(null);
    if (state.Status != DeckStatus.Unavailable)
        throw new InvalidOperationException("Out-of-focus state should be unavailable.");

    foreground.Window = new ForegroundWindowInfo("ssms", "Query Editor");
    result = await operation.ExecuteAsync(context);
    if (!result.Success || ssmsAction.Calls != 1 || vsAction.Calls != 0 || visualStudio.Lookups != 0)
        throw new InvalidOperationException("SSMS did not dispatch exclusively to its guarded shortcut.");

    foreground.Window = new ForegroundWindowInfo("devenv", "Visual Studio");
    visualStudio.Focused = new VisualStudioSolutionInstance(
        Path.Combine(Environment.CurrentDirectory, "Test.sln"), 1234, new IntPtr(4));
    result = await operation.ExecuteAsync(context);
    if (!result.Success || vsAction.Calls != 1 || ssmsAction.Calls != 1 ||
        vsAction.Last?.Developer.VisualStudioProcessId != 1234 ||
        vsAction.Last.Developer.SolutionName != "Test" || visualStudio.Lookups != 1)
        throw new InvalidOperationException("Visual Studio did not use the focused PID and solution.");

    // A focus match without a saved solution must not fall back to stale context.
    visualStudio.Focused = null;
    result = await operation.ExecuteAsync(context);
    if (result.Success || vsAction.Calls != 1)
        throw new InvalidOperationException("Close Others fell back to an old Visual Studio solution.");
}
static void TestActionCoordinator()
{
    var coordinator = new DevDeckActionCoordinator(3);
    using var sync = coordinator.TryBegin("key-1", "com.chris7197.devdeck.syncmain")
        ?? throw new InvalidOperationException("Could not reserve first key.");
    if (coordinator.TryBegin("key-1", "com.chris7197.devdeck.investigate") is not null)
        throw new InvalidOperationException("A busy key was allowed to run twice.");
    if (coordinator.TryBegin("key-2", "com.chris7197.devdeck.newmigration") is not null)
        throw new InvalidOperationException("Two project-changing actions ran simultaneously.");
    var read = coordinator.TryBegin("key-2", "com.chris7197.devdeck.latesterrorlog")
        ?? throw new InvalidOperationException("Independent log action was unnecessarily blocked by Git.");
    using var read2 = coordinator.TryBegin("key-3", "com.chris7197.devdeck.investigate")
        ?? throw new InvalidOperationException("Third independent slot failed.");
    if (coordinator.TryBegin("key-4", "com.chris7197.devdeck.lasterror") is not null)
        throw new InvalidOperationException("MaxConcurrentActions limit was ignored.");
    read.Dispose();
    read.Dispose(); // must be idempotent
    using var other = coordinator.TryBegin("key-4", "com.chris7197.devdeck.lasterror")
        ?? throw new InvalidOperationException("Action lease was not released.");
    sync.Dispose();
    // Closing tabs is an exclusive UI mutation, not a read-only action.
    using var close = coordinator.TryBegin("key-1", CloseOtherTabsOperation.ActionId)
        ?? throw new InvalidOperationException("Could not reserve Close Others key.");
    if (coordinator.TryBegin("key-5", "com.chris7197.devdeck.newmigration") is not null)
        throw new InvalidOperationException("Another mutation ran during Close Others.");
    close.Dispose();
    using var migration = coordinator.TryBegin("key-1", "com.chris7197.devdeck.newmigration")
        ?? throw new InvalidOperationException("Mutation lock was not released.");
}

static void TestAtomicProjectUpdates()
{
    string root = Path.Combine(Path.GetTempPath(), "DevDeckAtomic-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        string project = Path.Combine(root, "Legacy.csproj");
        string generated = Path.Combine(root, "Migrator.cs");
        File.WriteAllText(project,
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"><ItemGroup><Compile Include=\"Old.cs\" /></ItemGroup></Project>");
        File.WriteAllText(generated, "public class Migrator { }");
        var includer = new MigrationProjectItemIncluder();
        if (includer.EnsureIncluded(project, generated) != MigrationIncludeMethod.ProjectFileUpdated)
            throw new InvalidOperationException("Legacy project inclusion not applied.");
        if (includer.EnsureIncluded(project, generated) != MigrationIncludeMethod.AlreadyIncluded)
            throw new InvalidOperationException("Second include should not duplicate a Compile item.");
        string xml = File.ReadAllText(project);
        if (System.Text.RegularExpressions.Regex.Matches(xml, "Migrator.cs").Count != 1)
            throw new InvalidOperationException("Duplicate migration compile item.");

        // Another editor updates the project after DevDeck takes its snapshot.
        byte[] snapshot = File.ReadAllBytes(project);
        string staged = Path.Combine(root, ".devdeck-conflict.tmp");
        File.WriteAllText(staged, "<Project><ItemGroup /></Project>");
        File.AppendAllText(project, "<!-- edit from another process -->");
        bool detected = false;
        try { AtomicProjectFileUpdater.Commit(project, staged, snapshot); }
        catch (IOException) { detected = true; }
        if (!detected || !File.ReadAllText(project).Contains("edit from another process"))
            throw new InvalidOperationException("Concurrent editor changes were overwritten.");
        if (File.Exists(staged)) File.Delete(staged);
        if (Directory.GetFiles(root, ".devdeck-*.backup").Length != 0)
            throw new InvalidOperationException("Successful update left an unnecessary project backup.");
    }
    finally { Directory.Delete(root, recursive: true); }
}

static void TestReportRetention()
{
    string root = Path.Combine(Path.GetTempPath(), "DevDeckReports-" + Guid.NewGuid().ToString("N"));
    string captures = Path.Combine(root, "DbFlights");
    Directory.CreateDirectory(captures);
    DateTime now = DateTime.UtcNow;
    try
    {
        string Create(int daysOld, string name)
        {
            string folder = Path.Combine(captures,
                $"{now.AddDays(-daysOld):yyyyMMdd-HHmmss}-{name}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Summary.md"), "test capture");
            Directory.SetCreationTimeUtc(folder, now.AddDays(-daysOld));
            return folder;
        }
        string stale = Create(45, "Old");
        string young = Create(0, "Young");
        var extra = Enumerable.Range(0, 7).Select(i => Create(3 + i, "Recent")).ToArray();
        string arbitrary = Path.Combine(captures, "PersonalData");
        Directory.CreateDirectory(arbitrary);
        var retention = new ReportRetentionService(30, 5);
        int removed = retention.Prune(root, now);
        if (removed < 3 || Directory.Exists(stale) || !Directory.Exists(young) || !Directory.Exists(arbitrary))
            throw new InvalidOperationException("Report retention removed the wrong directories.");
        if (extra.Count(Directory.Exists) > 5)
            throw new InvalidOperationException("Report count limit was not applied.");
    }
    finally { Directory.Delete(root, recursive: true); }
}

static async Task TestSocketEventIsolationAsync()
{
    int errors = 0, received = 0;
    await using var socket = new StreamDockClient(_ => Interlocked.Increment(ref errors));
    socket.KeyDown += _ => throw new InvalidOperationException("Injected bad subscriber");
    socket.KeyDown += _ => { Interlocked.Increment(ref received); return Task.CompletedTask; };
    await socket.DispatchSafelyAsync(Encoding.UTF8.GetBytes("{ bad json"), CancellationToken.None);
    await socket.DispatchSafelyAsync(Encoding.UTF8.GetBytes(
        "{\"event\":\"keyDown\",\"context\":\"key-1\",\"action\":\"com.chris7197.devdeck.syncmain\"}"),
        CancellationToken.None);
    if (errors != 2 || received != 1)
        throw new InvalidOperationException($"Expected 2 recorded errors and 1 good subscriber call, found {errors}/{received}.");
}

static async Task TestMonitorRecoversAsync()
{
    string directory = Path.Combine(Path.GetTempPath(), "DevDeckMonitorTest-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var foreground = new ThrowOnceForeground();
        var runtime = DevDeckRuntime.Create(directory, foregroundWindowReader: foreground);
        IDevDeckOperation? close = runtime.FindAction(CloseOtherTabsOperation.ActionId);
        if (close is null ||
            !ReferenceEquals(close, runtime.FindAction(CloseOtherTabsOperation.OldVisualStudioActionId)) ||
            !ReferenceEquals(close, runtime.FindAction(CloseOtherTabsOperation.OldSsmsActionId)) ||
            runtime.Operations.Count(x => x.PluginActionId.Contains("closeothers", StringComparison.Ordinal)) != 1)
            throw new InvalidOperationException("Close Others compatibility aliases did not map to one operation.");
        var remembered = new ActiveDeveloperContext(runtime.ContextProvider,
            new DeveloperContext(directory, null, null));
        int updated = 0;
        await using var monitor = new DevDeckStatusMonitor(runtime, remembered, runtime.Profiles,
            (keyId, state) => { Interlocked.Increment(ref updated); return Task.CompletedTask; },
            TimeSpan.FromMilliseconds(120));
        monitor.Show("test-key", "com.chris7197.devdeck.opensolution");
        monitor.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (Volatile.Read(ref updated) == 0)
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(50, timeout.Token);
        }
        if (foreground.ReadCount < 2)
            throw new InvalidOperationException("Foreground detection was not retried.");
    }
    finally { Directory.Delete(directory, recursive: true); }
}

// Tests below use the dispatcher with synthetic callbacks: no VS installation is needed.
static async Task TestComDispatcherAsync()
{
    using (var dispatcher = new VisualStudioComDispatcher(12, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4)))
    {
        int running = 0, maximum = 0;
        var ids = new System.Collections.Concurrent.ConcurrentBag<int>();
        var jobs = Enumerable.Range(0, 10).Select(_ => dispatcher.InvokeAsync(() =>
        {
            int concurrent = Interlocked.Increment(ref running);
            int observed;
            do
            {
                observed = Volatile.Read(ref maximum);
                if (concurrent <= observed) break;
            } while (Interlocked.CompareExchange(ref maximum, concurrent, observed) != observed);
            ids.Add(Environment.CurrentManagedThreadId);
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("COM callbacks must run on an STA.");
            Thread.Sleep(15);
            Interlocked.Decrement(ref running);
            return 42;
        })).ToArray();
        int[] results = await Task.WhenAll(jobs);
        if (results.Any(x => x != 42) || maximum != 1 || ids.Distinct().Count() != 1)
            throw new InvalidOperationException("COM callbacks were not serialised on a single STA.");

        // Read-only RPC rejection may be retried, but modifying operations never are.
        int readAttempts = 0;
        int read = await dispatcher.InvokeAsync(() =>
        {
            if (Interlocked.Increment(ref readAttempts) <= 2)
                throw new System.Runtime.InteropServices.COMException("VS busy", unchecked((int)0x8001010A));
            return 17;
        });
        if (read != 17 || readAttempts != 3)
            throw new InvalidOperationException("Transient COM read retry failed.");

        int writeAttempts = 0;
        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                Interlocked.Increment(ref writeAttempts);
                throw new System.Runtime.InteropServices.COMException("VS busy", unchecked((int)0x8001010A));
#pragma warning disable CS0162
                return 0;
#pragma warning restore CS0162
            }, mayModifyVisualStudio: true);
            throw new InvalidOperationException("Expected COM rejection.");
        }
        catch (System.Runtime.InteropServices.COMException) { }
        if (writeAttempts != 1) throw new InvalidOperationException("A modifying COM call was retried.");

        // Missing/stale Visual Studio instances should fail cleanly and never poison
        // the shared STA: no COM automation is attempted for PID 0.
        var locator = new VisualStudioLocator(dispatcher);
        try
        {
            await locator.GetSolutionPathForProcessIdAsync(0);
            throw new InvalidOperationException("Expected a missing VS process failure.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("no longer running")) { }
        if (await dispatcher.InvokeAsync(() => 73) != 73)
            throw new InvalidOperationException("Missing VS process poisoned the COM dispatcher.");
    }

    using (var dispatcher = new VisualStudioComDispatcher(3,
        TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200)))
    using (var entered = new ManualResetEventSlim(false))
    using (var release = new ManualResetEventSlim(false))
    {
        Task<int> blocked = dispatcher.InvokeAsync(() =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(3));
            return 99;
        }, mayModifyVisualStudio: true);
        if (!entered.Wait(TimeSpan.FromSeconds(2))) throw new InvalidOperationException("Worker did not start.");
        try
        {
            await blocked;
            throw new InvalidOperationException("Expected bounded COM timeout.");
        }
        catch (VisualStudioAutomationTimeoutException error) when (error.OperationMayHaveExecuted) { }
        try
        {
            await dispatcher.InvokeAsync(() => 1);
            throw new InvalidOperationException("The stalled dispatcher accepted new calls.");
        }
        catch (VisualStudioAutomationUnavailableException) { }
        finally { release.Set(); }

        // A timed-out modifying request must NOT be retried, but once the original
        // callback really returns, the *same* STA worker must become available again.
        int resumed = await WaitForComRecoveryAsync(dispatcher);
        if (resumed != 123)
            throw new InvalidOperationException("STA did not recover after a late COM return.");
    }

    using (var dispatcher = new VisualStudioComDispatcher(3,
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)))
    using (var entered = new ManualResetEventSlim(false))
    using (var release = new ManualResetEventSlim(false))
    {
        Task<int> first = dispatcher.InvokeAsync(() =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(2));
            return 1;
        });
        if (!entered.Wait(TimeSpan.FromSeconds(1))) throw new InvalidOperationException("Worker did not start.");
        using var cancel = new CancellationTokenSource();
        int executed = 0;
        Task<int> queued = dispatcher.InvokeAsync(() => { Interlocked.Increment(ref executed); return 2; },
            cancellationToken: cancel.Token);
        cancel.Cancel();
        try { await queued; throw new InvalidOperationException("Queued cancellation failed."); }
        catch (OperationCanceledException) { }
        release.Set();
        if (await first != 1) throw new InvalidOperationException("Original callback failed.");
        int final = await dispatcher.InvokeAsync(() => 3);
        if (executed != 0 || final != 3)
            throw new InvalidOperationException("A cancelled queued callback ran or poisoned the dispatcher.");
    }

    using (var dispatcher = new VisualStudioComDispatcher(3,
        TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3)))
    using (var entered = new ManualResetEventSlim(false))
    using (var release = new ManualResetEventSlim(false))
    using (var cancellation = new CancellationTokenSource())
    {
        int calls = 0;
        Task<int> running = dispatcher.InvokeAsync(() =>
        {
            Interlocked.Increment(ref calls);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(3));
            return 1;
        }, mayModifyVisualStudio: true, cancellationToken: cancellation.Token);
        if (!entered.Wait(TimeSpan.FromSeconds(1)))
            throw new InvalidOperationException("Cancelled COM callback did not start.");
        cancellation.Cancel();
        try { await running; throw new InvalidOperationException("Expected caller cancellation."); }
        catch (OperationCanceledException) { }
        try { await dispatcher.InvokeAsync(() => 2); throw new InvalidOperationException("STA resumed too early."); }
        catch (VisualStudioAutomationUnavailableException) { }
        finally { release.Set(); }
        if (await WaitForComRecoveryAsync(dispatcher) != 123 || calls != 1)
            throw new InvalidOperationException("Cancellation caused a duplicate write or left the STA unavailable.");
    }
}

static async Task<int> WaitForComRecoveryAsync(VisualStudioComDispatcher dispatcher)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    while (true)
    {
        timeout.Token.ThrowIfCancellationRequested();
        try { return await dispatcher.InvokeAsync(() => 123, cancellationToken: timeout.Token); }
        catch (VisualStudioAutomationUnavailableException)
        {
            await Task.Delay(20, timeout.Token);
        }
    }
}
static void TestLogReaderAndSeverity()
{
    string folder = Path.Combine(Path.GetTempPath(), "DevDeckTrayTest-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    try
    {
        string path = Path.Combine(folder, "devdeck.log");
        var logger = new DeckLog(path);
        logger.Write("Normal startup");
        logger.WriteWarning("Transient issue");
        logger.WriteError("Action failed: unexpected\n  at ThisMethod()\n  at OtherMethod()");
        var watcher = new DevDeckLogReader(path);
        IReadOnlyList<DevDeckLogEntry> initial = watcher.Initialize();
        if (initial.Count != 3 || initial[0].Severity != DevDeckLogSeverity.Information ||
            initial[1].Severity != DevDeckLogSeverity.Warning ||
            initial[2].Severity != DevDeckLogSeverity.Error || !initial[2].Details.Contains("OtherMethod"))
            throw new InvalidOperationException("Log history or multiline classification incorrect.");
        if (watcher.Poll().Entries.Count != 0)
            throw new InvalidOperationException("Existing errors generated notifications on startup.");
        logger.WriteError("Action failed: new error");
        DevDeckLogPoll delta = watcher.Poll();
        if (delta.Reinitialized || delta.Entries.Count != 1 ||
            delta.Entries[0].Summary != "Action failed: new error")
            throw new InvalidOperationException("Incremental log read failed.");
        if (watcher.Poll().Entries.Count != 0)
            throw new InvalidOperationException("An old error was delivered twice.");
        File.WriteAllText(path, "[2026-10-14T12:00:00+01:00] [INFO] replacement\n");
        if (!watcher.Poll().Reinitialized)
            throw new InvalidOperationException("Truncated log was not reinitialized.");
        // Clearing through the tray must truncate the file without preventing future
        // incremental log reads. The tray explicitly calls Initialize after Clear.
        logger.Clear();
        if (new FileInfo(path).Length != 0)
            throw new InvalidOperationException("Clear log did not truncate the file.");
        if (watcher.Initialize().Count != 0)
            throw new InvalidOperationException("Reader still showed historical entries after clear.");
        logger.WriteError("Action failed: after clear");
        DevDeckLogPoll afterClear = watcher.Poll();
        if (afterClear.Reinitialized || afterClear.Entries.Count != 1 ||
            afterClear.Entries[0].Summary != "Action failed: after clear")
            throw new InvalidOperationException("Reader did not resume correctly after clearing the log.");
        if (DevDeckLogReader.Classify("", "No error log found for solution") != DevDeckLogSeverity.Information ||
            DevDeckLogReader.Classify("", "SYNC FAILED: issue") != DevDeckLogSeverity.Error)
            throw new InvalidOperationException("Legacy severity detection too broad or missing.");

        // Trace logging is disabled unless explicitly enabled. The running plugin
        // should observe changes immediately and the reader must classify TRACE.
        logger.WriteTrace("Should not exist");
        if (watcher.Poll().Entries.Count != 0)
            throw new InvalidOperationException("Trace was recorded while disabled.");
        logger.SetTraceEnabled(true);
        if (!logger.TraceEnabled) throw new InvalidOperationException("Trace was not enabled.");
        logger.WriteTrace("SQL[abc12345] SSMS copy timed out.");
        DevDeckLogPoll traces = watcher.Poll();
        if (traces.Entries.Count != 1 || traces.Entries[0].Severity != DevDeckLogSeverity.Trace)
            throw new InvalidOperationException("Trace record was not parsed correctly.");
        logger.SetTraceEnabled(false);
        logger.WriteTrace("Should not exist either");
        if (watcher.Poll().Entries.Count != 0)
            throw new InvalidOperationException("Trace continued after being disabled.");
    }
    finally { Directory.Delete(folder, recursive: true); }
}

sealed class MutableCloseTabsForeground : IForegroundWindowReader
{
    public ForegroundWindowInfo? Window { get; set; }
    public ForegroundWindowInfo? Read() => Window;
}

sealed class FakeCloseTabsVisualStudioDiscovery : IVisualStudioSolutionDiscovery
{
    public VisualStudioSolutionInstance? Focused { get; set; }
    public int Lookups { get; private set; }
    public Task<VisualStudioSolutionInstance?> GetFocusedSolutionAsync(CancellationToken cancellationToken = default)
    {
        Lookups++;
        return Task.FromResult(Focused);
    }
    public Task<IReadOnlyList<VisualStudioSolutionInstance>> GetOpenSolutionsAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The unified action should not enumerate unfocused solutions.");
    public Task SaveAllAsync(uint processId, string expectedSolutionPath, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The unified action should not save files.");
}

sealed class FakeCloseTabsAction : IDevDeckOperation
{
    public string Name => "Fake Close Others";
    public string PluginActionId => "test.closeothers";
    public IReadOnlyList<string> CliCommands => [];
    public string CliUsage => "";
    public int Calls { get; private set; }
    public DevDeckOperationContext? Last { get; private set; }
    public bool TryParseCliArguments(IReadOnlyList<string> args, out string? target)
    {
        target = null;
        return false;
    }
    public Task<DevDeckOperationResult> ExecuteAsync(DevDeckOperationContext context)
    {
        Calls++;
        Last = context;
        return Task.FromResult(new DevDeckOperationResult(true, "Mock close"));
    }
}

sealed class ThrowOnceForeground : IForegroundWindowReader
{
    private int _reads;
    public int ReadCount => Volatile.Read(ref _reads);
    public ForegroundWindowInfo? Read()
    {
        if (Interlocked.Increment(ref _reads) == 1)
            throw new InvalidOperationException("Injected transient foreground failure");
        return null;
    }
}
