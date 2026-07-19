using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.Shell.Interop;

namespace NuGetAudit.VisualStudioHost;

/// <summary>
/// Central tracing and Visual Studio Activity Log output for the extension host.
/// </summary>
internal static class HostDiagnostics
{
    private static IServiceProvider? _serviceProvider;

    /// <summary>
    /// Assigns the package (or other) service provider used for Activity Log access. Call from package initialization.
    /// </summary>
    internal static void AssignServiceProvider(IServiceProvider? serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Logs a recoverable artefact load/save failure with full diagnostic detail and attempts to write the Activity Log.
    /// </summary>
    internal static void LogArtefactFailure(string operation, string path, Exception exception)
    {
        string summary = $"{operation} failed for '{path}'. {exception.GetType().Name}: {exception.Message}";
        EmitTrace("VSHost", summary);
        EmitTrace("VSHost", exception.ToString());
        TryActivityLog((uint)__ACTIVITYLOG_ENTRYTYPE.ALE_WARNING, summary, exception);
    }

    /// <summary>
    /// Logs a non-fatal host message (mitigation applied; no exception).
    /// </summary>
    internal static void LogMitigation(string message)
    {
        EmitTrace("VSHost", message);
        TryActivityLog((uint)__ACTIVITYLOG_ENTRYTYPE.ALE_INFORMATION, message, null);
    }

    private static void EmitTrace(string category, string message, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        string className = Path.GetFileNameWithoutExtension(filePath);
        int threadId = Environment.CurrentManagedThreadId;
        using StringReader reader = new(message);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            Trace.WriteLine(
                $"{DateTimeOffset.Now:HH:mm:ss.fff} [VS:T{threadId}] [NuGetAudit:{category}] {className}.{memberName}:{lineNumber} - {line}");
        }
    }

    private static void TryActivityLog(uint entryType, string summary, Exception? exception)
    {
        try
        {
            if (_serviceProvider is null)
            {
                return;
            }

            if (_serviceProvider.GetService(typeof(SVsActivityLog)) is not IVsActivityLog log)
            {
                return;
            }

            string description = exception is null
                ? summary
                : $"{summary}{Environment.NewLine}{exception}";
            log.LogEntry(entryType, "NuGet Audit", description);
        }
        catch (Exception logException)
        {
            EmitTrace("VSHost", $"Activity Log write failed: {logException.Message}");
        }
    }
}
