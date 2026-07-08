using System.Globalization;
using AdbSync.Core.Transfer;

namespace AdbSync.Core.Merge;

/// <summary>
/// Manifest-driven two-way merge (the Unison replacement). Deliberately operates on FILES only - directories are
/// created implicitly wherever a file needs one and are never explicitly deleted by this engine. That sidesteps a
/// whole class of ordering bugs (e.g. a directory slated for delete-propagation racing a file independently
/// recreated inside it) at the cost of occasionally leaving an empty directory husk behind, which is harmless.
/// </summary>
public sealed class TwoWayMergeEngine : ITwoWayMergeEngine
{
    private static readonly TimeSpan ModifiedTolerance = TimeSpan.FromSeconds(2);
    private static readonly IExcludeMatcher NoExclude = new ExcludeMatcher([]);

    public Task<MergeResult> MergeAsync(string stagingPath, string masterPath, SyncManifest manifest, MergeOptions options, CancellationToken ct = default)
    {
        Directory.CreateDirectory(stagingPath);
        Directory.CreateDirectory(masterPath);

        var staging = LocalFileTreeScanner.Scan(stagingPath, NoExclude)
            .Where(e => !e.IsDirectory)
            .ToDictionary(e => e.RelativePath, StringComparer.Ordinal);
        var master = LocalFileTreeScanner.Scan(masterPath, NoExclude)
            .Where(e => !e.IsDirectory)
            .ToDictionary(e => e.RelativePath, StringComparer.Ordinal);

        var allPaths = new HashSet<string>(staging.Keys, StringComparer.Ordinal);
        allPaths.UnionWith(master.Keys);
        allPaths.UnionWith(manifest.Entries.Keys);

        var newEntries = new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
        var conflicts = new List<MergeConflict>();
        int created = 0, updated = 0, deleted = 0;

        foreach (var path in allPaths)
        {
            staging.TryGetValue(path, out var s);
            master.TryGetValue(path, out var m);
            manifest.Entries.TryGetValue(path, out var k);

            switch (Classify(s, m, k))
            {
                case DecisionKind.NoOp:
                    if (k is not null)
                        newEntries[path] = k;
                    else if (s is not null)
                        // no baseline yet, but both sides already agree - seed the manifest without flagging a conflict
                        newEntries[path] = new ManifestEntry(s.Size, s.ModifiedUtc);
                    break;

                case DecisionKind.RemoveFromManifest:
                    break;

                case DecisionKind.CopyToMaster:
                    if (!options.DryRun)
                        CopyFile(stagingPath, masterPath, path, s!);
                    newEntries[path] = new ManifestEntry(s!.Size, s.ModifiedUtc);
                    if (m is null) created++; else updated++;
                    break;

                case DecisionKind.CopyToStaging:
                    if (!options.DryRun)
                        CopyFile(masterPath, stagingPath, path, m!);
                    newEntries[path] = new ManifestEntry(m!.Size, m.ModifiedUtc);
                    if (s is null) created++; else updated++;
                    break;

                case DecisionKind.DeleteFromStaging:
                    if (!options.DryRun)
                        DeleteFile(stagingPath, path);
                    deleted++;
                    break;

                case DecisionKind.DeleteFromMaster:
                    if (!options.DryRun)
                        DeleteFile(masterPath, path);
                    deleted++;
                    break;

                case DecisionKind.ConflictStagingWins:
                    RecordConflict(path, "staging", s, m, options, masterPath, conflicts);
                    if (!options.DryRun)
                        CopyFile(stagingPath, masterPath, path, s!);
                    newEntries[path] = new ManifestEntry(s!.Size, s.ModifiedUtc);
                    break;

                case DecisionKind.ConflictMasterWins:
                    RecordConflict(path, "master", s, m, options, stagingPath, conflicts);
                    if (!options.DryRun)
                        CopyFile(masterPath, stagingPath, path, m!);
                    newEntries[path] = new ManifestEntry(m!.Size, m.ModifiedUtc);
                    break;
            }
        }

        var updatedManifest = new SyncManifest
        {
            Version = manifest.Version,
            LastMergedAtUtc = DateTimeOffset.UtcNow,
            Entries = newEntries,
        };

        return Task.FromResult(new MergeResult(created, updated, deleted, conflicts, updatedManifest));
    }

