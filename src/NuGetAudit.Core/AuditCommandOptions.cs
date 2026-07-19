namespace NuGetAudit.Core;

/// <summary>
/// Defines command-line options for an audit run.
/// </summary>
/// <param name="SolutionPath">The solution or project file to analyse.</param>
/// <param name="OutputDirectory">The directory where generated artefacts are written.</param>
public sealed record AuditCommandOptions(string SolutionPath, string OutputDirectory)
{
    /// <summary>
    /// Gets a value indicating whether a Markdown report should be written.
    /// </summary>
    public bool WriteMarkdownReport { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether JSON companion files should be written.
    /// </summary>
    public bool WriteJsonCompanion { get; init; } = true;
}
