using System.Collections.ObjectModel;

namespace NuGetAudit.Presentation;

internal sealed class SurfaceDependencyTreeNode
{
    private readonly Func<IReadOnlyList<SurfaceDependencyTreeNode>>? _childFactory;
    private bool _expanded;

    internal SurfaceDependencyTreeNode(SurfaceProjectSnapshot? project, SurfacePackageReference? package, string title, string tooltip, Func<IReadOnlyList<SurfaceDependencyTreeNode>>? childFactory = null, string rowVisualBand = "None")
    {
        Project = project;
        Package = package;
        Title = title;
        Tooltip = tooltip;
        RowVisualBand = rowVisualBand;
        _childFactory = childFactory;
    }

    internal SurfaceProjectSnapshot? Project { get; }
    internal SurfacePackageReference? Package { get; }

    /// <summary>
    /// Display text for the dependency tree; must be public for WPF <see cref="System.Windows.Data.Binding"/> path resolution.
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Tooltip text for the tree row; must be public for WPF binding.
    /// </summary>
    public string Tooltip { get; }

    /// <summary>
    /// Row band for tree item styling: <c>Critical</c>, <c>High</c>, <c>Attention</c>, <c>Ok</c>, or <c>None</c> (aligned with <see cref="PackageTableRow.RowVisualBand"/>).
    /// </summary>
    public string RowVisualBand { get; }

    internal bool IsPlaceholder { get; private set; }

    /// <summary>
    /// Child nodes; must be public for hierarchical data template bindings.
    /// </summary>
    public ObservableCollection<SurfaceDependencyTreeNode> Children { get; } = new();

    internal void AddPlaceholder()
    {
        if (Children.Count > 0)
        {
            return;
        }

        Children.Add(new SurfaceDependencyTreeNode(Project, null, "Loading...", "Expand this node to load dependency instances.")
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

        foreach (SurfaceDependencyTreeNode child in _childFactory())
        {
            Children.Add(child);
        }
    }
}

internal sealed class SurfaceProjectModel
{
    internal IReadOnlyDictionary<string, SurfacePackageReference> PackagesByKey { get; private set; } = new Dictionary<string, SurfacePackageReference>(StringComparer.OrdinalIgnoreCase);
    internal IReadOnlyDictionary<string, List<SurfaceDependencyEdge>> OutgoingEdges { get; private set; } = new Dictionary<string, List<SurfaceDependencyEdge>>(StringComparer.OrdinalIgnoreCase);
    internal IReadOnlyList<SurfacePackageReference> RootPackages { get; private set; } = Array.Empty<SurfacePackageReference>();
    internal IReadOnlyList<SurfaceDependencyEdge> DependencyEdges { get; private set; } = Array.Empty<SurfaceDependencyEdge>();

    internal static SurfaceProjectModel Create(SurfaceProjectSnapshot project)
    {
        Dictionary<string, SurfacePackageReference> packagesByKey = project.Packages
            .Where(static package => !string.IsNullOrWhiteSpace(package.StablePackageInstanceKey))
            .ToDictionary(static package => package.StablePackageInstanceKey!, StringComparer.OrdinalIgnoreCase);

        Dictionary<string, List<SurfaceDependencyEdge>> outgoingEdges = project.DependencyEdges
            .Where(static edge => !string.IsNullOrWhiteSpace(edge.FromStablePackageInstanceKey))
            .GroupBy(static edge => edge.FromStablePackageInstanceKey!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        SurfacePackageReference[] rootPackages = project.Packages
            .Where(static package => string.Equals(package.ReferenceKind, "Direct", StringComparison.OrdinalIgnoreCase) || package.DependencyParents.Count == 0)
            .OrderBy(static package => package.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static package => package.TargetFrameworkMoniker, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static package => package.StablePackageInstanceKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new SurfaceProjectModel
        {
            PackagesByKey = packagesByKey,
            OutgoingEdges = outgoingEdges,
            RootPackages = rootPackages,
            DependencyEdges = project.DependencyEdges
        };
    }
}
