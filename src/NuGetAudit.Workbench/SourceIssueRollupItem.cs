namespace NuGetAudit.Workbench;

internal sealed record SourceIssueRollupItem(string SourceIdentityKey, string SourceDisplayName, string PackageId, string ResolvedVersion, string CurrentHealth, string Remediation, double? RiskScore, double? AlertScore, string? RiskBand, string? AlertBand, int AffectedTargets, int AffectedLocations, DateTimeOffset LatestDeterminedUtc);
