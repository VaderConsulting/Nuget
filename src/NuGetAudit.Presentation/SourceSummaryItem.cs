namespace NuGetAudit.Presentation;

public sealed class SourceSummaryItem
{
    public SourceSummaryItem(string sourceIdentityKey, string sourceIdentityKind, string? gitRemoteUrl, string repositoryRootPath, int cloneCount, int targetCount, int machineCount, string displayName)
    {
        SourceIdentityKey = sourceIdentityKey;
        SourceIdentityKind = sourceIdentityKind;
        GitRemoteUrl = gitRemoteUrl;
        RepositoryRootPath = repositoryRootPath;
        CloneCount = cloneCount;
        TargetCount = targetCount;
        MachineCount = machineCount;
        DisplayName = displayName;
    }

    public string SourceIdentityKey { get; }
    public string SourceIdentityKind { get; }
    public string? GitRemoteUrl { get; }
    public string RepositoryRootPath { get; }
    public int CloneCount { get; }
    public int TargetCount { get; }
    public int MachineCount { get; }
    public string DisplayName { get; }
}
