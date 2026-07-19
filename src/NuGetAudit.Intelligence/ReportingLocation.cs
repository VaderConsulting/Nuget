namespace NuGetAudit.Intelligence;

public sealed record ReportingLocation(string LocationId, string MachineName, string Path, string LocationKind, IReadOnlyDictionary<string, string> Metadata);
