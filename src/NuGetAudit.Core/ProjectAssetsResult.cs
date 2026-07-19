namespace NuGetAudit.Core;

/// <summary>
/// Represents the packages and warnings produced by assets-file parsing.
/// </summary>
/// <param name="Packages">The parsed package records.</param>
/// <param name="DependencyEdges">The parsed dependency relationships.</param>
/// <param name="Warnings">Warnings generated while parsing.</param>
internal sealed record ProjectAssetsResult(IReadOnlyList<PackageReferenceRecord> Packages, IReadOnlyList<DependencyEdgeRecord> DependencyEdges, IReadOnlyList<string> Warnings);
