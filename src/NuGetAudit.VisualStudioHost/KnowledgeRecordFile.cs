namespace NuGetAudit.VisualStudioHost;

public sealed class KnowledgeRecordFile
{
    public string? PackageId { get; set; }
    public string? ResolvedVersion { get; set; }
    public DateTimeOffset? LatestStatusChangedUtc { get; set; }
    public DateTimeOffset LatestDeterminedUtc { get; set; }
    public RemediationFile Remediation { get; set; } = new();
    public CriticalityFile Criticality { get; set; } = new();
}
