namespace NuGetAudit.Intelligence;

public sealed record ReportingIssueOccurrence(string LocationId, string Scope, string? ProjectName, string? ProjectPath, string? TargetFramework, string? DependencyPath, bool IsDirectDependency, IReadOnlyDictionary<string, string> Metadata);
