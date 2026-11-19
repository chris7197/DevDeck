using System.Text;

namespace chris7197.DevDeck.Core;

/// <summary>File handling for the optional DevDeck pass; independent of Visual Studio COM.</summary>
public static class CSharpFormattedFile
{
    public static async Task<bool> ApplyAsync(string path, CSharpFormattingSettings settings, CancellationToken cancellationToken = default)
    {
        if (!settings.IsEnabled) return false;
        byte[] original = await File.ReadAllBytesAsync(path, cancellationToken);
        (Encoding encoding, int bomLength) = DetectEncoding(original);
        string source = encoding.GetString(original, bomLength, original.Length - bomLength);
        string formatted = CSharpPostFormatter.Format(source, settings, cancellationToken);
        if (source == formatted) return false;

        // Preserve the original BOM (if any), encoding and untouched text.
        byte[] content = encoding.GetBytes(formatted);
        byte[] replacement = new byte[bomLength + content.Length];
        original.AsSpan(0, bomLength).CopyTo(replacement);
        content.CopyTo(replacement, bomLength);

        path = Path.GetFullPath(path);
        string staged = Path.Combine(Path.GetDirectoryName(path)!, ".devdeck-format-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(staged, replacement, cancellationToken);
            byte[] current = await File.ReadAllBytesAsync(path, cancellationToken);
            if (!current.AsSpan().SequenceEqual(original))
                throw new IOException("The C# file changed during DevDeck post-formatting. Retry after saving the file.");
            cancellationToken.ThrowIfCancellationRequested();
            // Same-directory atomic replacement; like other external formatters,
            // this cannot completely exclude another editor saving concurrently.
            File.Replace(staged, path, null);
            return true;
        }
        finally
        {
            if (File.Exists(staged))
            {
                try { File.Delete(staged); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static (Encoding Encoding, int BomLength) DetectEncoding(byte[] bytes)
    {
        Encoding[] encodings =
        [
            new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true),
            new UTF8Encoding(true, true), new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true)
        ];
        foreach (Encoding encoding in encodings)
        {
            byte[] bom = encoding.GetPreamble();
            if (bytes.AsSpan().StartsWith(bom)) return (encoding, bom.Length);
        }
        // Reject unsupported/invalid encodings instead of silently corrupting characters.
        return (new UTF8Encoding(false, true), 0);
    }
}
