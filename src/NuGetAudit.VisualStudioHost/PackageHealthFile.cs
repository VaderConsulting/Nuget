namespace NuGetAudit.VisualStudioHost;

/// <summary>
/// Package health contract used by the Visual Studio host.
/// </summary>
public sealed class PackageHealthFile
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
    public IReadOnlyList<PackageVulnerabilityFile> Vulnerabilities { get; set; } = Array.Empty<PackageVulnerabilityFile>();
}
