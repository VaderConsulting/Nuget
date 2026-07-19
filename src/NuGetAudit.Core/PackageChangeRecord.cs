namespace NuGetAudit.Core;

/// <summary>
/// Represents a single package change between snapshots.
/// </summary>
/// <param name="StablePackageInstanceKey">The stable key of the package instance that changed.</param>
/// <param name="ProjectName">The project where the change occurred.</param>
/// <param name="PackageId">The package that changed.</param>
/// <param name="ChangeType">The kind of change detected.</param>
/// <param name="PreviousResolvedVersion">The previous resolved version, if available.</param>
/// <param name="CurrentResolvedVersion">The current resolved version, if available.</param>
/// <param name="PreviousRequestedVersion">The previous requested version, if available.</param>
/// <param name="CurrentRequestedVersion">The current requested version, if available.</param>
/// <param name="TargetFrameworkMoniker">The target framework where the change was observed, if applicable.</param>
/// <param name="PreviousReferenceKind">The previous direct/transitive classification, if available.</param>
/// <param name="CurrentReferenceKind">The current direct/transitive classification, if available.</param>
/// <param name="PreviousDependencyParents">The previous immediate parent packages, if available.</param>
/// <param name="CurrentDependencyParents">The current immediate parent packages, if available.</param>
/// <param name="PreviousDependencyPath">The previous derived dependency path, if available.</param>
/// <param name="CurrentDependencyPath">The current derived dependency path, if available.</param>
/// <param name="PreviousHealthInfo">The previous package health state, if available.</param>
/// <param name="CurrentHealthInfo">The current package health state, if available.</param>
public sealed record PackageChangeRecord(string StablePackageInstanceKey, string ProjectName, string PackageId, PackageChangeType ChangeType, string? PreviousResolvedVersion, string? CurrentResolvedVersion, string? PreviousRequestedVersion, string? CurrentRequestedVersion, string? TargetFrameworkMoniker, PackageReferenceKind? PreviousReferenceKind, PackageReferenceKind? CurrentReferenceKind, IReadOnlyList<string> PreviousDependencyParents, IReadOnlyList<string> CurrentDependencyParents, IReadOnlyList<string> PreviousDependencyPath, IReadOnlyList<string> CurrentDependencyPath, PackageHealthInfo? PreviousHealthInfo, PackageHealthInfo? CurrentHealthInfo);
