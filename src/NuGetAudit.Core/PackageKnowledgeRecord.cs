using NuGetAudit.Intelligence;

namespace NuGetAudit.Core;

/// <summary>
/// Stores the latest known status for a package version across analyses.
/// </summary>
/// <param name="PackageId">The package identifier.</param>
/// <param name="ResolvedVersion">The resolved package version the knowledge applies to.</param>
/// <param name="FirstObservedUtc">When this package version was first observed in an analysis.</param>
/// <param name="LastObservedUtc">When this package version was most recently observed in an analysis.</param>
/// <param name="FirstRequiresAttentionUtc">When this package version first required attention, if ever.</param>
/// <param name="LatestStatusChangedUtc">When the latest known status last changed, if known.</param>
/// <param name="LatestDeterminedUtc">When the latest knowledge was determined.</param>
/// <param name="LatestKnownHealth">The latest known health state for this package version.</param>
/// <param name="Remediation">The latest known remediation guidance.</param>
/// <param name="Criticality">
/// Risk and alert scores from <see cref="NuGetAudit.Intelligence.CriticalityCalculator.Calculate"/>, including per-factor explanations.
/// </param>
public sealed record PackageKnowledgeRecord(string PackageId, string ResolvedVersion, DateTimeOffset FirstObservedUtc, DateTimeOffset LastObservedUtc, DateTimeOffset? FirstRequiresAttentionUtc, DateTimeOffset? LatestStatusChangedUtc, DateTimeOffset LatestDeterminedUtc, PackageHealthInfo LatestKnownHealth, PackageRemediationAdvice Remediation, CriticalityAssessment Criticality);
