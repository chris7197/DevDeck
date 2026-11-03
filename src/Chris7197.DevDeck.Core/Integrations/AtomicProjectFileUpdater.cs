using System.Security.Cryptography;

namespace chris7197.DevDeck.Core;

/// <summary>
/// Commits a fully-written project file using a same-volume atomic replace with backup.
/// A compare-before-replace detects most concurrent editor updates; the caller must
/// still avoid writing underneath an open Visual Studio project where possible.
/// </summary>
public static class AtomicProjectFileUpdater
{
    public static void Commit(string projectFile, string stagedFile, byte[] expectedOriginal)
    {
        projectFile = Path.GetFullPath(projectFile);
        stagedFile = Path.GetFullPath(stagedFile);
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(projectFile),
                Path.GetDirectoryName(stagedFile)))
            throw new InvalidOperationException("Staged project replacement must be in the same directory.");

        // This lock protects CLI/Plugin DevDeck instances from colliding with each other.
        // It cannot prevent another editor from saving between our final check and replace.
        string hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            projectFile.ToUpperInvariant())));
        using var gate = new Mutex(false, @"Local\DevDeck-Project-" + hash);
        bool acquired = false;
        string? backup = null;
        try
        {
            try { acquired = gate.WaitOne(TimeSpan.FromSeconds(8)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Timed out waiting for another DevDeck project update.");

            byte[] current = File.ReadAllBytes(projectFile);
            if (!current.AsSpan().SequenceEqual(expectedOriginal))
                throw new IOException("The project was modified after DevDeck read it. No changes were made; retry the migration after checking the project.");
            if (!File.Exists(stagedFile)) throw new FileNotFoundException("Staged project file disappeared.", stagedFile);

            byte[] replacement = File.ReadAllBytes(stagedFile);
            backup = Path.Combine(Path.GetDirectoryName(projectFile)!,
                ".devdeck-" + Guid.NewGuid().ToString("N") + ".backup");

            // File.Replace is atomic on supported same-volume Windows file systems.
            // It also saves the replaced bytes into a recoverable backup file.
            File.Replace(stagedFile, projectFile, backup, ignoreMetadataErrors: false);
            byte[] committed = File.ReadAllBytes(projectFile);
            if (!committed.AsSpan().SequenceEqual(replacement))
                throw new IOException("Project replacement could not be verified. Backup preserved: " + backup);

            try { File.Delete(backup); }
            catch (IOException) { /* A verified update is complete; a leftover backup is harmless. */ }
            catch (UnauthorizedAccessException) { /* Keep backup on disk for later cleanup. */ }
        }
        catch (Exception e) when (backup is not null && File.Exists(backup) &&
                                  e is not OutOfMemoryException)
        {
            // If the replace succeeded but verification failed, do not overwrite
            // any subsequently written project file automatically. Retain backup.
            throw new ProjectFileUpdateUncertainException(backup, e);
        }
        finally
        {
            if (acquired) gate.ReleaseMutex();
        }
    }
}

/// <summary>An on-disk replacement may have happened. Preserve the generated migration.</summary>
public sealed class ProjectFileUpdateUncertainException(string backupPath, Exception inner)
    : IOException($"A project-file update may have completed. Backup preserved at '{backupPath}'. " +
        "Inspect the project and migration before retrying.", inner);
