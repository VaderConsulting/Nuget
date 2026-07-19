namespace NuGetAudit.VisualStudioHost;

public sealed class KnowledgeSnapshotFile
{
    public DateTimeOffset GeneratedUtc { get; set; }
    public IReadOnlyList<KnowledgeRecordFile> Packages { get; set; } = Array.Empty<KnowledgeRecordFile>();
}
