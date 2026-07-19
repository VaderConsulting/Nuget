namespace NuGetAudit.VisualStudioHost;

/// <summary>
/// Minimal diagnostic contract used by the Visual Studio host.
/// </summary>
public sealed class DiagnosticFile
{
    /// <summary>
    /// Gets or sets the diagnostic severity.
    /// </summary>
    public string? Severity { get; set; }

    /// <summary>
    /// Gets or sets the diagnostic message.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Gets or sets the project path used for navigation.
    /// </summary>
    public string? ProjectPath { get; set; }
}
