using System.Collections.ObjectModel;
using NuGetAudit.Presentation;

namespace NuGetAudit.VisualStudioHost;

internal sealed class DependencyTreeNode
{
    private readonly Func<IReadOnlyList<DependencyTreeNode>>? _childFactory;
    private bool _expanded;

    internal DependencyTreeNode(ProjectSnapshotFile? project, PackageReferenceFile? package, string title, string tooltip, Func<IReadOnlyList<DependencyTreeNode>>? childFactory = null)
    {
        Project = project;
        Package = package;
        Title = title;
        Tooltip = tooltip;
        _childFactory = childFactory;
    }

    internal ProjectSnapshotFile? Project { get; }
    internal PackageReferenceFile? Package { get; }
    internal string Title { get; }
    internal string Tooltip { get; }
    internal bool IsPlaceholder { get; private set; }
    internal ObservableCollection<DependencyTreeNode> Children { get; } = new();

    internal void AddPlaceholder()
    {
        if (Children.Count > 0)
        {
            return;
        }

        Children.Add(new DependencyTreeNode(Project, null, "Loading...", "Expand this node to load dependency instances.")
        {
            IsPlaceholder = true
        });
    }

    internal void EnsureExpanded()
    {
        if (_expanded || _childFactory is null)
        {
            return;
        }

        _expanded = true;
        Children.Clear();

        foreach (DependencyTreeNode child in _childFactory())
        {
            Children.Add(child);
        }
    }
}

internal sealed class SnapshotProjectModel
{
    internal IReadOnlyDictionary<string, PackageReferenceFile> PackagesByKey { get; private set; } = new Dictionary<string, PackageReferenceFile>(StringComparer.OrdinalIgnoreCase);
    internal IReadOnlyDictionary<string, List<DependencyEdgeFile>> OutgoingEdges { get; private set; } = new Dictionary<string, List<DependencyEdgeFile>>(StringComparer.OrdinalIgnoreCase);
    internal IReadOnlyList<PackageReferenceFile> RootPackages { get; private set; } = Array.Empty<PackageReferenceFile>();
    internal IReadOnlyList<DependencyEdgeFile> DependencyEdges { get; private set; } = Array.Empty<DependencyEdgeFile>();

    internal static SnapshotProjectModel Create(ProjectSnapshotFile project)
    {
        Dictionary<string, PackageReferenceFile> packagesByKey = project.Packages
            .Where(static package => !string.IsNullOrWhiteSpace(package.StablePackageInstanceKey))
            .ToDictionary(static package => package.StablePackageInstanceKey!, StringComparer.OrdinalIgnoreCase);

        Dictionary<string, List<DependencyEdgeFile>> outgoingEdges = project.DependencyEdges
            .Where(static edge => !string.IsNullOrWhiteSpace(edge.FromStablePackageInstanceKey))
            .GroupBy(static edge => edge.FromStablePackageInstanceKey!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        PackageReferenceFile[] rootPackages = project.Packages
            .Where(static package => string.Equals(package.ReferenceKind, "Direct", StringComparison.OrdinalIgnoreCase) || package.DependencyParents.Count == 0)
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
