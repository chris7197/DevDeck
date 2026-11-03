namespace chris7197.DevDeck.Core;

public sealed record SyncResult(bool Success, bool HasConflicts, string Message);

/// <summary>Commit working tree changes, fetch the remote main branch, merge into the CURRENT branch.</summary>
public sealed class GitSyncService(DeckSettings settings, GitProcess git, DeckLog log)
{
    public async Task<SyncResult> ExecuteAsync(string startingDirectory, Func<string, Task> progress, CancellationToken ct = default)
    {
        string workingDirectory = Path.GetFullPath(startingDirectory);
        try
        {
            await progress("Find repo");
            GitOutput rootResponse = await git.RunAsync(workingDirectory, ct, "rev-parse", "--show-toplevel");
            EnsureOk(rootResponse, "Not a Git repository");
            string root = rootResponse.StdOut.Trim();
            if (string.IsNullOrEmpty(root)) throw new InvalidOperationException("Unable to determine repository root.");

            GitOutput branchResponse = await git.RunAsync(root, ct, "symbolic-ref", "--quiet", "--short", "HEAD");
            EnsureOk(branchResponse, "Detached HEAD. Check out your feature branch first");
            string branch = branchResponse.StdOut.Trim();
            if (string.Equals(branch, settings.MainBranch, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"You're on '{settings.MainBranch}', not a feature branch. Nothing changed.");

            // Refuse to begin if another Git operation is unfinished.
            foreach (string marker in new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-apply", "rebase-merge" })
            {
                GitOutput pathResponse = await git.RunAsync(root, ct, "rev-parse", "--git-path", marker);
                EnsureOk(pathResponse, "Unable to inspect Git operation state");
                string gitPath = pathResponse.StdOut.Trim();
                if (!Path.IsPathRooted(gitPath)) gitPath = Path.GetFullPath(Path.Combine(root, gitPath));
                if (File.Exists(gitPath) || Directory.Exists(gitPath))
                    throw new InvalidOperationException($"An unfinished Git operation exists ({marker}). Resolve it first.");
            }

            GitOutput unmerged = await git.RunAsync(root, ct, "ls-files", "-u");
            EnsureOk(unmerged, "Cannot inspect merge conflicts");
            if (!string.IsNullOrWhiteSpace(unmerged.StdOut))
                throw new InvalidOperationException("Unmerged files exist. Resolve them before syncing.");

            GitOutput remote = await git.RunAsync(root, ct, "remote");
            EnsureOk(remote, "Cannot list remotes");
            if (!remote.StdOut.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Contains(settings.Remote, StringComparer.Ordinal))
                throw new InvalidOperationException($"Remote '{settings.Remote}' is not configured.");

            log.Write($"Starting sync | repo={root} | branch={branch} | remote={settings.Remote}/{settings.MainBranch}");
            GitOutput status = await git.RunAsync(root, ct, "status", "--porcelain=v1", "--untracked-files=all");
            EnsureOk(status, "Cannot check working tree");

            if (!string.IsNullOrWhiteSpace(status.StdOut))
            {
                await progress("Staging");
                EnsureOk(await git.RunAsync(root, ct, "add", "--all"), "Failed to stage changes");

                // Avoid 'nothing to commit' errors when status changed under us.
                GitOutput stagedDiff = await git.RunAsync(root, ct, "diff", "--cached", "--quiet");
                if (stagedDiff.ExitCode == 1)
                {
                    await progress("Committing");
                    // -m avoids the commit editor; existing commit hooks/signing can still fail.
                    EnsureOk(await git.RunAsync(root, ct, "commit", "-m", settings.CommitMessage), "Commit failed");
                }
                else if (!stagedDiff.Ok)
                    throw new InvalidOperationException("Unable to check staged changes: " + stagedDiff.Error);
            }
            else log.Write("No local changes; skipping commit.");

            await progress("Fetching");
            EnsureOk(await git.RunAsync(root, ct, "fetch", "--no-tags", settings.Remote, settings.MainBranch),
                $"Fetching {settings.Remote}/{settings.MainBranch} failed");

            // FETCH_HEAD is the exact branch just fetched; local main is untouched.
            // --no-edit prevents the merge message editor from opening.
            await progress("Merging");
            GitOutput merge = await git.RunAsync(root, ct, "merge", "--no-edit", "FETCH_HEAD");
            if (!merge.Ok)
            {
                GitOutput conflicts = await git.RunAsync(root, ct, "ls-files", "-u");
                if (conflicts.Ok && !string.IsNullOrWhiteSpace(conflicts.StdOut))
                {
                    string msg = $"Merge conflicts on '{branch}'. Changes are preserved; resolve them in Visual Studio. No push was attempted.";
                    log.Write(msg);
                    return new SyncResult(false, true, msg);
                }
                throw new InvalidOperationException("Merge failed: " + merge.Error);
            }

            string success = $"Merged {settings.Remote}/{settings.MainBranch} into '{branch}' (no push, local main untouched).";
            log.Write(success);
            await progress("Merged!");
            return new SyncResult(true, false, success);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.WriteError("SYNC FAILED: " + e);
            return new SyncResult(false, false, e.Message);
        }
    }

    private static void EnsureOk(GitOutput result, string message)
    {
        if (!result.Ok) throw new InvalidOperationException(message + ": " + result.Error);
    }
}
