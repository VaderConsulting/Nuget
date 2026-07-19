using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace NuGetAudit.Presentation;

public sealed class HistoryTimelineControl : UserControl
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

    private readonly ObservableCollection<SnapshotHistoryItem> _snapshots = new();
    private readonly ObservableCollection<KnowledgeTimelineItem> _knowledgeTimeline = new();
    private readonly ComboBox _baselineComboBox;
    private readonly ComboBox _currentComboBox;
    private readonly DataGrid _snapshotGrid;
    private readonly DataGrid _knowledgeGrid;
    private readonly TextBlock _comparisonSummaryText;
    private readonly TextBlock _detailText;

    public HistoryTimelineControl()
    {
        Grid root = new();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Border summaryBorder = new()
        {
            Padding = new Thickness(12),
            Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(215, 222, 231)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10)
        };

        StackPanel summaryPanel = new();
        summaryPanel.Children.Add(new TextBlock
        {
            Text = "Review snapshot history, compare two analysis points, and inspect the later knowledge changes that were learned after capture.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85))
        });

        Grid selectionGrid = new() { Margin = new Thickness(0, 12, 0, 0) };
        selectionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        selectionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        selectionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        selectionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        selectionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });

        selectionGrid.Children.Add(new TextBlock
        {
            Text = "Baseline",
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 8, 0)
        });

        _baselineComboBox = new ComboBox
        {
            DisplayMemberPath = nameof(SnapshotHistoryItem.SnapshotId),
            MinWidth = 220
        };
        _baselineComboBox.SelectionChanged += OnSnapshotSelectionChanged;
        Grid.SetColumn(_baselineComboBox, 1);
        selectionGrid.Children.Add(_baselineComboBox);

        TextBlock currentLabel = new()
        {
            Text = "Current",
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(currentLabel, 3);
        selectionGrid.Children.Add(currentLabel);

        _currentComboBox = new ComboBox
        {
            DisplayMemberPath = nameof(SnapshotHistoryItem.SnapshotId),
            MinWidth = 220
        };
        _currentComboBox.SelectionChanged += OnSnapshotSelectionChanged;
        Grid.SetColumn(_currentComboBox, 4);
        selectionGrid.Children.Add(_currentComboBox);

        summaryPanel.Children.Add(selectionGrid);

        _comparisonSummaryText = new TextBlock
        {
            Margin = new Thickness(0, 12, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
            Text = "Load an output folder to compare analyses."
        };
        summaryPanel.Children.Add(_comparisonSummaryText);
        summaryBorder.Child = summaryPanel;
        root.Children.Add(summaryBorder);

        Grid contentGrid = new() { Margin = new Thickness(0, 12, 0, 0) };
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(430) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(contentGrid, 1);
        root.Children.Add(contentGrid);

        Grid leftGrid = new();
        leftGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        leftGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        leftGrid.Children.Add(new TextBlock
        {
            Text = "Snapshot timeline",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });

        _snapshotGrid = BuildSnapshotGrid();
        _snapshotGrid.ItemsSource = _snapshots;
        Grid.SetRow(_snapshotGrid, 1);
        leftGrid.Children.Add(_snapshotGrid);
        contentGrid.Children.Add(leftGrid);

        Grid rightGrid = new();
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(160) });
        Grid.SetColumn(rightGrid, 2);
        contentGrid.Children.Add(rightGrid);

        rightGrid.Children.Add(new TextBlock
        {
            Text = "Knowledge timeline",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });

        _knowledgeGrid = BuildKnowledgeGrid();
        _knowledgeGrid.ItemsSource = _knowledgeTimeline;
        Grid.SetRow(_knowledgeGrid, 1);
        rightGrid.Children.Add(_knowledgeGrid);

        TextBlock detailHeader = new()
        {
            Text = "Timeline detail",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 8)
        };
        Grid.SetRow(detailHeader, 2);
        rightGrid.Children.Add(detailHeader);

        Border detailBorder = new()
        {
            Padding = new Thickness(12),
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(215, 222, 231)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10)
        };
        _detailText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "Select a snapshot or knowledge change to inspect the details."
        };
        detailBorder.Child = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _detailText
        };
        Grid.SetRow(detailBorder, 3);
        rightGrid.Children.Add(detailBorder);

        Content = root;
    }

    public string? OutputDirectory { get; private set; }

    public void LoadHistory(string outputDirectory)
    {
        string? normalizedOutputDirectory = string.IsNullOrWhiteSpace(outputDirectory) ? null : Path.GetFullPath(outputDirectory);
        OutputDirectory = normalizedOutputDirectory;
        ApplyHistory(
            string.IsNullOrWhiteSpace(normalizedOutputDirectory) || !Directory.Exists(normalizedOutputDirectory)
                ? new SurfaceHistory(Array.Empty<SnapshotHistoryItem>(), Array.Empty<KnowledgeTimelineItem>(), null)
                : SnapshotHistoryReader.Load(normalizedOutputDirectory!),
            normalizedOutputDirectory);
    }

    public void LoadHistory(IEnumerable<(string SourceName, string OutputDirectory)> sources)
    {
        OutputDirectory = null;
        ApplyHistory(SnapshotHistoryReader.LoadMany(sources), null);
    }

    public void SetLoadingState(string message)
    {
        _snapshots.Clear();
        _knowledgeTimeline.Clear();
        _baselineComboBox.ItemsSource = null;
        _currentComboBox.ItemsSource = null;
        _comparisonSummaryText.Text = message;
        _detailText.Text = "Loading history from the local SQLite store.";
    }

    public void ApplyHistory(SurfaceHistory history, string? normalizedOutputDirectory)
    {
        OutputDirectory = normalizedOutputDirectory;
        _snapshots.Clear();
        _knowledgeTimeline.Clear();

        if (string.IsNullOrWhiteSpace(normalizedOutputDirectory) && history.Snapshots.Count == 0 && history.KnowledgeTimeline.Count == 0 && string.IsNullOrWhiteSpace(history.KnowledgeDatabasePath))
        {
            _detailText.Text = "No output directory is available yet.";
            _comparisonSummaryText.Text = "Run an audit or choose an output folder to compare analyses.";
            _baselineComboBox.ItemsSource = null;
            _currentComboBox.ItemsSource = null;
            return;
        }

        foreach (SnapshotHistoryItem snapshot in history.Snapshots)
        {
            _snapshots.Add(snapshot);
        }

        foreach (KnowledgeTimelineItem item in history.KnowledgeTimeline)
        {
            _knowledgeTimeline.Add(item);
        }

        _baselineComboBox.ItemsSource = _snapshots;
        _currentComboBox.ItemsSource = _snapshots;

        if (_snapshots.Count > 0)
        {
            _currentComboBox.SelectedIndex = 0;
            _baselineComboBox.SelectedIndex = _snapshots.Count > 1 ? 1 : 0;
            _snapshotGrid.SelectedIndex = 0;
        }
        else
        {
            _comparisonSummaryText.Text = "No snapshots were found in the output directory yet.";
        }

        if (_knowledgeTimeline.Count > 0)
        {
            _knowledgeGrid.SelectedIndex = 0;
        }
        else if (!string.IsNullOrWhiteSpace(history.KnowledgeDatabasePath))
        {
            _detailText.Text = $"History loaded from:{Environment.NewLine}{history.KnowledgeDatabasePath}";
        }
        else
        {
            _detailText.Text = "No SQLite knowledge timeline has been created yet for this output directory.";
        }
    }

    private static DataGrid BuildSnapshotGrid()
    {
        DataGrid grid = new()
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            Margin = new Thickness(0),
            Background = Brushes.White,
            RowStyle = BuildSnapshotRowStyle(),
            CellStyle = BuildTransparentDataGridCellStyle()
        };
        VirtualizingPanel.SetVirtualizationMode(grid, VirtualizationMode.Standard);
        grid.SelectionChanged += static (sender, _) =>
        {
            if (sender is HistoryTimelineControl)
            {
                return;
            }
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Captured UTC", Binding = new Binding(nameof(SnapshotHistoryItem.CapturedUtc)) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Source", Binding = new Binding(nameof(SnapshotHistoryItem.SourceName)) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Snapshot", Binding = new Binding(nameof(SnapshotHistoryItem.SnapshotId)) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new Binding(nameof(SnapshotHistoryItem.AnalysisStatus)) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Projects", Binding = new Binding(nameof(SnapshotHistoryItem.ProjectCount)) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Packages", Binding = new Binding(nameof(SnapshotHistoryItem.PackageCount)) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Attention", Binding = new Binding(nameof(SnapshotHistoryItem.AttentionCount)) });
        return grid;
    }

    private static DataGrid BuildKnowledgeGrid()
    {
        DataGrid grid = new()
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            Margin = new Thickness(0),
            Background = Brushes.White,
            RowStyle = BuildKnowledgeRowStyle(),
            CellStyle = BuildTransparentDataGridCellStyle()
        };
        VirtualizingPanel.SetVirtualizationMode(grid, VirtualizationMode.Standard);
        grid.Columns.Add(new DataGridTextColumn { Header = "Determined UTC", Binding = new Binding(nameof(KnowledgeTimelineItem.DeterminedUtc)) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Source", Binding = new Binding(nameof(KnowledgeTimelineItem.SourceName)) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Package", Binding = new Binding(nameof(KnowledgeTimelineItem.PackageId)) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Version", Binding = new Binding(nameof(KnowledgeTimelineItem.ResolvedVersion)) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Current", Binding = new Binding(nameof(KnowledgeTimelineItem.CurrentHealth)) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Risk", Binding = new Binding(nameof(KnowledgeTimelineItem.RiskScore)) { StringFormat = "N2" } });
        grid.Columns.Add(new DataGridTextColumn { Header = "Alert", Binding = new Binding(nameof(KnowledgeTimelineItem.AlertScore)) { StringFormat = "N2" } });
        grid.Columns.Add(new DataGridTextColumn { Header = "Remediation", Binding = new Binding(nameof(KnowledgeTimelineItem.Remediation)) });
        return grid;
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        _snapshotGrid.SelectionChanged += OnSnapshotGridSelectionChanged;
        _knowledgeGrid.SelectionChanged += OnKnowledgeSelectionChanged;
    }

    private void OnSnapshotSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateComparisonSummary();
    }

    private void OnSnapshotGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_snapshotGrid.SelectedItem is not SnapshotHistoryItem item)
        {
            return;
        }

        _detailText.Text =
            $"Snapshot: {item.SnapshotId}{Environment.NewLine}" +
            $"Source: {item.SourceName}{Environment.NewLine}" +
            $"Captured: {item.CapturedUtc:O}{Environment.NewLine}" +
            $"Input: {item.SolutionName}{Environment.NewLine}" +
            $"Output: {item.OutputDirectory}{Environment.NewLine}" +
            $"Path: {item.SolutionPath}{Environment.NewLine}" +
            $"Projects: {item.ProjectCount}{Environment.NewLine}" +
            $"Package instances: {item.PackageCount}{Environment.NewLine}" +
            $"Attention count: {item.AttentionCount}{Environment.NewLine}" +
            $"Markdown: {item.MarkdownPath ?? "-"}{Environment.NewLine}" +
            $"Delta: {item.DeltaJsonPath ?? "-"}";
    }

    private void OnKnowledgeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_knowledgeGrid.SelectedItem is not KnowledgeTimelineItem item)
        {
            return;
        }

        _detailText.Text =
            $"Package: {item.PackageId}{Environment.NewLine}" +
            $"Version: {item.ResolvedVersion}{Environment.NewLine}" +
            $"Source: {item.SourceName}{Environment.NewLine}" +
            $"Output: {item.OutputDirectory}{Environment.NewLine}" +
            $"Snapshot: {item.SnapshotId}{Environment.NewLine}" +
            $"Determined: {item.DeterminedUtc:O}{Environment.NewLine}" +
            $"Previous: {item.PreviousHealth}{Environment.NewLine}" +
            $"Current: {item.CurrentHealth}{Environment.NewLine}" +
            $"Risk: {(item.RiskScore.HasValue ? $"{item.RiskScore.Value:N2} ({item.RiskBand ?? "Unknown"})" : "-")}{Environment.NewLine}" +
            $"Alert: {(item.AlertScore.HasValue ? $"{item.AlertScore.Value:N2} ({item.AlertBand ?? "Unknown"})" : "-")}{Environment.NewLine}" +
            $"Remediation: {item.Remediation}{Environment.NewLine}" +
            $"Summary: {item.Summary}";
    }

    private void UpdateComparisonSummary()
    {
        SnapshotHistoryItem? baseline = _baselineComboBox.SelectedItem as SnapshotHistoryItem;
        SnapshotHistoryItem? current = _currentComboBox.SelectedItem as SnapshotHistoryItem;

        if (baseline is null && current is null)
        {
            _comparisonSummaryText.Text = "Load an output folder to compare analyses.";
            return;
        }

        if (baseline is null || current is null)
        {
            SnapshotHistoryItem single = baseline ?? current!;
            _comparisonSummaryText.Text = $"Showing snapshot {single.SnapshotId} captured {single.CapturedUtc:O}. Select a second snapshot to compare drift over time.";
            return;
        }

        TimeSpan timeSpan = current.CapturedUtc - baseline.CapturedUtc;
        string direction = timeSpan.TotalSeconds >= 0 ? "later" : "earlier";
        TimeSpan elapsed = timeSpan.Duration();

        _comparisonSummaryText.Text =
            $"Comparing {baseline.SnapshotId} to {current.SnapshotId}. " +
            $"The current selection is {elapsed.TotalHours:N1} hours {direction}. " +
            $"Projects: {baseline.ProjectCount} -> {current.ProjectCount}. " +
            $"Package instances: {baseline.PackageCount} -> {current.PackageCount}. " +
            $"Attention count: {baseline.AttentionCount} -> {current.AttentionCount}.";
    }

    private static Style BuildSnapshotRowStyle()
    {
        Style style = new(typeof(DataGridRow));
        style.Setters.Add(new Setter(Control.ForegroundProperty, NeutralBrush));
        style.Setters.Add(new Setter(Control.BackgroundProperty, RowNeutralBackgroundBrush));

        style.Triggers.Add(BuildRowTrigger(nameof(SnapshotHistoryItem.RowVisualBand), "Critical", RowCriticalBackgroundBrush, CriticalBrush, FontWeights.SemiBold));
        style.Triggers.Add(BuildRowTrigger(nameof(SnapshotHistoryItem.RowVisualBand), "Attention", RowAttentionBackgroundBrush, AttentionBrush, FontWeights.Normal));
        style.Triggers.Add(BuildRowTrigger(nameof(SnapshotHistoryItem.RowVisualBand), "Ok", RowSuccessBackgroundBrush, SuccessBrush, FontWeights.Normal));

        return style;
    }

    private static Style BuildKnowledgeRowStyle()
    {
        Style style = new(typeof(DataGridRow));
        style.Setters.Add(new Setter(Control.ForegroundProperty, NeutralBrush));
        style.Setters.Add(new Setter(Control.BackgroundProperty, RowNeutralBackgroundBrush));

        style.Triggers.Add(BuildRowTrigger(nameof(KnowledgeTimelineItem.RowVisualBand), "Critical", RowCriticalBackgroundBrush, CriticalBrush, FontWeights.SemiBold));
        style.Triggers.Add(BuildRowTrigger(nameof(KnowledgeTimelineItem.RowVisualBand), "High", RowHighBackgroundBrush, HighBrush, FontWeights.SemiBold));
        style.Triggers.Add(BuildRowTrigger(nameof(KnowledgeTimelineItem.RowVisualBand), "Attention", RowAttentionBackgroundBrush, AttentionBrush, FontWeights.Normal));
        style.Triggers.Add(BuildRowTrigger(nameof(KnowledgeTimelineItem.RowVisualBand), "Ok", RowSuccessBackgroundBrush, SuccessBrush, FontWeights.Normal));

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
