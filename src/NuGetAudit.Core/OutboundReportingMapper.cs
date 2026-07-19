using NuGetAudit.Intelligence;

namespace NuGetAudit.Core;

internal static class OutboundReportingMapper
{
    internal static ReportingBatch Map(SolutionSnapshot snapshot, PackageKnowledgeSnapshot knowledgeSnapshot)
    {
        ReportingSource source = new ReportingSource(snapshot.Solution.SourceIdentity.IdentityKey, snapshot.Solution.SourceIdentity.IdentityKind, snapshot.Solution.SourceIdentity.RepositoryRootPath, snapshot.Solution.SourceIdentity.GitRemoteUrl, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["solutionName"] = snapshot.Solution.SolutionName, ["solutionPath"] = snapshot.Solution.SolutionPath, ["machineName"] = snapshot.Solution.SourceIdentity.MachineName });

        ReportingLocation[] locations =
        [
            new ReportingLocation(snapshot.Solution.SolutionKey, snapshot.Solution.SourceIdentity.MachineName, snapshot.Solution.SolutionPath, "solution-or-project", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["repositoryRoot"] = snapshot.Solution.SourceIdentity.RepositoryRootPath, ["identityKind"] = snapshot.Solution.SourceIdentity.IdentityKind })
        ];

        Dictionary<(string PackageId, string ResolvedVersion), PackageKnowledgeRecord> knowledgeIndex = knowledgeSnapshot.Packages
            .ToDictionary(
                static item => (item.PackageId, item.ResolvedVersion),
                static item => item);

        ReportingIssue[] issues = snapshot.Projects
            .SelectMany(static project => project.Packages.Select(package => (project, package)))
            .Where(static pair =>
                pair.package.HealthInfo.IsVulnerable
                || pair.package.HealthInfo.IsDeprecated
                || pair.package.HealthInfo.IsObsolete
                || pair.package.HealthInfo.IsOutdated)
            .GroupBy(static pair => (pair.package.PackageId, pair.package.ResolvedVersion))
            .Select(group =>
            {
                PackageReferenceRecord package = group.First().package;
                PackageKnowledgeRecord? knowledge = null;
                if (package.ResolvedVersion is not null)
                {
                    knowledgeIndex.TryGetValue((package.PackageId, package.ResolvedVersion), out knowledge);
                }

                CriticalityAssessment criticality = knowledge?.Criticality
                    ?? new CriticalityAssessment(0, 0, CriticalityBand.Low, CriticalityBand.Low, Array.Empty<FactorScore>(), "No criticality assessment was available.");

                ReportingRemediation remediation = new ReportingRemediation(knowledge?.Remediation.Summary, knowledge?.Remediation.RecommendedVersion, knowledge?.Remediation.AlternatePackageId, knowledge?.Remediation.AlternatePackageRange, new Dictionary<string, string>());

                ReportingIssueOccurrence[] occurrences = group
                    .Select(pair => new ReportingIssueOccurrence(snapshot.Solution.SolutionKey, "package-instance", pair.project.ProjectName, pair.project.ProjectPath, pair.package.TargetFrameworkMoniker, pair.package.DependencyPath.Count == 0 ? null : string.Join(" > ", pair.package.DependencyPath), pair.package.ReferenceKind == PackageReferenceKind.Direct, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["requestedVersion"] = pair.package.RequestedVersion ?? string.Empty }))
                    .ToArray();

                string currentState = DescribeHealth(knowledge?.LatestKnownHealth ?? package.HealthInfo);

                return new ReportingIssue($"{snapshot.Solution.SourceIdentity.IdentityKey}:{package.PackageId}:{package.ResolvedVersion}", "nuget-package", $"{package.PackageId} {package.ResolvedVersion} requires attention", package.PackageId, package.ResolvedVersion ?? "-", currentState, knowledge?.LatestDeterminedUtc ?? snapshot.CapturedUtc, knowledge?.LatestStatusChangedUtc, criticality, remediation, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["snapshotId"] = snapshot.SnapshotId, ["analysisStatus"] = snapshot.AnalysisStatus.ToString() }, occurrences);
            })
            .OrderByDescending(static issue => issue.Criticality.AlertScore)
            .ThenByDescending(static issue => issue.Criticality.RiskScore)
            .ToArray();

        return new ReportingBatch(snapshot.SnapshotId, DateTimeOffset.UtcNow, "NuGetAudit", source, locations, issues);
    }

    private static string DescribeHealth(PackageHealthInfo health)
    {
        List<string> states = new();
        if (health.IsVulnerable)
        {
            states.Add($"vulnerable:{health.MaxVulnerabilitySeverity}");
        }

        if (health.IsObsolete)
        {
            states.Add("obsolete");
        }
        else if (health.IsDeprecated)
        {
            states.Add("deprecated");
        }

        if (health.IsOutdated)
        {
            states.Add($"outdated->{health.LatestStableVersion ?? "unknown"}");
        }

        if (health.DevelopmentStatus == PackageDevelopmentStatus.Removed)
        {
            states.Add("removed");
        }
        else if (health.DevelopmentStatus == PackageDevelopmentStatus.Abandoned)
        {
            states.Add("abandoned");
        }

        return states.Count == 0 ? "ok" : string.Join(", ", states);
    }
}
