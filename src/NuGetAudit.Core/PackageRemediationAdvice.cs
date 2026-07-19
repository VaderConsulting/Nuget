namespace NuGetAudit.Core;

/// <summary>
/// Captures the best-known remediation hints for a package version, used for UI and for criticality inputs
/// <see cref="NuGetAudit.Intelligence.CriticalityFactor.RemediationDifficulty"/> and
/// <see cref="NuGetAudit.Intelligence.CriticalityFactor.FixAvailability"/>.
/// </summary>
/// <remarks>
/// <para>
/// Values are typically derived from NuGet.org deprecation metadata and related health fields when the audit runs.
/// They are not guaranteed to be complete or security-reviewed.
/// </para>
/// </remarks>
/// <param name="RecommendedVersion">
/// A concrete stable version the tool suggests upgrading to, when inferable (for example from “latest compatible stable”).
/// </param>
/// <param name="AlternatePackageId">
/// When the publisher recommends moving to a different package id (migration), that id appears here.
/// </param>
/// <param name="AlternatePackageRange">Suggested version range for <paramref name="AlternatePackageId"/>, when provided.</param>
/// <param name="Summary">Short free-text guidance when structured fields are missing.</param>
public sealed record PackageRemediationAdvice(string? RecommendedVersion, string? AlternatePackageId, string? AlternatePackageRange, string? Summary);
