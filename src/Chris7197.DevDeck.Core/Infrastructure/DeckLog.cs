namespace chris7197.DevDeck.Core;

/// <summary>Process-safe append-only diagnostics; all new entries carry an explicit severity.</summary>
public sealed class DeckLog
{
    private readonly object _sync = new();
    public string FilePath { get; }

    public DeckLog() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "chris7197", "DevDeck", "devdeck.log")) { }

    public DeckLog(string filePath) => FilePath = Path.GetFullPath(filePath);

    public void Write(string message) => Append("INFO", message);
    public void WriteWarning(string message) => Append("WARN", message);
    public void WriteError(string message) => Append("ERROR", message);

    /// <summary>
    /// Explicitly truncate the log, preserving its path. Unlike diagnostic writes,
    /// a user-requested clear must report IO failures to the caller.
    /// </summary>
    public void Clear()
    {
        lock (_sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            using var stream = new FileStream(FilePath, FileMode.OpenOrCreate, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            stream.SetLength(0);
            stream.Flush(flushToDisk: true);
        }
    }

    private void Append(string level, string message)
    {
        string line = $"[{DateTimeOffset.Now:O}] [{level}] {message}{Environment.NewLine}";
        try
        {
            lock (_sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, line);
            }
        }
        catch { /* Logging must never break Git safety handling. */ }
    }
}
