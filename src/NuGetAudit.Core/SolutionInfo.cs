namespace NuGetAudit.Core;

/// <summary>
/// Captures stable metadata about the analysed solution.
/// </summary>
/// <param name="SolutionName">The display name of the solution.</param>
/// <param name="SolutionPath">The full path to the solution file.</param>
/// <param name="SolutionKey">The stable key used to correlate snapshots for the solution.</param>
/// <param name="SourceIdentity">The broader source-code identity for this solution or project input.</param>
public sealed record SolutionInfo(string SolutionName, string SolutionPath, string SolutionKey, SourceCodeIdentity SourceIdentity);
