using Microsoft.VisualStudio.Shell;
using NuGetAudit.Presentation;

namespace NuGetAudit.VisualStudioHost;

/// <summary>
/// Tool window that presents project dependency information from the latest NuGet audit snapshot.
/// </summary>
public sealed class DependencyExplorerToolWindow : ToolWindowPane
{
    private readonly DependencyExplorerControl _control;

    public DependencyExplorerToolWindow() : base(null)
    {
        Caption = "NuGet Audit Dependency Explorer";
        _control = new DependencyExplorerControl();
        Content = _control;
    }

    internal event EventHandler? RefreshRequested
    {
        add => _control.RefreshRequested += value;
        remove => _control.RefreshRequested -= value;
    }

    internal event EventHandler<ReevaluationRequestEventArgs>? ReevaluationRequested
    {
        add => _control.ReevaluationRequested += value;
        remove => _control.ReevaluationRequested -= value;
    }

    internal void LoadSnapshot(SolutionSnapshotFile snapshot, KnowledgeSnapshotFile knowledgeSnapshot)
    {
        _control.LoadSnapshot(snapshot, knowledgeSnapshot);
    }

    internal void LoadHistory(string outputDirectory)
    {
        _control.LoadHistory(outputDirectory);
    }
}
