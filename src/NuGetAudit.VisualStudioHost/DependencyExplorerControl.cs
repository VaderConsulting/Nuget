using System.Windows;
using System.Windows.Controls;
using NuGetAudit.Presentation;

namespace NuGetAudit.VisualStudioHost;

internal sealed class DependencyExplorerControl : UserControl
{
    private readonly PresentationControl _explorerControl;
    private readonly HistoryTimelineControl _historyControl;
    private readonly IssueRollupControl _issueControl;

    public DependencyExplorerControl()
    {
        TabControl root = new();
        _explorerControl = new PresentationControl
        {
            Margin = new Thickness(10)
        };
        _explorerControl.RefreshRequested += OnRefreshRequested;
        _explorerControl.ReevaluationRequested += OnReevaluationRequested;

        root.Items.Add(new TabItem
        {
            Header = "Explorer",
            Content = _explorerControl
        });

        _historyControl = new HistoryTimelineControl
        {
            Margin = new Thickness(10)
        };
        root.Items.Add(new TabItem
        {
            Header = "History",
            Content = _historyControl
        });

        _issueControl = new IssueRollupControl
        {
            Margin = new Thickness(10)
        };
        root.Items.Add(new TabItem
        {
            Header = "Issues",
            Content = _issueControl
        });

        Content = root;
    }

    internal event EventHandler? RefreshRequested;
    internal event EventHandler<ReevaluationRequestEventArgs>? ReevaluationRequested;

    internal void LoadSnapshot(SolutionSnapshotFile snapshot, KnowledgeSnapshotFile knowledgeSnapshot)
        => _explorerControl.LoadSnapshot(VisualStudioSurfaceMapper.Map(snapshot, knowledgeSnapshot));

    internal void LoadHistory(string outputDirectory)
    {
        _historyControl.LoadHistory(outputDirectory);
        _issueControl.LoadIssues(outputDirectory);
    }

    private void OnRefreshRequested(object? sender, EventArgs e)
        => RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void OnReevaluationRequested(object? sender, ReevaluationRequestEventArgs e)
        => ReevaluationRequested?.Invoke(this, e);
}
