namespace NuGetAudit.Presentation;

public sealed class SnapshotHistoryItem
{
    public SnapshotHistoryItem(string sourceName, string outputDirectory, string snapshotId, DateTimeOffset capturedUtc, string solutionName, string solutionPath, string snapshotJsonPath, string? markdownPath, string? deltaJsonPath, string analysisStatus, int projectCount, int packageCount, int attentionCount, string rowVisualBand)
    {
        SourceName = sourceName;
        OutputDirectory = outputDirectory;
        SnapshotId = snapshotId;
        CapturedUtc = capturedUtc;
        SolutionName = solutionName;
        SolutionPath = solutionPath;
        SnapshotJsonPath = snapshotJsonPath;
        MarkdownPath = markdownPath;
        DeltaJsonPath = deltaJsonPath;
        AnalysisStatus = analysisStatus;
        ProjectCount = projectCount;
        PackageCount = packageCount;
        AttentionCount = attentionCount;
        RowVisualBand = rowVisualBand;
    }

    public string SourceName { get; }
    public string OutputDirectory { get; }
    public string SnapshotId { get; }
    public DateTimeOffset CapturedUtc { get; }
    public string SolutionName { get; }
    public string SolutionPath { get; }
    public string SnapshotJsonPath { get; }
    public string? MarkdownPath { get; }
    public string? DeltaJsonPath { get; }
    public string AnalysisStatus { get; }
    public int ProjectCount { get; }
    public int PackageCount { get; }
    public int AttentionCount { get; }
    /// <summary>
    /// Drives row background and foreground in the History snapshot grid, using the same bands and brushes as the knowledge timeline (Critical, Attention, Ok, None).
    /// </summary>
    public string RowVisualBand { get; }
}

public sealed class KnowledgeTimelineItem
{
    public KnowledgeTimelineItem(long id, string sourceName, string outputDirectory, string snapshotId, string packageId, string resolvedVersion, DateTimeOffset determinedUtc, string previousHealth, string currentHealth, string remediation, string summary, double? riskScore, double? alertScore, string? riskBand, string? alertBand, string rowVisualBand)
    {
        Id = id;
        SourceName = sourceName;
        OutputDirectory = outputDirectory;
        SnapshotId = snapshotId;
        PackageId = packageId;
        ResolvedVersion = resolvedVersion;
        DeterminedUtc = determinedUtc;
        PreviousHealth = previousHealth;
        CurrentHealth = currentHealth;
        Remediation = remediation;
        Summary = summary;
        RiskScore = riskScore;
        AlertScore = alertScore;
        RiskBand = riskBand;
        AlertBand = alertBand;
        RowVisualBand = rowVisualBand;
    }

    public long Id { get; }
    public string SourceName { get; }
    public string OutputDirectory { get; }
    public string SnapshotId { get; }
    public string PackageId { get; }
    public string ResolvedVersion { get; }
    public DateTimeOffset DeterminedUtc { get; }
    public string PreviousHealth { get; }
    public string CurrentHealth { get; }
    public string Remediation { get; }
    public string Summary { get; }
    public double? RiskScore { get; }
    public double? AlertScore { get; }
    public string? RiskBand { get; }
    public string? AlertBand { get; }
    /// <summary>
    /// Drives row background and foreground in the History knowledge grid, aligned with snapshot timeline severity (Critical, High, Attention, Ok, None).
    /// </summary>
    public string RowVisualBand { get; }
}

public sealed class SurfaceHistory
{
    public SurfaceHistory(IReadOnlyList<SnapshotHistoryItem> snapshots, IReadOnlyList<KnowledgeTimelineItem> knowledgeTimeline, string? knowledgeDatabasePath)
    {
        Snapshots = snapshots;
        KnowledgeTimeline = knowledgeTimeline;
        KnowledgeDatabasePath = knowledgeDatabasePath;
    }

    public IReadOnlyList<SnapshotHistoryItem> Snapshots { get; }
    public IReadOnlyList<KnowledgeTimelineItem> KnowledgeTimeline { get; }
    public string? KnowledgeDatabasePath { get; }
}
