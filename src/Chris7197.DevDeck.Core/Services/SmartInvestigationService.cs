using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace chris7197.DevDeck.Core;

public enum InvestigationKind { Exception, GitCommit, SqlObject, CSharpSymbol, ManufacturingCode, ProductCode, AccessoryCode, Url, Unrecognised }
public sealed record InvestigationResult(InvestigationKind Kind, string Message, string? ReportPath, string? SourcePath = null, int? SourceLine = null);

/// <summary>Local clipboard triage. Unrecognised content is never persisted.</summary>
public sealed class SmartInvestigationService(GitProcess git, DeckSettings? settings = null)
{
    private static readonly Regex GitHash = new(@"\A[0-9a-fA-F]{7,40}\z", RegexOptions.Compiled);
    private static readonly Regex Manufacturing = new(@"\A\d{6}\z", RegexOptions.Compiled);
    private static readonly Regex Identifier = new(@"\A(?:@?[\p{L}_][\p{L}\p{N}_]*)(?:\.(?:[\p{L}_][\p{L}\p{N}_]*|\[[^\]\r\n]{1,128}\])){0,5}\z", RegexOptions.Compiled);
    private static readonly Regex StackPath = new(@"\bin (?<path>[A-Za-z]:\\[^\r\n]+?\.(?:cs|vb)):line (?<line>\d+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SqlName = new(@"\A(?:\[[^\]\r\n]{1,128}\]|[A-Za-z_][A-Za-z_0-9]*)(?:\.(?:\[[^\]\r\n]{1,128}\]|[A-Za-z_][A-Za-z_0-9]*)){1,3}\z", RegexOptions.Compiled);
    private static readonly string[] SearchExtensions = [".cs", ".sql", ".cshtml", ".razor", ".json", ".config", ".xml"];
    private static readonly string[] IgnoredFolders = [".git", ".vs", "bin", "obj", "node_modules", "packages", "TestResults"];

    public static InvestigationKind Classify(string value)
    {
        string text = value.Trim();
        if (text.Contains("Exception", StringComparison.OrdinalIgnoreCase) || StackPath.IsMatch(text) ||
            text.Contains("   at ", StringComparison.Ordinal)) return InvestigationKind.Exception;
        if (GitHash.IsMatch(text)) return InvestigationKind.GitCommit;
        if (Manufacturing.IsMatch(text)) return InvestigationKind.ManufacturingCode;
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return InvestigationKind.Url;
        if (SqlName.IsMatch(text) && (text.Contains('[') || text.StartsWith("dbo.", StringComparison.OrdinalIgnoreCase)))
            return InvestigationKind.SqlObject;
        if (Identifier.IsMatch(text) && text.Length >= 3) return InvestigationKind.CSharpSymbol;
        return InvestigationKind.Unrecognised;
    }

    public static Task<string> ReadClipboardAsync()
    {
        var promise = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { promise.SetResult(Clipboard.ContainsText() ? Clipboard.GetText(TextDataFormat.UnicodeText) : ""); }
            catch (Exception error) { promise.SetException(error); }
        }) { IsBackground = true, Name = "DevDeck clipboard STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return promise.Task;
    }

    public async Task<InvestigationResult> InvestigateAsync(string text, DeveloperContext context, bool openReport,
        CancellationToken cancellationToken = default, InvestigationKind? selectedKind = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return new(InvestigationKind.Unrecognised, "Clipboard is empty.", null);
        if (text.Length > 16000) return new(InvestigationKind.Unrecognised, "Selection exceeds the 16KB safety limit.", null);
        string input = text.Trim();
        InvestigationKind kind = selectedKind is InvestigationKind.ProductCode or InvestigationKind.AccessoryCode
            ? selectedKind.Value : Classify(input);
        if (kind == InvestigationKind.Unrecognised)
            return new(kind, "Clipboard not recognised; no content saved. Try a Git SHA, exception, code symbol, SQL object or six-digit code.", null);

        string? source = null;
        int? line = null;
        if (kind == InvestigationKind.Exception)
        {
            Match match = StackPath.Match(input);
            if (match.Success && int.TryParse(match.Groups["line"].Value, out var number))
            {
                string candidate = match.Groups["path"].Value;
                if (File.Exists(candidate)) { source = candidate; line = number; }
            }
        }
        string directory = DeveloperReportStore.CreateDirectory("Investigations", kind.ToString());
        string report = Path.Combine(directory, "Investigation.md");
        var sb = new StringBuilder();
        sb.AppendLine($"# {kind} investigation\n");
        sb.AppendLine($"- Captured: {DateTimeOffset.Now:O}");
        sb.AppendLine($"- Solution: `{context.SolutionPath ?? "(not known)"}`");
        sb.AppendLine("- Local-only report; no external services were contacted, except local Git execution.\n");
        if (kind == InvestigationKind.GitCommit)
        {
            GitOutput result = await git.RunAsync(context.WorkingDirectory, cancellationToken,
                "show", "--no-ext-diff", "--stat", "--format=fuller", input);
            sb.AppendLine("## Git commit\n```text");
            sb.AppendLine(DeveloperReportStore.Limited(result.Ok ? result.StdOut : result.Error, 12000));
            sb.AppendLine("```");
        }
        else if (kind == InvestigationKind.Url)
        {
            sb.AppendLine($"## URL\n{input}\n\nURL was recorded, not opened automatically.");
        }
        else
        {
            if (kind == InvestigationKind.Exception)
            {
                sb.AppendLine("## Stack trace\n```text");
                sb.AppendLine(DeveloperReportStore.Limited(input, 8000));
                sb.AppendLine("```\n");
                if (source is not null) sb.AppendLine($"Source: `{source}` line {line}\n");
            }
            string needle = kind == InvestigationKind.SqlObject ? input.Replace("[", "").Replace("]", "") : input;
            if (kind == InvestigationKind.Exception)
            {
                var type = Regex.Match(input, @"\b(?:[A-Za-z_]\w*\.)*[A-Za-z_]\w*Exception\b");
                needle = type.Success ? type.Value : "";
            }
            if (needle.Length >= 3 && needle.Length <= 256 && context.SolutionDirectory is { } root)
            {
                sb.AppendLine("## Source matches (max 30 files, 2 MB per file)\n");
                foreach (var match in Search(root, needle, cancellationToken))
                    sb.AppendLine($"- `{match.Path}:{match.Line}` — `{DeveloperReportStore.Limited(match.Text.Trim(), 220).Replace('`', '\'')}`");
            }
            if (kind is InvestigationKind.ProductCode or InvestigationKind.AccessoryCode)
            {
                // Query templates live in local configuration rather than source code.
                // Never connect to SQL or execute the generated lookup automatically.
                if (settings is not null &&
                    settings.InvestigationSqlLookups.TryGetValue(kind.ToString(), out string? template) &&
                    !string.IsNullOrWhiteSpace(template))
                {
                    string escapedCode = input.Replace("'", "''");
                    string sql = template.Replace("{code}", escapedCode, StringComparison.Ordinal);
                    sb.AppendLine($"\n## {kind} manual SQL template (not executed)\n");
                    sb.AppendLine("```sql");
                    sb.AppendLine(sql);
                    sb.AppendLine("```\n");
                    sb.AppendLine("Review the generated SQL before running it. DevDeck has not executed any SQL.");
                }
                else
                    sb.AppendLine($"\nNo SQL lookup template configured for {kind}. Add InvestigationSqlLookups to settings.local.json if needed.");
            }
            else if (kind == InvestigationKind.ManufacturingCode)
                sb.AppendLine("\nThis may be an accessory or product code; the interactive action can prompt you to choose which.");
        }
        await File.WriteAllTextAsync(report, sb.ToString(), cancellationToken);
        if (openReport) DeveloperReportStore.Open(report);
        return new(kind, source is not null ? $"Stack trace: {Path.GetFileName(source)}:{line}" :
            $"{kind} investigation saved", report, source, line);
    }

    private static IEnumerable<(string Path, int Line, string Text)> Search(string root, string needle, CancellationToken ct)
    {
        if (!Directory.Exists(root)) yield break;
        var dirs = new Stack<string>();
        dirs.Push(root);
        int inspected = 0, matches = 0;
        while (dirs.Count > 0 && inspected < 4000 && matches < 30)
        {
            ct.ThrowIfCancellationRequested();
            string folder = dirs.Pop();
            IEnumerable<string> subdirs, files;
            try { subdirs = Directory.EnumerateDirectories(folder).ToArray(); files = Directory.EnumerateFiles(folder).ToArray(); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }
            foreach (var sub in subdirs)
                if (!IgnoredFolders.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase)) dirs.Push(sub);
            foreach (string file in files)
            {
                if (matches >= 30 || inspected++ >= 4000) yield break;
                if (!SearchExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
                FileInfo info;
                try { info = new FileInfo(file); if (info.Length > 2_000_000) continue; }
                catch (IOException) { continue; }
                string[] lines;
                try { lines = File.ReadAllLines(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                for (int i = 0; i < lines.Length && matches < 30; i++)
                    if (lines[i].Contains(needle, StringComparison.OrdinalIgnoreCase))
                    { yield return (file, i + 1, lines[i]); matches++; }
            }
        }
    }
}
