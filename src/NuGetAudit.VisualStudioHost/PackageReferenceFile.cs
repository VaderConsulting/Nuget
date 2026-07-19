namespace NuGetAudit.VisualStudioHost;

/// <summary>
/// Package contract used by the Visual Studio host.
/// </summary>
public sealed class PackageReferenceFile
{
    public string? PackageId { get; set; }
    public string? RequestedVersion { get; set; }
    public string? ResolvedVersion { get; set; }
    public string? ReferenceKind { get; set; }
    public bool IsCentralVersionManaged { get; set; }
    public string? TargetFrameworkMoniker { get; set; }
    public IReadOnlyList<string> DependencyParents { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> DependencyPath { get; set; } = Array.Empty<string>();
    public PackageHealthFile HealthInfo { get; set; } = new();
    public string? StablePackageInstanceKey { get; set; }
}
