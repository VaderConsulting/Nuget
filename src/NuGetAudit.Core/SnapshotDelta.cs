namespace NuGetAudit.Core;

/// <summary>
/// Describes the delta between two snapshots.
/// </summary>
/// <param name="CurrentSnapshotId">The current snapshot identifier.</param>
/// <param name="PreviousSnapshotId">The previous snapshot identifier, if one exists.</param>
/// <param name="IsChanged">Whether any change-significant difference was detected.</param>
/// <param name="ComparisonNote">An informational note when comparison was skipped or adjusted.</param>
/// <param name="Changes">The individual package changes that were identified.</param>
/// <param name="DependencyEdgeChanges">The dependency relationship changes that were identified.</param>
/// <param name="KnowledgeChanges">The newly discovered knowledge changes about package versions.</param>
public sealed record SnapshotDelta(string CurrentSnapshotId, string? PreviousSnapshotId, bool IsChanged, string? ComparisonNote, IReadOnlyList<PackageChangeRecord> Changes, IReadOnlyList<DependencyEdgeChangeRecord> DependencyEdgeChanges, IReadOnlyList<PackageKnowledgeChangeRecord> KnowledgeChanges);
