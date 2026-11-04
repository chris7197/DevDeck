using System.Diagnostics;

namespace chris7197.DevDeck.Core;

/// <summary>Launch an external developer shell using the actual Visual Studio instance's tools.</summary>
public static class DeveloperCommandPromptLauncher
{
    public static string FindVsDevCmd(string visualStudioExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(visualStudioExecutablePath) ||
            !Path.GetFileName(visualStudioExecutablePath).Equals("devenv.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Could not identify the selected Visual Studio installation.");

        // ...\Common7\IDE\devenv.exe -> ...\Common7\Tools\VsDevCmd.bat.
        string batch = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(visualStudioExecutablePath)!, "..", "Tools", "VsDevCmd.bat"));
        if (!File.Exists(batch))
            throw new FileNotFoundException(
                "The selected Visual Studio installation has no VsDevCmd.bat at its usual location.", batch);
        return batch;
    }

    public static ProcessStartInfo CreateStartInfo(string vsDevCmdBatchPath, string projectDirectory)
    {
        if (!File.Exists(vsDevCmdBatchPath))
            throw new FileNotFoundException("Visual Studio developer environment script was not found.", vsDevCmdBatchPath);
        if (!Directory.Exists(projectDirectory))
            throw new DirectoryNotFoundException($"The active project's folder is missing: {projectDirectory}");

        // /D disables per-user AutoRun; /K keeps the prompt open. VS initialization is
        // run first, then we explicitly cd to the PROJECT directory even if VsDevCmd
        // changes directories. Expansion via environment variables keeps user paths
        // (including spaces and cmd metacharacters) out of the command text.
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = "/D /V:OFF /K call \"%DEVDECK_VSDEVCMD%\" -no_logo && cd /d \"%DEVDECK_PROJECT_DIR%\"",
            WorkingDirectory = Path.GetFullPath(projectDirectory),
            UseShellExecute = false,
            CreateNoWindow = false
        };
        info.Environment["DEVDECK_VSDEVCMD"] = Path.GetFullPath(vsDevCmdBatchPath);
        info.Environment["DEVDECK_PROJECT_DIR"] = Path.GetFullPath(projectDirectory);
        return info;
    }
}
