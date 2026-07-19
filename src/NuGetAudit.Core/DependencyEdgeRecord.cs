namespace NuGetAudit.Core;

/// <summary>
/// Represents one dependency relationship between two packages within a project scope.
/// </summary>
/// <param name="FromPackageId">The package that depends on another package.</param>
/// <param name="ToPackageId">The package that is depended on.</param>
/// <param name="TargetFrameworkMoniker">The target framework scope where the relationship exists.</param>
/// <param name="FromStablePackageInstanceKey">The stable identity of the source package instance.</param>
/// <param name="ToStablePackageInstanceKey">The stable identity of the target package instance.</param>
public sealed record DependencyEdgeRecord(string FromPackageId, string ToPackageId, string? TargetFrameworkMoniker, string FromStablePackageInstanceKey, string ToStablePackageInstanceKey);
