using System.IO;
using Microsoft.VisualStudio.Shell;

namespace NuGetAudit.VisualStudioHost;

internal sealed class NuGetChangeWatcher : IDisposable
{
    private readonly AsyncPackage _package;
    private readonly Func<Task> _onSettledAsync;
    private readonly System.Timers.Timer _timer;
    private FileSystemWatcher? _solutionWatcher;
    private FileSystemWatcher? _outputWatcher;

    internal NuGetChangeWatcher(AsyncPackage package, Func<Task> onSettledAsync)
    {
        _package = package;
        _onSettledAsync = onSettledAsync;
        _timer = new System.Timers.Timer(TimeSpan.FromSeconds(8).TotalMilliseconds)
        {
            AutoReset = false
        };
        _timer.Elapsed += (_, _) => _package.JoinableTaskFactory.RunAsync(_onSettledAsync).FileAndForget("NuGetAudit/NuGetChangeWatcher");
    }

    internal void Track(string solutionPath, string outputDirectory)
    {
        string solutionDirectory = Directory.Exists(solutionPath)
            ? solutionPath
            : Path.GetDirectoryName(solutionPath) ?? solutionPath;

        ConfigureWatcher(ref _solutionWatcher, solutionDirectory, OnInputChanged);
        ConfigureWatcher(ref _outputWatcher, outputDirectory, OnOutputChanged);
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        DisposeWatcher(ref _solutionWatcher);
        DisposeWatcher(ref _outputWatcher);
    }

    private void OnInputChanged(object sender, FileSystemEventArgs e)
    {
        string fileName = Path.GetFileName(e.FullPath);
        string extension = Path.GetExtension(e.FullPath);

        if (!fileName.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase)
            && !fileName.Equals("packages.lock.json", StringComparison.OrdinalIgnoreCase)
            && !fileName.Equals("project.assets.json", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Queue();
    }

    private void OnOutputChanged(object sender, FileSystemEventArgs e)
    {
        string fileName = Path.GetFileName(e.FullPath);
        if (!fileName.Equals("latest.snapshot.json", StringComparison.OrdinalIgnoreCase)
            && !fileName.Equals("knowledge.timeline.db", StringComparison.OrdinalIgnoreCase)
            && !fileName.EndsWith(".snapshot.json", StringComparison.OrdinalIgnoreCase)
            && !fileName.EndsWith(".delta.json", StringComparison.OrdinalIgnoreCase)
            && !fileName.EndsWith(".knowledge.json", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Queue();
    }

    private void Queue()
    {
        _timer.Stop();
        _timer.Start();
    }

    private static void ConfigureWatcher(ref FileSystemWatcher? watcher, string directory, FileSystemEventHandler handler)
    {
        DisposeWatcher(ref watcher);
        if (!Directory.Exists(directory))
        {
            return;
        }

        watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size
        };
        watcher.Changed += handler;
        watcher.Created += handler;
        watcher.Deleted += handler;
        watcher.Renamed += (_, e) => handler(_, e);
        watcher.EnableRaisingEvents = true;
    }

    private static void DisposeWatcher(ref FileSystemWatcher? watcher)
    {
        if (watcher is null)
        {
            return;
        }

        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
        watcher = null;
    }
}
