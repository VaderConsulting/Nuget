namespace NuGetAudit.VisualStudioHost;

public sealed class RemediationFile
{
    public string? RecommendedVersion { get; set; }
    public string? AlternatePackageId { get; set; }
    public string? AlternatePackageRange { get; set; }
    public string? Summary { get; set; }
}
