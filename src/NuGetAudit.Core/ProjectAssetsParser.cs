using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Newtonsoft.Json.Linq;

namespace NuGetAudit.Core;

/// <summary>
/// Parses <c>project.assets.json</c> into package records and edges, complementing <see cref="PackagesLockFileParser"/> and project declarations.
/// </summary>
internal static class ProjectAssetsParser
{
    /// <summary>
    /// Parses <c>project.assets.json</c> using three capability levels: standard <see cref="System.Text.Json"/>,
    /// first fallback (Newtonsoft.Json round-trip for text STJ rejects), then second fallback (<see cref="ProjectAssetsNuGetProjectModelParser"/>).
    /// </summary>
    /// <param name="assetsFilePath">The assets file path.</param>
    /// <param name="projectPath">The project path for stable key generation.</param>
    /// <param name="solutionPath">The solution path for stable key generation.</param>
    /// <param name="directPackages">The direct package records already parsed from project declarations.</param>
    /// <returns>The parsed assets result, including fallback warnings when a tier beyond standard was required.</returns>
    public static ProjectAssetsResult ParseWithCapabilityFallbacks(string assetsFilePath, string projectPath, string solutionPath, IReadOnlyList<PackageReferenceRecord> directPackages)
    {
        string JsonText = File.ReadAllText(assetsFilePath);
        List<string> ChainWarnings = new();

        if (TryParseStandard(JsonText, projectPath, solutionPath, directPackages, out ProjectAssetsResult? StandardResult))
        {
            if (ResultHasGraph(StandardResult))
            {
                return StandardResult;
            }

            ChainWarnings.AddRange(StandardResult.Warnings);
        }
        else
        {
            ChainWarnings.Add(
                $"project.assets.json standard read (System.Text.Json) did not produce a usable document or graph for '{assetsFilePath}'.");
        }

        if (TryParseNewtonsoftRoundTrip(JsonText, projectPath, solutionPath, directPackages, out ProjectAssetsResult? NewtonsoftResult))
        {
            if (ResultHasGraph(NewtonsoftResult))
            {
                List<string> Merged = new();
                Merged.AddRange(ChainWarnings);
                Merged.Add(
                    "Resolved package graph was read using the first fallback (Newtonsoft.Json round-trip) after standard JSON handling did not yield a graph.");
                Merged.AddRange(NewtonsoftResult.Warnings);
                return new ProjectAssetsResult(NewtonsoftResult.Packages, NewtonsoftResult.DependencyEdges, Merged);
            }

            ChainWarnings.AddRange(NewtonsoftResult.Warnings);
        }
        else
        {
            ChainWarnings.Add("project.assets.json first fallback (Newtonsoft.Json round-trip) could not produce a usable document or graph.");
        }

        ProjectAssetsResult ProjectModelResult = ProjectAssetsNuGetProjectModelParser.Parse(assetsFilePath, projectPath, solutionPath, directPackages);

        if (ResultHasGraph(ProjectModelResult))
        {
            List<string> Merged = new();
            Merged.AddRange(ChainWarnings);
            Merged.Add(
                "Resolved package graph was read using the second fallback (NuGet.ProjectModel) after standard JSON and the first fallback did not yield a graph.");
            Merged.AddRange(ProjectModelResult.Warnings);
            return new ProjectAssetsResult(ProjectModelResult.Packages, ProjectModelResult.DependencyEdges, Merged);
        }

        ChainWarnings.AddRange(ProjectModelResult.Warnings);
        return new ProjectAssetsResult(Array.Empty<PackageReferenceRecord>(), Array.Empty<DependencyEdgeRecord>(), ChainWarnings);
    }

    private static bool ResultHasGraph(ProjectAssetsResult? Result)
    {
        if (Result is null)
        {
            return false;
        }

        return Result.Packages.Count > 0 || Result.DependencyEdges.Count > 0;
    }

