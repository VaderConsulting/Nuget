namespace NuGetAudit.Core;

/// <summary>
/// Captures health and attention signals for a package version after NuGet.org registration and vulnerability metadata are applied.
/// </summary>
/// <remarks>
/// <para>
/// Populated by <see cref="PackageHealthEnricher"/> during audit. The same record feeds
/// <see cref="PackageCriticalityAssessor.Assess"/> (severity, lifecycle, vulnerability presence) and the UI “health” summary.
/// </para>
/// </remarks>
/// <param name="IsDeprecated">Whether the package is deprecated.</param>
/// <param name="IsObsolete">Whether the package appears obsolete and likely replaced.</param>
/// <param name="IsOutdated">Whether a newer stable version is available.</param>
/// <param name="IsVulnerable">Whether known vulnerabilities affect the selected version.</param>
/// <param name="LatestStableVersion">The latest known compatible stable version, if available.</param>
/// <param name="DeprecationMessage">The package deprecation message, if any.</param>
/// <param name="AlternatePackageId">The suggested alternate package identifier, if any.</param>
/// <param name="AlternatePackageRange">The suggested alternate package version range, if any.</param>
/// <param name="MaxVulnerabilitySeverity">
/// The worst <see cref="PackageVulnerabilitySeverity"/> among <paramref name="Vulnerabilities"/>; drives criticality severity inputs.
/// </param>
/// <param name="Vulnerabilities">The vulnerability advisories affecting the package version.</param>
/// <param name="DevelopmentStatus">Inferred lifecycle status from NuGet.org registration metadata.</param>
public sealed record PackageHealthInfo(bool IsDeprecated, bool IsObsolete, bool IsOutdated, bool IsVulnerable, string? LatestStableVersion, string? DeprecationMessage, string? AlternatePackageId, string? AlternatePackageRange, PackageVulnerabilitySeverity MaxVulnerabilitySeverity, IReadOnlyList<PackageVulnerabilityRecord> Vulnerabilities, PackageDevelopmentStatus DevelopmentStatus = PackageDevelopmentStatus.Unknown)
{
    /// <summary>
    /// Gets an empty health record.
    /// </summary>
    public static PackageHealthInfo None { get; } = new(false, false, false, false, null, null, null, null, PackageVulnerabilitySeverity.None, Array.Empty<PackageVulnerabilityRecord>(), PackageDevelopmentStatus.Unknown);

    /// <summary>
    /// Gets a value indicating whether the package should be highlighted to the user.
    /// </summary>
    public bool RequiresAttention =>
        IsDeprecated
        || IsObsolete
        || IsOutdated
        || IsVulnerable
        || DevelopmentStatus == PackageDevelopmentStatus.Abandoned
        || DevelopmentStatus == PackageDevelopmentStatus.Removed;
}
