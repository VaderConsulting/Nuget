using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

namespace NuGetAudit.Core;

/// <summary>
/// Structured diagnostics for NuGet Audit. All entries use one line format and are written to <see cref="System.Diagnostics.Trace"/>.
/// </summary>
public static class DebugLog
{
    private static int? _uiThreadId;

    public static void RegisterUiThread()
    {
        _uiThreadId = Environment.CurrentManagedThreadId;
    }

    /// <summary>
    /// Writes a diagnostic line in every build configuration (use for recoverable infrastructure failures users may need in Release logs).
    /// </summary>
    public static void WriteTrace(string category, string message, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
    {
        Emit(category, message, memberName, filePath, lineNumber);
    }

    /// <summary>
    /// Writes a diagnostic line when <c>DEBUG</c> is defined (verbose UI and catalog tracing).
    /// </summary>
    [Conditional("DEBUG")]
    public static void Write(string category, string message, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
    {
        Emit(category, message, memberName, filePath, lineNumber);
    }

    private static void Emit(string category, string message, string memberName, string filePath, int lineNumber)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        string className = Path.GetFileNameWithoutExtension(filePath);
        int threadId = Environment.CurrentManagedThreadId;
        string threadKind = _uiThreadId is null
            ? "UNK"
            : _uiThreadId == threadId
                ? "UI"
                : "BG";

        using StringReader reader = new(message);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            Trace.WriteLine(
                $"{DateTimeOffset.Now:HH:mm:ss.fff} [{threadKind}:T{threadId}] [NuGetAudit:{category}] {className}.{memberName}:{lineNumber} - {line}");
        }
    }
}
