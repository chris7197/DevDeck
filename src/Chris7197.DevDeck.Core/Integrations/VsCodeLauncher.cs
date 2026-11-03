using System.Diagnostics;
using System.Runtime.InteropServices;

namespace chris7197.DevDeck.Core;

public static class VsCodeLauncher
{
    public static string? FindCli()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (string path in new[]
        {
            Path.Combine(local, "Programs", "Microsoft VS Code", "bin", "code.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "bin", "code.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft VS Code", "bin", "code.cmd")
        }) if (File.Exists(path)) return path;
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            string candidate = Path.Combine(directory.Trim('"'), "code.cmd");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static async Task<bool> TryFocusChatAsync(string folder, CancellationToken cancellationToken)
    {
        // Wait for VS Code to become foreground. Never inject shortcuts into an unrelated app.
        for (int attempt = 0; attempt < 35; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (WindowActivator.IsForegroundProcess("Code"))
            {
                WindowActivator.FocusChatShortcut();
                await Task.Delay(300, cancellationToken);
                if (!WindowActivator.IsForegroundProcess("Code")) return false;
                WindowActivator.FocusChatInputShortcut();
                return true;
            }
            // A newly opened window may take a few seconds before it is ready.
            await Task.Delay(200, cancellationToken);
        }
        return false;
    }
}
