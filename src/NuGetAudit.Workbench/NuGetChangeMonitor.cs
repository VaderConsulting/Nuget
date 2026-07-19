using System.IO;
using System.Windows.Threading;
using NuGetAudit.Core;

namespace NuGetAudit.Workbench;

internal sealed class NuGetChangeMonitor : IDisposable
{
    private static readonly string[] SupportedExtensions = [".csproj", ".vbproj", ".fsproj", ".props", ".targets", ".json", ".sln", ".slnx"];

    private readonly DispatcherTimer _timer;
    private readonly Action<string> _onSettled;
    private FileSystemWatcher? _inputWatcher;
    private FileSystemWatcher? _outputWatcher;
    private string? _trackedInputPath;
    private string? _pendingReason;

    internal NuGetChangeMonitor(Action<string> onSettled)
    {
        _onSettled = onSettled;
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(8)
        };
        _timer.Tick += OnTimerTick;
    }

    internal void Track(string inputPath, string outputDirectory)
    {
        _trackedInputPath = Path.GetFullPath(inputPath);
        ConfigureWatcher(ref _inputWatcher, GetWatchRoot(_trackedInputPath), OnInputChanged);
        ConfigureWatcher(ref _outputWatcher, Path.GetFullPath(outputDirectory), OnOutputChanged);
    }

    internal void CancelPending()
    {
        _pendingReason = null;
        _timer.Stop();
    }

    public void Dispose()
    {
        _timer.Stop();
        DisposeWatcher(ref _inputWatcher);
        DisposeWatcher(ref _outputWatcher);
    }

    private void OnInputChanged(object sender, FileSystemEventArgs e)
    {
        if (!IsRelevantInputChange(e.FullPath))
        {
            return;
        }

        Queue("NuGet package topology changed. Waiting for restore and file updates to settle before re-evaluating.");
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

        Queue("NuGet audit artefacts changed. Waiting briefly before refreshing.");
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (!string.IsNullOrWhiteSpace(_pendingReason))
        {
            _onSettled(_pendingReason!);
        }
    }

    private void Queue(string reason)
    {
        _pendingReason = reason;
        _timer.Stop();
        _timer.Start();
    }

    private bool IsRelevantInputChange(string fullPath)
    {
        string normalizedPath = Path.GetFullPath(fullPath);
        if (AuditProjectDisplayName.IsUnderVsHistoryFolder(normalizedPath))
        {
            return false;
        }

        string fileName = Path.GetFileName(normalizedPath);
        string extension = Path.GetExtension(normalizedPath);

        if (fileName.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("packages.lock.json", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("project.assets.json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(normalizedPath, _trackedInputPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedPath.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase)
            && fileName.Equals("project.assets.json", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetWatchRoot(string inputPath)
    {
        if (inputPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || inputPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetDirectoryName(inputPath) ?? Environment.CurrentDirectory;
        }

        return Path.GetDirectoryName(inputPath) ?? Environment.CurrentDirectory;
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
