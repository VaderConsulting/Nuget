namespace NuGetAudit.Core;

/// <summary>
/// Bundles the outputs produced by a completed audit run.
/// </summary>
/// <param name="CurrentSnapshot">The newly generated snapshot.</param>
/// <param name="PreviousSnapshot">The previous snapshot used for comparison, if any.</param>
/// <param name="Delta">The computed delta.</param>
/// <param name="KnowledgeSnapshot">The current package-knowledge snapshot.</param>
/// <param name="ReportSet">The generated report artefacts.</param>
public sealed record AuditRunResult(SolutionSnapshot CurrentSnapshot, SolutionSnapshot? PreviousSnapshot, SnapshotDelta Delta, PackageKnowledgeSnapshot KnowledgeSnapshot, GeneratedReportSet ReportSet);
