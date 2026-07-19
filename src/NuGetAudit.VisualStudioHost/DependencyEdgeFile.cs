namespace NuGetAudit.VisualStudioHost;

/// <summary>
/// Dependency-edge contract used by the Visual Studio host.
/// </summary>
public sealed class DependencyEdgeFile
{
    public string? FromPackageId { get; set; }
    public string? ToPackageId { get; set; }
    public string? TargetFrameworkMoniker { get; set; }
    public string? FromStablePackageInstanceKey { get; set; }
    public string? ToStablePackageInstanceKey { get; set; }
}
