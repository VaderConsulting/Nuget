using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace NuGetAudit.Presentation;

public sealed class PresentationControl : UserControl
{
    private static readonly Brush PackageRowCriticalBrush = CreatePresentationBrush(127, 29, 29);
    private static readonly Brush PackageRowHighBrush = CreatePresentationBrush(153, 27, 27);
    private static readonly Brush PackageRowAttentionBrush = CreatePresentationBrush(146, 64, 14);
    private static readonly Brush PackageRowSuccessBrush = CreatePresentationBrush(22, 101, 52);
    private static readonly Brush PackageRowNeutralBrush = CreatePresentationBrush(55, 65, 81);
    private static readonly Brush PackageRowCriticalBackgroundBrush = CreatePresentationBrush(254, 226, 226);
    private static readonly Brush PackageRowHighBackgroundBrush = CreatePresentationBrush(254, 242, 242);
    private static readonly Brush PackageRowAttentionBackgroundBrush = CreatePresentationBrush(255, 247, 237);
    private static readonly Brush PackageRowSuccessBackgroundBrush = CreatePresentationBrush(240, 253, 244);
    private static readonly Brush PackageRowNeutralBackgroundBrush = Brushes.White;

    private static readonly JsonSerializerOptions GraphPayloadJsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Button _refreshButton;
    private readonly Button _reevaluateSolutionButton;
    private readonly Button _reevaluateProjectButton;
    private readonly Button _reevaluatePackageButton;
    private readonly CheckBox _showAllProjectsCheckBox;
    private readonly TreeView _treeView;
    private readonly DataGrid _packageGrid;
    private readonly TextBlock _snapshotSummary;
    private readonly TextBlock _scopeSummary;
    private readonly TextBlock _detailsText;
    private readonly WebView2 _graphView;
    private readonly ObservableCollection<SurfaceDependencyTreeNode> _treeNodes = new();
    private readonly ObservableCollection<PackageTableRow> _visiblePackageRows = new();
    private readonly Dictionary<string, IReadOnlyList<PackageTableRow>> _rowsByProject = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SurfaceProjectModel> _projectModels = new(StringComparer.OrdinalIgnoreCase);
    private SurfaceProjectSnapshot? _selectedProject;
    private SurfacePackageReference? _selectedPackage;
    private bool _graphReady;
    private string? _pendingPayload;
    private bool _mergeNetVersions = true;
    private SurfaceSolutionSnapshot? _loadedSolutionSnapshot;
    private readonly Dictionary<string, Dictionary<string, string>> _mergeRemapByProjectKey = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? RefreshRequested;
    public event EventHandler<ReevaluationRequestEventArgs>? ReevaluationRequested;
    public event EventHandler? MergeNetVersionsChanged;

