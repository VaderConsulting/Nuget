namespace NuGetAudit.VisualStudioHost;

/// <summary>
/// Minimal project snapshot contract used by the Visual Studio host.
/// </summary>
public sealed class ProjectSnapshotFile
{
    /// <summary>
    /// Gets or sets the project name.
    /// </summary>
    public string? ProjectName { get; set; }

    /// <summary>
    /// Gets or sets the project path.
    /// </summary>
    public string? ProjectPath { get; set; }

    /// <summary>
    /// Gets or sets the declared target frameworks.
    /// </summary>
    public string? TargetFrameworks { get; set; }

    /// <summary>
    /// Gets or sets the package records for the project.
    /// </summary>
    public IReadOnlyList<PackageReferenceFile> Packages { get; set; } = Array.Empty<PackageReferenceFile>();

    /// <summary>
    /// Gets or sets the dependency edges for the project.
    /// </summary>
    public IReadOnlyList<DependencyEdgeFile> DependencyEdges { get; set; } = Array.Empty<DependencyEdgeFile>();

    /// <summary>
    /// Gets or sets the project diagnostics.
    /// </summary>
    public IReadOnlyList<DiagnosticFile> Diagnostics { get; set; } = Array.Empty<DiagnosticFile>();
}
