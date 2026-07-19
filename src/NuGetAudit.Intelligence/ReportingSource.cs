namespace NuGetAudit.Intelligence;

public sealed record ReportingSource(string SourceId, string SourceKind, string? RepositoryRoot, string? RemoteUrl, IReadOnlyDictionary<string, string> Metadata);
