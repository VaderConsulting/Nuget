using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace NuGetAudit.Presentation;

public sealed class SourceSummaryControl : UserControl
{
    private readonly ObservableCollection<SourceSummaryItem> _items = new();

    public SourceSummaryControl()
    {
        DataGrid grid = new()
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            ItemsSource = _items
        };

        grid.Columns.Add(new DataGridTextColumn { Header = "Source", Binding = new Binding(nameof(SourceSummaryItem.DisplayName)), Width = 240 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Source Key", Binding = new Binding(nameof(SourceSummaryItem.SourceIdentityKey)), Width = 120 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Source Kind", Binding = new Binding(nameof(SourceSummaryItem.SourceIdentityKind)), Width = 120 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Repo Root", Binding = new Binding(nameof(SourceSummaryItem.RepositoryRootPath)), Width = 260 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Git Remote", Binding = new Binding(nameof(SourceSummaryItem.GitRemoteUrl)), Width = 280 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Clones", Binding = new Binding(nameof(SourceSummaryItem.CloneCount)), Width = 80 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Targets", Binding = new Binding(nameof(SourceSummaryItem.TargetCount)), Width = 80 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Machines", Binding = new Binding(nameof(SourceSummaryItem.MachineCount)), Width = 90 });

        Content = grid;
    }

    public void LoadSummaries(IEnumerable<SourceSummaryItem> summaries)
    {
        _items.Clear();
        foreach (SourceSummaryItem summary in summaries)
        {
            _items.Add(summary);
        }
    }
}
