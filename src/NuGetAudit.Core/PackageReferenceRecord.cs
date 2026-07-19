namespace NuGetAudit.Core;

/// <summary>
/// Describes one package instance within a project and resolution scope.
/// </summary>
/// <param name="PackageId">The NuGet package identifier.</param>
/// <param name="RequestedVersion">The version requested by the project, if available.</param>
/// <param name="ResolvedVersion">The version resolved by restore, if available.</param>
/// <param name="ReferenceKind">Whether the package is direct or transitive.</param>
/// <param name="IsCentralVersionManaged">Whether the version is sourced from central package management.</param>
/// <param name="TargetFrameworkMoniker">The target framework scope for the package, if available.</param>
/// <param name="DependencyParents">The immediate parent packages that bring this package into the graph.</param>
/// <param name="DependencyPath">One derived dependency path for the package.</param>
/// <param name="HealthInfo">The package health and attention metadata.</param>
/// <param name="StablePackageInstanceKey">The deterministic identity key for the package instance.</param>
public sealed record PackageReferenceRecord(string PackageId, string? RequestedVersion, string? ResolvedVersion, PackageReferenceKind ReferenceKind, bool IsCentralVersionManaged, string? TargetFrameworkMoniker, IReadOnlyList<string> DependencyParents, IReadOnlyList<string> DependencyPath, PackageHealthInfo HealthInfo, string StablePackageInstanceKey);