    public PresentationControl()
    {
        Grid root = new();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Border summaryBorder = new()
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(215, 222, 231)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10)
        };

        Grid summaryGrid = new();
        summaryGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        summaryGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        summaryGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        StackPanel commandBar = new()
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8)
        };
        _refreshButton = BuildCommandButton("Refresh", OnRefreshClicked);
        _reevaluateSolutionButton = BuildCommandButton("Re-evaluate Solution", OnReevaluateSolutionClicked);
        _reevaluateProjectButton = BuildCommandButton("Re-evaluate Project", OnReevaluateProjectClicked);
        _reevaluatePackageButton = BuildCommandButton("Re-evaluate Package", OnReevaluatePackageClicked);
        commandBar.Children.Add(_refreshButton);
        commandBar.Children.Add(_reevaluateSolutionButton);
        commandBar.Children.Add(_reevaluateProjectButton);
        commandBar.Children.Add(_reevaluatePackageButton);
        summaryGrid.Children.Add(commandBar);

        _snapshotSummary = new TextBlock
        {
            Text = "Load a snapshot to inspect project dependency graphs.",
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(_snapshotSummary, 1);
        summaryGrid.Children.Add(_snapshotSummary);

        _scopeSummary = new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
            Text = "Current scope: none selected.",
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(_scopeSummary, 2);
        summaryGrid.Children.Add(_scopeSummary);
        summaryBorder.Child = summaryGrid;
        Grid.SetRow(summaryBorder, 0);
        root.Children.Add(summaryBorder);

        Grid contentGrid = new();
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(430) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(contentGrid, 1);
        root.Children.Add(contentGrid);

        Grid leftPane = new();
        leftPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        leftPane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        leftPane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(180) });

        Grid TreeHeaderRow = new();
        TreeHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        TreeHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        TextBlock TreeHeader = new()
        {
            Text = "Dependency tree",
            Margin = new Thickness(10, 10, 6, 6),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(TreeHeader, 0);
        TreeHeaderRow.Children.Add(TreeHeader);
        _showAllProjectsCheckBox = new CheckBox
        {
            Content = "Show all",
            IsChecked = false,
            Margin = new Thickness(0, 10, 10, 6),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Unchecked: only projects with NuGet packages appear. Checked: list every project."
        };
        _showAllProjectsCheckBox.Checked += OnShowAllProjectsCheckBoxChanged;
        _showAllProjectsCheckBox.Unchecked += OnShowAllProjectsCheckBoxChanged;
        Grid.SetColumn(_showAllProjectsCheckBox, 1);
        TreeHeaderRow.Children.Add(_showAllProjectsCheckBox);
        Grid.SetRow(TreeHeaderRow, 0);
        leftPane.Children.Add(TreeHeaderRow);

        _treeView = new TreeView
        {
            Margin = new Thickness(10, 0, 10, 10),
            ItemsSource = _treeNodes,
            ItemTemplate = BuildTreeTemplate(),
            ItemContainerStyle = BuildDependencyTreeItemContainerStyle()
        };
        _treeView.SelectedItemChanged += OnTreeSelectionChanged;
        _treeView.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(OnTreeItemExpanded));
        Grid.SetRow(_treeView, 1);
        leftPane.Children.Add(_treeView);

        Border detailsBorder = new()
        {
            Margin = new Thickness(10, 0, 10, 10),
            Padding = new Thickness(8),
            BorderBrush = new SolidColorBrush(Color.FromRgb(215, 222, 231)),
            BorderThickness = new Thickness(1)
        };
        _detailsText = new TextBlock
        {
            Text = "Select a node to inspect package details.",
            TextWrapping = TextWrapping.Wrap
        };
        detailsBorder.Child = _detailsText;
        Grid.SetRow(detailsBorder, 2);
        leftPane.Children.Add(detailsBorder);

        Grid.SetColumn(leftPane, 0);
        contentGrid.Children.Add(leftPane);

        Grid rightPane = new();
        rightPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rightPane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        rightPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rightPane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        TextBlock graphHeader = new()
        {
            Text = "Dependency graph",
            Margin = new Thickness(10, 10, 10, 6),
            FontWeight = FontWeights.SemiBold
        };
        Grid.SetRow(graphHeader, 0);
        rightPane.Children.Add(graphHeader);

        _graphView = new WebView2
        {
            Margin = new Thickness(10, 0, 10, 10),
            DefaultBackgroundColor = System.Drawing.Color.White
        };
        Grid.SetRow(_graphView, 1);
        rightPane.Children.Add(_graphView);

        TextBlock gridHeader = new()
        {
            Text = "Package table",
            Margin = new Thickness(10, 0, 10, 6),
            FontWeight = FontWeights.SemiBold
        };
        Grid.SetRow(gridHeader, 2);
        rightPane.Children.Add(gridHeader);

        _packageGrid = BuildPackageGrid();
        _packageGrid.ItemsSource = _visiblePackageRows;
        _packageGrid.SelectionChanged += OnGridSelectionChanged;
        Grid.SetRow(_packageGrid, 3);
        rightPane.Children.Add(_packageGrid);

        Grid.SetColumn(rightPane, 1);
        contentGrid.Children.Add(rightPane);

        Content = root;
        Loaded += OnLoaded;
        UpdateCommandState();
    }

    public void LoadSnapshot(SurfaceSolutionSnapshot snapshot)
    {
        _treeNodes.Clear();
        _visiblePackageRows.Clear();
        _rowsByProject.Clear();
        _projectModels.Clear();
        _mergeRemapByProjectKey.Clear();
        _loadedSolutionSnapshot = snapshot;

        int projectCount = snapshot.Projects.Count;
        int packageCount = snapshot.Projects.Sum(static project => project.Packages.Count);
        int edgeCount = snapshot.Projects.Sum(static project => project.DependencyEdges.Count);
        _snapshotSummary.Text = $"Snapshot {snapshot.SnapshotId ?? "(unknown)"} captured {snapshot.CapturedUtc:O}. Projects: {projectCount}. Packages: {packageCount}. Dependency edges: {edgeCount}. Expand a project to load package instances lazily.";

        foreach (SurfaceProjectSnapshot project in snapshot.Projects.OrderBy(static project => project.ProjectName, StringComparer.OrdinalIgnoreCase))
        {
            string ProjectKey = GetProjectKey(project);
            _projectModels[ProjectKey] = SurfaceProjectModel.Create(project);
            _rowsByProject[ProjectKey] = BuildRowsForDisplay(project);
        }

        PopulateDependencyTreeRootNodes();

        if (snapshot.Projects.Count == 0)
        {
            _selectedProject = null;
            _selectedPackage = null;
            _loadedSolutionSnapshot = null;
            _detailsText.Text = "No project dependency data was present in the snapshot.";
            QueueGraphRender(new GraphPayload(string.Empty, Array.Empty<GraphNode>(), Array.Empty<GraphEdge>(), string.Empty));
            UpdateCommandState();
            return;
        }

        if (_treeNodes.Count == 0)
        {
            _selectedProject = null;
            _selectedPackage = null;
            _detailsText.Text = "No projects match the current tree filter. Check \"Show all\" to include projects without packages.";
            QueueGraphRender(new GraphPayload(string.Empty, Array.Empty<GraphNode>(), Array.Empty<GraphEdge>(), string.Empty));
            UpdateCommandState();
            return;
        }

        _detailsText.Text = "Select a project or package instance to inspect dependency details. Hover in the graph for a quick summary, or keep a row or node selected to hold the details in place.";
        SurfaceDependencyTreeNode InitialNode = _treeNodes[0];
        SelectNode(InitialNode);
        SyncTreeViewItemSelection(InitialNode);
    }

    public void SetLoadingState(string message)
    {
        _treeNodes.Clear();
        _visiblePackageRows.Clear();
        _rowsByProject.Clear();
        _projectModels.Clear();
        _mergeRemapByProjectKey.Clear();
        _loadedSolutionSnapshot = null;
        _selectedProject = null;
        _selectedPackage = null;
        _snapshotSummary.Text = message;
        _detailsText.Text = "Loading dependency data from the local SQLite store.";
        QueueGraphRender(new GraphPayload(string.Empty, Array.Empty<GraphNode>(), Array.Empty<GraphEdge>(), string.Empty));
        UpdateCommandState();
    }

    public void SetScopeContext(string title, string description)
    {
        _scopeSummary.Text = string.IsNullOrWhiteSpace(description)
            ? $"Current scope: {title}"
            : $"Current scope: {title}{Environment.NewLine}{description}";
    }

    private void PopulateDependencyTreeRootNodes()
    {
        _treeNodes.Clear();
        if (_loadedSolutionSnapshot is null)
        {
            return;
        }

        bool ShowAllProjects = _showAllProjectsCheckBox.IsChecked == true;
        foreach (SurfaceProjectSnapshot SnapshotProject in _loadedSolutionSnapshot.Projects.OrderBy(static project => project.ProjectName, StringComparer.OrdinalIgnoreCase))
        {
            if (!ShowAllProjects && SnapshotProject.Packages.Count == 0)
            {
                continue;
            }

            string ProjectDotNetVersionsDisplay = TargetFrameworkDisplay.FormatProjectMonikers(SnapshotProject.TargetFrameworks);
            ProjectRollup ProjectRollup = ProjectRollupBuilder.Compute(SnapshotProject.Packages);
            string ProjectTitle = $"{SnapshotProject.ProjectName ?? "(unknown project)"} [{ProjectDotNetVersionsDisplay}]";
            string ProjectTooltip = BuildProjectTreeTooltip(SnapshotProject, ProjectRollup);
            SurfaceDependencyTreeNode ProjectNode = new SurfaceDependencyTreeNode(SnapshotProject, null, ProjectTitle, ProjectTooltip, () => BuildProjectChildren(SnapshotProject), ProjectRollup.RowVisualBand);
            ProjectNode.AddPlaceholder();
            _treeNodes.Add(ProjectNode);
        }
    }

    private void OnShowAllProjectsCheckBoxChanged(object sender, RoutedEventArgs e)
    {
        if (_loadedSolutionSnapshot is null)
        {
            return;
        }

        string? PreserveProjectKey = _selectedProject is not null ? GetProjectKey(_selectedProject) : null;
        PopulateDependencyTreeRootNodes();

        if (_treeNodes.Count == 0)
        {
            _selectedProject = null;
            _selectedPackage = null;
            _detailsText.Text = "No projects match the current tree filter. Check \"Show all\" to include projects without packages.";
            QueueGraphRender(new GraphPayload(string.Empty, Array.Empty<GraphNode>(), Array.Empty<GraphEdge>(), string.Empty));
            UpdateCommandState();
            return;
        }

        SurfaceDependencyTreeNode? Target = null;
        if (PreserveProjectKey is not null)
        {
            Target = _treeNodes.FirstOrDefault(
                node => node.Project is not null && string.Equals(GetProjectKey(node.Project), PreserveProjectKey, StringComparison.OrdinalIgnoreCase));
        }

        SurfaceDependencyTreeNode NodeToSelect = Target ?? _treeNodes[0];
        SelectNode(NodeToSelect);
        SyncTreeViewItemSelection(NodeToSelect);
    }

    private void SyncTreeViewItemSelection(SurfaceDependencyTreeNode node)
    {
        _treeView.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                if (_treeView.ItemContainerGenerator.ContainerFromItem(node) is TreeViewItem Item)
                {
                    Item.IsSelected = true;
                }
            }));
    }

    public void SetAuditActionAvailability(bool isEnabled)
    {
        _refreshButton.IsEnabled = isEnabled;
        _reevaluateSolutionButton.IsEnabled = isEnabled && _projectModels.Count > 0;
        _reevaluateProjectButton.IsEnabled = isEnabled && _selectedProject is not null;
        _reevaluatePackageButton.IsEnabled = isEnabled && _selectedPackage is not null;
    }

    /// <summary>
    /// When true, the explorer merges package rows that differ only by target framework moniker.
    /// </summary>
    public bool MergeNetVersions
    {
        get
        {
            return _mergeNetVersions;
        }
    }

    /// <summary>
    /// Rebuilds package table rows and the dependency graph for the current <see cref="MergeNetVersions"/> value without changing the dependency tree.
    /// </summary>
    public void RefreshPackageGridAndDependencyGraphForMergePreference()
    {
        if (_loadedSolutionSnapshot is null)
        {
            return;
        }

        _mergeRemapByProjectKey.Clear();
        foreach (SurfaceProjectSnapshot project in _loadedSolutionSnapshot.Projects)
        {
            string key = GetProjectKey(project);
            if (_rowsByProject.ContainsKey(key))
            {
                _rowsByProject[key] = BuildRowsForDisplay(project);
            }
        }

        if (_treeView.SelectedItem is SurfaceDependencyTreeNode selectedNode)
        {
            SelectNode(selectedNode);
        }
    }

    private static Button BuildCommandButton(string content, RoutedEventHandler onClick)
    {
        Button button = new()
        {
            Content = content,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(12, 4, 12, 4),
            MinWidth = 120
        };
        button.Click += onClick;
        return button;
    }

    private static HierarchicalDataTemplate BuildTreeTemplate()
    {
        FrameworkElementFactory textBlockFactory = new(typeof(TextBlock));
        textBlockFactory.SetBinding(TextBlock.TextProperty, new Binding(nameof(SurfaceDependencyTreeNode.Title)));
        textBlockFactory.SetBinding(ToolTipProperty, new Binding(nameof(SurfaceDependencyTreeNode.Tooltip)));

        return new HierarchicalDataTemplate(typeof(SurfaceDependencyTreeNode))
        {
            ItemsSource = new Binding(nameof(SurfaceDependencyTreeNode.Children)),
            VisualTree = textBlockFactory
        };
    }

    private static DataGrid BuildPackageGrid()
    {
        DataGrid grid = new()
        {
            Margin = new Thickness(10, 0, 10, 10),
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserResizeColumns = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            RowStyle = BuildPackageTableRowStyle(),
            CellStyle = BuildPackageTableTransparentCellStyle()
        };
        VirtualizingPanel.SetVirtualizationMode(grid, VirtualizationMode.Standard);

        Style columnTextStyle = BuildPackageGridColumnTextStyle();
        grid.Columns.Add(BuildPackageTextColumn("Project", nameof(PackageTableRow.ProjectName), 118, columnTextStyle));
        grid.Columns.Add(BuildPackageTextColumn("Package", nameof(PackageTableRow.PackageId), 152, columnTextStyle));
        grid.Columns.Add(BuildPackageTextColumn("Instance", nameof(PackageTableRow.InstanceKeyShort), 68, columnTextStyle));
        grid.Columns.Add(BuildPackageTextColumn("Kind", nameof(PackageTableRow.ReferenceKind), 64, columnTextStyle));
        grid.Columns.Add(BuildPackageTextColumn("Version", nameof(PackageTableRow.VersionDisplay), 108, columnTextStyle));
        grid.Columns.Add(BuildPackageTextColumn(".NET Version", nameof(PackageTableRow.TargetFrameworkMonikerDisplay), 108, columnTextStyle));
        grid.Columns.Add(BuildPackageTextColumn("Health", nameof(PackageTableRow.HealthSummary), 92, columnTextStyle));
        grid.Columns.Add(BuildPackageTextColumn("Remediation", nameof(PackageTableRow.RemediationSummary), 112, columnTextStyle));
        grid.Columns.Add(BuildPackageTextColumn("Risk", nameof(PackageTableRow.RiskSummary), 68, columnTextStyle));
        grid.Columns.Add(BuildPackageTextColumn("Alert", nameof(PackageTableRow.AlertSummary), 68, columnTextStyle));
        grid.Columns.Add(BuildPackageTextColumn("Path", nameof(PackageTableRow.Path), 200, columnTextStyle));
        return grid;
    }

    private static Style BuildPackageGridColumnTextStyle()
    {
        Style style = new(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap));
        return style;
    }

    private static DataGridTextColumn BuildPackageTextColumn(string header, string propertyName, double widthPixels, Style elementStyle)
    {
        return new DataGridTextColumn
        {
            Header = header,
            Width = new DataGridLength(widthPixels),
            Binding = new Binding(propertyName),
            ElementStyle = elementStyle
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ = InitializeGraphAsync();
    }

    private async Task InitializeGraphAsync()
    {
        if (_graphReady)
        {
            return;
        }

        try
        {
            await _graphView.EnsureCoreWebView2Async();
            _graphView.CoreWebView2.WebMessageReceived += OnGraphWebMessageReceived;
            _graphView.CoreWebView2.NavigationCompleted += OnGraphNavigationCompleted;
            _graphView.CoreWebView2.NavigateToString(GraphDocument.Html);
        }
        catch (Exception ex)
        {
            _detailsText.Text = $"The dependency graph panel could not start WebView2. {ex.Message}";
        }
    }

    private async void OnGraphNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _graphReady = e.IsSuccess;
        if (!_graphReady)
        {
            return;
        }

        string MergeScript = _mergeNetVersions
            ? "if (window.applyMergeNetVersionsFromHost) window.applyMergeNetVersionsFromHost(true);"
            : "if (window.applyMergeNetVersionsFromHost) window.applyMergeNetVersionsFromHost(false);";
        await _graphView.ExecuteScriptAsync(MergeScript);
        if (!string.IsNullOrWhiteSpace(_pendingPayload))
        {
            await _graphView.ExecuteScriptAsync($"window.renderGraph({JsonSerializer.Serialize(_pendingPayload)});");
        }
    }

    private void OnGraphWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => OnGraphWebMessageReceived(sender, e));
            return;
        }

        try
        {
            string Json = e.WebMessageAsJson;
            if (string.IsNullOrWhiteSpace(Json))
            {
                return;
            }

            if (TryReadMergeNetVersionsFromWebMessage(Json, out bool Value))
            {
                if (_mergeNetVersions == Value)
                {
                    return;
                }

                _mergeNetVersions = Value;
                MergeNetVersionsChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (TryReadGraphCopyPngDataUrlFromWebMessage(Json, out string? PngDataUrl))
            {
                if (TryCopyPngDataUrlToClipboard(PngDataUrl, out string? ClipboardError))
                {
                    CoreWebView2? Core = _graphView.CoreWebView2;
                    if (Core is not null)
                    {
                        _ = Core.ExecuteScriptAsync("if(window.__notifyGraphCopied)window.__notifyGraphCopied();");
                    }
                }
                else if (!string.IsNullOrWhiteSpace(ClipboardError))
                {
                    MessageBox.Show(ClipboardError, "Copy dependency graph", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                return;
            }

            if (TryReadGraphCopyResultFromWebMessage(Json, out bool CopyOk, out string? CopyMessage))
            {
                if (!CopyOk && !string.IsNullOrWhiteSpace(CopyMessage))
                {
                    MessageBox.Show(CopyMessage, "Copy dependency graph", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                return;
            }
        }
        catch (JsonException)
        {
        }
    }

    /// <summary>
    /// WebView2 <see cref="CoreWebView2WebMessageReceivedEventArgs.WebMessageAsJson"/> is either a JSON object or,
    /// when the page used <c>postMessage(JSON.stringify(...))</c>, a JSON string containing that object.
    /// </summary>
    private static bool TryReadMergeNetVersionsFromWebMessage(string WebMessageAsJson, out bool MergeNetVersionsValue)
    {
        MergeNetVersionsValue = false;
        using (JsonDocument Document = JsonDocument.Parse(WebMessageAsJson))
        {
            JsonElement Root = Document.RootElement;
            if (Root.ValueKind == JsonValueKind.String)
            {
                string? Inner = Root.GetString();
                if (string.IsNullOrWhiteSpace(Inner))
                {
                    return false;
                }

                using JsonDocument InnerDocument = JsonDocument.Parse(Inner!);
                return TryReadMergeNetVersionsPayload(InnerDocument.RootElement, out MergeNetVersionsValue);
            }

            if (Root.ValueKind == JsonValueKind.Object)
            {
                return TryReadMergeNetVersionsPayload(Root, out MergeNetVersionsValue);
            }
        }

        return false;
    }

    private static bool TryReadMergeNetVersionsPayload(JsonElement Payload, out bool MergeNetVersionsValue)
    {
        MergeNetVersionsValue = false;
        if (!Payload.TryGetProperty("type", out JsonElement TypeElement))
        {
            return false;
        }

        if (!string.Equals(TypeElement.GetString(), "mergeNetVersions", StringComparison.Ordinal))
        {
            return false;
        }

        if (!Payload.TryGetProperty("value", out JsonElement ValueElement))
        {
            return false;
        }

        if (ValueElement.ValueKind == JsonValueKind.True)
        {
            MergeNetVersionsValue = true;
            return true;
        }

        if (ValueElement.ValueKind == JsonValueKind.False)
        {
            MergeNetVersionsValue = false;
            return true;
        }

        return false;
    }

    private static bool TryCopyPngDataUrlToClipboard(string? DataUrl, out string? ErrorMessage)
    {
        ErrorMessage = null;
        const string PREFIX = "data:image/png;base64,";
        if (DataUrl is null || string.IsNullOrWhiteSpace(DataUrl) || !DataUrl.StartsWith(PREFIX, StringComparison.OrdinalIgnoreCase))
        {
            ErrorMessage = "The graph image could not be sent to the clipboard (invalid image data).";
            return false;
        }

        try
        {
            string Base64 = DataUrl.Substring(PREFIX.Length);
            byte[] Bytes = Convert.FromBase64String(Base64);
            using MemoryStream Stream = new MemoryStream(Bytes, writable: false);
            BitmapImage Bitmap = new BitmapImage();
            Bitmap.BeginInit();
            Bitmap.StreamSource = Stream;
            Bitmap.CacheOption = BitmapCacheOption.OnLoad;
            Bitmap.EndInit();
            Bitmap.Freeze();
            Clipboard.SetImage(Bitmap);
            return true;
        }
        catch (Exception CopyError)
        {
            ErrorMessage = CopyError.Message;
            return false;
        }
    }

    private static bool TryReadGraphCopyPngDataUrlFromWebMessage(string WebMessageAsJson, out string? DataUrl)
    {
        DataUrl = null;
        using (JsonDocument Document = JsonDocument.Parse(WebMessageAsJson))
        {
            JsonElement Root = Document.RootElement;
            if (Root.ValueKind == JsonValueKind.String)
            {
                string? Inner = Root.GetString();
                if (string.IsNullOrWhiteSpace(Inner))
                {
                    return false;
                }

                using JsonDocument InnerDocument = JsonDocument.Parse(Inner!);
                return TryReadGraphCopyPngDataUrlPayload(InnerDocument.RootElement, out DataUrl);
            }

            if (Root.ValueKind == JsonValueKind.Object)
            {
                return TryReadGraphCopyPngDataUrlPayload(Root, out DataUrl);
            }
        }

        return false;
    }

    private static bool TryReadGraphCopyPngDataUrlPayload(JsonElement Payload, out string? DataUrl)
    {
        DataUrl = null;
        if (!Payload.TryGetProperty("type", out JsonElement TypeElement))
        {
            return false;
        }

        if (!string.Equals(TypeElement.GetString(), "graphCopyPngDataUrl", StringComparison.Ordinal))
        {
            return false;
        }

        if (!Payload.TryGetProperty("dataUrl", out JsonElement DataUrlElement))
        {
            return false;
        }

        DataUrl = DataUrlElement.GetString();
        return !string.IsNullOrWhiteSpace(DataUrl);
    }

    private static bool TryReadGraphCopyResultFromWebMessage(string WebMessageAsJson, out bool Ok, out string? Message)
    {
        Ok = false;
        Message = null;
        using (JsonDocument Document = JsonDocument.Parse(WebMessageAsJson))
        {
            JsonElement Root = Document.RootElement;
            if (Root.ValueKind == JsonValueKind.String)
            {
                string? Inner = Root.GetString();
                if (string.IsNullOrWhiteSpace(Inner))
                {
                    return false;
                }

                using JsonDocument InnerDocument = JsonDocument.Parse(Inner!);
                return TryReadGraphCopyResultPayload(InnerDocument.RootElement, out Ok, out Message);
            }

            if (Root.ValueKind == JsonValueKind.Object)
            {
                return TryReadGraphCopyResultPayload(Root, out Ok, out Message);
            }
        }

        return false;
    }

    private static bool TryReadGraphCopyResultPayload(JsonElement Payload, out bool Ok, out string? Message)
    {
        Ok = false;
        Message = null;
        if (!Payload.TryGetProperty("type", out JsonElement TypeElement))
        {
            return false;
        }

        if (!string.Equals(TypeElement.GetString(), "graphCopyResult", StringComparison.Ordinal))
        {
            return false;
        }

        if (!Payload.TryGetProperty("ok", out JsonElement OkElement))
        {
            return false;
        }

        Ok = OkElement.ValueKind == JsonValueKind.True;
        if (Payload.TryGetProperty("message", out JsonElement MessageElement))
        {
            Message = MessageElement.GetString();
        }

        return true;
    }

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not SurfaceDependencyTreeNode node)
        {
            _detailsText.Text = "Select a node to inspect package details.";
            return;
        }

        SelectNode(node);
    }

    private void OnTreeItemExpanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem item && item.DataContext is SurfaceDependencyTreeNode node)
        {
            node.EnsureExpanded();
        }
    }

    private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_packageGrid.SelectedItem is PackageTableRow row)
        {
            _detailsText.Text = row.Tooltip;
        }
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnReevaluateSolutionClicked(object sender, RoutedEventArgs e)
    {
        ReevaluationRequested?.Invoke(this, new ReevaluationRequestEventArgs("Solution", _selectedProject, _selectedPackage));
    }

    private void OnReevaluateProjectClicked(object sender, RoutedEventArgs e)
    {
        ReevaluationRequested?.Invoke(this, new ReevaluationRequestEventArgs("Project", _selectedProject, _selectedPackage));
    }

    private void OnReevaluatePackageClicked(object sender, RoutedEventArgs e)
    {
        ReevaluationRequested?.Invoke(this, new ReevaluationRequestEventArgs("Package", _selectedProject, _selectedPackage));
    }

    private IReadOnlyList<SurfaceDependencyTreeNode> BuildProjectChildren(SurfaceProjectSnapshot project)
    {
        SurfaceProjectModel model = _projectModels[GetProjectKey(project)];
        return model.RootPackages.Select(package => BuildPackageNode(project, model, package, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase))).ToArray();
    }

    private SurfaceDependencyTreeNode BuildPackageNode(SurfaceProjectSnapshot project, SurfaceProjectModel model, SurfacePackageReference package, int depth, HashSet<string> visited)
    {
        string stableKey = package.StablePackageInstanceKey ?? string.Empty;
        HashSet<string> nextVisited = new(visited, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(stableKey))
        {
            nextVisited.Add(stableKey);
        }

        string PackageRowBand = TreeRowVisualBandForPackage(package);
        SurfaceDependencyTreeNode node = new SurfaceDependencyTreeNode(project, package, $"{package.PackageId} ({package.ResolvedVersion ?? package.RequestedVersion ?? "unknown"})", BuildPackageTooltip(package, depth), () => BuildPackageChildren(project, model, depth, nextVisited, stableKey), PackageRowBand);

        if (model.OutgoingEdges.ContainsKey(stableKey))
        {
            node.AddPlaceholder();
        }

        return node;
    }

    private IReadOnlyList<SurfaceDependencyTreeNode> BuildPackageChildren(SurfaceProjectSnapshot project, SurfaceProjectModel model, int depth, HashSet<string> visited, string stableKey)
    {
        if (!model.OutgoingEdges.TryGetValue(stableKey, out List<SurfaceDependencyEdge>? edges))
        {
            return Array.Empty<SurfaceDependencyTreeNode>();
        }

        List<SurfaceDependencyTreeNode> children = new();
        foreach (SurfaceDependencyEdge edge in edges.OrderBy(static edge => edge.ToPackageId, StringComparer.OrdinalIgnoreCase))
        {
            string? targetKey = edge.ToStablePackageInstanceKey;
            if (string.IsNullOrWhiteSpace(targetKey) || !model.PackagesByKey.TryGetValue(targetKey!, out SurfacePackageReference? childPackage))
            {
                children.Add(new SurfaceDependencyTreeNode(project, null, edge.ToPackageId ?? "(unknown package)", $"Dependency edge target could not be resolved from the snapshot. .NET version: {TargetFrameworkDisplay.Format(edge.TargetFrameworkMoniker)}"));
                continue;
            }

            string childKey = childPackage.StablePackageInstanceKey ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(childKey) && visited.Contains(childKey))
            {
                string CycleRowBand = TreeRowVisualBandForPackage(childPackage);
                children.Add(new SurfaceDependencyTreeNode(project, childPackage, $"{childPackage.PackageId} ({childPackage.ResolvedVersion ?? childPackage.RequestedVersion ?? "unknown"})", $"{BuildPackageTooltip(childPackage, depth + 1)}{Environment.NewLine}This node closes a repeated path in the dependency graph.", null, CycleRowBand));
                continue;
            }

            children.Add(BuildPackageNode(project, model, childPackage, depth + 1, visited));
        }

        return children;
    }

    private void SelectNode(SurfaceDependencyTreeNode node)
    {
        _selectedProject = node.Project;
        _selectedPackage = node.Package;
        _detailsText.Text = node.Tooltip;

        IReadOnlyList<PackageTableRow> rows = node.Package is null
            ? GetRowsForProject(node.Project)
            : BuildSubtreeRows(node);

        _visiblePackageRows.Clear();
        foreach (PackageTableRow row in rows)
        {
            _visiblePackageRows.Add(row);
        }

        QueueGraphRender(BuildGraphPayload(node.Project, node.Package));
        UpdateCommandState();
    }

    private IReadOnlyList<PackageTableRow> BuildSubtreeRows(SurfaceDependencyTreeNode node)
    {
        HashSet<string> stableKeys = new(StringComparer.OrdinalIgnoreCase);
        CollectStableKeys(node, stableKeys);
        if (!_mergeNetVersions)
        {
            return GetRowsForProject(node.Project)
                .Where(row => stableKeys.Contains(row.StablePackageInstanceKey))
                .ToArray();
        }

        if (node.Project is null)
        {
            return Array.Empty<PackageTableRow>();
        }

        Dictionary<string, string> remap = GetMergeRemap(node.Project);
        HashSet<string> canonicalKeys = new(StringComparer.OrdinalIgnoreCase);
        foreach (string stableKey in stableKeys)
        {
            string canonical = ExplorerSurfacePackageMerge.RemapStablePackageInstanceKey(stableKey, remap);
            if (!string.IsNullOrWhiteSpace(canonical))
            {
                canonicalKeys.Add(canonical);
            }
        }

        return GetRowsForProject(node.Project)
            .Where(row => canonicalKeys.Contains(row.StablePackageInstanceKey))
            .ToArray();
    }

    private IReadOnlyList<PackageTableRow> GetRowsForProject(SurfaceProjectSnapshot? project)
    {
        string key = GetProjectKey(project);
        return _rowsByProject.TryGetValue(key, out IReadOnlyList<PackageTableRow>? rows)
            ? rows
            : Array.Empty<PackageTableRow>();
    }

    private static void CollectStableKeys(SurfaceDependencyTreeNode node, ISet<string> stableKeys)
    {
        string? stablePackageInstanceKey = node.Package?.StablePackageInstanceKey;
        if (!string.IsNullOrWhiteSpace(stablePackageInstanceKey))
        {
            stableKeys.Add(stablePackageInstanceKey!);
        }

        node.EnsureExpanded();
        foreach (SurfaceDependencyTreeNode child in node.Children.Where(static child => !child.IsPlaceholder))
        {
            CollectStableKeys(child, stableKeys);
        }
    }

    private IReadOnlyList<PackageTableRow> BuildRowsForDisplay(SurfaceProjectSnapshot canonicalProject)
    {
        IReadOnlyList<SurfacePackageReference> sourcePackages = canonicalProject.Packages;
        if (_mergeNetVersions)
        {
            SurfaceProjectSnapshot mergedView = ExplorerSurfacePackageMerge.BuildMergedProjectSnapshot(canonicalProject);
            sourcePackages = mergedView.Packages;
        }

        return BuildRowsFromPackages(canonicalProject, sourcePackages);
    }

    private IReadOnlyList<PackageTableRow> BuildRowsFromPackages(SurfaceProjectSnapshot project, IReadOnlyList<SurfacePackageReference> packages)
    {
        return packages
            .OrderBy(static package => package.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static package => package.TargetFrameworkMoniker, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static package => package.StablePackageInstanceKey, StringComparer.OrdinalIgnoreCase)
            .Select(package =>
            {
                string healthSummary = PackageHealthSummary.Describe(package.HealthInfo);
                string rowVisualBand = PackageHealthRowVisualBand.Compute(package.RiskBand, package.AlertBand, healthSummary);
                return new PackageTableRow(
                    project.ProjectName ?? "(unknown project)",
                    package.PackageId ?? "(unknown package)",
                    BuildInstanceKeyShort(package.StablePackageInstanceKey),
                    package.ReferenceKind ?? "-",
                    package.RequestedVersion ?? "-",
                    package.ResolvedVersion ?? "-",
                    healthSummary,
                    DescribeRisk(package),
                    DescribeAlert(package),
                    package.RemediationSummary ?? "-",
                    package.DependencyParents.Count == 0 ? "-" : string.Join(", ", package.DependencyParents),
                    package.DependencyPath.Count == 0 ? "-" : string.Join(" -> ", package.DependencyPath),
                    package.TargetFrameworkMoniker ?? "-",
                    package.StablePackageInstanceKey ?? string.Empty,
                    BuildPackageTooltip(package, package.DependencyPath.Count == 0 ? 0 : package.DependencyPath.Count - 1),
                    rowVisualBand);
            })
            .ToArray();
    }

    private Dictionary<string, string> GetMergeRemap(SurfaceProjectSnapshot project)
    {
        string key = GetProjectKey(project);
        if (!_mergeRemapByProjectKey.TryGetValue(key, out Dictionary<string, string>? remap))
        {
            (_, remap) = ExplorerSurfacePackageMerge.MergeDuplicateRows(
                project.Packages.ToList(),
                project.SolutionPath ?? string.Empty,
                project.ProjectPath ?? string.Empty,
                mergeNetVersions: true);
            _mergeRemapByProjectKey[key] = remap;
        }

        return remap;
    }

    private SurfaceProjectModel ResolveGraphModel(SurfaceProjectSnapshot project)
    {
        if (!_mergeNetVersions)
        {
            return _projectModels[GetProjectKey(project)];
        }

        SurfaceProjectSnapshot mergedView = ExplorerSurfacePackageMerge.BuildMergedProjectSnapshot(project);
        return SurfaceProjectModel.Create(mergedView);
    }

    private GraphPayload BuildGraphPayload(SurfaceProjectSnapshot? project, SurfacePackageReference? selectedPackage)
    {
        if (project is null)
        {
            return new GraphPayload(string.Empty, Array.Empty<GraphNode>(), Array.Empty<GraphEdge>(), string.Empty);
        }

        SurfaceProjectModel model = ResolveGraphModel(project);
        HashSet<string> focusKeys = new(StringComparer.OrdinalIgnoreCase);
        string selectedKey = selectedPackage?.StablePackageInstanceKey ?? string.Empty;
        if (_mergeNetVersions && !string.IsNullOrWhiteSpace(selectedKey))
        {
            selectedKey = ExplorerSurfacePackageMerge.RemapStablePackageInstanceKey(selectedKey, GetMergeRemap(project));
        }

        if (!string.IsNullOrWhiteSpace(selectedKey))
        {
            Queue<string> queue = new();
            queue.Enqueue(selectedKey);
            while (queue.Count > 0)
            {
                string current = queue.Dequeue();
                if (!focusKeys.Add(current))
                {
                    continue;
                }

                if (model.OutgoingEdges.TryGetValue(current, out List<SurfaceDependencyEdge>? edges))
                {
                    foreach (SurfaceDependencyEdge edge in edges)
                    {
                        if (!string.IsNullOrWhiteSpace(edge.ToStablePackageInstanceKey))
                        {
                            queue.Enqueue(edge.ToStablePackageInstanceKey!);
                        }
                    }
                }

                foreach (SurfaceDependencyEdge edge in model.DependencyEdges.Where(edge => string.Equals(edge.ToStablePackageInstanceKey, current, StringComparison.OrdinalIgnoreCase)))
                {
                    if (!string.IsNullOrWhiteSpace(edge.FromStablePackageInstanceKey))
                    {
                        queue.Enqueue(edge.FromStablePackageInstanceKey!);
                    }
                }
            }
        }

        List<GraphNode> PackageGraphNodes = new();
#if DEBUG
        Dictionary<string, int> bandCounts = new(StringComparer.OrdinalIgnoreCase);
        StringBuilder styleLog = new StringBuilder();
#endif
        foreach (SurfacePackageReference package in model.PackagesByKey.Values
            .Where(package => focusKeys.Count == 0 || focusKeys.Contains(package.StablePackageInstanceKey ?? string.Empty)))
        {
            string healthSummary = PackageHealthSummary.Describe(package.HealthInfo);
            bool isSelected = string.Equals(package.StablePackageInstanceKey, selectedKey, StringComparison.OrdinalIgnoreCase);
            string RowVisualBand = PackageHealthRowVisualBand.Compute(package.RiskBand, package.AlertBand, healthSummary);
#if DEBUG
            bandCounts[RowVisualBand] = bandCounts.TryGetValue(RowVisualBand, out int existing) ? existing + 1 : 1;
            string packageLabel = package.PackageId ?? "(unknown)";
            styleLog.AppendLine(
                $"{packageLabel}: health=\"{healthSummary}\", riskBand={package.RiskBand}, alertBand={package.AlertBand}, selected={isSelected} -> rowVisualBand={RowVisualBand}; {DescribeExplorerGraphBandReason(RowVisualBand, healthSummary, package)}");
#endif
            bool IsTransitiveNode = string.Equals(package.ReferenceKind, "Transitive", StringComparison.OrdinalIgnoreCase);
            PackageGraphNodes.Add(new GraphNode(
                package.StablePackageInstanceKey ?? $"{package.PackageId}|{package.TargetFrameworkMoniker}",
                $"{package.PackageId}\n{package.ResolvedVersion ?? package.RequestedVersion ?? "unknown"}",
                TargetFrameworkDisplay.FormatMergedRowMonikers(package.TargetFrameworkMoniker),
                healthSummary,
                DescribeRisk(package),
                DescribeAlert(package),
                isSelected,
                package.ReferenceKind ?? "-",
                BuildPackageTooltip(package, package.DependencyPath.Count == 0 ? 0 : package.DependencyPath.Count - 1),
                RowVisualBand,
                IsTransitiveNode));
        }

        List<GraphNode> graphNodes = new();
        string? ProjectRootNodeId = null;
        if (selectedPackage is null)
        {
            ProjectRootNodeId = BuildGraphProjectRootNodeId(project);
            string ProjectMonikersDisplay = TargetFrameworkDisplay.FormatProjectMonikers(project.TargetFrameworks);
            graphNodes.Add(new GraphNode(
                ProjectRootNodeId,
                $"{project.ProjectName ?? "(project)"}\nProject",
                ProjectMonikersDisplay,
                "-",
                "-",
                "-",
                true,
                "Project",
                BuildProjectGraphTooltip(project),
                "None",
                false));
        }

        graphNodes.AddRange(PackageGraphNodes);
        GraphNode[] nodes = graphNodes.ToArray();

#if DEBUG
        string summary = string.Join(", ", bandCounts.OrderBy(static kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(static kv => $"{kv.Key}={kv.Value}"));
        string graphHeader = $"Dependency graph styles for '{project.ProjectName}' ({nodes.Length} nodes incl. project root). Package nodes: {PackageGraphNodes.Count}. Summary: {summary}.";
        EmitGraphStyleDebug($"{graphHeader}{Environment.NewLine}{styleLog}");
#endif

        HashSet<string> nodeIds = nodes.Select(static node => node.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> packageNodeIds = PackageGraphNodes.Select(static node => node.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<GraphEdge> graphEdgesList = model.DependencyEdges
            .Where(edge => !string.IsNullOrWhiteSpace(edge.FromStablePackageInstanceKey)
                        && !string.IsNullOrWhiteSpace(edge.ToStablePackageInstanceKey)
                        && nodeIds.Contains(edge.FromStablePackageInstanceKey!)
                        && nodeIds.Contains(edge.ToStablePackageInstanceKey!))
            .Select(edge => new GraphEdge(
                edge.FromStablePackageInstanceKey!,
                edge.ToStablePackageInstanceKey!,
                string.Empty,
                IsTransitivePackageReference(model, edge.ToStablePackageInstanceKey)))
            .ToList();

        if (ProjectRootNodeId is not null)
        {
            foreach (string RootPackageKey in ResolveGraphRootPackageKeys(model, packageNodeIds))
            {
                graphEdgesList.Add(new GraphEdge(ProjectRootNodeId, RootPackageKey, string.Empty, IsTransitivePackageReference(model, RootPackageKey)));
            }
        }

        GraphEdge[] graphEdges = graphEdgesList.ToArray();
        return new GraphPayload(project.ProjectName ?? "(unknown project)", nodes, graphEdges, project.ProjectPath ?? string.Empty);
    }

    private static string BuildGraphProjectRootNodeId(SurfaceProjectSnapshot project)
    {
        return "\uE000project:" + (project.ProjectPath ?? project.ProjectName ?? "default");
    }

    private static string BuildProjectGraphTooltip(SurfaceProjectSnapshot project)
    {
        string Monikers = TargetFrameworkDisplay.FormatProjectMonikers(project.TargetFrameworks);
        return $"Project: {project.ProjectName ?? "(unknown)"}{Environment.NewLine}Path: {project.ProjectPath ?? "(no path)"}{Environment.NewLine}.NET: {Monikers}";
    }

    /// <summary>
    /// Package stable keys that are direct children of the project in the graph (for edges from the synthetic project node).
    /// </summary>
    private static bool IsTransitivePackageReference(SurfaceProjectModel model, string? toStablePackageInstanceKey)
    {
        if (string.IsNullOrWhiteSpace(toStablePackageInstanceKey))
        {
            return false;
        }

        string Key = toStablePackageInstanceKey!;
        if (!model.PackagesByKey.TryGetValue(Key, out SurfacePackageReference? package))
        {
            return false;
        }

        return string.Equals(package.ReferenceKind, "Transitive", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ResolveGraphRootPackageKeys(SurfaceProjectModel model, HashSet<string> includedPackageNodeIds)
    {
        List<string> fromModelRoots = model.RootPackages
            .Select(static package => package.StablePackageInstanceKey)
            .Where(key => !string.IsNullOrWhiteSpace(key) && includedPackageNodeIds.Contains(key!))
            .Select(static key => key!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (fromModelRoots.Count > 0)
        {
            return fromModelRoots;
        }

        HashSet<string> incomingWithinGraph = new(StringComparer.OrdinalIgnoreCase);
        foreach (SurfaceDependencyEdge edge in model.DependencyEdges)
        {
            if (string.IsNullOrWhiteSpace(edge.FromStablePackageInstanceKey) || string.IsNullOrWhiteSpace(edge.ToStablePackageInstanceKey))
            {
                continue;
            }

            if (includedPackageNodeIds.Contains(edge.FromStablePackageInstanceKey!)
                && includedPackageNodeIds.Contains(edge.ToStablePackageInstanceKey!))
            {
                incomingWithinGraph.Add(edge.ToStablePackageInstanceKey!);
            }
        }

        return includedPackageNodeIds.Where(id => !incomingWithinGraph.Contains(id)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private void QueueGraphRender(GraphPayload payload)
    {
        _pendingPayload = JsonSerializer.Serialize(payload, GraphPayloadJsonOptions);
        if (_graphReady && !string.IsNullOrWhiteSpace(_pendingPayload))
        {
            _ = _graphView.ExecuteScriptAsync($"window.renderGraph({JsonSerializer.Serialize(_pendingPayload)});");
        }
    }

    private static string GetProjectKey(SurfaceProjectSnapshot? project)
        => project?.ProjectPath ?? project?.ProjectName ?? string.Empty;

    private static string BuildInstanceKeyShort(string? stablePackageInstanceKey)
    {
        if (string.IsNullOrWhiteSpace(stablePackageInstanceKey))
        {
            return "-";
        }

        string[] parts = stablePackageInstanceKey!.Split('|');
        return parts.Length == 0 ? stablePackageInstanceKey! : parts[parts.Length - 1];
    }

    private static string BuildDotNetVersionDetailLine(string? rawMoniker)
    {
        string Display = TargetFrameworkDisplay.FormatMergedRowMonikers(rawMoniker);
        string? TrimmedRaw = rawMoniker?.Trim();
        if (string.IsNullOrEmpty(TrimmedRaw)
            || string.Equals(TrimmedRaw, "-", StringComparison.Ordinal)
            || string.Equals(Display, TrimmedRaw, StringComparison.Ordinal))
        {
            return $".NET version: {Display}";
        }

        return $".NET version: {Display}{Environment.NewLine}Moniker: {rawMoniker}";
    }

    private static string BuildPackageTooltip(SurfacePackageReference package, int depth)
    {
        List<string> lines =
        [
            $"Package: {package.PackageId ?? "(unknown)"}",
            $"Depth: {depth}",
            $"Kind: {package.ReferenceKind ?? "-"}",
            $"Requested: {package.RequestedVersion ?? "-"}",
            $"Resolved: {package.ResolvedVersion ?? "-"}",
            BuildDotNetVersionDetailLine(package.TargetFrameworkMoniker),
            $"Central version managed: {(package.IsCentralVersionManaged ? "Yes" : "No")}",
            $"Parents: {(package.DependencyParents.Count == 0 ? "-" : string.Join(", ", package.DependencyParents))}",
            $"Path: {(package.DependencyPath.Count == 0 ? "-" : string.Join(" -> ", package.DependencyPath))}",
            $"Health: {PackageHealthSummary.Describe(package.HealthInfo)}",
            $"Risk: {DescribeRisk(package)}",
            $"Alert: {DescribeAlert(package)}",
            $"Remediation: {package.RemediationSummary ?? "-"}",
            $"Knowledge determined: {package.KnowledgeDeterminedUtc?.ToString("O") ?? "-"}",
            $"Status changed: {package.KnowledgeStatusChangedUtc?.ToString("O") ?? "-"}"
        ];

        if (package.HealthInfo.Vulnerabilities.Count > 0)
        {
            lines.Add("Vulnerabilities:");
            lines.AddRange(package.HealthInfo.Vulnerabilities.Select(static vulnerability => $"- {vulnerability.Severity}: {vulnerability.AdvisoryUrl}"));
        }

        if (!string.IsNullOrWhiteSpace(package.HealthInfo.DeprecationMessage))
        {
            lines.Add($"Deprecation: {package.HealthInfo.DeprecationMessage}");
        }

        if (!string.IsNullOrWhiteSpace(package.HealthInfo.AlternatePackageId))
        {
            lines.Add($"Alternate package: {package.HealthInfo.AlternatePackageId} {package.HealthInfo.AlternatePackageRange}".Trim());
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildProjectTreeTooltip(SurfaceProjectSnapshot project, ProjectRollup rollup)
    {
        string Monikers = TargetFrameworkDisplay.FormatProjectMonikers(project.TargetFrameworks);
        List<string> Lines =
        [
            $"Project: {project.ProjectName ?? "(unknown)"}",
            $"Path: {project.ProjectPath ?? "(no path)"}",
            $".NET version: {Monikers}",
            $"Health: {rollup.HealthSummary}",
            $"Risk: {rollup.RiskSummary}",
            $"Alert: {rollup.AlertSummary}",
            $"Packages: {rollup.TotalPackageCount} instance(s); {rollup.RemediationPackageCount} need remediation"
        ];
        return string.Join(Environment.NewLine, Lines);
    }

    private static string TreeRowVisualBandForPackage(SurfacePackageReference package)
    {
        string HealthSummary = PackageHealthSummary.Describe(package.HealthInfo);
        return PackageHealthRowVisualBand.Compute(package.RiskBand, package.AlertBand, HealthSummary);
    }

#if DEBUG
    private static string DescribeExplorerGraphBandReason(string rowVisualBand, string healthSummary, SurfacePackageReference package)
    {
        return $"Matches package table / Issues row styling (PackageHealthRowVisualBand): band={rowVisualBand} for riskBand={package.RiskBand}, alertBand={package.AlertBand}, health=\"{healthSummary}\".";
    }

    private static void EmitGraphStyleDebug(string message, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        string className = Path.GetFileNameWithoutExtension(filePath);
        int threadId = Environment.CurrentManagedThreadId;
        string threadKind = "UNK";
        using StringReader reader = new StringReader(message);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            Trace.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} [{threadKind}:T{threadId}] [NuGetAudit:Graph] {className}.{memberName}:{lineNumber} - {line}");
        }
    }
#endif

    private static string DescribeRisk(SurfacePackageReference package)
        => package.RiskScore.HasValue
            ? $"{package.RiskScore.Value:N2} ({package.RiskBand ?? "Unknown"})"
            : "-";

    private static string DescribeAlert(SurfacePackageReference package)
        => package.AlertScore.HasValue
            ? $"{package.AlertScore.Value:N2} ({package.AlertBand ?? "Unknown"})"
            : "-";

    private void UpdateCommandState()
    {
        SetAuditActionAvailability(true);
    }

    private static Style BuildPackageTableRowStyle()
    {
        Style style = new(typeof(DataGridRow));
        style.Setters.Add(new Setter(Control.ForegroundProperty, PackageRowNeutralBrush));
        style.Setters.Add(new Setter(Control.BackgroundProperty, PackageRowNeutralBackgroundBrush));

        style.Triggers.Add(BuildPackageTableRowTrigger(nameof(PackageTableRow.RowVisualBand), "Critical", PackageRowCriticalBackgroundBrush, PackageRowCriticalBrush, FontWeights.SemiBold));
        style.Triggers.Add(BuildPackageTableRowTrigger(nameof(PackageTableRow.RowVisualBand), "High", PackageRowHighBackgroundBrush, PackageRowHighBrush, FontWeights.SemiBold));
        style.Triggers.Add(BuildPackageTableRowTrigger(nameof(PackageTableRow.RowVisualBand), "Attention", PackageRowAttentionBackgroundBrush, PackageRowAttentionBrush, FontWeights.Normal));
        style.Triggers.Add(BuildPackageTableRowTrigger(nameof(PackageTableRow.RowVisualBand), "Ok", PackageRowSuccessBackgroundBrush, PackageRowSuccessBrush, FontWeights.Normal));

        return style;
    }

    private static Style BuildDependencyTreeItemContainerStyle()
    {
        Style style = new(typeof(TreeViewItem));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(2, 2, 4, 2)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, PackageRowNeutralBrush));
        style.Setters.Add(new Setter(Control.BackgroundProperty, PackageRowNeutralBackgroundBrush));

        style.Triggers.Add(BuildPackageTableRowTrigger(nameof(SurfaceDependencyTreeNode.RowVisualBand), "Critical", PackageRowCriticalBackgroundBrush, PackageRowCriticalBrush, FontWeights.SemiBold));
        style.Triggers.Add(BuildPackageTableRowTrigger(nameof(SurfaceDependencyTreeNode.RowVisualBand), "High", PackageRowHighBackgroundBrush, PackageRowHighBrush, FontWeights.SemiBold));
        style.Triggers.Add(BuildPackageTableRowTrigger(nameof(SurfaceDependencyTreeNode.RowVisualBand), "Attention", PackageRowAttentionBackgroundBrush, PackageRowAttentionBrush, FontWeights.Normal));
        style.Triggers.Add(BuildPackageTableRowTrigger(nameof(SurfaceDependencyTreeNode.RowVisualBand), "Ok", PackageRowSuccessBackgroundBrush, PackageRowSuccessBrush, FontWeights.Normal));

        return style;
    }

    private static Style BuildPackageTableTransparentCellStyle()
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

    private static DataTrigger BuildPackageTableRowTrigger(string propertyName, object value, Brush background, Brush foreground, FontWeight fontWeight)
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

    private static SolidColorBrush CreatePresentationBrush(byte r, byte g, byte b)
    {
        SolidColorBrush brush = new(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
