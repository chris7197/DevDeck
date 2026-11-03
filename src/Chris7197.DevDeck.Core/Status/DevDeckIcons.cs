using System.Collections.Concurrent;
using System.Drawing;
using System.Reflection;

namespace chris7197.DevDeck.Core;

public static class DevDeckIcons
{
    public const string SyncMain = "sync-main";
    public const string LatestLog = "latest-log";
    public const string LastError = "last-error";
    public const string NewMigration = "new-migration";
    public const string TeamsChannel = "teams-channel";
    public const string OpenVsCode = "open-vscode";
    public const string OpenSolution = "open-solution";
    public const string Investigate = "investigate";
    public const string DbFlight = "db-flight";
    public const string CloseOthers = "close-others";
    public const string FormatCSharp = "format-csharp";
    public const string FormatSql = "format-sql";
    public const string SqlNewQuery = "sql-newquery";
    public const string SqlExecute = "sql-execute";
    public const string SqlPlan = "sql-plan";
    public const string BrowserRefresh = "browser-refresh";
    public const string BrowserDevTools = "browser-devtools";
    public const string BrowserHardRefresh = "browser-hardrefresh";

    private static readonly ConcurrentDictionary<string, byte[]?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Image? TryLoad(string? iconName)
    {
        if (string.IsNullOrWhiteSpace(iconName)) return null;
        byte[]? bytes = Cache.GetOrAdd(iconName, static name => LoadBytes(name));
        if (bytes is null) return null;
        using var ms = new MemoryStream(bytes);
        using var original = Image.FromStream(ms);
        return new Bitmap(original);
    }

    private static byte[]? LoadBytes(string iconName)
    {
        string resource = $"chris7197.DevDeck.Core.Assets.Icons.{iconName}.png";
        Assembly assembly = typeof(DevDeckIcons).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(resource);
        if (stream is null) return null;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public static string? FallbackBadgeText(string? iconName) => iconName switch
    {
        SyncMain => "Git",
        LatestLog => "Log",
        LastError => "Err",
        NewMigration => "Mig",
        TeamsChannel => "Tm",
        OpenVsCode => "Code",
        OpenSolution => "VS",
        Investigate => "?",
        DbFlight => "DB",
        CloseOthers => "Close",
        FormatCSharp => "C#",
        FormatSql => "SQL",
        SqlNewQuery => "SQL",
        SqlExecute => "Run",
        SqlPlan => "Plan",
        BrowserRefresh => "BI",
        BrowserDevTools => "Dev",
        BrowserHardRefresh => "Hard",
        _ => null
    };
}
