using System.Collections.ObjectModel;
using NuGetAudit.Core;

namespace NuGetAudit.Workbench;

internal sealed class DependencyTreeNode
{
    private readonly Func<IReadOnlyList<DependencyTreeNode>>? _childFactory;
    private bool _expanded;

    internal DependencyTreeNode(ProjectSnapshot? project, PackageReferenceRecord? package, string title, string tooltip, Func<IReadOnlyList<DependencyTreeNode>>? childFactory = null)
    {
        Project = project;
        Package = package;
        Title = title;
        Tooltip = tooltip;
        _childFactory = childFactory;
    }

    internal ProjectSnapshot? Project { get; }
    internal PackageReferenceRecord? Package { get; }
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
