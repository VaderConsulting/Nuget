using Microsoft.Win32;
using NuGetAudit.Core;
using NuGetAudit.Presentation;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace NuGetAudit.Workbench;

public partial class MainWindow : Window
{
    private static readonly string[] SupportedExtensions = [".sln", ".slnx", ".csproj", ".vbproj", ".fsproj"];

    private readonly NuGetAuditRunner _runner = new();
    private readonly SnapshotTopologyStore _topologyStore = new();
    private readonly NuGetChangeMonitor _changeMonitor;
    private readonly AuditCatalogStore _catalogStore = new();
    private readonly ObservableCollection<AuditCatalogEntry> _catalogEntries = new();
    private readonly ObservableCollection<CatalogTreeNode> _catalogTreeNodes = new();
    private readonly ObservableCollection<FolderViewRow> _folderViewRows = new();
    private readonly ObservableCollection<SolutionViewRowModel> _solutionViewRows = new();
    private readonly ObservableCollection<PackageViewRow> _packageViewRows = new();
    private readonly ObservableCollection<AuditQueueRow> _queueRows = new();
    private readonly Dictionary<string, string> _auditStatusByInputPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _auditFailureByInputPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _auditStatusLock = new();
    private IReadOnlyList<ScannedFolderRecord> _scannedFolders = Array.Empty<ScannedFolderRecord>();
    private IReadOnlyList<AuditCatalogEntry> _pendingScopedEntries = Array.Empty<AuditCatalogEntry>();
    private CatalogTreeNode? _selectedCatalogNode;
    private bool _isAuditRunning;
    private bool _isBackgroundAuditRunning;
    private bool _startupLoadCompleted;
    private CancellationTokenSource? _backgroundAuditCts;
    private int _statusRefreshVersion;
    private int _catalogLoadVersion;
    private int _tabLoadVersion;
    private int _mainTabSelectedIndex = -1;
    private int _catalogDataSubTabSelectedIndex = -1;
    private string? _loadedExplorerScopeKey;
    private string? _loadedHistoryScopeKey;
    private string? _loadedIssueScopeKey;

    public MainWindow()
    {
        InitializeComponent();
        _mainTabSelectedIndex = MainTabControl.SelectedIndex;
        _catalogDataSubTabSelectedIndex = CatalogDataTabControl.SelectedIndex;
        DebugLog.RegisterUiThread();
        DebugLog.Write("Workbench", "Main window initialized.");
        _changeMonitor = new NuGetChangeMonitor(OnNuGetChangesSettled);
        CatalogTreeView.ItemsSource = _catalogTreeNodes;
        FolderCatalogGrid.ItemsSource = _folderViewRows;
        DiscoverPathsListBox.ItemsSource = _folderViewRows;
        CatalogGrid.ItemsSource = _solutionViewRows;
        PackageCatalogGrid.ItemsSource = _packageViewRows;
        QueueGrid.ItemsSource = _queueRows;
        CatalogDataTabControl.SelectionChanged += CatalogDataTabControl_SelectionChanged;
        ExplorerSurface.RefreshRequested += OnExplorerRefreshRequested;
        ExplorerSurface.ReevaluationRequested += OnExplorerReevaluationRequested;
        ExplorerSurface.MergeNetVersionsChanged += OnExplorerMergeNetVersionsChanged;
        string? repoRoot = FindRepositoryRoot();
        if (repoRoot is not null)
        {
            string defaultInput = Path.Combine(repoRoot, "NuGetAudit.slnx");
            InputPathTextBox.Text = defaultInput;
            OutputDirectoryTextBox.Text = GetDefaultOutputDirectory(defaultInput);
        }
        UpdateBusyState();
    }

