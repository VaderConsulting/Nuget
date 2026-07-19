namespace NuGetAudit.Presentation;

public sealed class SurfaceSolutionSnapshot
{
    public string? SnapshotId { get; set; }
    public DateTimeOffset CapturedUtc { get; set; }
    public IReadOnlyList<SurfaceProjectSnapshot> Projects { get; set; } = Array.Empty<SurfaceProjectSnapshot>();
}

public sealed class SurfaceProjectSnapshot
{
    /// <summary>
    /// Solution path used when normalizing stable instance keys during explorer row merge.
    /// </summary>
    public string? SolutionPath { get; set; }
    public string? ProjectName { get; set; }
    public string? ProjectPath { get; set; }
    public string? TargetFrameworks { get; set; }
    public IReadOnlyList<SurfacePackageReference> Packages { get; set; } = Array.Empty<SurfacePackageReference>();
    public IReadOnlyList<SurfaceDependencyEdge> DependencyEdges { get; set; } = Array.Empty<SurfaceDependencyEdge>();
}

public sealed class SurfacePackageReference
{
    public string? PackageId { get; set; }
    public string? RequestedVersion { get; set; }
    public string? ResolvedVersion { get; set; }
    public string? ReferenceKind { get; set; }
    public bool IsCentralVersionManaged { get; set; }
    public string? TargetFrameworkMoniker { get; set; }
    public IReadOnlyList<string> DependencyParents { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> DependencyPath { get; set; } = Array.Empty<string>();
    public SurfacePackageHealth HealthInfo { get; set; } = new();
    public string? StablePackageInstanceKey { get; set; }
    public DateTimeOffset? KnowledgeDeterminedUtc { get; set; }
    public DateTimeOffset? KnowledgeStatusChangedUtc { get; set; }
    public string? RemediationSummary { get; set; }
    public double? RiskScore { get; set; }
    public double? AlertScore { get; set; }
    public string? RiskBand { get; set; }
    public string? AlertBand { get; set; }
}

public sealed class SurfaceDependencyEdge
{
    public string? FromPackageId { get; set; }
    public string? ToPackageId { get; set; }
    public string? TargetFrameworkMoniker { get; set; }
    public string? FromStablePackageInstanceKey { get; set; }
    public string? ToStablePackageInstanceKey { get; set; }
}

public sealed class SurfacePackageHealth
{
    public bool IsDeprecated { get; set; }
    public bool IsObsolete { get; set; }
    public bool IsOutdated { get; set; }
    public bool IsVulnerable { get; set; }
    public string DevelopmentStatus { get; set; } = "Unknown";
    public string? LatestStableVersion { get; set; }
    public string? DeprecationMessage { get; set; }
    public string? AlternatePackageId { get; set; }
    public string? AlternatePackageRange { get; set; }
    public string? MaxVulnerabilitySeverity { get; set; }
    public IReadOnlyList<SurfacePackageVulnerability> Vulnerabilities { get; set; } = Array.Empty<SurfacePackageVulnerability>();
}

public sealed class SurfacePackageVulnerability
{
    public string? AdvisoryUrl { get; set; }
    public string? Severity { get; set; }
}
