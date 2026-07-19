namespace NuGetAudit.Intelligence;

public sealed record ReportingBatch(string BatchId, DateTimeOffset GeneratedUtc, string Producer, ReportingSource Source, IReadOnlyList<ReportingLocation> Locations, IReadOnlyList<ReportingIssue> Issues);
