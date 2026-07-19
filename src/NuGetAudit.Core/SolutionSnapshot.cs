namespace NuGetAudit.Core;

/// <summary>
/// Represents one persisted view of the solution's package state.
/// </summary>
/// <param name="SnapshotId">The unique identifier for the snapshot.</param>
/// <param name="CapturedUtc">The UTC timestamp when the snapshot was created.</param>
/// <param name="NormalisedContentHash">The deterministic hash for change tracking.</param>
/// <param name="AnalysisStatus">The overall analysis status.</param>
/// <param name="Solution">The analysed solution metadata.</param>
/// <param name="Projects">The projects included in the snapshot.</param>
/// <param name="Warnings">Warnings emitted while building the snapshot.</param>
public sealed record SolutionSnapshot(string SnapshotId, DateTimeOffset CapturedUtc, string NormalisedContentHash, AnalysisStatus AnalysisStatus, SolutionInfo Solution, IReadOnlyList<ProjectSnapshot> Projects, IReadOnlyList<string> Warnings);
