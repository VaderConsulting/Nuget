namespace NuGetAudit.Intelligence;

public sealed record ReportingRemediation(string? Summary, string? RecommendedVersion, string? AlternateId, string? AlternateRange, IReadOnlyDictionary<string, string> Metadata);
