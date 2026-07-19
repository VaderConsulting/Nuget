using System.Globalization;

namespace NuGetAudit.Presentation;

/// <summary>
/// Collapses duplicate package rows that match on every property except requested/resolved versions
/// (and, when <paramref name="MergeNetVersions"/> is true, except target framework moniker).
/// Records stable-key remaps for dependency edges.
/// </summary>
public static class ExplorerSurfacePackageMerge
{
    public static (List<SurfacePackageReference> Merged, Dictionary<string, string> StableKeyRemap) MergeDuplicateRows(
        IReadOnlyList<SurfacePackageReference> packages,
        string solutionPath,
        string projectPath,
        bool mergeNetVersions = true)
    {
        Dictionary<string, string> stableKeyRemap = new(StringComparer.OrdinalIgnoreCase);
        List<SurfacePackageReference> results = new();
        foreach (IGrouping<string, SurfacePackageReference> group in packages
            .GroupBy(package => BuildMergeGroupKey(package, mergeNetVersions), StringComparer.OrdinalIgnoreCase))
        {
            SurfacePackageReference[] items = group.ToArray();
            if (items.Length == 1)
            {
                SurfacePackageReference normalized = NormalizeStableKey(items[0], solutionPath, projectPath);
                RegisterStableKeyRemaps(items, normalized.StablePackageInstanceKey ?? string.Empty, stableKeyRemap);
                results.Add(normalized);
                continue;
            }

            SurfacePackageReference merged = MergeGroup(items, solutionPath, projectPath, mergeNetVersions);
            RegisterStableKeyRemaps(items, merged.StablePackageInstanceKey ?? string.Empty, stableKeyRemap);
            results.Add(merged);
        }

        return (results, stableKeyRemap);
    }

    public static SurfaceDependencyEdge RemapEdgeStableKeys(
        SurfaceDependencyEdge edge,
        IReadOnlyDictionary<string, string> remap)
    {
        return new SurfaceDependencyEdge
        {
            FromPackageId = edge.FromPackageId,
            ToPackageId = edge.ToPackageId,
            TargetFrameworkMoniker = edge.TargetFrameworkMoniker,
            FromStablePackageInstanceKey = RemapStablePackageInstanceKey(edge.FromStablePackageInstanceKey, remap),
            ToStablePackageInstanceKey = RemapStablePackageInstanceKey(edge.ToStablePackageInstanceKey, remap)
        };
    }

    public static string RemapStablePackageInstanceKey(string? key, IReadOnlyDictionary<string, string> remap)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        string lookupKey = key;
        if (remap.TryGetValue(lookupKey, out string? canonical))
        {
            return canonical ?? lookupKey;
        }

