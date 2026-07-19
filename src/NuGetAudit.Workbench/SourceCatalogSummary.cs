namespace NuGetAudit.Workbench;

internal sealed record SourceCatalogSummary(string SourceIdentityKey, string SourceIdentityKind, string? GitRemoteUrl, string RepositoryRootPath, int CloneCount, int TargetCount, int MachineCount, string DisplayName);
