using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace NuGetAudit.Presentation;

public sealed class IssueRollupControl : UserControl
{
    private static readonly Brush CriticalBrush = CreateBrush(127, 29, 29);
    private static readonly Brush HighBrush = CreateBrush(153, 27, 27);
    private static readonly Brush AttentionBrush = CreateBrush(146, 64, 14);
    private static readonly Brush SuccessBrush = CreateBrush(22, 101, 52);
    private static readonly Brush NeutralBrush = CreateBrush(55, 65, 81);
    private static readonly Brush RowCriticalBackgroundBrush = CreateBrush(254, 226, 226);
    private static readonly Brush RowHighBackgroundBrush = CreateBrush(254, 242, 242);
    private static readonly Brush RowAttentionBackgroundBrush = CreateBrush(255, 247, 237);
    private static readonly Brush RowSuccessBackgroundBrush = CreateBrush(240, 253, 244);
    private static readonly Brush RowNeutralBackgroundBrush = Brushes.White;

    private readonly ObservableCollection<IssueRollupItem> _items = new();
    private readonly TextBlock _summaryText;

    public IssueRollupControl()
    {
        Grid root = new();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Border summaryBorder = new()
        {
            Padding = new Thickness(12),
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(215, 222, 231)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10)
        };
        _summaryText = new TextBlock
        {
            Text = "Current issue rollup will appear here.",
            TextWrapping = TextWrapping.Wrap
        };
        summaryBorder.Child = _summaryText;
        root.Children.Add(summaryBorder);

        DataGrid grid = new()
        {
            Margin = new Thickness(0, 12, 0, 0),
            AutoGenerateColumns = false,
            IsReadOnly = true,
            ItemsSource = _items,
            RowStyle = BuildIssueRowStyle(),
            CellStyle = BuildTransparentDataGridCellStyle()
        };
        VirtualizingPanel.SetVirtualizationMode(grid, VirtualizationMode.Standard);
        grid.Columns.Add(new DataGridTextColumn { Header = "Source", Binding = new Binding(nameof(IssueRollupItem.SourceDisplayName)), Width = 220 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Package", Binding = new Binding(nameof(IssueRollupItem.PackageId)), Width = 180 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Version", Binding = new Binding(nameof(IssueRollupItem.ResolvedVersion)), Width = 110 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Current", Binding = new Binding(nameof(IssueRollupItem.CurrentHealth)), Width = 180 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Risk", Binding = new Binding(nameof(IssueRollupItem.RiskScore)) { StringFormat = "N2" }, Width = 80 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Alert", Binding = new Binding(nameof(IssueRollupItem.AlertScore)) { StringFormat = "N2" }, Width = 80 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Risk Band", Binding = new Binding(nameof(IssueRollupItem.RiskBand)), Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Alert Band", Binding = new Binding(nameof(IssueRollupItem.AlertBand)), Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Targets", Binding = new Binding(nameof(IssueRollupItem.AffectedTargets)), Width = 80 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Locations", Binding = new Binding(nameof(IssueRollupItem.AffectedLocations)), Width = 85 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Determined UTC", Binding = new Binding(nameof(IssueRollupItem.LatestDeterminedUtc)), Width = 180 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Remediation", Binding = new Binding(nameof(IssueRollupItem.Remediation)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        Grid.SetRow(grid, 1);
        root.Children.Add(grid);

        Content = root;
    }

    public void LoadIssues(string outputDirectory)
    {
        ApplyIssues(
            string.IsNullOrWhiteSpace(outputDirectory)
                ? Array.Empty<IssueRollupItem>()
                : IssueRollupReader.Load(outputDirectory),
            false);
    }

    public void LoadIssues(IEnumerable<IssueSourceDescriptor> sources)
    {
        ApplyIssues(IssueRollupReader.LoadMany(sources), true);
    }

    public void SetLoadingState(string message)
    {
        _items.Clear();
        _summaryText.Text = message;
    }

    public void ApplyIssues(IReadOnlyList<IssueRollupItem> items, bool isAggregate)
    {
        _items.Clear();
        foreach (IssueRollupItem item in items)
        {
            _items.Add(item);
        }

        int sourceCount = _items
            .Select(static item => item.SourceIdentityKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        _summaryText.Text = _items.Count == 0
            ? isAggregate
                ? "No current issues requiring attention were found across the tracked sources yet."
                : "No current issues requiring attention were found for this output directory yet."
            : isAggregate
                ? $"Showing {_items.Count} current rolled-up issue row(s) across {sourceCount} tracked source identit{(sourceCount == 1 ? "y" : "ies")}."
                : $"Showing {_items.Count} current rolled-up issue row(s) for the selected output directory.";
    }

    private static Style BuildIssueRowStyle()
    {
        Style style = new(typeof(DataGridRow));
        style.Setters.Add(new Setter(Control.ForegroundProperty, NeutralBrush));
        style.Setters.Add(new Setter(Control.BackgroundProperty, RowNeutralBackgroundBrush));

        style.Triggers.Add(BuildRowTrigger(nameof(IssueRollupItem.RowVisualBand), "Critical", RowCriticalBackgroundBrush, CriticalBrush, FontWeights.SemiBold));
        style.Triggers.Add(BuildRowTrigger(nameof(IssueRollupItem.RowVisualBand), "High", RowHighBackgroundBrush, HighBrush, FontWeights.SemiBold));
        style.Triggers.Add(BuildRowTrigger(nameof(IssueRollupItem.RowVisualBand), "Attention", RowAttentionBackgroundBrush, AttentionBrush, FontWeights.Normal));
        style.Triggers.Add(BuildRowTrigger(nameof(IssueRollupItem.RowVisualBand), "Ok", RowSuccessBackgroundBrush, SuccessBrush, FontWeights.Normal));

        return style;
    }

    private static Style BuildTransparentDataGridCellStyle()
    {
        Style style = new(typeof(DataGridCell));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
        Trigger selectedTrigger = new()
        {
            Property = DataGridCell.IsSelectedProperty,
            Value = true
        };
        selectedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(224, 231, 239))));
        style.Triggers.Add(selectedTrigger);
        return style;
    }

    private static DataTrigger BuildRowTrigger(string propertyName, object value, Brush background, Brush foreground, FontWeight fontWeight)
    {
        DataTrigger trigger = new()
        {
            Binding = new Binding(propertyName),
            Value = value
        };
        trigger.Setters.Add(new Setter(Control.BackgroundProperty, background));
        trigger.Setters.Add(new Setter(Control.ForegroundProperty, foreground));
        trigger.Setters.Add(new Setter(Control.FontWeightProperty, fontWeight));
        return trigger;
    }

    private static SolidColorBrush CreateBrush(byte r, byte g, byte b)
    {
        SolidColorBrush brush = new(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