        return lookupKey;
    }

    /// <summary>
    /// Matches the core host stable package key format (solution, project, tfm, no RID, package id) so merged keys align with SQLite topology.
    /// </summary>
    private static string CreateStablePackageInstanceKey(string solutionPath, string projectPath, string? targetFramework, string packageId)
    {
        return $"{NormalizePathSegment(solutionPath)}|{NormalizePathSegment(projectPath)}|{NormalizePathSegment(targetFramework)}|NONE|{(packageId ?? string.Empty).ToUpperInvariant()}";
    }

    private static string NormalizePathSegment(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "NONE"
            : value.Replace('\\', '/').Trim().ToUpperInvariant();
    }

    /// <summary>
    /// Merges package rows across target frameworks and remaps edges (for dependency graph + merged table rows).
    /// </summary>
    public static SurfaceProjectSnapshot BuildMergedProjectSnapshot(SurfaceProjectSnapshot project)
    {
        (List<SurfacePackageReference> merged, Dictionary<string, string> remap) = MergeDuplicateRows(
            project.Packages.ToList(),
            project.SolutionPath ?? string.Empty,
            project.ProjectPath ?? string.Empty,
            mergeNetVersions: true);
        SurfaceDependencyEdge[] edges = RemapAndFilterDependencyEdges(project.DependencyEdges, merged, remap);
        return new SurfaceProjectSnapshot
        {
            SolutionPath = project.SolutionPath,
            ProjectName = project.ProjectName,
            ProjectPath = project.ProjectPath,
            TargetFrameworks = project.TargetFrameworks,
            Packages = merged,
            DependencyEdges = edges
        };
    }

    public static SurfaceDependencyEdge[] RemapAndFilterDependencyEdges(
        IReadOnlyList<SurfaceDependencyEdge> edges,
        IReadOnlyList<SurfacePackageReference> mergedPackages,
        IReadOnlyDictionary<string, string> stableKeyRemap)
    {
        if (edges.Count == 0)
        {
            return Array.Empty<SurfaceDependencyEdge>();
        }

        HashSet<string> validKeys = new(StringComparer.OrdinalIgnoreCase);
        foreach (SurfacePackageReference package in mergedPackages)
        {
            string? key = package.StablePackageInstanceKey;
            if (!string.IsNullOrWhiteSpace(key))
            {
                validKeys.Add(key!);
            }
        }

        List<SurfaceDependencyEdge> result = new();
        foreach (SurfaceDependencyEdge edge in edges)
        {
            string fromKey = RemapStablePackageInstanceKey(edge.FromStablePackageInstanceKey, stableKeyRemap);
            string toKey = RemapStablePackageInstanceKey(edge.ToStablePackageInstanceKey, stableKeyRemap);
            if (!validKeys.Contains(fromKey)
                || !validKeys.Contains(toKey))
            {
                continue;
            }

            result.Add(new SurfaceDependencyEdge
            {
                FromPackageId = edge.FromPackageId,
                ToPackageId = edge.ToPackageId,
                TargetFrameworkMoniker = edge.TargetFrameworkMoniker,
                FromStablePackageInstanceKey = fromKey,
                ToStablePackageInstanceKey = toKey
            });
        }

        return result.ToArray();
    }

    private static string BuildMergeGroupKey(SurfacePackageReference package, bool mergeNetVersions)
    {
        string tfmSegment = mergeNetVersions ? string.Empty : (package.TargetFrameworkMoniker ?? string.Empty);
        return string.Join(
            "\u001F",
            package.PackageId ?? string.Empty,
            package.ReferenceKind ?? string.Empty,
            tfmSegment,
            package.IsCentralVersionManaged ? "1" : "0",
            JoinSorted(package.DependencyParents),
            JoinSequence(package.DependencyPath),
            BuildHealthMergeKey(package.HealthInfo),
            package.RemediationSummary ?? string.Empty,
            package.RiskBand ?? string.Empty,
            package.AlertBand ?? string.Empty,
            FormatNullableDouble(package.RiskScore),
            FormatNullableDouble(package.AlertScore),
            FormatNullableUtc(package.KnowledgeDeterminedUtc),
            FormatNullableUtc(package.KnowledgeStatusChangedUtc));
    }

    private static string JoinSorted(IReadOnlyList<string>? items)
    {
        if (items is null || items.Count == 0)
        {
            return string.Empty;
        }

        return string.Join("\u001E", items.OrderBy(static item => item, StringComparer.OrdinalIgnoreCase));
    }

    private static string JoinSequence(IReadOnlyList<string>? items)
    {
        if (items is null || items.Count == 0)
        {
            return string.Empty;
        }

        return string.Join("\u001E", items);
    }

    private static string BuildHealthMergeKey(SurfacePackageHealth health)
    {
        string vulnerabilityPart = string.Join(
            "\u001D",
            health.Vulnerabilities
                .OrderBy(static v => (v.Severity ?? string.Empty) + "\u0001" + (v.AdvisoryUrl ?? string.Empty), StringComparer.OrdinalIgnoreCase)
                .Select(static v => $"{v.Severity ?? string.Empty}\u0001{v.AdvisoryUrl ?? string.Empty}"));
        return string.Join(
            "\u001F",
            health.IsDeprecated ? "1" : "0",
            health.IsObsolete ? "1" : "0",
            health.IsOutdated ? "1" : "0",
            health.IsVulnerable ? "1" : "0",
            health.DevelopmentStatus ?? string.Empty,
            health.LatestStableVersion ?? string.Empty,
            health.DeprecationMessage ?? string.Empty,
            health.AlternatePackageId ?? string.Empty,
            health.AlternatePackageRange ?? string.Empty,
            health.MaxVulnerabilitySeverity ?? string.Empty,
            vulnerabilityPart);
    }

    private static string FormatNullableDouble(double? value)
    {
        return value.HasValue ? value.Value.ToString("G17", CultureInfo.InvariantCulture) : string.Empty;
    }

    private static string FormatNullableUtc(DateTimeOffset? value)
    {
        return value.HasValue ? value.Value.ToString("O", CultureInfo.InvariantCulture) : string.Empty;
    }

    private static void RegisterStableKeyRemaps(
        SurfacePackageReference[] items,
        string canonicalKey,
        Dictionary<string, string> remap)
    {
        if (string.IsNullOrWhiteSpace(canonicalKey))
        {
            return;
        }

        foreach (SurfacePackageReference item in items)
        {
            string? fromKey = item.StablePackageInstanceKey;
            if (!string.IsNullOrWhiteSpace(fromKey))
            {
                remap[fromKey!] = canonicalKey;
            }
        }

        remap[canonicalKey] = canonicalKey;
    }

    private static SurfacePackageReference NormalizeStableKey(
        SurfacePackageReference package,
        string solutionPath,
        string projectPath)
    {
        string expectedKey = CreateStablePackageInstanceKey(
            solutionPath,
            projectPath,
            package.TargetFrameworkMoniker,
            package.PackageId ?? string.Empty);
        if (string.Equals(package.StablePackageInstanceKey, expectedKey, StringComparison.OrdinalIgnoreCase))
        {
            return package;
        }

        return CloneWithStableKey(package, expectedKey);
    }

    private static SurfacePackageReference MergeGroup(
        SurfacePackageReference[] items,
        string solutionPath,
        string projectPath,
        bool mergeNetVersions)
    {
        SurfacePackageReference canonical = PickCanonicalRow(items);
        string? requested = MergeVersionValues(items.Select(static item => item.RequestedVersion));
        string? resolved = MergeVersionValues(items.Select(static item => item.ResolvedVersion));
        string? mergedTfm = mergeNetVersions
            ? MergeVersionValues(items.Select(static item => item.TargetFrameworkMoniker))
            : canonical.TargetFrameworkMoniker;
        bool centralManaged = items.Any(static item => item.IsCentralVersionManaged);
        string instanceKey = CreateStablePackageInstanceKey(
            solutionPath,
            projectPath,
            mergedTfm,
            canonical.PackageId ?? string.Empty);

        return new SurfacePackageReference
        {
            PackageId = canonical.PackageId,
            RequestedVersion = requested,
            ResolvedVersion = resolved,
            ReferenceKind = canonical.ReferenceKind,
            IsCentralVersionManaged = centralManaged,
            TargetFrameworkMoniker = mergedTfm,
            DependencyParents = canonical.DependencyParents,
            DependencyPath = canonical.DependencyPath,
            HealthInfo = canonical.HealthInfo,
            StablePackageInstanceKey = instanceKey,
            KnowledgeDeterminedUtc = MaxNullableUtc(items.Select(static item => item.KnowledgeDeterminedUtc)),
            KnowledgeStatusChangedUtc = MaxNullableUtc(items.Select(static item => item.KnowledgeStatusChangedUtc)),
            RemediationSummary = items.Select(static item => item.RemediationSummary).FirstOrDefault(static text => !string.IsNullOrWhiteSpace(text)),
            RiskScore = canonical.RiskScore ?? items.FirstOrDefault(static item => item.RiskScore.HasValue)?.RiskScore,
            AlertScore = canonical.AlertScore ?? items.FirstOrDefault(static item => item.AlertScore.HasValue)?.AlertScore,
            RiskBand = canonical.RiskBand ?? items.FirstOrDefault(static item => !string.IsNullOrWhiteSpace(item.RiskBand))?.RiskBand,
            AlertBand = canonical.AlertBand ?? items.FirstOrDefault(static item => !string.IsNullOrWhiteSpace(item.AlertBand))?.AlertBand
        };
    }

    private static SurfacePackageReference PickCanonicalRow(SurfacePackageReference[] items)
    {
        return items
            .OrderByDescending(static item => item.KnowledgeDeterminedUtc ?? DateTimeOffset.MinValue)
            .ThenByDescending(VersionCompletenessScore)
            .First();
    }

    private static int VersionCompletenessScore(SurfacePackageReference item)
    {
        int score = 0;
        if (!string.IsNullOrWhiteSpace(item.RequestedVersion))
        {
            score += 1;
        }

        if (!string.IsNullOrWhiteSpace(item.ResolvedVersion))
        {
            score += 2;
        }

        return score;
    }

    private static string? MergeVersionValues(IEnumerable<string?> values)
    {
        string[] distinct = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return distinct.Length switch
        {
            0 => null,
            1 => distinct[0],
            _ => string.Join(" / ", distinct)
        };
    }

    private static DateTimeOffset? MaxNullableUtc(IEnumerable<DateTimeOffset?> values)
    {
        DateTimeOffset? best = null;
        foreach (DateTimeOffset? value in values)
        {
            if (value is DateTimeOffset utc && (!best.HasValue || utc > best.Value))
            {
                best = utc;
            }
        }

        return best;
    }

    private static SurfacePackageReference CloneWithStableKey(SurfacePackageReference package, string stableKey)
    {
        return new SurfacePackageReference
        {
            PackageId = package.PackageId,
            RequestedVersion = package.RequestedVersion,
            ResolvedVersion = package.ResolvedVersion,
            ReferenceKind = package.ReferenceKind,
            IsCentralVersionManaged = package.IsCentralVersionManaged,
            TargetFrameworkMoniker = package.TargetFrameworkMoniker,
            DependencyParents = package.DependencyParents,
            DependencyPath = package.DependencyPath,
            HealthInfo = package.HealthInfo,
            StablePackageInstanceKey = stableKey,
            KnowledgeDeterminedUtc = package.KnowledgeDeterminedUtc,
            KnowledgeStatusChangedUtc = package.KnowledgeStatusChangedUtc,
            RemediationSummary = package.RemediationSummary,
            RiskScore = package.RiskScore,
            AlertScore = package.AlertScore,
            RiskBand = package.RiskBand,
            AlertBand = package.AlertBand
        };
    }
}
