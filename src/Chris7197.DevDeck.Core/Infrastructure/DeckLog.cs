namespace chris7197.DevDeck.Core;

/// <summary>
/// Shared, process-safe diagnostic log. TRACE is explicitly opt-in from the tray.
/// Never log SQL text, clipboard contents, credentials or command-line secrets.
/// </summary>
public sealed class DeckLog
{
    private readonly object _sync = new();
    public string FilePath { get; }
    private string TraceFlagPath => Path.Combine(Path.GetDirectoryName(FilePath)!, "trace.enabled");

    public DeckLog() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "chris7197", "DevDeck", "devdeck.log")) { }

    public DeckLog(string filePath) => FilePath = Path.GetFullPath(filePath);

    /// <summary>Checked for each trace event so running plugin processes pick up tray changes immediately.</summary>
    public bool TraceEnabled => File.Exists(TraceFlagPath);

    public void SetTraceEnabled(bool enabled)
    {
        lock (_sync)
        {
            if (enabled)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(TraceFlagPath)!);
                File.WriteAllText(TraceFlagPath, "DevDeck diagnostic tracing enabled. Delete this file to disable.\n");
            }
            else if (File.Exists(TraceFlagPath)) File.Delete(TraceFlagPath);
        }
    }

    public void Write(string message) => Append("INFO", message);
    public void WriteWarning(string message) => Append("WARN", message);
    public void WriteError(string message) => Append("ERROR", message);
    public void WriteTrace(string message)
    {
        if (TraceEnabled) Append("TRACE", message);
    }

    /// <summary>Clear the active log only. Preserve the independently controlled trace switch.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            using var mutex = new Mutex(false, @"Local\chris7197.DevDeck.LogWrite");
            bool acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new IOException("DevDeck log is busy; try again.");
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                using var stream = new FileStream(FilePath, FileMode.OpenOrCreate, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                stream.SetLength(0);
                stream.Flush(flushToDisk: true);
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }
    }

    private void Append(string level, string message)
    {
        string line = $"[{DateTimeOffset.Now:O}] [{level}] {message}{Environment.NewLine}";
        // One named mutex shared by the tray, simulator and SOOMFON plugin. This prevents
        // concurrently appending or truncating in separate processes from losing records.
        try
        {
            lock (_sync)
            {
                using var mutex = new Mutex(false, @"Local\chris7197.DevDeck.LogWrite");
                bool acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(TimeSpan.FromMilliseconds(750)); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) return; // Diagnostics must not stall controller actions.
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var writer = new StreamWriter(stream);
                    writer.Write(line);
                }
                finally { if (acquired) mutex.ReleaseMutex(); }
            }
        }
        catch
        {
            // Do not let diagnostics break the action that was being diagnosed.
        }
    }
}
