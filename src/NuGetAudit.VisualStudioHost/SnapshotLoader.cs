using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NuGetAudit.VisualStudioHost;

/// <summary>
/// Result of loading <see cref="SolutionSnapshotFile"/> JSON from disk.
/// </summary>
internal readonly struct SolutionSnapshotLoadResult
{
    public SolutionSnapshotFile Snapshot { get; }
    public Exception? Error { get; }

    public SolutionSnapshotLoadResult(SolutionSnapshotFile snapshot, Exception? error)
    {
        Snapshot = snapshot;
        Error = error;
    }
}

/// <summary>
/// Result of loading <see cref="KnowledgeSnapshotFile"/> JSON from disk.
/// </summary>
internal readonly struct KnowledgeSnapshotLoadResult
{
    public KnowledgeSnapshotFile Knowledge { get; }
    public Exception? Error { get; }

    public KnowledgeSnapshotLoadResult(KnowledgeSnapshotFile knowledge, Exception? error)
    {
        Knowledge = knowledge;
        Error = error;
    }
}

internal static class SnapshotLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static SolutionSnapshotLoadResult LoadFromPath(string snapshotPath)
    {
        if (string.IsNullOrWhiteSpace(snapshotPath) || !File.Exists(snapshotPath))
        {
            return new SolutionSnapshotLoadResult(new SolutionSnapshotFile(), null);
        }

        try
        {
            SolutionSnapshotFile? deserialized = JsonSerializer.Deserialize<SolutionSnapshotFile>(File.ReadAllText(snapshotPath), JsonOptions);
            return new SolutionSnapshotLoadResult(deserialized ?? new SolutionSnapshotFile(), null);
        }
        catch (Exception exception) when (exception is IOException
            or JsonException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            HostDiagnostics.LogArtefactFailure("Load snapshot JSON", snapshotPath, exception);
            return new SolutionSnapshotLoadResult(new SolutionSnapshotFile(), exception);
        }
    }

    internal static KnowledgeSnapshotLoadResult LoadKnowledgeFromDirectory(string? outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return new KnowledgeSnapshotLoadResult(new KnowledgeSnapshotFile(), null);
        }

        string latestKnowledgePath = Path.Combine(outputDirectory, "latest.knowledge.json");
        if (!File.Exists(latestKnowledgePath))
        {
            return new KnowledgeSnapshotLoadResult(new KnowledgeSnapshotFile(), null);
        }

        try
        {
            KnowledgeSnapshotFile? deserialized = JsonSerializer.Deserialize<KnowledgeSnapshotFile>(File.ReadAllText(latestKnowledgePath), JsonOptions);
            return new KnowledgeSnapshotLoadResult(deserialized ?? new KnowledgeSnapshotFile(), null);
        }
        catch (Exception exception) when (exception is IOException
            or JsonException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            HostDiagnostics.LogArtefactFailure("Load knowledge JSON", latestKnowledgePath, exception);
            return new KnowledgeSnapshotLoadResult(new KnowledgeSnapshotFile(), exception);
        }
    }
}
