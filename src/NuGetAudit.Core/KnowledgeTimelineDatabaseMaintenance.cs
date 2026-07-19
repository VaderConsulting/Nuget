namespace NuGetAudit.Core;

/// <summary>
/// Repairs <c>knowledge.timeline.db</c> files by removing topology and knowledge-change rows that no longer reference a row in the <c>SnapshotRun</c> table.
/// </summary>
public static class KnowledgeTimelineDatabaseMaintenance
{
    /// <summary>
    /// Removes dangling rows (e.g. after a failed transaction or manual file edit). Does not remove legitimate historical <c>SnapshotRun</c> rows.
    /// </summary>
    /// <param name="databasePath">Full path to <c>knowledge.timeline.db</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Count of rows deleted across all affected tables.</returns>
    public static Task<int> TryRemoveOrphanRowsAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        return PackageKnowledgeSqliteStore.TryRemoveOrphanTimelineRowsForFileAsync(databasePath, cancellationToken);
    }
}
