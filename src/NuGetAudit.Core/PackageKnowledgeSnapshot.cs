namespace NuGetAudit.Core;

/// <summary>
/// Represents the accumulated package-knowledge state for one output directory.
/// </summary>
/// <param name="GeneratedUtc">When the knowledge snapshot was generated.</param>
/// <param name="Packages">The known package-version records.</param>
public sealed record PackageKnowledgeSnapshot(DateTimeOffset GeneratedUtc, IReadOnlyList<PackageKnowledgeRecord> Packages);
