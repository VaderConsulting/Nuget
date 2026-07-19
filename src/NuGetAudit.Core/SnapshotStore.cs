using NuGetAudit.Core.Reporting;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NuGetAudit.Core;

/// <summary>
/// Loads and writes snapshot and report artefacts on disk.
/// </summary>
internal sealed class SnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly PackageKnowledgeSqliteStore _knowledgeStore = new();

    /// <summary>
    /// Loads the most recent accepted snapshot from the output directory.
    /// </summary>
    /// <param name="outputDirectory">The output directory that contains persisted snapshots.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The latest accepted snapshot, or <see langword="null"/> when none exists.</returns>
    public async Task<SolutionSnapshot?> LoadLatestAcceptedAsync(string outputDirectory, CancellationToken cancellationToken)
    {
        string latestAcceptedSnapshotPath = Path.Combine(outputDirectory, "latest.accepted.snapshot.json");
        if (!File.Exists(latestAcceptedSnapshotPath))
        {
            return null;
        }

        try
        {
            await using FileStream stream = File.OpenRead(latestAcceptedSnapshotPath);
            return await JsonSerializer.DeserializeAsync<SolutionSnapshot>(stream, JsonOptions, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException
            or JsonException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            string detail =
                $"Could not load latest accepted snapshot from '{latestAcceptedSnapshotPath}': {exception.GetType().Name}: {exception.Message}. Mitigation: treating as no previous baseline for delta comparison.";
            DebugLog.WriteTrace("Snapshot", detail);
            return null;
        }
    }

    /// <summary>
    /// Loads the latest package-knowledge snapshot from the output directory.
    /// </summary>
    /// <param name="outputDirectory">The output directory that contains persisted knowledge files.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The latest knowledge snapshot, or <see langword="null"/> when none exists.</returns>
    public async Task<PackageKnowledgeSnapshot?> LoadLatestKnowledgeAsync(string outputDirectory, CancellationToken cancellationToken)
    {
        return await _knowledgeStore.LoadLatestAsync(outputDirectory, cancellationToken);
    }

    /// <summary>
    /// Writes the generated artefacts for a completed audit run.
    /// </summary>
    /// <param name="outputDirectory">The directory where outputs should be written.</param>
    /// <param name="snapshot">The snapshot to persist.</param>
    /// <param name="delta">The delta to persist.</param>
    /// <param name="options">The options controlling which artefacts are written.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The generated report paths.</returns>
    public async Task<GeneratedReportSet> WriteAsync(string outputDirectory, SolutionSnapshot snapshot, SnapshotDelta delta, PackageKnowledgeSnapshot knowledgeSnapshot, AuditCommandOptions options, CancellationToken cancellationToken)
    {
        try
        {
            string safeSolutionName = string.Concat(snapshot.Solution.SolutionName.Select(static character =>
                Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
            string stem = $"{safeSolutionName}_{snapshot.CapturedUtc:yyyyMMdd_HHmmss}_{snapshot.SnapshotId}";

            string snapshotJsonPath = Path.Combine(outputDirectory, $"{stem}.snapshot.json");
            string latestSnapshotPath = Path.Combine(outputDirectory, "latest.snapshot.json");
            string latestAcceptedSnapshotPath = Path.Combine(outputDirectory, "latest.accepted.snapshot.json");
            string knowledgeJsonPath = Path.Combine(outputDirectory, $"{stem}.knowledge.json");
            string latestKnowledgePath = Path.Combine(outputDirectory, "latest.knowledge.json");
            string knowledgeDatabasePath = _knowledgeStore.GetDatabasePath(outputDirectory);
            string? deltaJsonPath = options.WriteJsonCompanion ? Path.Combine(outputDirectory, $"{stem}.delta.json") : null;
            string? markdownPath = options.WriteMarkdownReport ? Path.Combine(outputDirectory, $"{stem}.md") : null;

            await File.WriteAllTextAsync(snapshotJsonPath, JsonSerializer.Serialize(snapshot, JsonOptions), cancellationToken);
            await File.WriteAllTextAsync(latestSnapshotPath, JsonSerializer.Serialize(snapshot, JsonOptions), cancellationToken);

            if (snapshot.AnalysisStatus == AnalysisStatus.Complete)
            {
                await File.WriteAllTextAsync(latestAcceptedSnapshotPath, JsonSerializer.Serialize(snapshot, JsonOptions), cancellationToken);
            }

            if (deltaJsonPath is not null)
            {
                await File.WriteAllTextAsync(deltaJsonPath, JsonSerializer.Serialize(delta, JsonOptions), cancellationToken);
            }

            await File.WriteAllTextAsync(knowledgeJsonPath, JsonSerializer.Serialize(knowledgeSnapshot, JsonOptions), cancellationToken);
            await File.WriteAllTextAsync(latestKnowledgePath, JsonSerializer.Serialize(knowledgeSnapshot, JsonOptions), cancellationToken);
            bool sqlitePersisted = await _knowledgeStore.PersistAsync(
                outputDirectory,
                snapshot,
                knowledgeSnapshot,
                delta.KnowledgeChanges,
                snapshotJsonPath,
                markdownPath,
                deltaJsonPath,
                knowledgeJsonPath,
                cancellationToken);

            if (markdownPath is not null)
            {
                string markdown = MarkdownReportBuilder.Build(snapshot, delta, knowledgeSnapshot);
                await File.WriteAllTextAsync(markdownPath, markdown, cancellationToken);
            }

            return new GeneratedReportSet(markdownPath, snapshotJsonPath, deltaJsonPath, knowledgeJsonPath, sqlitePersisted ? knowledgeDatabasePath : null);
        }
        catch (Exception exception)
        {
            string detail =
                $"Snapshot artefact write failed under '{outputDirectory}'. {exception.GetType().Name}: {exception.Message}. Mitigation: rethrowing so the host can report failure; partial files may exist in the output folder.";
            DebugLog.WriteTrace("Snapshot", $"{detail}{Environment.NewLine}{exception}");
            throw;
        }
    }
}
