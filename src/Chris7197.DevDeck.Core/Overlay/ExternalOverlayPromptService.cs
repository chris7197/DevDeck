using System.Diagnostics;
using System.Text.Json;

namespace chris7197.DevDeck.Core;

/// <summary>
/// Spawns a short-lived WinForms overlay. Communication uses redirected standard streams:
/// no clipboard text or option labels are placed on the process command line or written to disk.
/// </summary>
public sealed class ExternalOverlayPromptService : IDevDeckPromptService
{
    public async Task<string?> ChooseAsync(DevDeckPromptRequest request, CancellationToken cancellationToken = default)
    {
        DevDeckPromptValidation.Validate(request);
        string overlay = FindOverlay();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(overlay)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(overlay)!
            }
        };
        if (!process.Start()) throw new InvalidOperationException("Could not start DevDeck overlay.");
        try
        {
            // The executable reads exactly one JSON line and prints exactly one reply.
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), cancellationToken);
            process.StandardInput.Close();
            Task<string> errors = process.StandardError.ReadToEndAsync(cancellationToken);
            string? answer = await process.StandardOutput.ReadLineAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
                throw new InvalidOperationException("DevDeck overlay failed: " + (await errors).Trim());
            if (string.IsNullOrWhiteSpace(answer)) return null;
            DevDeckPromptResponse? response = JsonSerializer.Deserialize<DevDeckPromptResponse>(answer);
            if (response?.ChoiceId is null) return null;
            if (!request.Choices.Any(x => x.Id == response.ChoiceId))
                throw new InvalidOperationException("Overlay returned an unexpected choice ID.");
            return response.ChoiceId;
        }
        catch
        {
            // Cancellation must also close the UI process. Never mask the original
            // failure if Windows reports that the child has already exited.
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(3));
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or
                System.ComponentModel.Win32Exception or TimeoutException) { }
            throw;
        }
    }

    public static string FindOverlay()
    {
        const string exe = "chris7197.DevDeck.Overlay.exe";
        string? configured = Environment.GetEnvironmentVariable("DEVDECK_OVERLAY_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
        string baseDir = AppContext.BaseDirectory;
        foreach (string candidate in new[] { Path.Combine(baseDir, "overlay", exe), Path.Combine(baseDir, exe) })
            if (File.Exists(candidate)) return candidate;

        // Local Visual Studio build: discover the solution and the UI project's output.
        DirectoryInfo? directory = new(baseDir);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "chris7197.DevDeck.sln")))
            {
                foreach (string configuration in new[] { "Debug", "Release" })
                {
                    string candidate = Path.Combine(directory.FullName, "src", "chris7197.DevDeck.Overlay",
                        "bin", configuration, "net8.0-windows", exe);
                    if (File.Exists(candidate)) return candidate;
                }
                break;
            }
            directory = directory.Parent;
        }
        throw new FileNotFoundException("DevDeck Overlay isn't built or installed. Build chris7197.DevDeck.sln " +
            "and reinstall the plugin, or set DEVDECK_OVERLAY_PATH for local testing.");
    }
}
