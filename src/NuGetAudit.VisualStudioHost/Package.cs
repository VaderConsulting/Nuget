using System.ComponentModel.Design;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using NuGetAudit.Presentation;
using Task = System.Threading.Tasks.Task;

namespace NuGetAudit.VisualStudioHost;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[InstalledProductRegistration("NuGet Audit", "Publishes NuGet audit diagnostics to the Error List.", "1.0")]
[ProvideMenuResource("Menus.ctmenu", 1)]
[Guid(Guids.PackageString)]
[ProvideToolWindow(typeof(DependencyExplorerToolWindow))]
[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
public sealed class Package : AsyncPackage
{
    private ErrorListHost? _errorListHost;
    private SolutionEventsListener? _solutionEventsListener;
    private NuGetChangeWatcher? _nuGetChangeWatcher;

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        _errorListHost = new ErrorListHost(this);
        HostDiagnostics.AssignServiceProvider(this);
        _solutionEventsListener = new SolutionEventsListener(this, _errorListHost);
        await _solutionEventsListener.InitializeAsync(cancellationToken);
        _nuGetChangeWatcher = new NuGetChangeWatcher(this, async () => await LoadLatestSnapshotAsync(DisposalToken));

        if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
        {
            CommandID commandId = new(Guids.CommandSet, PackageIds.LoadLatestSnapshotCommand);
            OleMenuCommand command = new((_, _) => JoinableTaskFactory.RunAsync(async delegate
            {
                await LoadLatestSnapshotAsync(DisposalToken);
            }).FileAndForget("NuGetAudit/LoadLatestSnapshot"), commandId);
            commandService.AddCommand(command);

            CommandID explorerCommandId = new(Guids.CommandSet, PackageIds.OpenDependencyExplorerCommand);
            OleMenuCommand explorerCommand = new((_, _) => JoinableTaskFactory.RunAsync(async delegate
            {
                await ShowDependencyExplorerAsync(DisposalToken);
            }).FileAndForget("NuGetAudit/OpenDependencyExplorer"), explorerCommandId);
            commandService.AddCommand(explorerCommand);
        }
    }

    private async Task LoadLatestSnapshotAsync(CancellationToken cancellationToken)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        string? snapshotPath = await TryGetLatestSnapshotPathAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(snapshotPath))
        {
            PublishSnapshot(snapshotPath!);
        }
    }

    private async Task ShowDependencyExplorerAsync(CancellationToken cancellationToken)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        ToolWindowPane window = await ShowToolWindowAsync(typeof(DependencyExplorerToolWindow), 0, true, cancellationToken);
        if (window is not DependencyExplorerToolWindow dependencyWindow)
        {
            return;
        }

        dependencyWindow.RefreshRequested -= OnDependencyExplorerRefreshRequested;
        dependencyWindow.RefreshRequested += OnDependencyExplorerRefreshRequested;
        dependencyWindow.ReevaluationRequested -= OnDependencyExplorerReevaluationRequested;
        dependencyWindow.ReevaluationRequested += OnDependencyExplorerReevaluationRequested;

        string? snapshotPath = await TryGetLatestSnapshotPathAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(snapshotPath))
        {
            dependencyWindow.LoadSnapshot(new SolutionSnapshotFile(), new KnowledgeSnapshotFile());
            return;
        }

        PublishSnapshot(snapshotPath!, dependencyWindow);
    }

    private async Task<string?> TryGetLatestSnapshotPathAsync(CancellationToken cancellationToken)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        if (await GetServiceAsync(typeof(SVsSolution)) is not IVsSolution solution)
        {
            return null;
        }

        string? solutionPath = SolutionEventsListener.TryGetSolutionPath(solution);
        string? snapshotPath = SolutionEventsListener.TryGetLatestSnapshotPath(solutionPath);
        if (!string.IsNullOrWhiteSpace(solutionPath) && !string.IsNullOrWhiteSpace(snapshotPath))
        {
            string solutionPathValue = solutionPath!;
            string outputDirectory = Path.GetDirectoryName(snapshotPath) ?? Path.Combine(Path.GetDirectoryName(solutionPathValue) ?? solutionPathValue, ".nugetaudit");
            _nuGetChangeWatcher?.Track(solutionPathValue, outputDirectory);
        }
        return !string.IsNullOrWhiteSpace(snapshotPath) && File.Exists(snapshotPath) ? snapshotPath : null;
    }

    private void PublishSnapshot(string snapshotPath, DependencyExplorerToolWindow? dependencyWindow = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            SolutionSnapshotLoadResult snapshotResult = SnapshotLoader.LoadFromPath(snapshotPath);
            KnowledgeSnapshotLoadResult knowledgeResult = SnapshotLoader.LoadKnowledgeFromDirectory(Path.GetDirectoryName(snapshotPath));

            List<DiagnosticFile> diagnostics = new();
            if (snapshotResult.Error is not null)
            {
                diagnostics.Add(CreateSnapshotLoadDiagnostic(snapshotPath, snapshotResult.Error));
            }
            else
            {
                diagnostics.AddRange(snapshotResult.Snapshot.Projects.SelectMany(static project => project.Diagnostics));
                if (knowledgeResult.Error is not null)
                {
                    diagnostics.Add(CreateKnowledgeLoadDiagnostic(knowledgeResult.Error));
                }
            }

            _errorListHost?.Publish(diagnostics);

            dependencyWindow ??= FindToolWindow(typeof(DependencyExplorerToolWindow), 0, create: false) as DependencyExplorerToolWindow;
            string? outputDirectory = Path.GetDirectoryName(snapshotPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                dependencyWindow?.LoadHistory(outputDirectory!);
            }

            dependencyWindow?.LoadSnapshot(snapshotResult.Snapshot, knowledgeResult.Knowledge);
        }
        catch (Exception exception)
        {
            HostDiagnostics.LogArtefactFailure("PublishSnapshot", snapshotPath, exception);
            _errorListHost?.Publish(new[]
            {
                new DiagnosticFile
                {
                    Severity = "Warning",
                    Message =
                        $"NuGet Audit failed while publishing results: {exception.Message}. Error List and Dependency Explorer were reset. See Activity Log for detail.",
                    ProjectPath = snapshotPath,
                }
            });
            MitigatePublishSnapshotFailure(dependencyWindow);
            HostDiagnostics.LogMitigation("PublishSnapshot: Dependency Explorer cleared after unexpected failure.");
        }
    }

    private static DiagnosticFile CreateSnapshotLoadDiagnostic(string snapshotPath, Exception error)
    {
        return new DiagnosticFile
        {
            Severity = "Warning",
            Message =
                $"NuGet Audit could not load snapshot JSON: {error.Message}. Explorer will show no packages until a valid latest.snapshot.json is available.",
            ProjectPath = snapshotPath,
        };
    }

    private static DiagnosticFile CreateKnowledgeLoadDiagnostic(Exception error)
    {
        return new DiagnosticFile
        {
            Severity = "Warning",
            Message =
                $"NuGet Audit could not load latest.knowledge.json: {error.Message}. Knowledge overlay in the explorer may be incomplete.",
        };
    }

    private void MitigatePublishSnapshotFailure(DependencyExplorerToolWindow? dependencyWindow)
    {
        DependencyExplorerToolWindow? window = dependencyWindow ?? FindToolWindow(typeof(DependencyExplorerToolWindow), 0, create: false) as DependencyExplorerToolWindow;
        window?.LoadSnapshot(new SolutionSnapshotFile(), new KnowledgeSnapshotFile());
    }

    private void OnDependencyExplorerRefreshRequested(object? sender, EventArgs e)
    {
        JoinableTaskFactory.RunAsync(async delegate
        {
            await LoadLatestSnapshotAsync(DisposalToken);
        }).FileAndForget("NuGetAudit/ExplorerRefresh");
    }

    private void OnDependencyExplorerReevaluationRequested(object? sender, ReevaluationRequestEventArgs e)
    {
        JoinableTaskFactory.RunAsync(async delegate
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);

            string scopeMessage = e.Scope switch
            {
                "Package" when e.Package is not null => $"NuGetAudit package re-evaluation is not wired to a package-only runner yet. The extension will refresh the latest snapshot for package {e.Package.PackageId} {e.Package.ResolvedVersion ?? e.Package.RequestedVersion ?? "-"} instead.",
                "Project" when e.Project is not null => $"NuGetAudit project re-evaluation is not wired to a project-only runner yet. The extension will refresh the latest snapshot for project {e.Project.ProjectName ?? "(unknown)"} instead.",
                _ => "NuGetAudit solution re-evaluation currently refreshes the latest available snapshot from disk. Running a fresh audit from inside Visual Studio is the next host step."
            };

            VsShellUtilities.ShowMessageBox(
                this,
                scopeMessage,
                "NuGet Audit",
                OLEMSGICON.OLEMSGICON_INFO,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);

            await LoadLatestSnapshotAsync(DisposalToken);
        }).FileAndForget("NuGetAudit/ExplorerReevaluate");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            HostDiagnostics.AssignServiceProvider(null);
            if (_solutionEventsListener is not null)
            {
                SolutionEventsListener listener = _solutionEventsListener;
                _solutionEventsListener = null;

                JoinableTaskFactory.Run(async delegate
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    listener.Dispose();
                });
            }

            _errorListHost?.Dispose();
            _nuGetChangeWatcher?.Dispose();
        }

        base.Dispose(disposing);
    }
}
