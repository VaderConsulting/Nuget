namespace NuGetAudit.Presentation;

public sealed class IssueSourceDescriptor
{
    public IssueSourceDescriptor(string sourceIdentityKey, string sourceDisplayName, string outputDirectory)
    {
        SourceIdentityKey = sourceIdentityKey;
        SourceDisplayName = sourceDisplayName;
        OutputDirectory = outputDirectory;
    }

    public string SourceIdentityKey { get; }
    public string SourceDisplayName { get; }
    public string OutputDirectory { get; }
}

public sealed class IssueRollupItem
{
    public IssueRollupItem(string sourceIdentityKey, string sourceDisplayName, string packageId, string resolvedVersion, string currentHealth, string remediation, double? riskScore, double? alertScore, string? riskBand, string? alertBand, int affectedTargets, int affectedLocations, DateTimeOffset latestDeterminedUtc, string rowVisualBand)
    {
        SourceIdentityKey = sourceIdentityKey;
        SourceDisplayName = sourceDisplayName;
        PackageId = packageId;
        ResolvedVersion = resolvedVersion;
        CurrentHealth = currentHealth;
        Remediation = remediation;
        RiskScore = riskScore;
        AlertScore = alertScore;
        RiskBand = riskBand;
        AlertBand = alertBand;
        AffectedTargets = affectedTargets;
        AffectedLocations = affectedLocations;
        LatestDeterminedUtc = latestDeterminedUtc;
        RowVisualBand = rowVisualBand;
    }

    public string SourceIdentityKey { get; }
    public string SourceDisplayName { get; }
    public string PackageId { get; }
    public string ResolvedVersion { get; }
    public string CurrentHealth { get; }
    public string Remediation { get; }
    public double? RiskScore { get; }
    public double? AlertScore { get; }
    public string? RiskBand { get; }
    public string? AlertBand { get; }
    public int AffectedTargets { get; }
    public int AffectedLocations { get; }
    public DateTimeOffset LatestDeterminedUtc { get; }

    /// <summary>
    /// Drives row accent in the Issues grid: Critical, High, Attention, Ok, or None.
    /// </summary>
    public string RowVisualBand { get; }
}
