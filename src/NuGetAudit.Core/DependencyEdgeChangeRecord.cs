namespace NuGetAudit.Core;

/// <summary>
/// Represents one dependency-edge change between two snapshots.
/// </summary>
/// <param name="ProjectName">The project where the dependency relationship changed.</param>
/// <param name="FromPackageId">The package that depends on another package.</param>
/// <param name="ToPackageId">The package that is depended on.</param>
/// <param name="TargetFrameworkMoniker">The target framework where the relationship changed, if known.</param>
/// <param name="ChangeType">Whether the dependency relationship was added or removed.</param>
public sealed record DependencyEdgeChangeRecord(string ProjectName, string FromPackageId, string ToPackageId, string? TargetFrameworkMoniker, DependencyEdgeChangeType ChangeType);
