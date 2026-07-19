namespace NuGetAudit.Core;

/// <summary>
/// Identifies the generated files written for a run.
/// </summary>
/// <param name="MarkdownReportPath">The Markdown report path, if one was written.</param>
/// <param name="SnapshotJsonPath">The snapshot JSON path.</param>
/// <param name="DeltaJsonPath">The delta JSON path, if one was written.</param>
/// <param name="KnowledgeJsonPath">The per-run package-knowledge JSON export path.</param>
/// <param name="KnowledgeDatabasePath">The SQLite package-knowledge database path, if SQLite persistence succeeded.</param>
public sealed record GeneratedReportSet(string? MarkdownReportPath, string SnapshotJsonPath, string? DeltaJsonPath, string KnowledgeJsonPath, string? KnowledgeDatabasePath);
