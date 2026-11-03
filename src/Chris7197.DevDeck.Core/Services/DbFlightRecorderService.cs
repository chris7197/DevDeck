using System.Data;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace chris7197.DevDeck.Core;

public sealed class DbFlightRecorderTarget
{
    public string Name { get; set; } = "";
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public bool TrustServerCertificate { get; set; } = false;
    public bool CaptureSqlText { get; set; } = false;
    public bool CaptureExecutionPlans { get; set; } = false;
}

public sealed record DbFlightPreview(string TargetName, string Server, string Database, bool SqlText, bool Plans, string Source = "");
public sealed record DbFlightResult(string Directory, string SummaryFile, int SectionsSucceeded, int SectionsFailed);

/// <summary>On-demand, bounded, read-only SQL Server DMV capture. Never connects during status refresh.</summary>
public sealed class DbFlightRecorderService(DeckSettings settings)
{
    private static readonly (string Name, string Query)[] Queries =
    [
        ("Server info", """
            SELECT @@SERVERNAME AS ServerName, DB_NAME() AS DatabaseName,
                   SYSDATETIMEOFFSET() AS CollectedAt, @@VERSION AS ServerVersion;
            """),
        ("Active requests", """
            SELECT TOP (30) r.session_id, r.blocking_session_id,
                DB_NAME(r.database_id) AS DatabaseName, r.status, r.command,
                r.wait_type, r.wait_time, r.total_elapsed_time, r.cpu_time, r.reads, r.writes,
                s.login_name, s.host_name, s.program_name
            FROM sys.dm_exec_requests r
            LEFT JOIN sys.dm_exec_sessions s ON s.session_id = r.session_id
            WHERE r.database_id = DB_ID() AND r.session_id <> @@SPID
            ORDER BY r.total_elapsed_time DESC;
            """),
        ("Blocked requests", """
            SELECT TOP (30) r.session_id, r.blocking_session_id, r.wait_type,
                   r.wait_time, r.wait_resource, r.total_elapsed_time, r.status
            FROM sys.dm_exec_requests r
            WHERE r.database_id = DB_ID() AND r.blocking_session_id <> 0
            ORDER BY r.wait_time DESC;
            """),
        ("Waiting tasks", """
            SELECT TOP (30) w.session_id, w.blocking_session_id, w.wait_type,
                   w.wait_duration_ms, w.resource_description
            FROM sys.dm_os_waiting_tasks w
            INNER JOIN sys.dm_exec_requests r ON r.session_id = w.session_id
            WHERE r.database_id = DB_ID()
            ORDER BY w.wait_duration_ms DESC;
            """),
        ("Server wait stats", """
            SELECT TOP (20) wait_type, waiting_tasks_count, wait_time_ms,
                   max_wait_time_ms, signal_wait_time_ms
            FROM sys.dm_os_wait_stats
            WHERE waiting_tasks_count > 0 AND wait_type NOT LIKE 'SLEEP%'
            ORDER BY wait_time_ms DESC;
            """),
        ("File IO", """
            SELECT DB_NAME(v.database_id) AS DatabaseName, f.name AS LogicalFileName,
                   v.num_of_reads, v.io_stall_read_ms, v.num_of_writes, v.io_stall_write_ms,
                   v.num_of_bytes_read, v.num_of_bytes_written
            FROM sys.dm_io_virtual_file_stats(DB_ID(), NULL) v
            INNER JOIN sys.database_files f ON f.file_id = v.file_id;
            """)
    ];

    private readonly DbFlightConnectionResolver _connections = new(settings);

    public DbFlightPreview Preview(string name) => Preview(name, null);

    /// <summary>Offline preview: reads configuration but never connects to SQL.</summary>
    public DbFlightPreview Preview(string? targetName, string? solutionPath)
    {
        ResolvedDbFlightConnection target = _connections.Resolve(targetName, solutionPath);
        return new(target.Name, target.Server, target.Database, target.CaptureSqlText,
            target.CaptureExecutionPlans, target.Source);
    }

    // Preserve the original named-target CLI API.
    public Task<DbFlightResult> CaptureAsync(string targetName, CancellationToken cancellationToken = default) =>
        CaptureAsync(targetName, null, cancellationToken);

