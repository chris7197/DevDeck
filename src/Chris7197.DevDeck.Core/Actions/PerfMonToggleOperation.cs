using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace chris7197.DevDeck.Core;

/// <summary>
/// Toggles an existing Windows Performance Monitor Data Collector Set (not an ad-hoc
/// counter trace). Never starts a collector when its current state is ambiguous.
/// </summary>
public sealed class PerfMonToggleOperation : IDevDeckOperation
{
    public const string ActionId = "com.chris7197.devdeck.perfmon.toggle";
    private readonly string? _defaultCollectorName;
    private readonly IPerfMonCommandRunner _runner;
    // Also prevents two different keys mapped to this action from racing each other.
    private readonly SemaphoreSlim _toggleLock = new(1, 1);

    public PerfMonToggleOperation(DeckSettings settings, IPerfMonCommandRunner? runner = null)
    {
        _defaultCollectorName = settings.PerfMonCollectorName;
        _runner = runner ?? new LogmanCommandRunner();
    }

    public string Name => "PerfMon";
    public string PluginActionId => ActionId;
    public string? IconName => DevDeckIcons.PerfMon;
    public IReadOnlyList<string> CliCommands => [];
    public string CliUsage => "";
    public bool RequiresSolutionContext => false;
    public bool TryParseCliArguments(IReadOnlyList<string> args, out string? target)
    {
        target = null;
        return false;
    }

    public Task<DevDeckButtonState> GetStateAsync(DeveloperContext? context, CancellationToken cancellationToken = default)
        => GetConfiguredStateAsync(context, null, cancellationToken);

    public async Task<DevDeckButtonState> GetConfiguredStateAsync(DeveloperContext? context,
        DeckActionSettings? actionSettings, CancellationToken cancellationToken = default)
    {
        string title = actionSettings?.Label is { Length: > 0 } label ? label : Name;
        string? name = GetCollectorName(actionSettings);
        if (name is null)
            return new(title, "Set name", DeckStatus.Unavailable,
                "Configure PerfMonCollectorName for this key or in settings.json. " +
                "Use 'logman query' to list installed collector sets.", IconName: IconName);

        try
        {
            PerfMonCollectorStatus status = await ReadStatusAsync(name, cancellationToken);
            return status switch
            {
                PerfMonCollectorStatus.Running => new(title, "RECORDING", DeckStatus.Error,
                    $"{name} is running. Press to STOP PerfMon logging.", IconName: IconName),
                PerfMonCollectorStatus.Stopped => new(title, "START", DeckStatus.Neutral,
                    $"{name} is stopped. Press to START PerfMon logging.", IconName: IconName),
                PerfMonCollectorStatus.NotFound => new(title, "Not found", DeckStatus.Unavailable,
                    $"Collector '{name}' was not found. Check 'logman query'.", IconName: IconName),
                _ => new(title, "Unknown", DeckStatus.Unavailable,
                    $"Cannot determine '{name}' status safely. No toggling while status is unknown.", IconName: IconName)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new(title, "Unavailable", DeckStatus.Unavailable,
                $"Cannot query PerfMon collector '{name}': {ex.Message}", IconName: IconName);
        }
    }

    public async Task<DevDeckOperationResult> ExecuteAsync(DevDeckOperationContext context)
    {
        string? name = GetCollectorName(context.ActionSettings);
        if (name is null)
            return new(false, "Configure PerfMonCollectorName first (for the key or globally in settings.json).");

        await _toggleLock.WaitAsync(context.CancellationToken);
        try
        {
            PerfMonCollectorStatus before = await ReadStatusAsync(name, context.CancellationToken);
            if (before is not (PerfMonCollectorStatus.Running or PerfMonCollectorStatus.Stopped))
                return new(false, $"Refusing to toggle '{name}': collector is {before}. Check 'logman query'.");

            bool stop = before == PerfMonCollectorStatus.Running;
            string verb = stop ? "stop" : "start";
            await context.ProgressAsync(stop ? "Stopping..." : "Starting...");
            PerfMonCommandResult result = await _runner.RunAsync([verb, name], context.CancellationToken);
            if (result.ExitCode != 0)
                return new(false, $"logman {verb} '{name}' failed: {FailureDetails(result)}. " +
                    "The collector may require elevated permissions.");

            PerfMonCollectorStatus after = await ReadStatusAsync(name, context.CancellationToken);
            PerfMonCollectorStatus expected = stop ? PerfMonCollectorStatus.Stopped : PerfMonCollectorStatus.Running;
            if (after != expected)
                return new(false, $"logman {verb} returned success, but '{name}' reports {after}. Check PerfMon.");

            return new(true, $"PerfMon collector '{name}' {(stop ? "stopped" : "started")}.");
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new(false, $"Could not toggle PerfMon '{name}': {ex.Message}");
        }
        finally { _toggleLock.Release(); }
    }

    private string? GetCollectorName(DeckActionSettings? settings)
    {
        string? name = string.IsNullOrWhiteSpace(settings?.PerfMonCollectorName)
            ? _defaultCollectorName : settings.PerfMonCollectorName;
        name = name?.Trim();
        // The process is invoked with ArgumentList, never through a shell.
        // Reject newlines/control characters to keep logging and matching unambiguous.
        return string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.Any(char.IsControl)
            ? null : name;
    }

    private async Task<PerfMonCollectorStatus> ReadStatusAsync(string name, CancellationToken ct)
    {
        PerfMonCommandResult query = await _runner.RunAsync(["query"], ct);
        if (query.ExitCode != 0)
            throw new InvalidOperationException("logman query failed: " + FailureDetails(query));
        return ParseStatus(query.Output, name);
    }

    /// <summary>Parses the tabular `logman query` output; never assumes an unknown value is Stopped.</summary>
    public static PerfMonCollectorStatus ParseStatus(string output, string collectorName)
    {
        foreach (string line in output.Split('\n'))
        {
            string[] columns = Regex.Split(line.Trim(), @"\s{2,}")
                .Where(column => column.Length > 0).ToArray();
            if (columns.Length < 3 || !string.Equals(columns[0], collectorName, StringComparison.OrdinalIgnoreCase))
                continue;

            string status = columns[^1].Trim();
            if (status.Equals("Running", StringComparison.OrdinalIgnoreCase))
                return PerfMonCollectorStatus.Running;
            if (status.Equals("Stopped", StringComparison.OrdinalIgnoreCase))
                return PerfMonCollectorStatus.Stopped;
            return PerfMonCollectorStatus.Unknown;
        }
        return PerfMonCollectorStatus.NotFound;
    }

    private static string FailureDetails(PerfMonCommandResult result)
    {
        string detail = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
        return string.IsNullOrWhiteSpace(detail) ? $"exit code {result.ExitCode}" : detail.Trim();
    }
}

public enum PerfMonCollectorStatus { Unknown, NotFound, Stopped, Running }

public sealed record PerfMonCommandResult(int ExitCode, string Output, string Error);

public interface IPerfMonCommandRunner
{
    Task<PerfMonCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

/// <summary>Runs only logman.exe with separate, non-shell arguments and a bounded timeout.</summary>
public sealed class LogmanCommandRunner : IPerfMonCommandRunner
{
    public async Task<PerfMonCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "logman.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (string argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Could not launch Windows logman.exe.");
            Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return new(process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException("Windows logman.exe did not respond within 8 seconds.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException("Could not start logman.exe: " + ex.Message, ex);
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }
}
