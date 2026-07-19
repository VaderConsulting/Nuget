using System.IO;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace NuGetAudit.VisualStudioHost;

internal sealed class SolutionEventsListener : IVsSolutionEvents, IDisposable
{
    private readonly AsyncPackage _package;
    private readonly ErrorListHost _errorListHost;
    private IVsSolution? _solution;
    private uint _cookie;

    public SolutionEventsListener(AsyncPackage package, ErrorListHost errorListHost)
    {
        _package = package;
        _errorListHost = errorListHost;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        object? solutionService = await _package.GetServiceAsync(typeof(SVsSolution));
        _solution = solutionService as IVsSolution;

        if (_solution is not null)
        {
            ErrorHandler.ThrowOnFailure(_solution.AdviseSolutionEvents(this, out _cookie));
            PublishLatestSnapshot();
        }
    }

    public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        PublishLatestSnapshot();
        return VSConstants.S_OK;
    }

    public int OnAfterCloseSolution(object pUnkReserved) => VSConstants.S_OK;
    public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => VSConstants.S_OK;
    public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) => VSConstants.S_OK;
    public int OnAfterRenameProject(IVsHierarchy pHierarchy) => VSConstants.S_OK;
    public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => VSConstants.S_OK;
    public int OnBeforeCloseSolution(object pUnkReserved) => VSConstants.S_OK;
    public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;
    public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;
    public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.S_OK;
    public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => VSConstants.S_OK;

    public void Dispose()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_solution is not null && _cookie != 0)
        {
            _solution.UnadviseSolutionEvents(_cookie);
            _cookie = 0;
        }
    }

    internal static string? TryGetSolutionPath(IVsSolution solution)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        ErrorHandler.ThrowOnFailure(solution.GetSolutionInfo(out string? solutionDirectory, out string? solutionFile, out _));

        if (!string.IsNullOrWhiteSpace(solutionFile))
        {
            return solutionFile;
        }

        if (!string.IsNullOrWhiteSpace(solutionDirectory))
        {
            return solutionDirectory;
        }

        return null;
    }

    internal static string? TryGetLatestSnapshotPath(string? solutionPath)
    {
        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            return null;
        }

        string? parentDirectory = Path.GetDirectoryName(solutionPath);
        string baseDirectory = Directory.Exists(solutionPath)
            ? solutionPath!
            : parentDirectory ?? solutionPath!;

        return Path.Combine(baseDirectory, ".nugetaudit", "latest.snapshot.json");
    }

    private void PublishLatestSnapshot()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_solution is null)
        {
            return;
        }

        string? solutionPath = TryGetSolutionPath(_solution);
        string? snapshotPath = TryGetLatestSnapshotPath(solutionPath);

        if (!string.IsNullOrWhiteSpace(snapshotPath) && File.Exists(snapshotPath))
        {
            _errorListHost.PublishFromSnapshot(snapshotPath!);
        }
    }
}