    public async Task<DbFlightResult> CaptureAsync(string? targetName, string? solutionPath,
        CancellationToken cancellationToken = default)
    {
        ResolvedDbFlightConnection target = _connections.Resolve(targetName, solutionPath);
        // The application connection string may contain SQL credentials. Keep it in memory only;
        // never write the raw value to reports, output or the DevDeck log.
        string connectionString = target.ConnectionString;
        string directory = DeveloperReportStore.CreateDirectory("DbFlights", target.Name);
        var summary = new StringBuilder($"# DB Flight Recorder — {target.Name}\n\n");
        summary.AppendLine($"- Time: {DateTimeOffset.Now:O}");
        summary.AppendLine($"- Server: `{target.Server}`");
        summary.AppendLine($"- Database: `{target.Database}`");
        summary.AppendLine($"- Source: `{target.Source}`");
        summary.AppendLine($"- SQL text: {target.CaptureSqlText}; execution plans: {target.CaptureExecutionPlans}");
        summary.AppendLine("- Scope: read-only DMV snapshot; sections have row limits and timeouts. Some need server-level monitoring permissions. Server wait stats are instance-wide, not database-specific.\n");
        int success = 0, failures = 0;
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            // Ensure connection did not resolve to an unexpected database.
            if (!string.Equals(connection.Database, target.Database, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SQL Server selected a different database than configured.");
            foreach ((string name, string query) in Queries)
                await CaptureSectionAsync(name, query, connection);

            if (target.CaptureSqlText)
            {
                await CaptureSectionAsync("Active SQL", """
                    SELECT TOP (20) r.session_id, r.total_elapsed_time,
                           r.wait_type, LEFT(st.text, 4000) AS SqlText
                    FROM sys.dm_exec_requests r
                    OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) st
                    WHERE r.database_id = DB_ID() AND r.session_id <> @@SPID
                    ORDER BY r.total_elapsed_time DESC;
                    """, connection);
                await CaptureSectionAsync("Top cached SQL", """
                    SELECT TOP (20) qs.execution_count, qs.total_worker_time, qs.total_elapsed_time,
                           qs.total_logical_reads, qs.last_execution_time,
                           LEFT(st.text, 4000) AS SqlText
                    FROM sys.dm_exec_query_stats qs
                    CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
                    WHERE st.dbid = DB_ID()
                    ORDER BY qs.total_elapsed_time DESC;
                    """, connection);
            }
            if (target.CaptureExecutionPlans)
            {
                // XML plan inspection can be expensive; hard cap to TWO active plans.
                const string planSql = """
                    SELECT TOP (2) r.session_id, TRY_CONVERT(nvarchar(max), qp.query_plan) AS PlanXml
                    FROM sys.dm_exec_requests r
                    OUTER APPLY sys.dm_exec_query_plan(r.plan_handle) qp
                    WHERE r.database_id = DB_ID() AND r.session_id <> @@SPID AND qp.query_plan IS NOT NULL
                    ORDER BY r.total_elapsed_time DESC;
                    """;
                try
                {
                    await using var command = new SqlCommand(planSql, connection) { CommandTimeout = 10 };
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    int count = 0;
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        int session = Convert.ToInt32(reader.GetValue(0));
                        string xml = reader.IsDBNull(1) ? "" : reader.GetString(1);
                        if (xml.Length == 0 || xml.Length > 2_000_000)
                        {
                            summary.AppendLine($"- Plan for session {session}: unavailable or larger than 2 MB; skipped.");
                            continue;
                        }
                        string file = $"plan-session-{session}.sqlplan";
                        await File.WriteAllTextAsync(Path.Combine(directory, file), xml, cancellationToken);
                        summary.AppendLine($"- Captured plan: `{file}`");
                        count++;
                    }
                    success++;
                    if (count == 0) summary.AppendLine("- Plans: no active query plans available.");
                }
                catch (Exception error) when (error is SqlException or InvalidCastException)
                {
                    failures++;
                    summary.AppendLine($"- Plans: {error.Message} (skipped)");
                }
            }
        }
        catch (Exception error) when (error is SqlException or InvalidOperationException)
        {
            failures++;
            summary.AppendLine($"\n**Connection/capture failure:** {error.Message}");
        }
        string summaryPath = Path.Combine(directory, "Summary.md");
        await File.WriteAllTextAsync(summaryPath, summary.ToString(), cancellationToken);
        return new(directory, summaryPath, success, failures);

        async Task CaptureSectionAsync(string name, string query, SqlConnection connection)
        {
            summary.AppendLine($"## {name}");
            try
            {
                await using var command = new SqlCommand(query, connection) { CommandTimeout = 10 };
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                var records = new List<Dictionary<string, object?>>();
                while (records.Count < 30 && await reader.ReadAsync(cancellationToken))
                {
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    for (int col = 0; col < reader.FieldCount; col++)
                    {
                        object? value = reader.IsDBNull(col) ? null : reader.GetValue(col);
                        row[reader.GetName(col)] = value is string txt ? DeveloperReportStore.Limited(txt, 4000)
                            : value is byte[] b ? Convert.ToHexString(b.AsSpan(0, Math.Min(b.Length, 512))) : value;
                    }
                    records.Add(row);
                }
                string filename = name.Replace(' ', '-') + ".json";
                await File.WriteAllTextAsync(Path.Combine(directory, filename),
                    JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
                summary.AppendLine($"{records.Count} rows. See `{filename}`.\n");
                success++;
            }
            catch (Exception error) when (error is SqlException or InvalidOperationException)
            {
                failures++;
                summary.AppendLine($"Unavailable: {error.Message}\n");
            }
        }
    }
}
