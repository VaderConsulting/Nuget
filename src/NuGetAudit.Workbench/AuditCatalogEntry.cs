namespace NuGetAudit.Workbench;

internal sealed record AuditCatalogEntry(string SourceIdentityKey, string SourceIdentityKind, string MachineName, string RepositoryRootPath, string? GitRemoteUrl, string? GitRemoteKey, string InputPath, string DisplayName, string DiscoveredFromPath, string OutputDirectory, DateTimeOffset AddedUtc);