    private async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        DebugLog.Write("Workbench", $"Manual audit requested for '{InputPathTextBox.Text}'.");
        string? inputPath = NormalizeAndValidateInputPath(InputPathTextBox.Text);
        if (inputPath is null)
        {
            string? raw = InputPathTextBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                try
                {
                    string candidate = Path.GetFullPath(raw);
                    if (File.Exists(candidate) && AuditProjectDisplayName.IsUnderVsHistoryFolder(candidate))
                    {
                        MessageBox.Show(
                            this,
                            "Projects and solutions under Visual Studio local file history (.vshistory) are not audited.",
                            "NuGet Audit Workbench",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        return;
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            MessageBox.Show(this, "Choose a .sln, .slnx, .csproj, .vbproj, or .fsproj file before running the audit.", "NuGet Audit Workbench", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string outputDirectory = string.IsNullOrWhiteSpace(OutputDirectoryTextBox.Text)
            ? GetDefaultOutputDirectory(inputPath)
            : Path.GetFullPath(OutputDirectoryTextBox.Text);

        RunButton.IsEnabled = false;
        _isAuditRunning = true;
        UpdateBusyState();
        if (inputPath is not null)
        {
            SetAuditStatus(inputPath, "auditing");
        }
        _changeMonitor.CancelPending();
        ClearFailureDetails();
        SummaryTextBlock.Text = "Running audit...";
        ComparisonTextBlock.Text = "The workbench is invoking the same core runner used by the CLI.";

        try
        {
            AuditCommandOptions options = new(inputPath!, outputDirectory)
            {
                WriteJsonCompanion = true,
                WriteMarkdownReport = true
            };

            AuditRunResult result = await _runner.RunAsync(options, CancellationToken.None);
            SetAuditStatus(inputPath!, "complete");
            ClearAuditFailure(inputPath!);
            LoadResult(result);
        }
        catch (Exception ex)
        {
            if (inputPath is not null)
            {
                SetAuditStatus(inputPath, "failed");
                SetAuditFailure(inputPath, ex);
            }
            ShowFailureDetails($"Audit failed for {inputPath}.", ex);
        }
        finally
        {
            _isAuditRunning = false;
            UpdateBusyState();
        }
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Filter = "Supported inputs|*.sln;*.slnx;*.csproj;*.vbproj;*.fsproj|Solutions|*.sln;*.slnx|Projects|*.csproj;*.vbproj;*.fsproj|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            InputPathTextBox.Text = dialog.FileName;
            OutputDirectoryTextBox.Text = GetDefaultOutputDirectory(dialog.FileName);
            LoadCatalogScopedViews();
        }
    }

    private async void DiscoverAddButton_Click(object sender, RoutedEventArgs e)
    {
        string RawPath = DiscoverPathTextBox.Text?.Trim() ?? string.Empty;
        string? FolderPath = null;
        if (string.IsNullOrWhiteSpace(RawPath))
        {
            OpenFolderDialog Dialog = new();
            if (Dialog.ShowDialog(this) != true)
            {
                return;
            }

            FolderPath = Dialog.FolderName;
        }
        else
        {
            try
            {
                string FullPath = Path.GetFullPath(RawPath);
                if (Directory.Exists(FullPath))
                {
                    FolderPath = FullPath;
                }
                else if (File.Exists(FullPath))
                {
                    MessageBox.Show(
                        this,
                        "Choose a folder path to scan, not a single file. Use the section below to run an audit on a solution or project file.",
                        "NuGet Audit Workbench",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }
                else
                {
                    MessageBox.Show(
                        this,
                        $"The path does not exist:\r\n{FullPath}",
                        "NuGet Audit Workbench",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
            }
            catch (Exception Ex)
            {
                MessageBox.Show(this, $"Invalid path: {Ex.Message}", "NuGet Audit Workbench", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        DiscoverPathTextBox.Text = string.Empty;
        await RegisterDiscoveredTargetsAsync(FolderPath!);
    }

    private void ProcessStartButton_Click(object sender, RoutedEventArgs e)
    {
        EnsureQueuedWorkRunning();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_startupLoadCompleted)
        {
            DebugLog.Write("Workbench", "Window loaded again; ensuring queued work is running.");
            EnsureQueuedWorkRunning();
            return;
        }

        _startupLoadCompleted = true;
        DebugLog.Write("Workbench", "Window loaded; starting deferred catalog bootstrap.");
        SummaryTextBlock.Text = "Loading catalog...";
        ComparisonTextBlock.Text = "Preparing the local audit catalog and queue after the window opens.";

        await Task.Yield();
        Stopwatch stopwatch = Stopwatch.StartNew();
        (IReadOnlyList<AuditCatalogEntry> targets, IReadOnlyList<ScannedFolderRecord> scannedFolders) =
            await Task.Run(() => _catalogStore.LoadAll());
        DebugLog.Write("Workbench", $"Catalog bootstrap data loaded in {stopwatch.ElapsedMilliseconds} ms. Targets={targets.Count}, Folders={scannedFolders.Count}.");

        if (!IsLoaded)
        {
            DebugLog.Write("Workbench", "Window no longer loaded; aborting startup apply.");
            return;
        }

        ReloadCatalog(targets, scannedFolders);
        await Dispatcher.InvokeAsync(EnsureQueuedWorkRunning, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedFile(e) is not null || GetDroppedDirectory(e) is not null
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        string? droppedFile = GetDroppedFile(e);
        if (droppedFile is not null)
        {
            InputPathTextBox.Text = droppedFile;
            OutputDirectoryTextBox.Text = GetDefaultOutputDirectory(droppedFile);
            LoadCatalogScopedViews();
            return;
        }

        string? droppedDirectory = GetDroppedDirectory(e);
        if (droppedDirectory is not null)
        {
            _ = RegisterDiscoveredTargetsAsync(droppedDirectory);
        }
    }

    private void OnExplorerRefreshRequested(object? sender, EventArgs e)
    {
        InvalidateDeferredLoads();
        LoadCatalogScopedViews();
    }

    private void OnExplorerMergeNetVersionsChanged(object? sender, EventArgs e)
    {
        ExplorerSurface.RefreshPackageGridAndDependencyGraphForMergePreference();
    }

    private async void OnExplorerReevaluationRequested(object? sender, ReevaluationRequestEventArgs e)
    {
        string? inputPath = NormalizeAndValidateInputPath(InputPathTextBox.Text);
        if (inputPath is null)
        {
            string? raw = InputPathTextBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                try
                {
                    string candidate = Path.GetFullPath(raw);
                    if (File.Exists(candidate) && AuditProjectDisplayName.IsUnderVsHistoryFolder(candidate))
                    {
                        MessageBox.Show(
                            this,
                            "Projects and solutions under Visual Studio local file history (.vshistory) are not audited.",
                            "NuGet Audit Workbench",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        return;
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            MessageBox.Show(this, "Choose an input file before re-evaluating.", "NuGet Audit Workbench", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string outputDirectory = string.IsNullOrWhiteSpace(OutputDirectoryTextBox.Text)
            ? GetDefaultOutputDirectory(inputPath)
            : Path.GetFullPath(OutputDirectoryTextBox.Text);

        string scopeNote = e.Scope switch
        {
            "Package" when e.Package is not null => $"Package-only re-evaluation is not wired yet, so the workbench will rerun the full audit and refresh shared knowledge for {e.Package.PackageId} {e.Package.ResolvedVersion ?? e.Package.RequestedVersion ?? "-"} across all matching instances.",
            "Project" when e.Project is not null => $"Project-only re-evaluation is not wired yet, so the workbench will rerun the full audit and refresh shared knowledge for project {e.Project.ProjectName ?? "(unknown)"} as part of the solution snapshot.",
            _ => "Re-evaluating the current input."
        };

        ComparisonTextBlock.Text = scopeNote;
        RunButton.IsEnabled = false;
        _isAuditRunning = true;
        UpdateBusyState();
        _changeMonitor.CancelPending();
        ClearFailureDetails();
        SetAuditStatus(inputPath, "auditing");

        try
        {
            AuditRunResult result = await _runner.RunAsync(new AuditCommandOptions(inputPath!, outputDirectory)
            {
                WriteJsonCompanion = true,
                WriteMarkdownReport = true
            }, CancellationToken.None);
            SetAuditStatus(inputPath, "complete");
            ClearAuditFailure(inputPath);
            LoadResult(result);
        }
        catch (Exception ex)
        {
            SetAuditStatus(inputPath!, "failed");
            SetAuditFailure(inputPath!, ex);
            ShowFailureDetails($"Re-evaluation failed for {inputPath}.", ex);
        }
        finally
        {
            _isAuditRunning = false;
            UpdateBusyState();
        }
    }

    private void LoadResult(AuditRunResult result)
    {
        string comparison = !string.IsNullOrWhiteSpace(result.Delta.ComparisonNote)
            ? result.Delta.ComparisonNote!
            : result.PreviousSnapshot is null
                ? "No previous accepted snapshot was available for comparison."
                : $"Detected {result.Delta.Changes.Count} package changes and {result.Delta.DependencyEdgeChanges.Count} dependency-edge changes.";

        SummaryTextBlock.Text = $"Snapshot {result.CurrentSnapshot.SnapshotId} analysed {result.CurrentSnapshot.Projects.Count} projects and discovered {result.CurrentSnapshot.Projects.Sum(static project => project.Packages.Count)} package instances.";
        ComparisonTextBlock.Text = comparison;
        ClearFailureDetails();
        ReportPathsTextBlock.Text = $"Snapshot JSON: {result.ReportSet.SnapshotJsonPath}{Environment.NewLine}Markdown: {result.ReportSet.MarkdownReportPath ?? "(not written)"}{Environment.NewLine}Delta JSON: {result.ReportSet.DeltaJsonPath ?? "(not written)"}";
        GeneratedFilesTextBlock.Text = ReportPathsTextBlock.Text;
        MarkdownPreviewTextBox.Text = BuildMarkdownPreviewText(result.ReportSet.MarkdownReportPath);
        LoadCatalogScopedViews();
        string? inputPath = NormalizeAndValidateInputPath(InputPathTextBox.Text);
        if (inputPath is not null)
        {
            _changeMonitor.Track(inputPath, Path.GetDirectoryName(result.ReportSet.SnapshotJsonPath) ?? OutputDirectoryTextBox.Text);
        }

        RegisterKnownTarget(inputPath, Path.GetDirectoryName(result.ReportSet.SnapshotJsonPath) ?? OutputDirectoryTextBox.Text);
    }

    private static string BuildMarkdownPreviewText(string? markdownReportPath)
    {
        if (string.IsNullOrWhiteSpace(markdownReportPath) || !File.Exists(markdownReportPath))
        {
            return "No markdown report was generated for this run.";
        }

        try
        {
            return File.ReadAllText(markdownReportPath);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            string detail =
                $"Could not read markdown report '{markdownReportPath}'. {exception.GetType().Name}: {exception.Message}. Mitigation: showing this message in the preview instead of report content.";
            DebugLog.WriteTrace("Workbench", detail);
            return $"Could not open markdown report: {exception.Message}";
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _backgroundAuditCts?.Cancel();
        _changeMonitor.Dispose();
        base.OnClosed(e);
    }

    private async void OnNuGetChangesSettled(string reason)
    {
        if (_isAuditRunning)
        {
            return;
        }

        string? inputPath = NormalizeAndValidateInputPath(InputPathTextBox.Text);
        if (inputPath is null)
        {
            return;
        }

        string outputDirectory = string.IsNullOrWhiteSpace(OutputDirectoryTextBox.Text)
            ? GetDefaultOutputDirectory(inputPath)
            : Path.GetFullPath(OutputDirectoryTextBox.Text);

        ComparisonTextBlock.Text = reason;
        RunButton.IsEnabled = false;
        _isAuditRunning = true;
        UpdateBusyState();
        ClearFailureDetails();
        SetAuditStatus(inputPath, "auditing");

        try
        {
            AuditRunResult result = await _runner.RunAsync(new AuditCommandOptions(inputPath!, outputDirectory)
            {
                WriteJsonCompanion = true,
                WriteMarkdownReport = true
            }, CancellationToken.None);
            SetAuditStatus(inputPath, "complete");
            ClearAuditFailure(inputPath);
            LoadResult(result);
        }
        catch (Exception ex)
        {
            SetAuditStatus(inputPath, "failed");
            SetAuditFailure(inputPath, ex);
            ShowFailureDetails($"Automatic re-evaluation failed for {inputPath}.", ex);
        }
        finally
        {
            _isAuditRunning = false;
            UpdateBusyState();
        }
    }

    private static string? GetDroppedFile(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return null;
        }

        string[]? files = e.Data.GetData(DataFormats.FileDrop) as string[];
        string? first = files?.FirstOrDefault();
        return NormalizeAndValidateInputPath(first);
    }

    private static string? GetDroppedDirectory(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return null;
        }

        string[]? files = e.Data.GetData(DataFormats.FileDrop) as string[];
        string? first = files?.FirstOrDefault();
        return !string.IsNullOrWhiteSpace(first) && Directory.Exists(first)
            ? Path.GetFullPath(first)
            : null;
    }

    private static string? NormalizeAndValidateInputPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            return null;
        }

        if (AuditProjectDisplayName.IsUnderVsHistoryFolder(fullPath))
        {
            return null;
        }

        string extension = Path.GetExtension(fullPath);
        return SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) ? fullPath : null;
    }

    private static string GetDefaultOutputDirectory(string inputPath)
    {
        string fullPath = Path.GetFullPath(inputPath);
        string baseDirectory = Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory;
        return Path.Combine(baseDirectory, ".nugetaudit");
    }

    private static string? FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(Environment.CurrentDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NuGetAudit.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private void CatalogTreeView_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (CatalogTreeAuditMenuItem is not null)
        {
            CatalogTreeAuditMenuItem.IsEnabled = CanAuditNode(_selectedCatalogNode) && !IsUiBusy();
        }
    }

    private async void CatalogTreeAuditMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCatalogNode is null)
        {
            return;
        }

        await QueueAuditForNodeAsync(_selectedCatalogNode);
    }

    private async void ScopeAuditButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCatalogNode is null)
        {
            return;
        }

        await QueueAuditForNodeAsync(_selectedCatalogNode);
    }

    private void CatalogTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not CatalogTreeNode node)
        {
            return;
        }

        _selectedCatalogNode = node;
        DebugLog.Write("Catalog", $"Tree selection changed to '{node.Title}' ({node.NodeType}).");
        if (node.Entry is not null)
        {
            SolutionViewRowModel? solutionRow = _solutionViewRows.FirstOrDefault(row =>
                string.Equals(row.CatalogInputPath, node.Entry.InputPath, StringComparison.OrdinalIgnoreCase));
            if (solutionRow is not null)
            {
                CatalogGrid.SelectedItem = solutionRow;
                CatalogGrid.ScrollIntoView(solutionRow);
            }
            InputPathTextBox.Text = node.Entry.InputPath;
            OutputDirectoryTextBox.Text = node.Entry.OutputDirectory;
        }

        CatalogScopeDetailsTextBox.Text = BuildCatalogScopeDetails(node);
        UpdateCatalogContextMenuLabels(node);
        UpdateScopeActionState(node);
        LoadCatalogScopedViews();
    }

    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, MainTabControl))
        {
            return;
        }

        int currentIndex = MainTabControl.SelectedIndex;
        if (currentIndex == _mainTabSelectedIndex)
        {
            return;
        }

        _mainTabSelectedIndex = currentIndex;
        EnsureActiveTabLoaded();
    }

    private void QueueGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateQueueButtons();
    }

