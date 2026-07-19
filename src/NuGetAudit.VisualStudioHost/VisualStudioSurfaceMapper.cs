using NuGetAudit.Presentation;

namespace NuGetAudit.VisualStudioHost;

internal static class VisualStudioSurfaceMapper
{
    internal static SurfaceSolutionSnapshot Map(SolutionSnapshotFile snapshot, KnowledgeSnapshotFile knowledgeSnapshot)
    {
        Dictionary<string, KnowledgeRecordFile> knowledgeByKey = knowledgeSnapshot.Packages
            .Where(static package => !string.IsNullOrWhiteSpace(package.PackageId) && !string.IsNullOrWhiteSpace(package.ResolvedVersion))
            .ToDictionary(
                static package => CreateKnowledgeKey(package.PackageId!, package.ResolvedVersion!),
                StringComparer.OrdinalIgnoreCase);

        return new SurfaceSolutionSnapshot
        {
            SnapshotId = snapshot.SnapshotId,
            CapturedUtc = snapshot.CapturedUtc,
            Projects = snapshot.Projects.Select(project => Map(project, knowledgeByKey)).ToArray()
        };
    }

    private static SurfaceProjectSnapshot Map(ProjectSnapshotFile project, IReadOnlyDictionary<string, KnowledgeRecordFile> knowledgeByKey)
    {
        return new SurfaceProjectSnapshot
        {
            ProjectName = project.ProjectName,
            ProjectPath = project.ProjectPath,
            TargetFrameworks = project.TargetFrameworks,
            Packages = project.Packages.Select(package => Map(package, knowledgeByKey)).ToArray(),
            DependencyEdges = project.DependencyEdges.Select(Map).ToArray()
        };
    }

    private static SurfacePackageReference Map(PackageReferenceFile package, IReadOnlyDictionary<string, KnowledgeRecordFile> knowledgeByKey)
    {
        string resolvedVersion = package.ResolvedVersion ?? package.RequestedVersion ?? string.Empty;
        KnowledgeRecordFile? knowledgeRecord = null;
        if (!string.IsNullOrWhiteSpace(package.PackageId) && !string.IsNullOrWhiteSpace(resolvedVersion))
        {
            knowledgeByKey.TryGetValue(CreateKnowledgeKey(package.PackageId!, resolvedVersion), out knowledgeRecord);
        }

        return new SurfacePackageReference
        {
            PackageId = package.PackageId,
            RequestedVersion = package.RequestedVersion,
            ResolvedVersion = package.ResolvedVersion,
            ReferenceKind = package.ReferenceKind,
            IsCentralVersionManaged = package.IsCentralVersionManaged,
            TargetFrameworkMoniker = package.TargetFrameworkMoniker,
            DependencyParents = package.DependencyParents.ToArray(),
            DependencyPath = package.DependencyPath.ToArray(),
            StablePackageInstanceKey = package.StablePackageInstanceKey,
            KnowledgeDeterminedUtc = knowledgeRecord?.LatestDeterminedUtc,
            KnowledgeStatusChangedUtc = knowledgeRecord?.LatestStatusChangedUtc,
            RemediationSummary = DescribeRemediation(knowledgeRecord?.Remediation),
            RiskScore = knowledgeRecord?.Criticality?.RiskScore,
            AlertScore = knowledgeRecord?.Criticality?.AlertScore,
            RiskBand = knowledgeRecord?.Criticality?.RiskBand,
            AlertBand = knowledgeRecord?.Criticality?.AlertBand,
            HealthInfo = new SurfacePackageHealth
            {
                IsDeprecated = package.HealthInfo.IsDeprecated,
                IsObsolete = package.HealthInfo.IsObsolete,
                IsOutdated = package.HealthInfo.IsOutdated,
                IsVulnerable = package.HealthInfo.IsVulnerable,
                DevelopmentStatus = package.HealthInfo.DevelopmentStatus,
                LatestStableVersion = package.HealthInfo.LatestStableVersion,
                DeprecationMessage = package.HealthInfo.DeprecationMessage,
                AlternatePackageId = package.HealthInfo.AlternatePackageId,
                AlternatePackageRange = package.HealthInfo.AlternatePackageRange,
                MaxVulnerabilitySeverity = package.HealthInfo.MaxVulnerabilitySeverity,
                Vulnerabilities = package.HealthInfo.Vulnerabilities
                    .Select(vulnerability => new SurfacePackageVulnerability
                    {
                        AdvisoryUrl = vulnerability.AdvisoryUrl,
                        Severity = vulnerability.Severity
                    })
                    .ToArray()
            }
        };
    }

    private static SurfaceDependencyEdge Map(DependencyEdgeFile edge)
    {
        return new SurfaceDependencyEdge
        {
            FromPackageId = edge.FromPackageId,
            ToPackageId = edge.ToPackageId,
            TargetFrameworkMoniker = edge.TargetFrameworkMoniker,
            FromStablePackageInstanceKey = edge.FromStablePackageInstanceKey,
            ToStablePackageInstanceKey = edge.ToStablePackageInstanceKey
        };
    }

    private static string CreateKnowledgeKey(string packageId, string resolvedVersion)
        => $"{packageId}|{resolvedVersion}".ToUpperInvariant();

    private static string? DescribeRemediation(RemediationFile? remediation)
        => remediation is null
            ? null
            : remediation.Summary
              ?? remediation.RecommendedVersion
              ?? (!string.IsNullOrWhiteSpace(remediation.AlternatePackageId)
                  ? $"{remediation.AlternatePackageId} {remediation.AlternatePackageRange}".Trim()
                  : null);
}
