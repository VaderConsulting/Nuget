namespace NuGetAudit.Intelligence;

public sealed record ReportingIssue(string IssueId, string Category, string Title, string SubjectId, string SubjectVersion, string CurrentState, DateTimeOffset DeterminedUtc, DateTimeOffset? StatusChangedUtc, CriticalityAssessment Criticality, ReportingRemediation Remediation, IReadOnlyDictionary<string, string> Metadata, IReadOnlyList<ReportingIssueOccurrence> Occurrences);
