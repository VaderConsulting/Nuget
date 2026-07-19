using NuGet.Common;
using NuGet.LibraryModel;
using NuGet.Packaging.Core;
using NuGet.ProjectModel;

namespace NuGetAudit.Core;

/// <summary>
/// Reads NuGet <c>packages.lock.json</c> using <see cref="PackagesLockFileFormat"/> to complement or substitute <c>project.assets.json</c>.
/// </summary>
internal static class PackagesLockFileParser
{
    /// <summary>
    /// Parses <c>packages.lock.json</c> into package records and dependency edges aligned with assets-file analysis.
    /// </summary>
    /// <param name="lockFilePath">The path to <c>packages.lock.json</c>.</param>
    /// <param name="projectPath">The project path for stable keys.</param>
    /// <param name="solutionPath">The solution path for stable keys.</param>
    /// <param name="directPackages">Direct package records already discovered from the project.</param>
    /// <returns>Packages, edges, and warnings.</returns>
    public static ProjectAssetsResult Parse(string lockFilePath, string projectPath, string solutionPath, IReadOnlyList<PackageReferenceRecord> directPackages)
    {
        List<PackageReferenceRecord> Packages = new();
        List<DependencyEdgeRecord> DependencyEdges = new();
        List<string> Warnings = new();

        try
        {
            PackagesLockFile File = PackagesLockFileFormat.Read(lockFilePath, NullLogger.Instance);
            if (File.Targets is null || File.Targets.Count == 0)
            {
                Warnings.Add("packages.lock.json contains no targets.");
                return new ProjectAssetsResult(Packages, DependencyEdges, Warnings);
            }

            foreach (PackagesLockFileTarget Target in File.Targets)
            {
                string TargetFramework = ResolveTargetFrameworkMoniker(Target);
                Dictionary<string, string[]> DependencyLookup = new(StringComparer.OrdinalIgnoreCase);
                HashSet<string> DirectIds = new(StringComparer.OrdinalIgnoreCase);

                foreach (LockFileDependency Dependency in Target.Dependencies)
                {
                    if (string.IsNullOrWhiteSpace(Dependency.Id))
                    {
                        continue;
                    }

                    if (Dependency.Type == PackageDependencyType.Direct)
                    {
                        DirectIds.Add(Dependency.Id);
                    }

                    List<string> ChildIds = new();
                    foreach (PackageDependency Child in Dependency.Dependencies)
                    {
                        if (!string.IsNullOrWhiteSpace(Child.Id))
                        {
                            ChildIds.Add(Child.Id);
                        }
                    }

                    DependencyLookup[Dependency.Id] = ChildIds.ToArray();
                }

                foreach (PackageReferenceRecord Direct in directPackages)
                {
                    if (string.IsNullOrWhiteSpace(Direct.TargetFrameworkMoniker)
                        || string.Equals(Direct.TargetFrameworkMoniker, TargetFramework, StringComparison.OrdinalIgnoreCase))
                    {
                        DirectIds.Add(Direct.PackageId);
                    }
                }

                Dictionary<string, string> StableKeysByPackageId = new(StringComparer.OrdinalIgnoreCase);

                foreach (LockFileDependency Dependency in Target.Dependencies)
                {
                    if (string.IsNullOrWhiteSpace(Dependency.Id))
                    {
                        continue;
                    }

                    string PackageId = Dependency.Id;
                    string ResolvedVersion = Dependency.ResolvedVersion?.ToString() ?? string.Empty;
                    PackageReferenceKind ReferenceKind = Dependency.Type == PackageDependencyType.Direct
                        ? PackageReferenceKind.Direct
                        : PackageReferenceKind.Transitive;

                    List<string> Parents = DependencyLookup
                        .Where(entry => entry.Value.Contains(PackageId, StringComparer.OrdinalIgnoreCase))
                        .Select(static entry => entry.Key)
                        .OrderBy(static package => package, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    List<string> DependencyPath = BuildPath(PackageId, Parents, DependencyLookup, DirectIds);

                    Packages.Add(new PackageReferenceRecord(PackageId, FindRequestedVersion(directPackages, PackageId, TargetFramework) ?? Dependency.RequestedVersion?.ToString(), ResolvedVersion, ReferenceKind, IsCentralVersionManaged(directPackages, PackageId, TargetFramework), TargetFramework, Parents, DependencyPath, PackageHealthInfo.None, StableKey.Create(solutionPath, projectPath, TargetFramework, null, PackageId)));

                    StableKeysByPackageId[PackageId] = StableKey.Create(solutionPath, projectPath, TargetFramework, null, PackageId);
                }

                foreach (KeyValuePair<string, string[]> Entry in DependencyLookup)
                {
                    string ParentPackageId = Entry.Key;
                    if (!StableKeysByPackageId.TryGetValue(ParentPackageId, out string? ParentStableKey))
                    {
                        continue;
                    }

                    foreach (string DependencyPackageId in Entry.Value.OrderBy(static dependency => dependency, StringComparer.OrdinalIgnoreCase))
                    {
                        if (!StableKeysByPackageId.TryGetValue(DependencyPackageId, out string? DependencyStableKey))
                        {
                            continue;
                        }

                        DependencyEdges.Add(new DependencyEdgeRecord(ParentPackageId, DependencyPackageId, TargetFramework, ParentStableKey, DependencyStableKey));
                    }
                }
            }

            return new ProjectAssetsResult(Packages
                    .GroupBy(static package => package.StablePackageInstanceKey, StringComparer.OrdinalIgnoreCase)
                    .Select(static group => group.First())
                    .ToArray(),
                DependencyEdges
                    .Distinct()
                    .OrderBy(static edge => edge.FromPackageId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static edge => edge.ToPackageId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static edge => edge.TargetFrameworkMoniker, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                Warnings);
        }
        catch (Exception Exception)
        {
            Warnings.Add($"packages.lock.json could not be read '{lockFilePath}': {Exception.GetType().Name}: {Exception.Message}");
            return new ProjectAssetsResult(Array.Empty<PackageReferenceRecord>(), Array.Empty<DependencyEdgeRecord>(), Warnings);
        }
    }

    private static string ResolveTargetFrameworkMoniker(PackagesLockFileTarget Target)
    {
        if (Target.TargetFramework is { } Framework && Framework.IsSpecificFramework)
        {
            return Framework.GetShortFolderName();
        }

        if (!string.IsNullOrWhiteSpace(Target.Name))
        {
            int Slash = Target.Name.IndexOf('/');
            if (Slash >= 0)
            {
                return Target.Name[..Slash];
            }

            return Target.Name;
        }

        return string.Empty;
    }

    private static string? FindRequestedVersion(IReadOnlyList<PackageReferenceRecord> directPackages, string packageId, string targetFramework)
    {
        return directPackages.FirstOrDefault(Existing =>
                   Existing.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(Existing.TargetFrameworkMoniker, targetFramework, StringComparison.OrdinalIgnoreCase))?.RequestedVersion
               ?? directPackages.FirstOrDefault(Existing =>
                   Existing.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase)
                   && string.IsNullOrWhiteSpace(Existing.TargetFrameworkMoniker))?.RequestedVersion;
    }

    private static bool IsCentralVersionManaged(IReadOnlyList<PackageReferenceRecord> directPackages, string packageId, string targetFramework)
    {
        return directPackages.Any(Existing =>
                   Existing.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(Existing.TargetFrameworkMoniker, targetFramework, StringComparison.OrdinalIgnoreCase)
                   && Existing.IsCentralVersionManaged)
               || directPackages.Any(Existing =>
                   Existing.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase)
                   && string.IsNullOrWhiteSpace(Existing.TargetFrameworkMoniker)
                   && Existing.IsCentralVersionManaged);
    }

    private static List<string> BuildPath(string packageId, IReadOnlyList<string> parents, IReadOnlyDictionary<string, string[]> dependencyLookup, IReadOnlySet<string> directIds)
    {
        if (directIds.Contains(packageId) || parents.Count == 0)
        {
            return new List<string> { packageId };
        }

        string Current = parents[0];
        Stack<string> Stack = new();
        Stack.Push(packageId);

        while (true)
        {
            Stack.Push(Current);

            if (directIds.Contains(Current))
            {
                return Stack.Reverse().ToList();
            }

            string? Next = dependencyLookup
                .Where(Entry => Entry.Value.Contains(Current, StringComparer.OrdinalIgnoreCase))
                .Select(static entry => entry.Key)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(Next))
            {
                return Stack.Reverse().ToList();
            }

            Current = Next;
        }
    }
}
