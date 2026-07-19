namespace NuGetAudit.VisualStudioHost;

/// <summary>
/// Snapshot contract used by the Visual Studio host to read diagnostics and dependency data from generated snapshot JSON.
/// </summary>
public sealed class SolutionSnapshotFile
{
    /// <summary>
    /// Gets or sets the snapshot identifier.
    /// </summary>
    public string? SnapshotId { get; set; }

    /// <summary>
    /// Gets or sets the capture time.
    /// </summary>
    public DateTimeOffset CapturedUtc { get; set; }

    /// <summary>
    /// Gets or sets the project snapshots.
    /// </summary>
    public IReadOnlyList<ProjectSnapshotFile> Projects { get; set; } = Array.Empty<ProjectSnapshotFile>();
}