    private void CatalogDataTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, CatalogDataTabControl))
        {
            return;
        }

        int currentIndex = CatalogDataTabControl.SelectedIndex;
        if (currentIndex == _catalogDataSubTabSelectedIndex)
        {
            return;
        }

        _catalogDataSubTabSelectedIndex = currentIndex;
        LoadCatalogScopedViews();
    }

    private void QueueGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (QueueGrid.SelectedItem is not AuditQueueRow selectedRow)
        {
            return;
        }

        AuditCatalogEntry? entry = _catalogEntries.FirstOrDefault(item =>
            string.Equals(item.InputPath, selectedRow.InputPath, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return;
        }

        SolutionViewRowModel? solutionRow = _solutionViewRows.FirstOrDefault(row =>
            string.Equals(row.CatalogInputPath, entry.InputPath, StringComparison.OrdinalIgnoreCase));
        FocusCatalogEntry(entry, solutionRow);
    }

    private async Task QueueAuditForNodeAsync(CatalogTreeNode node)
    {
        if (node.NodeType == "folder" && !string.IsNullOrWhiteSpace(node.FolderPath))
        {
            await QueueAuditForFolderAsync(node.FolderPath);
            return;
        }

        IReadOnlyList<AuditCatalogEntry> entries = node.NodeType switch
        {
            "target" when node.Entry is not null => [node.Entry],
            "source" when !string.IsNullOrWhiteSpace(node.SourceIdentityKey) => _catalogEntries
                .Where(entry => string.Equals(entry.SourceIdentityKey, node.SourceIdentityKey, StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.InputPath, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            _ => Array.Empty<AuditCatalogEntry>()
        };

        if (entries.Count == 0)
        {
            return;
        }

        (string scopeLabel, string summary) = node.NodeType switch
        {
            "target" when node.Entry is not null && IsSolutionInput(node.Entry.InputPath)
                => ("solution", $"Queued solution audit for {node.Entry.DisplayName}."),
            "target" when node.Entry is not null && IsProjectInput(node.Entry.InputPath)
                => ("project", $"Queued project audit for {node.Entry.DisplayName}."),
            "target" when node.Entry is not null
                => ("target", $"Queued audit for {node.Entry.DisplayName}."),
            "source"
                => ("source scope", $"Queued source-scope audit for {node.SourceIdentityKey}."),
            _
                => ("scope", "Queued audit.")
        };

        QueueAuditEntries(entries, summary, scopeLabel);
    }

    private async Task QueueAuditForFolderAsync(string folderPath)
    {
        string fullFolderPath = Path.GetFullPath(folderPath);
        SummaryTextBlock.Text = $"Searching {fullFolderPath} for solutions and project files to queue.";
        ComparisonTextBlock.Text = "Rediscovering audit targets for the selected folder scope.";
        ClearFailureDetails();

        IReadOnlyList<string> discoveredTargets = await Task.Run(() => AuditTargetDiscovery.DiscoverTargets(fullFolderPath));
        AuditCatalogStore.CatalogUpdateResult update = _catalogStore.AddDiscoveredTargets(fullFolderPath, discoveredTargets);
        ReloadCatalog(update.Targets, update.ScannedFolders);

        IReadOnlyList<AuditCatalogEntry> folderEntries = update.FolderTargets
            .OrderBy(entry => entry.InputPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (folderEntries.Count == 0)
        {
            SummaryTextBlock.Text = $"No supported inputs were found under {fullFolderPath}.";
            ComparisonTextBlock.Text = "Nothing was added to the audit queue because the selected folder does not contain supported solution or project files.";
            return;
        }

        QueueAuditEntries(folderEntries, $"Queued folder audit for {fullFolderPath}.", "folder");
    }

    private void QueueAuditPath(string inputPath, string discoveredFromPath, string summary)
    {
        string fullInputPath = Path.GetFullPath(inputPath);
        if (AuditProjectDisplayName.IsUnderVsHistoryFolder(fullInputPath))
        {
            _catalogStore.PersistPruneVsHistory();
            ReloadCatalog();
            return;
        }

        if (!File.Exists(fullInputPath))
        {
            return;
        }

        string rootPath = Directory.Exists(discoveredFromPath)
            ? Path.GetFullPath(discoveredFromPath)
            : Path.GetDirectoryName(fullInputPath) ?? Environment.CurrentDirectory;

        AuditCatalogStore.CatalogUpdateResult update = _catalogStore.AddDiscoveredTargets(rootPath, [fullInputPath]);
        ReloadCatalog(update.Targets, update.ScannedFolders);

        AuditCatalogEntry? entry = update.Targets.FirstOrDefault(item =>
            string.Equals(item.InputPath, fullInputPath, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return;
        }

        string scopeLabel = IsSolutionInput(fullInputPath)
            ? "solution"
            : IsProjectInput(fullInputPath)
                ? "project"
                : "input";
        QueueAuditEntries([entry], summary, scopeLabel);
    }

    private void QueueAuditEntries(IReadOnlyList<AuditCatalogEntry> entries, string summary, string scopeLabel)
    {
        AuditCatalogEntry[] distinctEntries = AuditCatalogStore.DeduplicateCatalogEntries(entries);

        int beforeVsHistoryFilter = distinctEntries.Length;
        distinctEntries = distinctEntries
            .Where(entry => !AuditProjectDisplayName.IsUnderVsHistoryFolder(entry.InputPath))
            .ToArray();

        if (distinctEntries.Length < beforeVsHistoryFilter)
        {
            _catalogStore.PersistPruneVsHistory();
            (IReadOnlyList<AuditCatalogEntry> prunedTargets, IReadOnlyList<ScannedFolderRecord> prunedFolders) = _catalogStore.LoadAll();
            ReloadCatalog(prunedTargets, prunedFolders, invokeEnsureQueuedWorkRunning: false);
        }

        if (distinctEntries.Length == 0)
        {
            return;
        }

        foreach (AuditCatalogEntry entry in distinctEntries)
        {
            SetAuditStatus(entry.InputPath, "queued");
            ClearAuditFailure(entry.InputPath);
        }

        SummaryTextBlock.Text = summary;
        ComparisonTextBlock.Text = $"Queued {distinctEntries.Length} {scopeLabel} item(s) for background audit.";
        ReloadCatalog();
        EnsureQueuedWorkRunning();
    }

    private void RetrySelectedQueueItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (QueueGrid.SelectedItem is not AuditQueueRow selectedRow
            || !string.Equals(selectedRow.AuditStatus, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        AuditCatalogEntry? entry = _catalogEntries.FirstOrDefault(item =>
            string.Equals(item.InputPath, selectedRow.InputPath, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return;
        }

        if (AuditProjectDisplayName.IsUnderVsHistoryFolder(entry.InputPath))
        {
            _catalogStore.PersistPruneVsHistory();
            ReloadCatalog();
            return;
        }

        string retryScopeLabel = IsSolutionInput(entry.InputPath)
            ? "solution"
            : IsProjectInput(entry.InputPath)
                ? "project"
                : "input";
        StartBackgroundAuditQueue(entry.FolderPathOrDefault(), [entry], "Retrying the selected failed audit item.", retryScopeLabel);
    }

    private void RetryScopeFailedButton_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<AuditCatalogEntry> failedEntries = _pendingScopedEntries
            .Where(entry => string.Equals(GetAuditStatus(entry.InputPath), "failed", StringComparison.OrdinalIgnoreCase))
            .Where(entry => !AuditProjectDisplayName.IsUnderVsHistoryFolder(entry.InputPath))
            .OrderBy(entry => entry.InputPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (failedEntries.Count == 0)
        {
            return;
        }

        string scopeRoot = _selectedCatalogNode?.FolderPath
            ?? _selectedCatalogNode?.SourceIdentityKey
            ?? _selectedCatalogNode?.Entry?.InputPath
            ?? "current scope";
        string retryScopeLabel = _selectedCatalogNode?.NodeType switch
        {
            "folder" => "folder",
            "source" => "source scope",
            "target" when _selectedCatalogNode.Entry is not null && IsSolutionInput(_selectedCatalogNode.Entry.InputPath) => "solution",
            "target" when _selectedCatalogNode.Entry is not null && IsProjectInput(_selectedCatalogNode.Entry.InputPath) => "project",
            _ => "scope"
        };
        StartBackgroundAuditQueue(scopeRoot, failedEntries, "Retrying failed audit items in the current scope.", retryScopeLabel);
    }

    private async Task RegisterDiscoveredTargetsAsync(string rootPath)
    {
        RunButton.IsEnabled = false;
        DiscoverAddButton.IsEnabled = false;
        UpdateBusyState();
        ClearFailureDetails();
        SummaryTextBlock.Text = $"Scanning {rootPath} for solutions and project files...";
        ComparisonTextBlock.Text = "Searching recursively for .sln, .slnx, .csproj, .vbproj, and .fsproj files. Large trees can take a little while.";

        try
        {
            AuditCatalogStore.CatalogUpdateResult update = _catalogStore.AddDiscoveredTargets(
                rootPath,
                await Task.Run(() => AuditTargetDiscovery.DiscoverTargets(rootPath)));

            ReloadCatalog(update.Targets, update.ScannedFolders);

            SummaryTextBlock.Text = update.AddedCount == 0
                ? $"Scan complete. No new audit targets were found under {rootPath}. Existing tracked solutions and projects were left unchanged."
                : $"Scan complete. Added {update.AddedCount} new audit target(s) from {rootPath}. The catalog now tracks {update.Targets.Count} unique solution/project input(s).";

            if (update.Targets.Count > 0)
            {
                int uniqueSourceCount = update.Targets
                    .Select(static entry => entry.SourceIdentityKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                ComparisonTextBlock.Text = $"Discovered {update.DiscoveredCount} supported file(s). Tracked sources: {uniqueSourceCount}. Tracked local clones/paths: {update.Targets.Count}. Duplicate clones of the same repository are stored as separate locations under one source identity when Git metadata matches.";
            }

            if (update.AddedCount > 0)
            {
                AuditCatalogEntry latest = update.Targets.Last();
                InputPathTextBox.Text = latest.InputPath;
                OutputDirectoryTextBox.Text = latest.OutputDirectory;
                LoadCatalogScopedViews();
            }

            IReadOnlyList<AuditCatalogEntry> pendingAudits = update.FolderTargets
                .Where(static entry => !File.Exists(Path.Combine(entry.OutputDirectory, "latest.snapshot.json")))
                .OrderBy(static entry => entry.InputPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (pendingAudits.Count > 0)
            {
                StartBackgroundAuditQueue(rootPath, pendingAudits);
            }
        }
        catch (Exception ex)
        {
            ShowFailureDetails("Folder scan failed.", ex);
        }
        finally
        {
            UpdateBusyState();
        }
    }

    private void RegisterKnownTarget(string? inputPath, string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            return;
        }

        if (AuditProjectDisplayName.IsUnderVsHistoryFolder(inputPath))
        {
            _catalogStore.PersistPruneVsHistory();
            ReloadCatalog();
            return;
        }

        _catalogStore.AddDiscoveredTargets(Path.GetDirectoryName(inputPath) ?? Environment.CurrentDirectory, [inputPath]);
        ReloadCatalog();
    }

    private void ReloadCatalog()
    {
        (IReadOnlyList<AuditCatalogEntry> targets, IReadOnlyList<ScannedFolderRecord> scannedFolders) = _catalogStore.LoadAll();
        ReloadCatalog(targets, scannedFolders);
    }

    private void ReloadCatalog(IReadOnlyList<AuditCatalogEntry> entries, IReadOnlyList<ScannedFolderRecord> scannedFolders, bool invokeEnsureQueuedWorkRunning = true)
    {
        DebugLog.Write("Catalog", $"Reloading catalog. Targets={entries.Count}, Folders={scannedFolders.Count}.");
        _scannedFolders = scannedFolders;
        InvalidateDeferredLoads();
        _catalogEntries.Clear();
        foreach (AuditCatalogEntry entry in entries)
        {
            _catalogEntries.Add(entry);
            if (!_auditStatusByInputPath.ContainsKey(entry.InputPath))
            {
                _auditStatusByInputPath[entry.InputPath] = File.Exists(Path.Combine(entry.OutputDirectory, "latest.snapshot.json"))
                    ? "complete"
                    : "discovered";
            }
        }

        ReloadCatalogTree(entries, scannedFolders);
        ScheduleKnowledgeTimelineOrphanRepair(entries);

        if (_selectedCatalogNode is null)
        {
            CatalogScopeDetailsTextBox.Text = BuildCatalogScopeDetails(null);
            UpdateScopeActionState(null);
        }

        LoadCatalogScopedViews();
        if (invokeEnsureQueuedWorkRunning)
        {
            EnsureQueuedWorkRunning();
        }
    }

    private void ScheduleKnowledgeTimelineOrphanRepair(IReadOnlyList<AuditCatalogEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        string[] DatabasePaths = entries
            .Select(static entry => Path.Combine(entry.OutputDirectory, "knowledge.timeline.db"))
            .Where(static path => File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (DatabasePaths.Length == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            foreach (string DatabasePath in DatabasePaths)
            {
                try
                {
                    int RemovedCount = await KnowledgeTimelineDatabaseMaintenance.TryRemoveOrphanRowsAsync(DatabasePath, CancellationToken.None);
                    if (RemovedCount > 0)
                    {
                        DebugLog.Write("Catalog", $"SQLite timeline repair removed {RemovedCount} orphan row(s) from '{DatabasePath}'.");
                    }
                }
                catch (Exception exception)
                {
                    DebugLog.Write("Catalog", $"SQLite timeline repair skipped for '{DatabasePath}': {exception.Message}");
                }
            }
        });
    }

    private void ReloadCatalogTree(IReadOnlyList<AuditCatalogEntry> entries, IReadOnlyList<ScannedFolderRecord> scannedFolders)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        _catalogTreeNodes.Clear();

        foreach (ScannedFolderRecord folder in scannedFolders)
        {
            CatalogTreeNode discoveredNode = new CatalogTreeNode(Path.GetFileName(folder.FolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), $"{folder.LastScanStatus}{Environment.NewLine}Scanned folder: {folder.FolderPath}", "folder", folderPath: folder.FolderPath);

            foreach (IGrouping<string, AuditCatalogEntry> sourceGroup in entries
                         .Where(entry => string.Equals(entry.DiscoveredFromPath, folder.FolderPath, StringComparison.OrdinalIgnoreCase))
                         .GroupBy(static entry => entry.SourceIdentityKey, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(static group => group.First().GitRemoteUrl ?? group.First().RepositoryRootPath ?? group.Key, StringComparer.OrdinalIgnoreCase))
            {
                AuditCatalogEntry first = sourceGroup.First();
                string sourceTitle = !string.IsNullOrWhiteSpace(first.GitRemoteUrl)
                    ? first.GitRemoteUrl!
                    : !string.IsNullOrWhiteSpace(first.RepositoryRootPath)
                        ? first.RepositoryRootPath
                        : first.SourceIdentityKey;

                CatalogTreeNode sourceNode = new CatalogTreeNode(sourceTitle, $"{sourceGroup.Count()} tracked target(s) across {sourceGroup.Select(static entry => entry.RepositoryRootPath).Distinct(StringComparer.OrdinalIgnoreCase).Count()} location(s)", "source", folderPath: folder.FolderPath, sourceIdentityKey: first.SourceIdentityKey);

                foreach (AuditCatalogEntry entry in sourceGroup.OrderBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
                {
                    string latestSnapshotPath = Path.Combine(entry.OutputDirectory, "latest.snapshot.json");
                    string status = File.Exists(latestSnapshotPath)
                        ? $"Snapshot present in {entry.OutputDirectory}"
                        : $"No snapshot yet in {entry.OutputDirectory}";

                    sourceNode.AddChild(new CatalogTreeNode(entry.DisplayName, $"{status}{Environment.NewLine}{entry.InputPath}", "target", entry));
                }

                discoveredNode.AddChild(sourceNode);
            }

            _catalogTreeNodes.Add(discoveredNode);
        }

        DebugLog.Write("Catalog", $"Tree rebuilt in {stopwatch.ElapsedMilliseconds} ms. RootNodes={_catalogTreeNodes.Count}.");
    }

    private CatalogTreeNode? FindCatalogTreeNodeForEntry(AuditCatalogEntry entry)
    {
        foreach (CatalogTreeNode folderNode in _catalogTreeNodes)
        {
            CatalogTreeNode? match = FindCatalogTreeNodeRecursive(folderNode, entry.InputPath);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static CatalogTreeNode? FindCatalogTreeNodeRecursive(CatalogTreeNode node, string inputPath)
    {
        if (node.Entry is not null && string.Equals(node.Entry.InputPath, inputPath, StringComparison.OrdinalIgnoreCase))
        {
            return node;
        }

        foreach (CatalogTreeNode child in node.Children)
        {
            CatalogTreeNode? match = FindCatalogTreeNodeRecursive(child, inputPath);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static void ExpandCatalogTreeToNode(CatalogTreeNode node)
    {
        CatalogTreeNode? current = node.Parent;
        while (current is not null)
        {
            current.IsExpanded = true;
            current = current.Parent;
        }

        node.IsExpanded = true;
    }

    private void FocusCatalogEntry(AuditCatalogEntry entry, SolutionViewRowModel? solutionRow = null)
    {
        CatalogTreeNode? targetNode = FindCatalogTreeNodeForEntry(entry);
        if (targetNode is not null)
        {
            ExpandCatalogTreeToNode(targetNode);
            targetNode.IsSelected = true;
        }

        InputPathTextBox.Text = entry.InputPath;
        OutputDirectoryTextBox.Text = entry.OutputDirectory;

        SolutionViewRowModel? effectiveSolutionRow = solutionRow ?? _solutionViewRows.FirstOrDefault(row =>
            string.Equals(row.CatalogInputPath, entry.InputPath, StringComparison.OrdinalIgnoreCase));
        if (effectiveSolutionRow is not null)
        {
            CatalogGrid.SelectedItem = effectiveSolutionRow;
            CatalogGrid.ScrollIntoView(effectiveSolutionRow);
        }

        SelectMainTabByHeader("Process");
    }

    private void SelectMainTabByHeader(string Header)
    {
        for (int Index = 0; Index < MainTabControl.Items.Count; Index++)
        {
            if (MainTabControl.Items[Index] is TabItem Tab && string.Equals(Tab.Header?.ToString(), Header, StringComparison.Ordinal))
            {
                MainTabControl.SelectedIndex = Index;
                return;
            }
        }
    }

    private async void LoadCatalogScopedViews()
    {
        if (HistorySurface is null || IssueSurface is null || OutputDirectoryTextBox is null || CatalogGrid is null || QueueGrid is null)
        {
            return;
        }

        IReadOnlyList<AuditCatalogEntry> scopedEntries = GetScopedEntries();
        bool includePackages = string.Equals(GetCurrentCatalogDataTabHeader(), "Packages", StringComparison.Ordinal);
        DebugLog.Write("Catalog", $"Loading scoped views. Scope='{BuildScopeKey(_selectedCatalogNode, scopedEntries)}', Entries={scopedEntries.Count}, IncludePackages={includePackages}.");
        _pendingScopedEntries = scopedEntries;
        PopulateQueueViews(scopedEntries);
        SetCatalogLoadingState(includePackages);
        int loadVersion = Interlocked.Increment(ref _catalogLoadVersion);
        Stopwatch stopwatch = Stopwatch.StartNew();
        CatalogViewState viewState = await Task.Run(() => BuildCatalogViewState(scopedEntries, includePackages));
        if (loadVersion != _catalogLoadVersion)
        {
            DebugLog.Write("Catalog", "Discarded stale scoped view load.");
            return;
        }

        ApplyCatalogViewState(viewState);
        DebugLog.Write("Catalog", $"Scoped views applied in {stopwatch.ElapsedMilliseconds} ms. FolderRows={viewState.FolderRows.Count}, SolutionRows={viewState.SolutionRows.Count}, PackageRows={viewState.PackageRows.Count}.");
        EnsureActiveTabLoaded();
    }

    private IReadOnlyList<AuditCatalogEntry> GetScopedEntries()
    {
        AuditCatalogEntry[] raw = _selectedCatalogNode?.Entry is AuditCatalogEntry selectedEntry
            ? [selectedEntry]
            : !string.IsNullOrWhiteSpace(_selectedCatalogNode?.SourceIdentityKey)
                ? _catalogEntries
                    .Where(entry => string.Equals(entry.SourceIdentityKey, _selectedCatalogNode.SourceIdentityKey, StringComparison.OrdinalIgnoreCase))
                    .ToArray()
                : !string.IsNullOrWhiteSpace(_selectedCatalogNode?.FolderPath)
                    ? _catalogEntries
                        .Where(entry => string.Equals(entry.DiscoveredFromPath, _selectedCatalogNode.FolderPath, StringComparison.OrdinalIgnoreCase))
                        .ToArray()
                    : _catalogEntries.ToArray();

        return AuditCatalogStore.DeduplicateCatalogEntries(raw)
            .OrderBy(static entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static entry => entry.InputPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private CatalogViewState BuildCatalogViewState(IReadOnlyList<AuditCatalogEntry> scopedEntries, bool includePackages)
    {
        Dictionary<string, CatalogSnapshotData> snapshotMap = LoadScopedSnapshots(scopedEntries, includePackages);
        List<FolderViewRow> folderRows = new();
        List<SolutionViewRowModel> solutionRows = new();
        List<PackageViewRow> packageRows = new();

        IEnumerable<ScannedFolderRecord> scopedFolders = _selectedCatalogNode?.FolderPath is { Length: > 0 } folderPath
            ? _scannedFolders.Where(folder => string.Equals(folder.FolderPath, folderPath, StringComparison.OrdinalIgnoreCase))
            : _selectedCatalogNode?.SourceIdentityKey is { Length: > 0 } sourceIdentityKey
                ? _scannedFolders.Where(folder => scopedEntries.Any(entry => string.Equals(entry.DiscoveredFromPath, folder.FolderPath, StringComparison.OrdinalIgnoreCase)))
                : _selectedCatalogNode?.Entry is not null
                    ? _scannedFolders.Where(folder => scopedEntries.Any(entry => string.Equals(entry.DiscoveredFromPath, folder.FolderPath, StringComparison.OrdinalIgnoreCase)))
                    : _scannedFolders;

        foreach (ScannedFolderRecord folder in scopedFolders.OrderBy(static item => item.FolderPath, StringComparer.OrdinalIgnoreCase))
        {
            IReadOnlyList<AuditCatalogEntry> folderEntries = scopedEntries
                .Where(entry => string.Equals(entry.DiscoveredFromPath, folder.FolderPath, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            IReadOnlyList<CatalogSnapshotData> folderSnapshots = folderEntries
                .Select(entry => snapshotMap.GetValueOrDefault(entry.InputPath))
                .Where(static snapshot => snapshot is not null)
                .Cast<CatalogSnapshotData>()
                .ToArray();

            folderRows.Add(new FolderViewRow(folder.FolderPath, DescribeFolderAuditStatus(folderEntries), folderEntries.Count(entry => IsSolutionInput(entry.InputPath)), folderSnapshots.Sum(static snapshot => snapshot.ProjectCount), folderSnapshots.Sum(static snapshot => snapshot.PackageCount), folderEntries.Count, folder.LastScanStatus, folder.LastScannedUtc));
        }

        foreach (AuditCatalogEntry entry in scopedEntries.OrderBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            CatalogSnapshotData? snapshot = snapshotMap.GetValueOrDefault(entry.InputPath);
            solutionRows.Add(SolutionViewRowModel.FromEntry(
                entry,
                GetAuditStatus(entry.InputPath),
                snapshot));

            if (includePackages && snapshot is not null)
            {
                foreach (PackageViewRow packageRow in snapshot.Packages.OrderBy(static row => row.SolutionName, StringComparer.OrdinalIgnoreCase).ThenBy(static row => row.ProjectName, StringComparer.OrdinalIgnoreCase).ThenBy(static row => row.PackageId, StringComparer.OrdinalIgnoreCase))
                {
                    packageRows.Add(packageRow);
                }
            }
        }

        return new CatalogViewState(folderRows, solutionRows, packageRows, includePackages);
    }

    private void ApplyCatalogViewState(CatalogViewState viewState)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        _folderViewRows.Clear();
        foreach (FolderViewRow row in viewState.FolderRows)
        {
            _folderViewRows.Add(row);
        }

        _solutionViewRows.Clear();
        foreach (SolutionViewRowModel row in viewState.SolutionRows)
        {
            _solutionViewRows.Add(row);
        }

        if (viewState.PackageRowsLoaded)
        {
            _packageViewRows.Clear();
            foreach (PackageViewRow row in viewState.PackageRows)
            {
                _packageViewRows.Add(row);
            }
        }

        DebugLog.Write("Catalog", $"Observable collections updated in {stopwatch.ElapsedMilliseconds} ms.");
    }

    private void SetCatalogLoadingState(bool includePackages)
    {
        _folderViewRows.Clear();
        _solutionViewRows.Clear();
        if (includePackages)
        {
            _packageViewRows.Clear();
        }
    }

    private void PopulateQueueViews(IReadOnlyList<AuditCatalogEntry> scopedEntries)
    {
        _queueRows.Clear();

        AuditQueueRow[] rows = scopedEntries
            .Select(entry => new AuditQueueRow(GetAuditStatus(entry.InputPath), entry.DisplayName, entry.InputPath, entry.OutputDirectory, entry.DiscoveredFromPath, GetAuditFailure(entry.InputPath)))
            .Where(static row => !string.Equals(row.AuditStatus, "complete", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static row => GetQueuePriority(row.AuditStatus))
            .ThenBy(static row => row.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (AuditQueueRow row in rows)
        {
            _queueRows.Add(row);
        }

        int queued = rows.Count(static row => string.Equals(row.AuditStatus, "queued", StringComparison.OrdinalIgnoreCase));
        int auditing = rows.Count(static row => string.Equals(row.AuditStatus, "auditing", StringComparison.OrdinalIgnoreCase));
        int failed = rows.Count(static row => string.Equals(row.AuditStatus, "failed", StringComparison.OrdinalIgnoreCase));
        int discovered = rows.Count(static row => string.Equals(row.AuditStatus, "discovered", StringComparison.OrdinalIgnoreCase));

        string scopeDisplay = GetScopeDisplayName(_selectedCatalogNode, scopedEntries);
        if (rows.Length == 0)
        {
            QueueSummaryTextBlock.Text = scopedEntries.Count == 0
                ? $"No audit targets are visible in the current {scopeDisplay}."
                : $"No items are queued in the current {scopeDisplay}; completed audits are removed from the queue.";
        }
        else
        {
            QueueSummaryTextBlock.Text =
                $"{ToDisplayScope(scopeDisplay)} queue: {queued} queued, {auditing} auditing, {failed} failed, {discovered} discovered but not yet audited.";
        }

        UpdateQueueButtons();
    }

    private Dictionary<string, CatalogSnapshotData> LoadScopedSnapshots(IReadOnlyList<AuditCatalogEntry> scopedEntries, bool includePackages)
    {
        Dictionary<string, CatalogSnapshotData> snapshots = new(StringComparer.OrdinalIgnoreCase);

        foreach (AuditCatalogEntry entry in scopedEntries)
        {
            CatalogSnapshotData? data = TryBuildCatalogSnapshotData(entry, includePackages);
            if (data is not null)
            {
                snapshots[entry.InputPath] = data;
            }
        }

        return snapshots;
    }

    /// <summary>
    /// Returns <see langword="null"/> when no topology snapshot exists yet; returns a sentinel when the store is unreadable.
    /// </summary>
    private CatalogSnapshotData? TryBuildCatalogSnapshotData(AuditCatalogEntry entry, bool includePackages)
    {
        try
        {
            SqliteTopologySnapshot? sqliteSnapshot = _topologyStore.LoadLatest(entry.OutputDirectory);
            if (sqliteSnapshot is null)
            {
                return null;
            }

            return new CatalogSnapshotData(sqliteSnapshot.SolutionName, sqliteSnapshot.SolutionPath, sqliteSnapshot.AnalysisStatus, sqliteSnapshot.CapturedUtc, sqliteSnapshot.Projects.Count, sqliteSnapshot.Projects.Sum(static project => project.Packages.Count), includePackages
                    ? sqliteSnapshot.Projects
                        .SelectMany(project => project.Packages.Select(package => new PackageViewRow(entry.DiscoveredFromPath, GetAuditStatus(entry.InputPath), sqliteSnapshot.SolutionName, sqliteSnapshot.SolutionPath, project.ProjectName, project.ProjectPath, package.PackageId, package.RequestedVersion, package.ResolvedVersion, package.ReferenceKind, TargetFrameworkDisplay.Format(package.TargetFrameworkMoniker), DescribeHealth(package.HealthInfo))))
                        .ToArray()
                    : Array.Empty<PackageViewRow>());
        }
        catch
        {
            return new CatalogSnapshotData(entry.DisplayName, entry.InputPath, "Snapshot unreadable", entry.AddedUtc, 0, 0, Array.Empty<PackageViewRow>());
        }
    }

    private void TryPatchSolutionRowAfterAudit(AuditCatalogEntry entry, CatalogSnapshotData? snapshotOrNull)
    {
        SolutionViewRowModel? row = _solutionViewRows.FirstOrDefault(candidate =>
            string.Equals(candidate.CatalogInputPath, entry.InputPath, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            return;
        }

        row.RefreshDisplay(GetAuditStatus(entry.InputPath), entry, snapshotOrNull);
    }

    private SurfaceSolutionSnapshot BuildExplorerSnapshot(IReadOnlyList<AuditCatalogEntry> scopedEntries, bool mergeNetVersions)
    {
        AuditCatalogEntry[] uniqueEntries = AuditCatalogStore.DeduplicateCatalogEntries(scopedEntries)
            .OrderBy(static entry => entry.InputPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Dictionary<string, (DateTimeOffset CapturedUtc, SurfaceProjectSnapshot Snapshot)> projectByNormalizedPath = new(StringComparer.OrdinalIgnoreCase);
        string? snapshotId = null;
        DateTimeOffset capturedUtc = DateTimeOffset.MinValue;

        foreach (AuditCatalogEntry entry in uniqueEntries)
        {
            SqliteTopologySnapshot? snapshot = _topologyStore.LoadLatest(entry.OutputDirectory);
            if (snapshot is null)
            {
                continue;
            }

            if (snapshot.CapturedUtc > capturedUtc)
            {
                capturedUtc = snapshot.CapturedUtc;
                snapshotId = snapshot.SnapshotId;
            }

            foreach (SqliteTopologyProject project in snapshot.Projects)
            {
                string projectKey = AuditCatalogStore.NormalizeCatalogInputPath(project.ProjectPath);
                if (string.IsNullOrEmpty(projectKey))
                {
                    continue;
                }

                SurfaceProjectSnapshot mapped = CreateSurfaceProjectSnapshot(snapshot, project, mergeNetVersions);
                if (!projectByNormalizedPath.TryGetValue(projectKey, out (DateTimeOffset CapturedUtc, SurfaceProjectSnapshot Snapshot) existing)
                    || snapshot.CapturedUtc >= existing.CapturedUtc)
                {
                    projectByNormalizedPath[projectKey] = (snapshot.CapturedUtc, mapped);
                }
            }
        }

        List<SurfaceProjectSnapshot> projects = projectByNormalizedPath.Values
            .OrderBy(static item => item.Snapshot.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Snapshot.ProjectPath, StringComparer.OrdinalIgnoreCase)
            .Select(static item => item.Snapshot)
            .ToList();

        return new SurfaceSolutionSnapshot
        {
            SnapshotId = snapshotId,
            CapturedUtc = capturedUtc == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : capturedUtc,
            Projects = projects
        };
    }

    private static SurfaceProjectSnapshot CreateSurfaceProjectSnapshot(SqliteTopologySnapshot snapshot, SqliteTopologyProject project, bool mergeNetVersions)
    {
        List<SurfacePackageReference> MappedPackages = project.Packages
            .Select(package => new SurfacePackageReference
            {
                PackageId = package.PackageId,
                RequestedVersion = package.RequestedVersion,
                ResolvedVersion = package.ResolvedVersion,
                ReferenceKind = package.ReferenceKind,
                TargetFrameworkMoniker = package.TargetFrameworkMoniker,
                HealthInfo = new SurfacePackageHealth
                {
                    IsDeprecated = package.HealthInfo.IsDeprecated,
                    IsObsolete = package.HealthInfo.IsObsolete,
                    IsOutdated = package.HealthInfo.IsOutdated,
                    IsVulnerable = package.HealthInfo.IsVulnerable,
                    DevelopmentStatus = package.HealthInfo.DevelopmentStatus.ToString(),
                    LatestStableVersion = package.HealthInfo.LatestStableVersion,
                    DeprecationMessage = package.HealthInfo.DeprecationMessage,
                    AlternatePackageId = package.HealthInfo.AlternatePackageId,
                    AlternatePackageRange = package.HealthInfo.AlternatePackageRange,
                    MaxVulnerabilitySeverity = package.HealthInfo.MaxVulnerabilitySeverity.ToString(),
                    Vulnerabilities = package.HealthInfo.Vulnerabilities.Select(vulnerability => new SurfacePackageVulnerability
                    {
                        AdvisoryUrl = vulnerability.AdvisoryUrl,
                        Severity = vulnerability.Severity.ToString()
                    }).ToArray()
                },
                KnowledgeDeterminedUtc = package.KnowledgeDeterminedUtc,
                KnowledgeStatusChangedUtc = package.KnowledgeStatusChangedUtc,
                RemediationSummary = package.RemediationSummary,
                RiskScore = package.RiskScore,
                AlertScore = package.AlertScore,
                RiskBand = package.RiskBand,
                AlertBand = package.AlertBand,
                StablePackageInstanceKey = StableKey.Create(
                    snapshot.SolutionPath,
                    project.ProjectPath,
                    package.TargetFrameworkMoniker,
                    null,
                    package.PackageId ?? string.Empty),
                DependencyParents = Array.Empty<string>(),
                DependencyPath = string.IsNullOrEmpty(package.PackageId)
                    ? Array.Empty<string>()
                    : new[] { package.PackageId }
            })
            .ToList();

        (List<SurfacePackageReference> mergedList, Dictionary<string, string> stableKeyRemap) = ExplorerSurfacePackageMerge.MergeDuplicateRows(
            MappedPackages,
            snapshot.SolutionPath,
            project.ProjectPath ?? string.Empty,
            mergeNetVersions);
        SurfacePackageReference[] mergedPackages = mergedList.ToArray();

        return new SurfaceProjectSnapshot
        {
            SolutionPath = snapshot.SolutionPath,
            ProjectName = project.ProjectName,
            ProjectPath = project.ProjectPath,
            TargetFrameworks = project.TargetFrameworks,
            Packages = mergedPackages,
            DependencyEdges = MapSqliteDependencyEdgesToSurface(project.DependencyEdges, mergedPackages, stableKeyRemap)
        };
    }

    private static SurfaceDependencyEdge[] MapSqliteDependencyEdgesToSurface(
        IReadOnlyList<SqliteTopologyDependencyEdge> edges,
        IReadOnlyList<SurfacePackageReference> mergedPackages,
        IReadOnlyDictionary<string, string> stableKeyRemap)
    {
        if (edges.Count == 0)
        {
            return Array.Empty<SurfaceDependencyEdge>();
        }

        List<SurfaceDependencyEdge> raw = new();
        foreach (SqliteTopologyDependencyEdge edge in edges)
        {
            raw.Add(new SurfaceDependencyEdge
            {
                FromPackageId = edge.FromPackageId,
                ToPackageId = edge.ToPackageId,
                TargetFrameworkMoniker = edge.TargetFrameworkMoniker,
                FromStablePackageInstanceKey = edge.FromStablePackageInstanceKey,
                ToStablePackageInstanceKey = edge.ToStablePackageInstanceKey
            });
        }

        return ExplorerSurfacePackageMerge.RemapAndFilterDependencyEdges(raw, mergedPackages, stableKeyRemap);
    }

    private static string GetScopeTitle(CatalogTreeNode? node, IReadOnlyList<AuditCatalogEntry> scopedEntries)
    {
        if (node?.Entry is not null)
        {
            return $"target {node.Entry.DisplayName}";
        }

        if (!string.IsNullOrWhiteSpace(node?.SourceIdentityKey))
        {
            return $"source {node.SourceIdentityKey}";
        }

        if (!string.IsNullOrWhiteSpace(node?.FolderPath))
        {
            return $"folder {node.FolderPath}";
        }

        return $"all tracked targets ({scopedEntries.Count})";
    }

    private static string BuildExplorerScopeDescription(CatalogTreeNode? node, IReadOnlyList<AuditCatalogEntry> scopedEntries)
    {
        int solutionCount = scopedEntries.Count;
        int folderCount = scopedEntries.Select(static entry => entry.DiscoveredFromPath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return node?.NodeType switch
        {
            "target" when node.Entry is not null => $"Showing one selected solution or project from {node.Entry.InputPath}.",
            "source" => $"Showing {solutionCount} tracked solution/project input(s) across {folderCount} scanned folder(s) for this source identity.",
            "folder" => $"Showing {solutionCount} tracked solution/project input(s) discovered from this folder.",
            _ => $"Showing {solutionCount} tracked solution/project input(s) across {folderCount} scanned folder(s)."
        };
    }

    private async void EnsureActiveTabLoaded()
    {
        if (MainTabControl?.SelectedItem is not TabItem selectedTab)
        {
            return;
        }

        string header = selectedTab.Header?.ToString() ?? string.Empty;
        string scopeKey = BuildScopeKey(_selectedCatalogNode, _pendingScopedEntries);
        int loadVersion = Interlocked.Increment(ref _tabLoadVersion);

        switch (header)
        {
            case "Visualise":
                if (!string.Equals(_loadedExplorerScopeKey, scopeKey, StringComparison.Ordinal))
                {
                    DebugLog.Write("Tabs", $"Loading Visualise (Explorer) for scope '{scopeKey}'.");
                    ExplorerSurface.SetScopeContext(GetScopeTitle(_selectedCatalogNode, _pendingScopedEntries), BuildExplorerScopeDescription(_selectedCatalogNode, _pendingScopedEntries));
                    ExplorerSurface.SetLoadingState("Loading dependency data for the selected scope...");
                    Stopwatch ExplorerStopwatch = Stopwatch.StartNew();
                    IReadOnlyList<AuditCatalogEntry> ScopedEntries = _pendingScopedEntries.ToArray();
                    SurfaceSolutionSnapshot Snapshot = await Task.Run(() => BuildExplorerSnapshot(ScopedEntries, mergeNetVersions: false));
                    if (loadVersion != _tabLoadVersion || !string.Equals(GetCurrentTabHeader(), "Visualise", StringComparison.Ordinal))
                    {
                        DebugLog.Write("Tabs", "Discarded stale Visualise load.");
                        return;
                    }

                    ExplorerSurface.LoadSnapshot(Snapshot);
                    _loadedExplorerScopeKey = scopeKey;
                    DebugLog.Write("Tabs", $"Visualise loaded in {ExplorerStopwatch.ElapsedMilliseconds} ms. Projects={Snapshot.Projects.Count}.");
                }
                break;
            case "History":
                if (!string.Equals(_loadedHistoryScopeKey, scopeKey, StringComparison.Ordinal))
                {
                    DebugLog.Write("Tabs", $"Loading History for scope '{scopeKey}'.");
                    HistorySurface.SetLoadingState("Loading history from the local SQLite store...");
                    Stopwatch historyStopwatch = Stopwatch.StartNew();
                    IReadOnlyList<AuditCatalogEntry> historyWorklist = CopyCatalogWorklistForBackground();
                    SurfaceHistory history = await Task.Run(() => LoadHistoryDataForScope(historyWorklist));
                    if (loadVersion != _tabLoadVersion || !string.Equals(GetCurrentTabHeader(), "History", StringComparison.Ordinal))
                    {
                        DebugLog.Write("Tabs", "Discarded stale History load.");
                        return;
                    }

                    ApplyHistoryForScope(_pendingScopedEntries, history);
                    _loadedHistoryScopeKey = scopeKey;
                    DebugLog.Write("Tabs", $"History loaded in {historyStopwatch.ElapsedMilliseconds} ms. Snapshots={history.Snapshots.Count}, Timeline={history.KnowledgeTimeline.Count}.");
                }
                break;
            case "Issues":
                if (!string.Equals(_loadedIssueScopeKey, scopeKey, StringComparison.Ordinal))
                {
                    DebugLog.Write("Tabs", $"Loading Issues for scope '{scopeKey}'.");
                    IssueSurface.SetLoadingState("Loading rolled-up issues from the local SQLite store...");
                    Stopwatch issueStopwatch = Stopwatch.StartNew();
                    IReadOnlyList<AuditCatalogEntry> issueWorklist = CopyCatalogWorklistForBackground();
                    (IReadOnlyList<IssueRollupItem> items, bool isAggregate) = await Task.Run(() => LoadIssueDataForScope(issueWorklist));
                    if (loadVersion != _tabLoadVersion || !string.Equals(GetCurrentTabHeader(), "Issues", StringComparison.Ordinal))
                    {
                        DebugLog.Write("Tabs", "Discarded stale Issues load.");
                        return;
                    }

                    IssueSurface.ApplyIssues(items, isAggregate);
                    _loadedIssueScopeKey = scopeKey;
                    DebugLog.Write("Tabs", $"Issues loaded in {issueStopwatch.ElapsedMilliseconds} ms. Items={items.Count}, Aggregate={isAggregate}.");
                }
                break;
        }
    }

    /// <summary>
    /// Copies the current scope or full catalog on the UI thread so background history/issue loads do not enumerate a live observable catalog list while it is being modified.
    /// </summary>
    private AuditCatalogEntry[] CopyCatalogWorklistForBackground()
    {
        if (_pendingScopedEntries.Count > 0)
        {
            return _pendingScopedEntries.ToArray();
        }

        if (_catalogEntries.Count > 0)
        {
            return _catalogEntries.ToArray();
        }

        return Array.Empty<AuditCatalogEntry>();
    }

    private SurfaceHistory LoadHistoryDataForScope(IReadOnlyList<AuditCatalogEntry> scopedEntries)
    {
        if (_selectedCatalogNode?.Entry is AuditCatalogEntry selectedEntry)
        {
            return SnapshotHistoryReader.Load(selectedEntry.OutputDirectory);
        }

        if (!string.IsNullOrWhiteSpace(_selectedCatalogNode?.SourceIdentityKey) || !string.IsNullOrWhiteSpace(_selectedCatalogNode?.FolderPath))
        {
            if (scopedEntries.Count == 0)
            {
                return new SurfaceHistory(Array.Empty<SnapshotHistoryItem>(), Array.Empty<KnowledgeTimelineItem>(), null);
            }

            return SnapshotHistoryReader.LoadMany(ToHistorySourceTuples(scopedEntries));
        }

        if (scopedEntries.Count == 0)
        {
            string outputDirectory = OutputDirectoryTextBox.Text;
            return string.IsNullOrWhiteSpace(outputDirectory)
                ? new SurfaceHistory(Array.Empty<SnapshotHistoryItem>(), Array.Empty<KnowledgeTimelineItem>(), null)
                : SnapshotHistoryReader.Load(outputDirectory);
        }

        return SnapshotHistoryReader.LoadMany(ToHistorySourceTuples(scopedEntries));
    }

    private static (string SourceName, string OutputDirectory)[] ToHistorySourceTuples(IReadOnlyList<AuditCatalogEntry> scopedEntries)
    {
        (string SourceName, string OutputDirectory)[] rows = new (string, string)[scopedEntries.Count];
        for (int i = 0; i < scopedEntries.Count; i++)
        {
            AuditCatalogEntry entry = scopedEntries[i];
            rows[i] = (entry.DisplayName, entry.OutputDirectory);
        }

        return rows;
    }

    private void ApplyHistoryForScope(IReadOnlyList<AuditCatalogEntry> scopedEntries, SurfaceHistory history)
    {
        if (_selectedCatalogNode?.Entry is AuditCatalogEntry selectedEntry)
        {
            HistorySurface.ApplyHistory(history, selectedEntry.OutputDirectory);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_selectedCatalogNode?.SourceIdentityKey) || !string.IsNullOrWhiteSpace(_selectedCatalogNode?.FolderPath))
        {
            HistorySurface.ApplyHistory(history, null);
            return;
        }

        if (_catalogEntries.Count == 0)
        {
            HistorySurface.ApplyHistory(history, string.IsNullOrWhiteSpace(OutputDirectoryTextBox.Text) ? null : OutputDirectoryTextBox.Text);
            return;
        }

        HistorySurface.ApplyHistory(history, null);
    }

    private (IReadOnlyList<IssueRollupItem> Items, bool IsAggregate) LoadIssueDataForScope(IReadOnlyList<AuditCatalogEntry> scopedEntries)
    {
        if (_selectedCatalogNode?.Entry is AuditCatalogEntry selectedEntry)
        {
            return (IssueRollupReader.Load(selectedEntry.OutputDirectory), false);
        }

        if (!string.IsNullOrWhiteSpace(_selectedCatalogNode?.SourceIdentityKey) || !string.IsNullOrWhiteSpace(_selectedCatalogNode?.FolderPath))
        {
            if (scopedEntries.Count == 0)
            {
                return (Array.Empty<IssueRollupItem>(), true);
            }

            return (IssueRollupReader.LoadMany(ToIssueSourceDescriptors(scopedEntries)), true);
        }

        if (scopedEntries.Count == 0)
        {
            string outputDirectory = OutputDirectoryTextBox.Text;
            return (string.IsNullOrWhiteSpace(outputDirectory)
                ? Array.Empty<IssueRollupItem>()
                : IssueRollupReader.Load(outputDirectory), false);
        }

        return (IssueRollupReader.LoadMany(ToIssueSourceDescriptors(scopedEntries)), true);
    }

    private static IssueSourceDescriptor[] ToIssueSourceDescriptors(IReadOnlyList<AuditCatalogEntry> scopedEntries)
    {
        IssueSourceDescriptor[] rows = new IssueSourceDescriptor[scopedEntries.Count];
        for (int i = 0; i < scopedEntries.Count; i++)
        {
            rows[i] = BuildIssueSourceDescriptor(scopedEntries[i]);
        }

        return rows;
    }

    private void InvalidateDeferredLoads()
    {
        Interlocked.Increment(ref _catalogLoadVersion);
        Interlocked.Increment(ref _tabLoadVersion);
        _loadedExplorerScopeKey = null;
        _loadedHistoryScopeKey = null;
        _loadedIssueScopeKey = null;
    }

    private string GetCurrentTabHeader()
        => MainTabControl?.SelectedItem is TabItem tabItem
            ? tabItem.Header?.ToString() ?? string.Empty
            : string.Empty;

    private string GetCurrentCatalogDataTabHeader()
        => CatalogDataTabControl?.SelectedItem is TabItem tabItem
            ? tabItem.Header?.ToString() ?? string.Empty
            : string.Empty;

    private void StartBackgroundAuditQueue(string rootPath, IReadOnlyList<AuditCatalogEntry> entries)
    {
        StartBackgroundAuditQueue(rootPath, entries, $"Queued {entries.Count} discovered input(s) from {rootPath} for background audit.", "discovered");
    }

    private void StartBackgroundAuditQueue(string rootPath, IReadOnlyList<AuditCatalogEntry> entries, string queueSummary, string scopeLabel)
    {
        if (entries.Count == 0)
        {
            return;
        }

        AuditCatalogEntry[] filteredForVsHistory = entries
            .Where(entry => !AuditProjectDisplayName.IsUnderVsHistoryFolder(entry.InputPath))
            .ToArray();

        if (filteredForVsHistory.Length < entries.Count)
        {
            _catalogStore.PersistPruneVsHistory();
            (IReadOnlyList<AuditCatalogEntry> prunedTargets, IReadOnlyList<ScannedFolderRecord> prunedFolders) = _catalogStore.LoadAll();
            ReloadCatalog(prunedTargets, prunedFolders, invokeEnsureQueuedWorkRunning: false);
        }

        AuditCatalogEntry[] workEntries = AuditCatalogStore.DeduplicateCatalogEntries(filteredForVsHistory)
            .OrderBy(static entry => entry.InputPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (workEntries.Length == 0)
        {
            return;
        }

        DebugLog.Write("Queue", $"Starting background queue. Scope='{scopeLabel}', Root='{rootPath}', Entries={workEntries.Length}.");
        _backgroundAuditCts?.Cancel();
        _backgroundAuditCts = new CancellationTokenSource();
        CancellationToken cancellationToken = _backgroundAuditCts.Token;
        int workerCount = Math.Max(1, Math.Min(Environment.ProcessorCount / 2, 4));
        _isBackgroundAuditRunning = true;

        foreach (AuditCatalogEntry entry in workEntries)
        {
            SetAuditStatus(entry.InputPath, "queued");
        }
        RefreshVisibleAuditState();

        SummaryTextBlock.Text = queueSummary;
        ComparisonTextBlock.Text = $"{ToDisplayScope(scopeLabel)} audit is running with {workerCount} worker(s). Solutions and packages will populate as audits complete.";

        _ = Task.Run(async () =>
        {
            SemaphoreSlim gate = new(workerCount);
            Dictionary<string, SemaphoreSlim> outputDirectoryGates = workEntries
                .Select(static entry => entry.OutputDirectory)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    static outputDirectory => outputDirectory,
                    static _ => new SemaphoreSlim(1, 1),
                    StringComparer.OrdinalIgnoreCase);
            int completed = 0;
            int failed = 0;

            Task[] tasks = workEntries.Select(async entry =>
            {
                await gate.WaitAsync(cancellationToken);
                SemaphoreSlim outputGate = outputDirectoryGates[entry.OutputDirectory];
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    SetAuditStatus(entry.InputPath, "auditing");
                    await outputGate.WaitAsync(cancellationToken);
                    try
                    {
                        DebugLog.Write("Queue", $"Audit starting for '{entry.InputPath}'.");
                        NuGetAuditRunner runner = new();
                        await runner.RunAsync(new AuditCommandOptions(entry.InputPath, entry.OutputDirectory)
                        {
                            WriteJsonCompanion = true,
                            WriteMarkdownReport = true
                        }, cancellationToken);
                    }
                    finally
                    {
                        outputGate.Release();
                    }
                    SetAuditStatus(entry.InputPath, "complete");
                    ClearAuditFailure(entry.InputPath);
                    DebugLog.Write("Queue", $"Audit completed for '{entry.InputPath}'.");

                    int done = Interlocked.Increment(ref completed);
                    CatalogSnapshotData? snapshotUpdate = await Task.Run(
                        () => TryBuildCatalogSnapshotData(entry, includePackages: false),
                        cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    await Dispatcher.InvokeAsync(
                        () =>
                        {
                            SummaryTextBlock.Text = $"{ToDisplayScope(scopeLabel)} audit progress: {done}/{workEntries.Length} completed.";
                            ComparisonTextBlock.Text = $"{ToDisplayScope(scopeLabel)} audit is processing items from {rootPath}. Failed: {failed}.";
                            TryPatchSolutionRowAfterAudit(entry, snapshotUpdate);
                            PopulateQueueViews(_pendingScopedEntries);
                        },
                        DispatcherPriority.Background,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    SetAuditStatus(entry.InputPath, "failed");
                    SetAuditFailure(entry.InputPath, ex);
                    DebugLog.Write("Queue", $"Audit failed for '{entry.InputPath}': {ex.Message}");
                    int currentFailed = Interlocked.Increment(ref failed);
                    int done = Interlocked.Increment(ref completed);
                    await Dispatcher.InvokeAsync(
                        () =>
                        {
                            SummaryTextBlock.Text = $"{ToDisplayScope(scopeLabel)} audit progress: {done}/{workEntries.Length} completed.";
                            ComparisonTextBlock.Text = $"{ToDisplayScope(scopeLabel)} audit has {currentFailed} failure(s). Latest: {entry.InputPath}.";
                            ShowFailureDetails($"{ToDisplayScope(scopeLabel)} audit item failed for {entry.InputPath}.", ex);
                            TryPatchSolutionRowAfterAudit(entry, null);
                            PopulateQueueViews(_pendingScopedEntries);
                        },
                        DispatcherPriority.Background,
                        cancellationToken);
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();

            await Task.WhenAll(tasks);

            await Dispatcher.InvokeAsync(() =>
            {
                _isBackgroundAuditRunning = false;
                _backgroundAuditCts = null;
                SummaryTextBlock.Text = $"{ToDisplayScope(scopeLabel)} audit queue finished for {rootPath}. Completed: {completed}. Failed: {failed}.";
                ComparisonTextBlock.Text = failed == 0
                    ? $"All queued {scopeLabel} input(s) have been analysed."
                    : $"{failed} queued {scopeLabel} input(s) failed during background audit.";
                InvalidateDeferredLoads();
                ReloadCatalog();
                LoadCatalogScopedViews();
            });
        }, cancellationToken);
    }

    private void EnsureQueuedWorkRunning()
    {
        if (_isAuditRunning || _isBackgroundAuditRunning || _catalogEntries.Count == 0)
        {
            return;
        }

        AuditCatalogEntry[] pendingEntries = AuditCatalogStore.DeduplicateCatalogEntries(
                _catalogEntries
                    .Where(entry =>
                    {
                        if (AuditProjectDisplayName.IsUnderVsHistoryFolder(entry.InputPath))
                        {
                            return false;
                        }

                        string status = GetAuditStatus(entry.InputPath);
                        return string.Equals(status, "queued", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(status, "discovered", StringComparison.OrdinalIgnoreCase)
                            || !File.Exists(Path.Combine(entry.OutputDirectory, "latest.snapshot.json"));
                    })
                    .ToArray())
            .OrderBy(entry => entry.InputPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (pendingEntries.Length == 0)
        {
            return;
        }

        StartBackgroundAuditQueue("catalog queue", pendingEntries, $"Queued {pendingEntries.Length} catalog item(s) for background audit.", "catalog");
    }

    private static string ToDisplayScope(string scopeLabel)
        => string.IsNullOrWhiteSpace(scopeLabel)
            ? "Background"
            : char.ToUpperInvariant(scopeLabel[0]) + scopeLabel[1..];

    private static string BuildScopeKey(CatalogTreeNode? node, IReadOnlyList<AuditCatalogEntry> scopedEntries)
    {
        if (node?.Entry is not null)
        {
            return $"target|{node.Entry.InputPath}|{node.Entry.OutputDirectory}";
        }

        if (!string.IsNullOrWhiteSpace(node?.SourceIdentityKey))
        {
            return $"source|{node.SourceIdentityKey}|{scopedEntries.Count}";
        }

        if (!string.IsNullOrWhiteSpace(node?.FolderPath))
        {
            return $"folder|{node.FolderPath}|{scopedEntries.Count}";
        }

        return $"all|{scopedEntries.Count}";
    }

    private static bool IsSolutionInput(string inputPath)
    {
        string extension = Path.GetExtension(inputPath);
        return string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProjectInput(string inputPath)
    {
        string extension = Path.GetExtension(inputPath);
        return string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".vbproj", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".fsproj", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeHealth(PackageHealthInfo healthInfo)
    {
        if (healthInfo.IsVulnerable)
        {
            return "Vulnerable";
        }

        if (healthInfo.IsDeprecated || healthInfo.IsObsolete)
        {
            return "Deprecated";
        }

        if (healthInfo.DevelopmentStatus == PackageDevelopmentStatus.Removed)
        {
            return "Removed";
        }

        if (healthInfo.DevelopmentStatus == PackageDevelopmentStatus.Abandoned)
        {
            return "Abandoned";
        }

        if (healthInfo.IsOutdated)
        {
            return "Outdated";
        }

        return "Healthy";
    }

    private string GetAuditStatus(string inputPath)
    {
        lock (_auditStatusLock)
        {
            return _auditStatusByInputPath.TryGetValue(inputPath, out string? status) ? status : "discovered";
        }
    }

    private string DescribeFolderAuditStatus(IReadOnlyList<AuditCatalogEntry> folderEntries)
    {
        if (folderEntries.Count == 0)
        {
            return "discovered";
        }

        IReadOnlyList<string> statuses = folderEntries.Select(entry => GetAuditStatus(entry.InputPath)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (statuses.Contains("auditing", StringComparer.OrdinalIgnoreCase))
        {
            return "auditing";
        }

        if (statuses.Contains("queued", StringComparer.OrdinalIgnoreCase))
        {
            return "queued";
        }

        if (statuses.Contains("failed", StringComparer.OrdinalIgnoreCase))
        {
            return "failed";
        }

        if (statuses.All(static status => string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase)))
        {
            return "complete";
        }

        return "mixed";
    }

    private void SetAuditStatus(string inputPath, string status)
    {
        lock (_auditStatusLock)
        {
            _auditStatusByInputPath[inputPath] = status;
        }
        ScheduleVisibleAuditStateRefresh();
    }

    private string? GetAuditFailure(string inputPath)
    {
        lock (_auditStatusLock)
        {
            return _auditFailureByInputPath.TryGetValue(inputPath, out string? failure) ? failure : null;
        }
    }

    private void SetAuditFailure(string inputPath, Exception exception)
    {
        lock (_auditStatusLock)
        {
            _auditFailureByInputPath[inputPath] = exception.Message;
        }
        ScheduleVisibleAuditStateRefresh();
    }

    private void ClearAuditFailure(string inputPath)
    {
        lock (_auditStatusLock)
        {
            _auditFailureByInputPath.Remove(inputPath);
        }
        ScheduleVisibleAuditStateRefresh();
    }

    private void ScheduleVisibleAuditStateRefresh()
    {
        int refreshVersion = Interlocked.Increment(ref _statusRefreshVersion);
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (refreshVersion != _statusRefreshVersion)
            {
                return;
            }

            RefreshVisibleAuditState();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void RefreshVisibleAuditState()
    {
        PopulateQueueViews(_pendingScopedEntries);
        UpdateQueueButtons();
        UpdateScopeActionState(_selectedCatalogNode);
        UpdateCatalogContextMenuLabels(_selectedCatalogNode);
    }

    private void UpdateQueueButtons()
    {
        bool isBusy = IsUiBusy();
        if (RetrySelectedQueueItemButton is not null)
        {
            RetrySelectedQueueItemButton.IsEnabled =
                QueueGrid?.SelectedItem is AuditQueueRow selectedRow
                && string.Equals(selectedRow.AuditStatus, "failed", StringComparison.OrdinalIgnoreCase)
                && !isBusy;
        }

        if (RetryScopeFailedButton is not null)
        {
            RetryScopeFailedButton.IsEnabled = !isBusy && _pendingScopedEntries.Any(entry =>
                string.Equals(GetAuditStatus(entry.InputPath), "failed", StringComparison.OrdinalIgnoreCase));
        }
    }

    private static int GetQueuePriority(string status)
    {
        return status.ToLowerInvariant() switch
        {
            "failed" => 0,
            "auditing" => 1,
            "queued" => 2,
            "discovered" => 3,
            "complete" => 4,
            _ => 5
        };
    }

    private static IssueSourceDescriptor BuildIssueSourceDescriptor(AuditCatalogEntry entry)
    {
        string sourceDisplayName =
            !string.IsNullOrWhiteSpace(entry.GitRemoteUrl)
                ? entry.GitRemoteUrl!
                : !string.IsNullOrWhiteSpace(entry.RepositoryRootPath)
                    ? entry.RepositoryRootPath
                    : entry.SourceIdentityKey;

        return new IssueSourceDescriptor(entry.SourceIdentityKey, sourceDisplayName, entry.OutputDirectory);
    }

    private string BuildCatalogScopeDetails(CatalogTreeNode? node)
    {
        if (node is null)
        {
            int totalFolderCount = _scannedFolders.Count;
            int totalTargetCount = _catalogEntries.Count;
            return $"Current scope: all tracked targets\r\nFolders: {totalFolderCount}\r\nTracked inputs: {totalTargetCount}\r\nPrimary action: audit selected folders, solutions, or projects from the Catalog views.\r\nWhat happens next: the queue runs automatically, then Visualise, History, Issues, and the report preview on Process update from the latest audit data.";
        }

        IReadOnlyList<AuditCatalogEntry> scopedEntries = GetScopedEntries();
        int targetCount = scopedEntries.Count;
        int folderCount = scopedEntries.Select(static entry => entry.DiscoveredFromPath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        string auditAction = node.NodeType switch
        {
            "folder" => "Audit Folder",
            "source" => "Audit Source Scope",
            "target" when node.Entry is not null && IsSolutionInput(node.Entry.InputPath) => "Audit Solution",
            "target" when node.Entry is not null && IsProjectInput(node.Entry.InputPath) => "Audit Project",
            _ => "Audit"
        };

        return node.NodeType switch
        {
            "target" when node.Entry is not null && IsSolutionInput(node.Entry.InputPath)
                => $"Current scope: solution\r\nName: {node.Entry.DisplayName}\r\nInput: {node.Entry.InputPath}\r\nOutput: {node.Entry.OutputDirectory}\r\nFolder: {node.Entry.DiscoveredFromPath}\r\nAction: {auditAction}\r\nWhat happens next: this exact solution is placed into the queue and analysed automatically.",
            "target" when node.Entry is not null && IsProjectInput(node.Entry.InputPath)
                => $"Current scope: project\r\nName: {node.Entry.DisplayName}\r\nInput: {node.Entry.InputPath}\r\nOutput: {node.Entry.OutputDirectory}\r\nFolder: {node.Entry.DiscoveredFromPath}\r\nAction: {auditAction}\r\nWhat happens next: this exact project file is placed into the queue and analysed automatically.",
            "target" when node.Entry is not null
                => $"Current scope: target\r\nName: {node.Entry.DisplayName}\r\nInput: {node.Entry.InputPath}\r\nOutput: {node.Entry.OutputDirectory}\r\nFolder: {node.Entry.DiscoveredFromPath}\r\nAction: {auditAction}\r\nWhat happens next: this selected input is placed into the queue and analysed automatically.",
            "source"
                => $"Current scope: source scope\r\nSource identity: {node.SourceIdentityKey}\r\nFolders in scope: {folderCount}\r\nTracked inputs in scope: {targetCount}\r\nAction: {auditAction}\r\nWhat happens next: every tracked input for this source scope is placed into the queue and analysed automatically.",
            "folder"
                => $"Current scope: folder\r\nFolder: {node.FolderPath}\r\nTracked inputs in scope: {targetCount}\r\nAction: {auditAction}\r\nWhat happens next: the folder is searched again for solutions and projects, then the discovered inputs are placed into the queue and analysed automatically.",
            _ => "Current scope: all tracked targets."
        };
    }

    private void UpdateCatalogContextMenuLabels(CatalogTreeNode? node)
    {
        if (CatalogTreeAuditMenuItem is null)
        {
            return;
        }

        CatalogTreeAuditMenuItem.Header = node?.NodeType switch
        {
            "folder" => "Audit Folder",
            "source" => "Audit Source Scope",
            "target" when node.Entry is not null && IsSolutionInput(node.Entry.InputPath) => "Audit Solution",
            "target" when node.Entry is not null && IsProjectInput(node.Entry.InputPath) => "Audit Project",
            "target" => "Audit Selected Target",
            _ => "Audit Selected Scope"
        };

        CatalogTreeAuditMenuItem.IsEnabled = CanAuditNode(node) && !IsUiBusy();
    }

    private void UpdateScopeActionState(CatalogTreeNode? node)
    {
        bool canAudit = CanAuditNode(node);
        bool isBusy = IsUiBusy();
        if (ScopeAuditButton is not null)
        {
            ScopeAuditButton.IsEnabled = canAudit && !isBusy;
            ScopeAuditButton.Content = node?.NodeType switch
            {
                "folder" => "Audit Folder",
                "source" => "Audit Source Scope",
                "target" when node.Entry is not null && IsSolutionInput(node.Entry.InputPath) => "Audit Solution",
                "target" when node.Entry is not null && IsProjectInput(node.Entry.InputPath) => "Audit Project",
                "target" => "Audit Target",
                _ => "Audit Scope"
            };
        }

        if (ScopeRetryFailedButton is not null)
        {
            ScopeRetryFailedButton.IsEnabled = !isBusy && _pendingScopedEntries.Any(entry =>
                string.Equals(GetAuditStatus(entry.InputPath), "failed", StringComparison.OrdinalIgnoreCase));
        }
    }

    private bool CanAuditNode(CatalogTreeNode? node)
    {
        if (node is null)
        {
            return false;
        }

        return node.NodeType switch
        {
            "folder" => !string.IsNullOrWhiteSpace(node.FolderPath),
            "source" => !string.IsNullOrWhiteSpace(node.SourceIdentityKey),
            "target" when node.Entry is not null => true,
            _ => false
        };
    }

    private bool IsUiBusy() => _isAuditRunning || _isBackgroundAuditRunning;

    private void UpdateBusyState()
    {
        bool isBusy = IsUiBusy();
        RunButton.IsEnabled = !isBusy;
        DiscoverAddButton.IsEnabled = !isBusy;
        ExplorerSurface.SetAuditActionAvailability(!isBusy);
        UpdateQueueButtons();
        UpdateScopeActionState(_selectedCatalogNode);

        if (CatalogTreeAuditMenuItem is not null)
        {
            CatalogTreeAuditMenuItem.IsEnabled = CanAuditNode(_selectedCatalogNode) && !isBusy;
        }
    }

    private string GetScopeDisplayName(CatalogTreeNode? node, IReadOnlyList<AuditCatalogEntry> scopedEntries)
    {
        return node?.NodeType switch
        {
            "folder" => "folder",
            "source" => "source scope",
            "target" when node.Entry is not null && IsSolutionInput(node.Entry.InputPath) => "solution",
            "target" when node.Entry is not null && IsProjectInput(node.Entry.InputPath) => "project",
            "target" => "target",
            _ => scopedEntries.Count == _catalogEntries.Count ? "catalog" : "scope"
        };
    }

    private void ShowFailureDetails(string summary, Exception exception)
    {
        SummaryTextBlock.Text = summary;
        ComparisonTextBlock.Text = exception.Message;
        if (FindName("ErrorDetailsTextBox") is System.Windows.Controls.TextBox errorDetailsTextBox)
        {
            errorDetailsTextBox.Text = exception.ToString();
        }

        if (FindName("ErrorDetailsBorder") is System.Windows.Controls.Border errorDetailsBorder)
        {
            errorDetailsBorder.Visibility = Visibility.Visible;
        }
    }

    private void ClearFailureDetails()
    {
        if (FindName("ErrorDetailsTextBox") is System.Windows.Controls.TextBox errorDetailsTextBox)
        {
            errorDetailsTextBox.Text = string.Empty;
        }

        if (FindName("ErrorDetailsBorder") is System.Windows.Controls.Border errorDetailsBorder)
        {
            errorDetailsBorder.Visibility = Visibility.Collapsed;
        }
    }

    private sealed record CatalogViewState(IReadOnlyList<FolderViewRow> FolderRows, IReadOnlyList<SolutionViewRowModel> SolutionRows, IReadOnlyList<PackageViewRow> PackageRows, bool PackageRowsLoaded);
}

internal static class AuditCatalogEntryExtensions
{
    internal static string FolderPathOrDefault(this AuditCatalogEntry entry)
    {
        return !string.IsNullOrWhiteSpace(entry.DiscoveredFromPath)
            ? entry.DiscoveredFromPath
            : Path.GetDirectoryName(entry.InputPath) ?? entry.InputPath;
    }
}
