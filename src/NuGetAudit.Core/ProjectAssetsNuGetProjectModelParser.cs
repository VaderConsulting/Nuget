using NuGet.Common;
using NuGet.LibraryModel;
using NuGet.Packaging.Core;
using NuGet.ProjectModel;

namespace NuGetAudit.Core;

/// <summary>
/// Parses <c>project.assets.json</c> using NuGet.ProjectModel as the second fallback when standard JSON and the first fallback do not yield a graph.
/// </summary>
internal static class ProjectAssetsNuGetProjectModelParser
{
    /// <summary>
    /// Builds package and dependency-edge records from a lock file, mirroring <see cref="ProjectAssetsParser"/> output shape.
    /// </summary>
    /// <param name="AssetsFilePath">The assets file path.</param>
    /// <param name="ProjectPath">The project path for stable key generation.</param>
    /// <param name="SolutionPath">The solution path for stable key generation.</param>
    /// <param name="DirectPackages">The direct package records already parsed from project declarations.</param>
    /// <returns>The parsed assets result.</returns>
    public static ProjectAssetsResult Parse(string AssetsFilePath, string ProjectPath, string SolutionPath, IReadOnlyList<PackageReferenceRecord> DirectPackages)
    {
        List<PackageReferenceRecord> GraphPackages = new();
        List<DependencyEdgeRecord> DependencyEdgeList = new();
        List<string> WarningList = new();

        try
        {
            LockFile RestoredLockFile = new LockFileFormat().Read(AssetsFilePath, NullLogger.Instance);

            if (RestoredLockFile.Targets is null || RestoredLockFile.Targets.Count == 0)
            {
                WarningList.Add("NuGet.ProjectModel: lock file has no targets.");
                return new ProjectAssetsResult(GraphPackages, DependencyEdgeList, WarningList);
            }

            foreach (LockFileTarget Target in RestoredLockFile.Targets)
            {
                string TargetFramework = ExtractTargetFrameworkMoniker(Target.Name);
                IReadOnlySet<string> DirectIds = GetDirectPackageIds(RestoredLockFile, Target.Name, DirectPackages);

                Dictionary<string, string[]> DependencyLookup = new(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, string> StableKeysByPackageId = new(StringComparer.OrdinalIgnoreCase);

                foreach (LockFileTargetLibrary Library in Target.Libraries)
                {
                    if (!string.Equals(Library.Type, "package", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string? PackageId = Library.Name;
                    if (string.IsNullOrWhiteSpace(PackageId))
                    {
                        continue;
                    }

                    List<string> DependencyIds = new();
                    foreach (PackageDependency Dependency in Library.Dependencies)
                    {
                        if (!string.IsNullOrWhiteSpace(Dependency.Id))
                        {
                            DependencyIds.Add(Dependency.Id);
                        }
                    }

                    DependencyLookup[PackageId] = DependencyIds.ToArray();
                }

                foreach (LockFileTargetLibrary Library in Target.Libraries)
                {
                    if (!string.Equals(Library.Type, "package", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string? PackageId = Library.Name;
                    if (string.IsNullOrWhiteSpace(PackageId))
                    {
                        continue;
                    }

                    string ResolvedVersion = Library.Version?.ToString() ?? string.Empty;
                    PackageReferenceKind ReferenceKind = DirectIds.Contains(PackageId)
                        ? PackageReferenceKind.Direct
                        : PackageReferenceKind.Transitive;

                    List<string> Parents = DependencyLookup
                        .Where(entry => entry.Value.Contains(PackageId, StringComparer.OrdinalIgnoreCase))
                        .Select(static entry => entry.Key)
                        .OrderBy(static package => package, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    List<string> DependencyPath = BuildPath(PackageId, Parents, DependencyLookup, DirectIds);

                    GraphPackages.Add(new PackageReferenceRecord(PackageId, FindRequestedVersion(DirectPackages, PackageId, TargetFramework), ResolvedVersion, ReferenceKind, IsCentralVersionManaged(DirectPackages, PackageId, TargetFramework), TargetFramework, Parents, DependencyPath, PackageHealthInfo.None, StableKey.Create(SolutionPath, ProjectPath, TargetFramework, null, PackageId)));

                    StableKeysByPackageId[PackageId] = StableKey.Create(SolutionPath, ProjectPath, TargetFramework, null, PackageId);
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

                        DependencyEdgeList.Add(new DependencyEdgeRecord(ParentPackageId, DependencyPackageId, TargetFramework, ParentStableKey, DependencyStableKey));
                    }
                }
            }

            return new ProjectAssetsResult(GraphPackages
                    .GroupBy(static package => package.StablePackageInstanceKey, StringComparer.OrdinalIgnoreCase)
                    .Select(static group => group.First())
                    .ToArray(),
                DependencyEdgeList
                    .Distinct()
                    .OrderBy(static edge => edge.FromPackageId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static edge => edge.ToPackageId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static edge => edge.TargetFrameworkMoniker, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                WarningList);
        }
        catch (Exception ProjectModelException)
        {
            WarningList.Add($"NuGet.ProjectModel could not read '{AssetsFilePath}': {ProjectModelException.GetType().Name}: {ProjectModelException.Message}");
            return new ProjectAssetsResult(Array.Empty<PackageReferenceRecord>(), Array.Empty<DependencyEdgeRecord>(), WarningList);
        }
    }

    private static string ExtractTargetFrameworkMoniker(string TargetName)
    {
        if (string.IsNullOrWhiteSpace(TargetName))
        {
            return string.Empty;
        }

        int Slash = TargetName.IndexOf('/');
        if (Slash >= 0)
        {
            return TargetName[..Slash];
        }

        return TargetName;
    }

    private static IReadOnlySet<string> GetDirectPackageIds(LockFile AssetsLockFile, string TargetName, IReadOnlyList<PackageReferenceRecord> DirectPackages)
    {
        HashSet<string> DirectIds = new(StringComparer.OrdinalIgnoreCase);

        if (AssetsLockFile.PackageSpec?.TargetFrameworks is not null)
        {
            foreach (TargetFrameworkInformation TfmInfo in AssetsLockFile.PackageSpec.TargetFrameworks)
            {
                string ShortFolder = TfmInfo.FrameworkName.GetShortFolderName();
                if (!TargetNameMatchesFramework(TargetName, ShortFolder))
                {
                    continue;
                }

                foreach (LibraryDependency Dependency in TfmInfo.Dependencies)
                {
                    if (Dependency.LibraryRange is not null && !string.IsNullOrWhiteSpace(Dependency.LibraryRange.Name))
                    {
                        DirectIds.Add(Dependency.LibraryRange.Name);
                    }
                }
            }
        }

        string TfmPrefix = ExtractTargetFrameworkMoniker(TargetName);
        foreach (PackageReferenceRecord Package in DirectPackages)
        {
            if (string.IsNullOrWhiteSpace(Package.TargetFrameworkMoniker))
            {
                DirectIds.Add(Package.PackageId);
                continue;
            }

            if (string.Equals(Package.TargetFrameworkMoniker, TfmPrefix, StringComparison.OrdinalIgnoreCase))
            {
                DirectIds.Add(Package.PackageId);
            }
        }

        return DirectIds;
    }

    private static bool TargetNameMatchesFramework(string TargetName, string ShortFolderName)
    {
        if (string.IsNullOrWhiteSpace(ShortFolderName))
        {
            return false;
        }

        string Moniker = ExtractTargetFrameworkMoniker(TargetName);
        return string.Equals(Moniker, ShortFolderName, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindRequestedVersion(IReadOnlyList<PackageReferenceRecord> DirectPackages, string PackageId, string TargetFramework)
    {
        return DirectPackages.FirstOrDefault(Existing =>
                   Existing.PackageId.Equals(PackageId, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(Existing.TargetFrameworkMoniker, TargetFramework, StringComparison.OrdinalIgnoreCase))?.RequestedVersion
               ?? DirectPackages.FirstOrDefault(Existing =>
                   Existing.PackageId.Equals(PackageId, StringComparison.OrdinalIgnoreCase)
                   && string.IsNullOrWhiteSpace(Existing.TargetFrameworkMoniker))?.RequestedVersion;
    }

    private static bool IsCentralVersionManaged(IReadOnlyList<PackageReferenceRecord> DirectPackages, string PackageId, string TargetFramework)
    {
        return DirectPackages.Any(Existing =>
                   Existing.PackageId.Equals(PackageId, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(Existing.TargetFrameworkMoniker, TargetFramework, StringComparison.OrdinalIgnoreCase)
                   && Existing.IsCentralVersionManaged)
               || DirectPackages.Any(Existing =>
                   Existing.PackageId.Equals(PackageId, StringComparison.OrdinalIgnoreCase)
                   && string.IsNullOrWhiteSpace(Existing.TargetFrameworkMoniker)
                   && Existing.IsCentralVersionManaged);
    }

    private static List<string> BuildPath(string PackageId, IReadOnlyList<string> Parents, IReadOnlyDictionary<string, string[]> DependencyLookup, IReadOnlySet<string> DirectIds)
    {
        if (DirectIds.Contains(PackageId) || Parents.Count == 0)
        {
            return new List<string> { PackageId };
        }

        string Current = Parents[0];
        Stack<string> Stack = new();
        Stack.Push(PackageId);

        while (true)
        {
            Stack.Push(Current);

            if (DirectIds.Contains(Current))
            {
                return Stack.Reverse().ToList();
            }

            string? Next = DependencyLookup
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