    private enum DecisionKind
    {
        NoOp,
        RemoveFromManifest,
        CopyToMaster,
        CopyToStaging,
        DeleteFromStaging,
        DeleteFromMaster,
        ConflictStagingWins,
        ConflictMasterWins,
    }

    private static DecisionKind Classify(FileEntry? s, FileEntry? m, ManifestEntry? k)
    {
        var sPresent = s is not null;
        var mPresent = m is not null;
        var kPresent = k is not null;

        if (sPresent && !mPresent && !kPresent)
            return DecisionKind.CopyToMaster; // new in staging

        if (!sPresent && mPresent && !kPresent)
            return DecisionKind.CopyToStaging; // new in master

        if (sPresent && mPresent && !kPresent)
        {
            // no shared baseline yet, but if both sides already agree there's nothing to resolve - just seed the manifest
            if (AreEqual(s!, m!))
                return DecisionKind.NoOp;

            // created independently on both sides with genuinely different content - newer wins
            return s!.ModifiedUtc >= m!.ModifiedUtc ? DecisionKind.ConflictStagingWins : DecisionKind.ConflictMasterWins;
        }

        if (sPresent && !mPresent && kPresent)
            // master no longer has it; staging unchanged since baseline -> propagate delete, else staging wins
            return IsUnchanged(s!, k!) ? DecisionKind.DeleteFromStaging : DecisionKind.ConflictStagingWins;

        if (!sPresent && mPresent && kPresent)
            return IsUnchanged(m!, k!) ? DecisionKind.DeleteFromMaster : DecisionKind.ConflictMasterWins;

        if (!sPresent && !mPresent && kPresent)
            return DecisionKind.RemoveFromManifest; // deleted on both sides (or already synced away)

        // sPresent && mPresent && kPresent
        var sChanged = !IsUnchanged(s!, k!);
        var mChanged = !IsUnchanged(m!, k!);

        if (!sChanged && !mChanged) return DecisionKind.NoOp;
        if (sChanged && !mChanged) return DecisionKind.CopyToMaster;
        if (!sChanged) return DecisionKind.CopyToStaging;

        return s!.ModifiedUtc >= m!.ModifiedUtc ? DecisionKind.ConflictStagingWins : DecisionKind.ConflictMasterWins;
    }

    private static bool IsUnchanged(FileEntry entry, ManifestEntry baseline) =>
        entry.Size == baseline.Size && (entry.ModifiedUtc - baseline.ModifiedUtc).Duration() <= ModifiedTolerance;

    private static bool AreEqual(FileEntry a, FileEntry b) =>
        a.Size == b.Size && (a.ModifiedUtc - b.ModifiedUtc).Duration() <= ModifiedTolerance;

    private static void CopyFile(string sourceRoot, string destRoot, string relativePath, FileEntry sourceEntry)
    {
        var sourcePath = Path.Combine(sourceRoot, relativePath);
        var destPath = Path.Combine(destRoot, relativePath);
        var destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        var tempPath = $"{destPath}.tmp-{Guid.NewGuid():N}";
        File.Copy(sourcePath, tempPath, overwrite: true);
        File.SetLastWriteTimeUtc(tempPath, sourceEntry.ModifiedUtc.UtcDateTime);
        File.Move(tempPath, destPath, overwrite: true);
    }

    private static void DeleteFile(string root, string relativePath)
    {
        var path = Path.Combine(root, relativePath);
        if (File.Exists(path))
            File.Delete(path);
    }

    private static void RecordConflict(
        string path, string winner, FileEntry? staging, FileEntry? master, MergeOptions options, string loserRoot, List<MergeConflict> conflicts)
    {
        string? backupPath = null;
        var loserEntry = winner == "staging" ? master : staging;

        if (loserEntry is not null && options.BackupConflictLosers && !options.DryRun)
        {
            var loserFullPath = Path.Combine(loserRoot, path);
            if (File.Exists(loserFullPath))
            {
                var backupDir = options.ConflictBackupDir ?? Path.Combine(loserRoot, ".conflicts");
                Directory.CreateDirectory(backupDir);
                var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);
                backupPath = Path.Combine(backupDir, $"{Path.GetFileName(path)}.{timestamp}.conflict");
                File.Copy(loserFullPath, backupPath, overwrite: true);
            }
        }

        conflicts.Add(new MergeConflict(path, winner, staging?.ModifiedUtc, master?.ModifiedUtc, backupPath));
    }
}
