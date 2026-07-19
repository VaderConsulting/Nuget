using Microsoft.VisualStudio.Shell;

namespace NuGetAudit.VisualStudioHost;

/// <summary>
/// Publishes NuGet audit diagnostics into the Visual Studio Error List.
/// </summary>
public sealed class ErrorListHost : IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ErrorListProvider _errorListProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="ErrorListHost"/> class.
    /// </summary>
    /// <param name="serviceProvider">The Visual Studio service provider.</param>
    public ErrorListHost(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _errorListProvider = new ErrorListProvider(serviceProvider)
        {
            ProviderName = "NuGet Audit",
            ProviderGuid = new Guid("7EB6B31D-319F-49F9-9F87-53D3F7AAEE81")
        };
    }

    /// <summary>
    /// Loads diagnostics from a snapshot file and publishes them to the Error List.
    /// </summary>
    /// <param name="snapshotPath">The path to the snapshot JSON file.</param>
    public void PublishFromSnapshot(string snapshotPath)
    {
        try
        {
            SolutionSnapshotLoadResult loadResult = SnapshotLoader.LoadFromPath(snapshotPath);
            if (loadResult.Error is not null)
            {
                PublishSnapshotLoadWarning("latest.snapshot.json", snapshotPath, loadResult.Error);
                return;
            }

            Publish(loadResult.Snapshot.Projects.SelectMany(static project => project.Diagnostics));
        }
        catch (Exception exception)
        {
            HostDiagnostics.LogArtefactFailure("ErrorListHost.PublishFromSnapshot", snapshotPath, exception);
            PublishSnapshotLoadWarning("Error List publish", snapshotPath, exception);
        }
    }

    /// <summary>
    /// Publishes a single warning row describing a snapshot artefact failure (mitigation: prior tasks cleared).
    /// </summary>
    public void PublishSnapshotLoadWarning(string artefactLabel, string path, Exception exception)
    {
        Publish(new[]
        {
            new DiagnosticFile
            {
                Severity = "Warning",
                Message =
                    $"NuGet Audit could not use {artefactLabel} at '{path}': {exception.Message}. See Activity Log (Extensions) for full detail.",
                ProjectPath = path,
            }
        });
    }

    /// <summary>
    /// Publishes diagnostics to the Error List.
    /// </summary>
    /// <param name="diagnostics">The diagnostics to publish.</param>
    public void Publish(IEnumerable<DiagnosticFile> diagnostics)
    {
        _errorListProvider.Tasks.Clear();
        _errorListProvider.SuspendRefresh();

        try
        {
            foreach (DiagnosticFile diagnostic in diagnostics)
            {
                ErrorTask task = new()
                {
                    Category = TaskCategory.BuildCompile,
                    ErrorCategory = MapSeverity(diagnostic.Severity),
                    Text = diagnostic.Message ?? "NuGet audit diagnostic.",
                    Document = diagnostic.ProjectPath ?? string.Empty
                };

                string? navigationTarget = diagnostic.ProjectPath;
                if (!string.IsNullOrWhiteSpace(navigationTarget))
                {
                    task.Navigate += (_, _) => VsShellUtilities.OpenDocument(_serviceProvider, navigationTarget);
                }

                _errorListProvider.Tasks.Add(task);
            }
        }
        catch (Exception exception)
        {
            HostDiagnostics.LogArtefactFailure("ErrorListHost.Publish", "(enumerating diagnostics)", exception);
            _errorListProvider.Tasks.Clear();
        }
        finally
        {
            _errorListProvider.ResumeRefresh();
        }

        if (_errorListProvider.Tasks.Count > 0)
        {
            _errorListProvider.Show();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _errorListProvider.Dispose();
    }

    private static TaskErrorCategory MapSeverity(string? severity)
    {
        return severity switch
        {
            "Error" => TaskErrorCategory.Error,
            "Warning" => TaskErrorCategory.Warning,
            _ => TaskErrorCategory.Message
        };
    }
}