    private static bool TryParseStandard(string jsonText, string projectPath, string solutionPath, IReadOnlyList<PackageReferenceRecord> directPackages, [NotNullWhen(true)] out ProjectAssetsResult? Result)
    {
        Result = null;
        try
        {
            using JsonDocument Document = JsonDocument.Parse(jsonText);
            Result = ParseCore(Document, projectPath, solutionPath, directPackages);
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static bool TryParseNewtonsoftRoundTrip(string jsonText, string projectPath, string solutionPath, IReadOnlyList<PackageReferenceRecord> directPackages, [NotNullWhen(true)] out ProjectAssetsResult? Result)
    {
        Result = null;
        try
        {
            JObject Root = JObject.Parse(jsonText);
            string Canonical = Root.ToString(Newtonsoft.Json.Formatting.None);
            using JsonDocument Document = JsonDocument.Parse(Canonical);
            Result = ParseCore(Document, projectPath, solutionPath, directPackages);
            return true;
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return false;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Parses resolved package information from a restore assets file.
    /// </summary>
    /// <param name="assetsFilePath">The assets file path.</param>
    /// <param name="projectPath">The project path for stable key generation.</param>
    /// <param name="solutionPath">The solution path for stable key generation.</param>
    /// <param name="directPackages">The direct package records already parsed from project declarations.</param>
    /// <returns>The parsed assets result.</returns>
    public static ProjectAssetsResult Parse(string assetsFilePath, string projectPath, string solutionPath, IReadOnlyList<PackageReferenceRecord> directPackages)
    {
        using JsonDocument Document = JsonDocument.Parse(File.ReadAllText(assetsFilePath));
        return ParseCore(Document, projectPath, solutionPath, directPackages);
    }

    private static ProjectAssetsResult ParseCore(JsonDocument document, string projectPath, string solutionPath, IReadOnlyList<PackageReferenceRecord> directPackages)
    {
        List<PackageReferenceRecord> packages = new();
        List<DependencyEdgeRecord> dependencyEdges = new();
        List<string> warnings = new();
        Dictionary<string, HashSet<string>> directPackageIdsByTargetFramework = GetDirectPackageIdsByTargetFramework(document, directPackages);

        if (!document.RootElement.TryGetProperty("targets", out JsonElement targetsElement))
        {
            warnings.Add("project.assets.json does not contain a targets section.");
            return new ProjectAssetsResult(packages, dependencyEdges, warnings);
        }

        foreach (JsonProperty target in targetsElement.EnumerateObject())
        {
            string targetFramework = target.Name.Split('/', 2)[0];
            IReadOnlySet<string> directIds = GetDirectPackageIdsForTargetFramework(targetFramework, directPackageIdsByTargetFramework);
            Dictionary<string, string[]> dependencyLookup = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> stableKeysByPackageId = new(StringComparer.OrdinalIgnoreCase);

            foreach (JsonProperty library in target.Value.EnumerateObject())
            {
                string[] packageParts = library.Name.Split('/');
                if (packageParts.Length != 2)
                {
                    continue;
                }

                string packageId = packageParts[0];
                string[] dependencies = Array.Empty<string>();

                if (library.Value.TryGetProperty("dependencies", out JsonElement dependenciesElement))
                {
                    dependencies = dependenciesElement.EnumerateObject().Select(static property => property.Name).ToArray();
                }

                dependencyLookup[packageId] = dependencies;
            }

            foreach (JsonProperty library in target.Value.EnumerateObject())
            {
                string[] packageParts = library.Name.Split('/');
                if (packageParts.Length != 2)
                {
                    continue;
                }

                string packageId = packageParts[0];
                string resolvedVersion = packageParts[1];
                PackageReferenceKind referenceKind = directIds.Contains(packageId)
                    ? PackageReferenceKind.Direct
                    : PackageReferenceKind.Transitive;

                List<string> parents = dependencyLookup
                    .Where(entry => entry.Value.Contains(packageId, StringComparer.OrdinalIgnoreCase))
                    .Select(static entry => entry.Key)
                    .OrderBy(static package => package, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                List<string> dependencyPath = BuildPath(packageId, parents, dependencyLookup, directIds);

                packages.Add(new PackageReferenceRecord(packageId, FindRequestedVersion(directPackages, packageId, targetFramework), resolvedVersion, referenceKind, IsCentralVersionManaged(directPackages, packageId, targetFramework), targetFramework, parents, dependencyPath, PackageHealthInfo.None, StableKey.Create(solutionPath, projectPath, targetFramework, null, packageId)));

                stableKeysByPackageId[packageId] = StableKey.Create(solutionPath, projectPath, targetFramework, null, packageId);
            }

            foreach ((string parentPackageId, string[] dependencies) in dependencyLookup)
            {
                if (!stableKeysByPackageId.TryGetValue(parentPackageId, out string? parentStableKey))
                {
                    continue;
                }

                foreach (string dependencyPackageId in dependencies.OrderBy(static dependency => dependency, StringComparer.OrdinalIgnoreCase))
                {
                    if (!stableKeysByPackageId.TryGetValue(dependencyPackageId, out string? dependencyStableKey))
                    {
                        continue;
                    }

                    dependencyEdges.Add(new DependencyEdgeRecord(parentPackageId, dependencyPackageId, targetFramework, parentStableKey, dependencyStableKey));
                }
            }
        }

        return new ProjectAssetsResult(packages
                .GroupBy(static package => package.StablePackageInstanceKey, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .ToArray(),
            dependencyEdges
                .Distinct()
                .OrderBy(static edge => edge.FromPackageId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static edge => edge.ToPackageId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static edge => edge.TargetFrameworkMoniker, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            warnings);
    }

    /// <summary>
    /// Reads direct package declarations per target framework from the assets file, with a fallback to parsed project declarations.
    /// </summary>
    /// <param name="document">The loaded assets document.</param>
    /// <param name="directPackages">The project-declared direct packages.</param>
    /// <returns>A map of target frameworks to direct package identifiers.</returns>
    private static Dictionary<string, HashSet<string>> GetDirectPackageIdsByTargetFramework(JsonDocument document, IReadOnlyList<PackageReferenceRecord> directPackages)
    {
        Dictionary<string, HashSet<string>> directPackageIdsByTargetFramework = new(StringComparer.OrdinalIgnoreCase);

        if (document.RootElement.TryGetProperty("project", out JsonElement projectElement)
            && projectElement.TryGetProperty("frameworks", out JsonElement frameworksElement))
        {
            foreach (JsonProperty framework in frameworksElement.EnumerateObject())
            {
                HashSet<string> directIds = new(StringComparer.OrdinalIgnoreCase);

                if (framework.Value.TryGetProperty("dependencies", out JsonElement dependenciesElement))
                {
                    foreach (JsonProperty dependency in dependenciesElement.EnumerateObject())
                    {
                        directIds.Add(dependency.Name);
                    }
                }

                directPackageIdsByTargetFramework[framework.Name] = directIds;
            }
        }

        foreach (IGrouping<string?, PackageReferenceRecord> packageGroup in directPackages.GroupBy(static package => package.TargetFrameworkMoniker, StringComparer.OrdinalIgnoreCase))
        {
            string targetFramework = packageGroup.Key ?? string.Empty;

            if (!directPackageIdsByTargetFramework.TryGetValue(targetFramework, out HashSet<string>? directIds))
            {
                directIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                directPackageIdsByTargetFramework[targetFramework] = directIds;
            }

            foreach (PackageReferenceRecord package in packageGroup)
            {
                directIds.Add(package.PackageId);
            }
        }

        return directPackageIdsByTargetFramework;
    }

    /// <summary>
    /// Gets the direct package IDs that apply to the supplied target framework.
    /// </summary>
    /// <param name="targetFramework">The target framework from the assets file target section.</param>
    /// <param name="directPackageIdsByTargetFramework">The discovered direct package mapping.</param>
    /// <returns>The applicable direct package IDs.</returns>
    private static IReadOnlySet<string> GetDirectPackageIdsForTargetFramework(string targetFramework, IReadOnlyDictionary<string, HashSet<string>> directPackageIdsByTargetFramework)
    {
        if (directPackageIdsByTargetFramework.TryGetValue(targetFramework, out HashSet<string>? exactMatch))
        {
            return exactMatch;
        }

        if (directPackageIdsByTargetFramework.TryGetValue(string.Empty, out HashSet<string>? unscopedMatch))
        {
            return unscopedMatch;
        }

        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Finds the requested version that best matches the package and target framework.
    /// </summary>
    /// <param name="directPackages">The direct package records parsed from the project.</param>
    /// <param name="packageId">The package identifier.</param>
    /// <param name="targetFramework">The target framework scope.</param>
    /// <returns>The requested version, if available.</returns>
    private static string? FindRequestedVersion(IReadOnlyList<PackageReferenceRecord> directPackages, string packageId, string targetFramework)
    {
        return directPackages.FirstOrDefault(existing =>
                   existing.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(existing.TargetFrameworkMoniker, targetFramework, StringComparison.OrdinalIgnoreCase))?.RequestedVersion
               ?? directPackages.FirstOrDefault(existing =>
                   existing.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase)
                   && string.IsNullOrWhiteSpace(existing.TargetFrameworkMoniker))?.RequestedVersion;
    }

    /// <summary>
    /// Determines whether a package is centrally managed for the supplied target framework.
    /// </summary>
    /// <param name="directPackages">The direct package records parsed from the project.</param>
    /// <param name="packageId">The package identifier.</param>
    /// <param name="targetFramework">The target framework scope.</param>
    /// <returns><see langword="true"/> when the package is centrally managed; otherwise, <see langword="false"/>.</returns>
    private static bool IsCentralVersionManaged(IReadOnlyList<PackageReferenceRecord> directPackages, string packageId, string targetFramework)
    {
        return directPackages.Any(existing =>
                   existing.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(existing.TargetFrameworkMoniker, targetFramework, StringComparison.OrdinalIgnoreCase)
                   && existing.IsCentralVersionManaged)
               || directPackages.Any(existing =>
                   existing.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase)
                   && string.IsNullOrWhiteSpace(existing.TargetFrameworkMoniker)
                   && existing.IsCentralVersionManaged);
    }

    /// <summary>
    /// Builds one dependency path for a resolved package using the discovered parent relationships.
    /// </summary>
    /// <param name="packageId">The package to resolve.</param>
    /// <param name="parents">The package's immediate parent packages.</param>
    /// <param name="dependencyLookup">The dependency lookup built from the assets file.</param>
    /// <param name="directIds">The direct package identifiers.</param>
    /// <returns>A dependency path ordered from ancestor to package.</returns>
    private static List<string> BuildPath(string packageId, IReadOnlyList<string> parents, IReadOnlyDictionary<string, string[]> dependencyLookup, IReadOnlySet<string> directIds)
    {
        if (directIds.Contains(packageId) || parents.Count == 0)
        {
            return new List<string> { packageId };
        }

        string current = parents[0];
        Stack<string> stack = new();
        stack.Push(packageId);

        while (true)
        {
            stack.Push(current);

            if (directIds.Contains(current))
            {
                return stack.Reverse().ToList();
            }

            string? next = dependencyLookup
                .Where(entry => entry.Value.Contains(current, StringComparer.OrdinalIgnoreCase))
                .Select(static entry => entry.Key)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(next))
            {
                return stack.Reverse().ToList();
            }

            current = next;
        }
    }
}
