namespace NuGetAudit.Core;

/// <summary>
/// Computes a package-level delta between two solution snapshots.
/// </summary>
internal static class DeltaCalculator
{
    /// <summary>
    /// Compares the previous snapshot with the current snapshot.
    /// </summary>
    /// <param name="previousSnapshot">The previously stored snapshot, if any.</param>
    /// <param name="currentSnapshot">The current snapshot.</param>
    /// <returns>The calculated delta.</returns>
    public static SnapshotDelta Calculate(SolutionSnapshot? previousSnapshot, SolutionSnapshot currentSnapshot)
    {
        if (previousSnapshot is null)
        {
            return new SnapshotDelta(currentSnapshot.SnapshotId, null, false, null, Array.Empty<PackageChangeRecord>(), Array.Empty<DependencyEdgeChangeRecord>(), Array.Empty<PackageKnowledgeChangeRecord>());
        }

        if (!string.Equals(previousSnapshot.Solution.SolutionKey, currentSnapshot.Solution.SolutionKey, StringComparison.Ordinal))
        {
            return new SnapshotDelta(currentSnapshot.SnapshotId, previousSnapshot.SnapshotId, false, "Comparison was skipped because the previous accepted snapshot belongs to a different solution.", Array.Empty<PackageChangeRecord>(), Array.Empty<DependencyEdgeChangeRecord>(), Array.Empty<PackageKnowledgeChangeRecord>());
        }

        if (previousSnapshot.AnalysisStatus != AnalysisStatus.Complete || currentSnapshot.AnalysisStatus != AnalysisStatus.Complete)
        {
            return new SnapshotDelta(currentSnapshot.SnapshotId, previousSnapshot.SnapshotId, false, "Comparison was skipped because either the previous accepted snapshot or the current snapshot is incomplete.", Array.Empty<PackageChangeRecord>(), Array.Empty<DependencyEdgeChangeRecord>(), Array.Empty<PackageKnowledgeChangeRecord>());
        }

        Dictionary<string, IndexedPackage> previous = Index(previousSnapshot);
        Dictionary<string, IndexedPackage> current = Index(currentSnapshot);
        List<PackageChangeRecord> changes = new();
        List<DependencyEdgeChangeRecord> dependencyEdgeChanges = CalculateDependencyEdgeChanges(previousSnapshot, currentSnapshot);

        foreach ((string key, IndexedPackage currentPackage) in current)
        {
            if (!previous.TryGetValue(key, out IndexedPackage? previousPackage))
            {
                changes.Add(new PackageChangeRecord(key, currentPackage.Project.ProjectName, currentPackage.Package.PackageId, PackageChangeType.Added, null, currentPackage.Package.ResolvedVersion, null, currentPackage.Package.RequestedVersion, currentPackage.Package.TargetFrameworkMoniker, null, currentPackage.Package.ReferenceKind, Array.Empty<string>(), currentPackage.Package.DependencyParents, Array.Empty<string>(), currentPackage.Package.DependencyPath, null, currentPackage.Package.HealthInfo));
                continue;
            }

            if (HasPackageChanged(previousPackage.Package, currentPackage.Package))
            {
                changes.Add(new PackageChangeRecord(key, currentPackage.Project.ProjectName, currentPackage.Package.PackageId, DetermineChangeType(previousPackage.Package, currentPackage.Package), previousPackage.Package.ResolvedVersion, currentPackage.Package.ResolvedVersion, previousPackage.Package.RequestedVersion, currentPackage.Package.RequestedVersion, currentPackage.Package.TargetFrameworkMoniker, previousPackage.Package.ReferenceKind, currentPackage.Package.ReferenceKind, previousPackage.Package.DependencyParents, currentPackage.Package.DependencyParents, previousPackage.Package.DependencyPath, currentPackage.Package.DependencyPath, previousPackage.Package.HealthInfo, currentPackage.Package.HealthInfo));
            }
        }

        foreach ((string key, IndexedPackage previousPackage) in previous)
        {
            if (current.ContainsKey(key))
            {
                continue;
            }

            changes.Add(new PackageChangeRecord(key, previousPackage.Project.ProjectName, previousPackage.Package.PackageId, PackageChangeType.Removed, previousPackage.Package.ResolvedVersion, null, previousPackage.Package.RequestedVersion, null, previousPackage.Package.TargetFrameworkMoniker, previousPackage.Package.ReferenceKind, null, previousPackage.Package.DependencyParents, Array.Empty<string>(), previousPackage.Package.DependencyPath, Array.Empty<string>(), previousPackage.Package.HealthInfo, null));
        }

        return new SnapshotDelta(currentSnapshot.SnapshotId, previousSnapshot.SnapshotId, changes.Count > 0 || dependencyEdgeChanges.Count > 0 || !string.Equals(previousSnapshot.NormalisedContentHash, currentSnapshot.NormalisedContentHash, StringComparison.Ordinal), null, changes.OrderBy(static change => change.ProjectName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static change => change.PackageId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static change => change.TargetFrameworkMoniker, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            dependencyEdgeChanges, Array.Empty<PackageKnowledgeChangeRecord>());
    }

    /// <summary>
    /// Indexes packages by stable key for efficient snapshot comparison.
    /// </summary>
    /// <param name="snapshot">The snapshot to index.</param>
    /// <returns>The indexed package map.</returns>
    private static Dictionary<string, IndexedPackage> Index(SolutionSnapshot snapshot)
    {
        return snapshot.Projects
            .SelectMany(project => project.Packages.Select(package => new IndexedPackage(project, package)))
            .ToDictionary(static entry => entry.Package.StablePackageInstanceKey, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Infers the direction of a version change.
    /// </summary>
    /// <param name="previousVersion">The previous version.</param>
    /// <param name="currentVersion">The current version.</param>
    /// <returns>The detected change type.</returns>
    private static PackageChangeType CompareVersions(string? previousVersion, string? currentVersion)
    {
        if (string.IsNullOrWhiteSpace(previousVersion) || string.IsNullOrWhiteSpace(currentVersion))
        {
            return PackageChangeType.MetadataChanged;
        }

        if (Version.TryParse(previousVersion.Split('-')[0], out Version? previous)
            && Version.TryParse(currentVersion.Split('-')[0], out Version? current))
        {
            return current > previous ? PackageChangeType.Upgraded : PackageChangeType.Downgraded;
        }

        return PackageChangeType.MetadataChanged;
    }

    private static bool HasPackageChanged(PackageReferenceRecord previousPackage, PackageReferenceRecord currentPackage)
    {
        return !string.Equals(previousPackage.ResolvedVersion, currentPackage.ResolvedVersion, StringComparison.OrdinalIgnoreCase)
               || !string.Equals(previousPackage.RequestedVersion, currentPackage.RequestedVersion, StringComparison.OrdinalIgnoreCase)
               || previousPackage.ReferenceKind != currentPackage.ReferenceKind
               || !previousPackage.DependencyParents.SequenceEqual(currentPackage.DependencyParents, StringComparer.OrdinalIgnoreCase)
               || !previousPackage.DependencyPath.SequenceEqual(currentPackage.DependencyPath, StringComparer.OrdinalIgnoreCase)
               || !AreHealthInfosEqual(previousPackage.HealthInfo, currentPackage.HealthInfo);
    }

    private static PackageChangeType DetermineChangeType(PackageReferenceRecord previousPackage, PackageReferenceRecord currentPackage)
    {
        if (!string.Equals(previousPackage.ResolvedVersion, currentPackage.ResolvedVersion, StringComparison.OrdinalIgnoreCase))
        {
            return CompareVersions(previousPackage.ResolvedVersion, currentPackage.ResolvedVersion);
        }

        return PackageChangeType.MetadataChanged;
    }

    private static List<DependencyEdgeChangeRecord> CalculateDependencyEdgeChanges(SolutionSnapshot previousSnapshot, SolutionSnapshot currentSnapshot)
    {
        Dictionary<string, IndexedEdge> previous = IndexEdges(previousSnapshot);
        Dictionary<string, IndexedEdge> current = IndexEdges(currentSnapshot);
        List<DependencyEdgeChangeRecord> changes = new();

        foreach ((string key, IndexedEdge currentEdge) in current)
        {
            if (previous.ContainsKey(key))
            {
                continue;
            }

            changes.Add(new DependencyEdgeChangeRecord(currentEdge.Project.ProjectName, currentEdge.Edge.FromPackageId, currentEdge.Edge.ToPackageId, currentEdge.Edge.TargetFrameworkMoniker, DependencyEdgeChangeType.Added));
        }

        foreach ((string key, IndexedEdge previousEdge) in previous)
        {
            if (current.ContainsKey(key))
            {
                continue;
            }

            changes.Add(new DependencyEdgeChangeRecord(previousEdge.Project.ProjectName, previousEdge.Edge.FromPackageId, previousEdge.Edge.ToPackageId, previousEdge.Edge.TargetFrameworkMoniker, DependencyEdgeChangeType.Removed));
        }

        return changes
            .OrderBy(static change => change.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static change => change.FromPackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static change => change.ToPackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static change => change.TargetFrameworkMoniker, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Dictionary<string, IndexedEdge> IndexEdges(SolutionSnapshot snapshot)
    {
        return snapshot.Projects
            .SelectMany(project => project.DependencyEdges.Select(edge => new IndexedEdge(project, edge)))
            .ToDictionary(static entry => CreateEdgeKey(entry.Project.ProjectId, entry.Edge), StringComparer.OrdinalIgnoreCase);
    }

    private static string CreateEdgeKey(string projectId, DependencyEdgeRecord edge)
    {
        return string.Join("|", projectId, edge.TargetFrameworkMoniker ?? string.Empty, edge.FromPackageId, edge.ToPackageId);
    }

    private static bool AreHealthInfosEqual(PackageHealthInfo? previousHealth, PackageHealthInfo? currentHealth)
    {
        if (ReferenceEquals(previousHealth, currentHealth))
        {
            return true;
        }

        if (previousHealth is null || currentHealth is null)
        {
            return false;
        }

        return previousHealth.IsDeprecated == currentHealth.IsDeprecated
               && previousHealth.IsObsolete == currentHealth.IsObsolete
               && previousHealth.IsOutdated == currentHealth.IsOutdated
               && previousHealth.IsVulnerable == currentHealth.IsVulnerable
               && previousHealth.DevelopmentStatus == currentHealth.DevelopmentStatus
               && string.Equals(previousHealth.LatestStableVersion, currentHealth.LatestStableVersion, StringComparison.OrdinalIgnoreCase)
               && string.Equals(previousHealth.DeprecationMessage, currentHealth.DeprecationMessage, StringComparison.Ordinal)
               && string.Equals(previousHealth.AlternatePackageId, currentHealth.AlternatePackageId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(previousHealth.AlternatePackageRange, currentHealth.AlternatePackageRange, StringComparison.OrdinalIgnoreCase)
               && previousHealth.MaxVulnerabilitySeverity == currentHealth.MaxVulnerabilitySeverity
               && previousHealth.Vulnerabilities.Count == currentHealth.Vulnerabilities.Count
               && previousHealth.Vulnerabilities
                   .OrderBy(static vulnerability => vulnerability.AdvisoryUrl, StringComparer.OrdinalIgnoreCase)
                   .ThenBy(static vulnerability => vulnerability.Severity)
                   .SequenceEqual(
                       currentHealth.Vulnerabilities
                           .OrderBy(static vulnerability => vulnerability.AdvisoryUrl, StringComparer.OrdinalIgnoreCase)
                           .ThenBy(static vulnerability => vulnerability.Severity));
    }

    private sealed record IndexedPackage(ProjectSnapshot Project, PackageReferenceRecord Package);
    private sealed record IndexedEdge(ProjectSnapshot Project, DependencyEdgeRecord Edge);
}
