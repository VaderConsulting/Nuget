using NuGetAudit.Core;
using NuGetAudit.Presentation;

namespace NuGetAudit.Workbench;

internal sealed class SnapshotProjectModel
{
    internal IReadOnlyDictionary<string, PackageReferenceRecord> PackagesByKey { get; private set; } = new Dictionary<string, PackageReferenceRecord>(StringComparer.OrdinalIgnoreCase);
    internal IReadOnlyDictionary<string, List<DependencyEdgeRecord>> OutgoingEdges { get; private set; } = new Dictionary<string, List<DependencyEdgeRecord>>(StringComparer.OrdinalIgnoreCase);
    internal IReadOnlyList<PackageReferenceRecord> RootPackages { get; private set; } = Array.Empty<PackageReferenceRecord>();
    internal IReadOnlyList<DependencyEdgeRecord> DependencyEdges { get; private set; } = Array.Empty<DependencyEdgeRecord>();

    internal static SnapshotProjectModel Create(ProjectSnapshot project)
    {
        Dictionary<string, PackageReferenceRecord> packagesByKey = project.Packages
            .ToDictionary(static package => package.StablePackageInstanceKey, StringComparer.OrdinalIgnoreCase);

        Dictionary<string, List<DependencyEdgeRecord>> outgoingEdges = project.DependencyEdges
            .GroupBy(static edge => edge.FromStablePackageInstanceKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        PackageReferenceRecord[] rootPackages = project.Packages
            .Where(static package => package.ReferenceKind == PackageReferenceKind.Direct || package.DependencyParents.Count == 0)
            .OrderBy(static package => package.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static package => package.TargetFrameworkMoniker, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static package => package.StablePackageInstanceKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new SnapshotProjectModel
        {
            PackagesByKey = packagesByKey,
            OutgoingEdges = outgoingEdges,
            RootPackages = rootPackages,
            DependencyEdges = project.DependencyEdges
        };
    }
}
