using System.Collections.Generic;
using NuGetAudit.Core;
using NuGetAudit.Presentation;

namespace NuGetAudit.Workbench;

internal static class PresentationMapper
{
    internal static SurfaceSolutionSnapshot Map(SolutionSnapshot snapshot, PackageKnowledgeSnapshot knowledgeSnapshot, bool MergeNetVersions = true)
    {
        Dictionary<string, PackageKnowledgeRecord> knowledgeByKey = knowledgeSnapshot.Packages
            .ToDictionary(static package => CreateKnowledgeKey(package.PackageId, package.ResolvedVersion), StringComparer.OrdinalIgnoreCase);

        return new SurfaceSolutionSnapshot
        {
            SnapshotId = snapshot.SnapshotId,
            CapturedUtc = snapshot.CapturedUtc,
            Projects = snapshot.Projects.Select(project => Map(snapshot, project, knowledgeByKey, MergeNetVersions)).ToArray()
        };
    }

    private static SurfaceProjectSnapshot Map(
        SolutionSnapshot snapshot,
        ProjectSnapshot project,
        IReadOnlyDictionary<string, PackageKnowledgeRecord> knowledgeByKey,
        bool MergeNetVersions)
    {
        List<SurfacePackageReference> mappedPackages = project.Packages
            .Select(package => Map(package, knowledgeByKey))
            .ToList();

        (List<SurfacePackageReference> mergedPackages, Dictionary<string, string> stableKeyRemap) = ExplorerSurfacePackageMerge.MergeDuplicateRows(
            mappedPackages,
            snapshot.Solution.SolutionPath,
            project.ProjectPath ?? string.Empty,
            MergeNetVersions);

        return new SurfaceProjectSnapshot
        {
            SolutionPath = snapshot.Solution.SolutionPath,
            ProjectName = project.ProjectName,
            ProjectPath = project.ProjectPath,
            TargetFrameworks = project.TargetFrameworks,
            Packages = mergedPackages.ToArray(),
            DependencyEdges = project.DependencyEdges
                .Select(Map)
                .Select(edge => ExplorerSurfacePackageMerge.RemapEdgeStableKeys(edge, stableKeyRemap))
                .ToArray()
        };
    }

    private static SurfacePackageReference Map(PackageReferenceRecord package, IReadOnlyDictionary<string, PackageKnowledgeRecord> knowledgeByKey)
    {
        string resolvedVersion = package.ResolvedVersion ?? package.RequestedVersion ?? string.Empty;
        knowledgeByKey.TryGetValue(CreateKnowledgeKey(package.PackageId, resolvedVersion), out PackageKnowledgeRecord? knowledgeRecord);

        return new SurfacePackageReference
        {
            PackageId = package.PackageId,
            RequestedVersion = package.RequestedVersion,
            ResolvedVersion = package.ResolvedVersion,
            ReferenceKind = package.ReferenceKind.ToString(),
            IsCentralVersionManaged = package.IsCentralVersionManaged,
            TargetFrameworkMoniker = package.TargetFrameworkMoniker,
            DependencyParents = package.DependencyParents.ToArray(),
            DependencyPath = package.DependencyPath.ToArray(),
            StablePackageInstanceKey = package.StablePackageInstanceKey,
            KnowledgeDeterminedUtc = knowledgeRecord?.LatestDeterminedUtc,
            KnowledgeStatusChangedUtc = knowledgeRecord?.LatestStatusChangedUtc,
            RemediationSummary = DescribeRemediation(knowledgeRecord?.Remediation),
            RiskScore = knowledgeRecord?.Criticality.RiskScore,
            AlertScore = knowledgeRecord?.Criticality.AlertScore,
            RiskBand = knowledgeRecord?.Criticality.RiskBand.ToString(),
            AlertBand = knowledgeRecord?.Criticality.AlertBand.ToString(),
            HealthInfo = new SurfacePackageHealth
            {
                IsDeprecated = package.HealthInfo.IsDeprecated,
                IsObsolete = package.HealthInfo.IsObsolete,
                IsOutdated = package.HealthInfo.IsOutdated,
                IsVulnerable = package.HealthInfo.IsVulnerable,
                DevelopmentStatus = package.HealthInfo.DevelopmentStatus.ToString(),
                LatestStableVersion = package.HealthInfo.LatestStableVersion,
                DeprecationMessage = package.HealthInfo.DeprecationMessage,
                AlternatePackageId = package.HealthInfo.AlternatePackageId,
                AlternatePackageRange = package.HealthInfo.AlternatePackageRange,
                MaxVulnerabilitySeverity = package.HealthInfo.MaxVulnerabilitySeverity.ToString(),
                Vulnerabilities = package.HealthInfo.Vulnerabilities
                    .Select(vulnerability => new SurfacePackageVulnerability
                    {
                        AdvisoryUrl = vulnerability.AdvisoryUrl,
                        Severity = vulnerability.Severity.ToString()
                    })
                    .ToArray()
            }
        };
    }

    private static SurfaceDependencyEdge Map(DependencyEdgeRecord edge)
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

    private static string? DescribeRemediation(PackageRemediationAdvice? remediation)
        => remediation is null
            ? null
            : remediation.Summary
              ?? remediation.RecommendedVersion
              ?? (!string.IsNullOrWhiteSpace(remediation.AlternatePackageId)
                  ? $"{remediation.AlternatePackageId} {remediation.AlternatePackageRange}".Trim()
                  : null);
}
