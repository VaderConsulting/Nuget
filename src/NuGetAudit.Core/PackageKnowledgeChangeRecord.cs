namespace NuGetAudit.Core;

/// <summary>
/// Describes a newly discovered knowledge change for a package version.
/// </summary>
/// <param name="PackageId">The package identifier.</param>
/// <param name="ResolvedVersion">The resolved package version.</param>
/// <param name="DeterminedUtc">When the knowledge change was determined.</param>
/// <param name="PreviousHealthInfo">The previously known health state, if any.</param>
/// <param name="CurrentHealthInfo">The current known health state.</param>
/// <param name="Remediation">The current remediation guidance.</param>
/// <param name="Summary">A concise description of the knowledge change.</param>
public sealed record PackageKnowledgeChangeRecord(string PackageId, string ResolvedVersion, DateTimeOffset DeterminedUtc, PackageHealthInfo? PreviousHealthInfo, PackageHealthInfo CurrentHealthInfo, PackageRemediationAdvice Remediation, string Summary);
