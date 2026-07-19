namespace NuGetAudit.Core;

/// <summary>
/// Captures package analysis results for a single project.
/// </summary>
/// <param name="ProjectId">The stable identifier for the project.</param>
/// <param name="ProjectName">The display name of the project.</param>
/// <param name="ProjectPath">The full path to the project file.</param>
/// <param name="ProjectStyle">The detected package management style.</param>
/// <param name="LoadState">The project load state.</param>
/// <param name="AnalysisStatus">The project analysis status.</param>
/// <param name="TargetFrameworks">The declared target frameworks, if known.</param>
/// <param name="AssetsFilePath">The resolved assets file path, if present.</param>
/// <param name="PackagesLockFilePath">The <c>packages.lock.json</c> path, if present.</param>
/// <param name="Packages">The package records found for the project.</param>
/// <param name="DependencyEdges">The dependency relationships found for the project.</param>
/// <param name="Diagnostics">Diagnostics emitted for the project.</param>
/// <param name="Warnings">Warnings emitted while analysing the project.</param>
public sealed record ProjectSnapshot(string ProjectId, string ProjectName, string ProjectPath, ProjectStyle ProjectStyle, ProjectLoadState LoadState, AnalysisStatus AnalysisStatus, string? TargetFrameworks, string? AssetsFilePath, string? PackagesLockFilePath, IReadOnlyList<PackageReferenceRecord> Packages, IReadOnlyList<DependencyEdgeRecord> DependencyEdges, IReadOnlyList<AuditDiagnostic> Diagnostics, IReadOnlyList<string> Warnings);
