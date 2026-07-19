using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NuGetAudit.Workbench;

internal sealed class CatalogTreeNode : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isSelected;

    public CatalogTreeNode(string title, string subtitle, string nodeType, AuditCatalogEntry? entry = null, string? folderPath = null, string? sourceIdentityKey = null)
    {
        Title = title;
        Subtitle = subtitle;
        NodeType = nodeType;
        Entry = entry;
        FolderPath = folderPath;
        SourceIdentityKey = sourceIdentityKey;
    }

    public string Title { get; }
    public string Subtitle { get; }
    public string NodeType { get; }
    public AuditCatalogEntry? Entry { get; }
    public string? FolderPath { get; }
    public string? SourceIdentityKey { get; }
    public ObservableCollection<CatalogTreeNode> Children { get; } = new();
    public CatalogTreeNode? Parent { get; private set; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            OnPropertyChanged();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void AddChild(CatalogTreeNode child)
    {
        child.Parent = this;
        Children.Add(child);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
