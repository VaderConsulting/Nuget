using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using NuGetAudit.DbUtilities;

namespace NuGetAudit.Presentation;

public static class SnapshotHistoryReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly HashSet<string> ReportedFailures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object ReportedFailuresLock = new();

    public static SurfaceHistory Load(string outputDirectory)
    {
        string fullOutputDirectory = Path.GetFullPath(outputDirectory);
        string sourceName = Path.GetFileName(Path.GetDirectoryName(fullOutputDirectory) ?? fullOutputDirectory);
        string databasePath = Path.Combine(fullOutputDirectory, "knowledge.timeline.db");

        List<SnapshotHistoryItem> snapshots = new();
        List<KnowledgeTimelineItem> knowledgeTimeline = new();
        if (File.Exists(databasePath))
        {
            KnowledgeTimelineDbVsHistoryPurge.TryPurge(fullOutputDirectory);
            try
            {
                using SqliteConnection connection = OpenReadOnlyConnection(databasePath);
                if (HasRequiredSchema(connection))
                {
                    snapshots = LoadSnapshots(connection, fullOutputDirectory, sourceName);
                    knowledgeTimeline = LoadKnowledgeTimeline(connection, sourceName, fullOutputDirectory);
                }
                else
                {
                    ReportHandledFailure(databasePath, "schema-mismatch", "Skipped history load because the SQLite schema is missing required tables or columns.");
                }
            }
            catch (SqliteException exception)
            {
                ReportHandledFailure(databasePath, "sqlite", $"Handled SQLite failure while loading history: {exception.Message}");
            }
            catch (IOException exception)
            {
                ReportHandledFailure(databasePath, "io", $"Handled I/O failure while loading history: {exception.Message}");
            }
            catch (UnauthorizedAccessException exception)
            {
                ReportHandledFailure(databasePath, "access", $"Handled access failure while loading history: {exception.Message}");
            }
            catch (JsonException exception)
            {
                ReportHandledFailure(databasePath, "json", $"Handled JSON failure while loading history: {exception.Message}");
            }
        }

        return new SurfaceHistory(snapshots.OrderByDescending(static item => item.CapturedUtc).ToArray(), knowledgeTimeline.OrderByDescending(static item => item.DeterminedUtc).ToArray(), File.Exists(databasePath) ? databasePath : null);
    }

    public static SurfaceHistory LoadMany(IEnumerable<(string SourceName, string OutputDirectory)> sources)
    {
        (string SourceName, string OutputDirectory)[] sourceRows = sources is (string, string)[] existingRows
            ? existingRows
            : sources.ToArray();

        List<SnapshotHistoryItem> snapshots = new();
        List<KnowledgeTimelineItem> knowledgeTimeline = new();
        List<string> knowledgeDatabases = new();

        foreach ((string sourceName, string outputDirectory) in sourceRows)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
            {
                continue;
            }

            string fullOutputDirectory = Path.GetFullPath(outputDirectory);
            string effectiveSourceName = string.IsNullOrWhiteSpace(sourceName)
                ? Path.GetFileName(Path.GetDirectoryName(fullOutputDirectory) ?? fullOutputDirectory)
                : sourceName;

            string databasePath = Path.Combine(fullOutputDirectory, "knowledge.timeline.db");
            if (File.Exists(databasePath))
            {
                KnowledgeTimelineDbVsHistoryPurge.TryPurge(fullOutputDirectory);
                try
                {
                    using SqliteConnection connection = OpenReadOnlyConnection(databasePath);
                    if (!HasRequiredSchema(connection))
                    {
                        ReportHandledFailure(databasePath, "schema-mismatch", "Skipped aggregated history load because the SQLite schema is missing required tables or columns.");
                        continue;
                    }

                    snapshots.AddRange(LoadSnapshots(connection, fullOutputDirectory, effectiveSourceName));
                    knowledgeTimeline.AddRange(LoadKnowledgeTimeline(connection, effectiveSourceName, fullOutputDirectory));
                    knowledgeDatabases.Add(databasePath);
                }
                catch (SqliteException exception)
                {
                    ReportHandledFailure(databasePath, "sqlite", $"Handled SQLite failure while loading aggregated history: {exception.Message}");
                }
                catch (IOException exception)
                {
                    ReportHandledFailure(databasePath, "io", $"Handled I/O failure while loading aggregated history: {exception.Message}");
                }
                catch (UnauthorizedAccessException exception)
                {
                    ReportHandledFailure(databasePath, "access", $"Handled access failure while loading aggregated history: {exception.Message}");
                }
                catch (JsonException exception)
                {
                    ReportHandledFailure(databasePath, "json", $"Handled JSON failure while loading aggregated history: {exception.Message}");
                }
            }
        }

        return new SurfaceHistory(snapshots.OrderByDescending(static item => item.CapturedUtc).ToArray(), knowledgeTimeline.OrderByDescending(static item => item.DeterminedUtc).ToArray(), knowledgeDatabases.Count == 0 ? null : string.Join(Environment.NewLine, knowledgeDatabases.Distinct(StringComparer.OrdinalIgnoreCase)));
    }

    private static List<SnapshotHistoryItem> LoadSnapshots(SqliteConnection connection, string outputDirectory, string sourceName)
    {
        List<SnapshotHistoryItem> snapshots = new();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                run.SnapshotId,
                run.CapturedUtc,
                run.SolutionName,
                run.SolutionPath,
                run.AnalysisStatus,
                run.SnapshotJsonPath,
                run.MarkdownPath,
                run.DeltaJsonPath,
                COUNT(DISTINCT project.ProjectId) AS ProjectCount,
                COUNT(package.StablePackageInstanceKey) AS PackageCount,
                COALESCE(SUM(
                    CASE
                        WHEN json_extract(package.HealthInfoJson, '$.IsVulnerable') = 1
                          OR json_extract(package.HealthInfoJson, '$.IsDeprecated') = 1
                          OR json_extract(package.HealthInfoJson, '$.IsObsolete') = 1
                          OR json_extract(package.HealthInfoJson, '$.IsOutdated') = 1
                          OR json_extract(package.HealthInfoJson, '$.DevelopmentStatus') IN ('Abandoned', 'Removed')
                        THEN 1
                        ELSE 0
                    END), 0) AS AttentionCount
            FROM SnapshotRun run
            LEFT JOIN ProjectSnapshot project
                ON project.SnapshotId = run.SnapshotId
            LEFT JOIN PackageInstance package
                ON package.SnapshotId = project.SnapshotId
               AND package.ProjectId = project.ProjectId
            GROUP BY
                run.SnapshotId,
                run.CapturedUtc,
                run.SolutionName,
                run.SolutionPath,
                run.AnalysisStatus,
                run.SnapshotJsonPath,
                run.MarkdownPath,
                run.DeltaJsonPath
            ORDER BY run.CapturedUtc DESC;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string analysisStatus = reader.GetString(4);
            int attentionCount = reader.GetInt32(10);
            string rowVisualBand = ComputeSnapshotRowVisualBand(analysisStatus, attentionCount);
            snapshots.Add(new SnapshotHistoryItem(sourceName, outputDirectory, reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.IsDBNull(5) ? string.Empty : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), analysisStatus, reader.GetInt32(8), reader.GetInt32(9), attentionCount, rowVisualBand));
        }

        return snapshots;
    }

    /// <summary>
    /// Snapshot row accent aligned with <see cref="ComputeKnowledgeTimelineRowVisualBand"/> colours (Critical / Attention / Ok / None).
    /// </summary>
    private static string ComputeSnapshotRowVisualBand(string analysisStatus, int attentionCount)
    {
        if (string.Equals(analysisStatus, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            return "Critical";
        }

        if (string.Equals(analysisStatus, "Unsupported", StringComparison.OrdinalIgnoreCase)
            || string.Equals(analysisStatus, "Partial", StringComparison.OrdinalIgnoreCase))
        {
            return "Attention";
        }

        if (string.Equals(analysisStatus, "Complete", StringComparison.OrdinalIgnoreCase))
        {
            return attentionCount > 0 ? "Attention" : "Ok";
        }

        return "None";
    }

    private static List<KnowledgeTimelineItem> LoadKnowledgeTimeline(SqliteConnection connection, string sourceName, string outputDirectory)
    {
        List<KnowledgeTimelineItem> items = new();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                changeRow.Id,
                changeRow.SnapshotId,
                changeRow.PackageId,
                changeRow.ResolvedVersion,
                changeRow.DeterminedUtc,
                changeRow.PreviousHealthJson,
                changeRow.CurrentHealthJson,
                changeRow.RemediationJson,
                changeRow.Summary,
                knowledge.CriticalityJson
            FROM PackageKnowledgeChange changeRow
            LEFT JOIN PackageKnowledge knowledge
                ON knowledge.PackageId = changeRow.PackageId
                AND knowledge.ResolvedVersion = changeRow.ResolvedVersion
            ORDER BY changeRow.DeterminedUtc DESC, changeRow.PackageId, changeRow.ResolvedVersion;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            SurfaceHealthInfo? previousHealthModel = reader.IsDBNull(5) ? null : JsonSerializer.Deserialize<SurfaceHealthInfo>(reader.GetString(5), JsonOptions);
            SurfaceHealthInfo? currentHealthModel = JsonSerializer.Deserialize<SurfaceHealthInfo>(reader.GetString(6), JsonOptions);
            string previousHealth = DescribeHealth(previousHealthModel);
            string currentHealth = DescribeHealth(currentHealthModel);
            SurfaceRemediationAdvice? remediation = JsonSerializer.Deserialize<SurfaceRemediationAdvice>(reader.GetString(7), JsonOptions);
            SurfaceCriticalityAssessment? criticality = !reader.IsDBNull(9)
                ? JsonSerializer.Deserialize<SurfaceCriticalityAssessment>(reader.GetString(9), JsonOptions)
                : null;
            string rowVisualBand = ComputeKnowledgeTimelineRowVisualBand(currentHealthModel, criticality);

            items.Add(new KnowledgeTimelineItem(reader.GetInt64(0), sourceName, outputDirectory, reader.GetString(1), reader.GetString(2), reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4)), previousHealth, currentHealth, DescribeRemediation(remediation), reader.GetString(8), criticality?.RiskScore, criticality?.AlertScore, criticality?.RiskBand, criticality?.AlertBand, rowVisualBand));
        }

        return items;
    }

    private static string DescribeHealth(SurfaceHealthInfo? health)
    {
        if (health is null)
        {
            return "-";
        }

        List<string> states = new();
        if (health.IsVulnerable)
        {
            states.Add($"vulnerable:{health.MaxVulnerabilitySeverity ?? "unknown"}");
        }

        if (health.IsObsolete)
        {
            states.Add("obsolete");
        }
        else if (health.IsDeprecated)
        {
            states.Add("deprecated");
        }

        if (health.IsOutdated)
        {
            states.Add($"outdated->{health.LatestStableVersion ?? "unknown"}");
        }

        if (string.Equals(health.DevelopmentStatus, "Removed", StringComparison.OrdinalIgnoreCase))
        {
            states.Add("removed");
        }
        else if (string.Equals(health.DevelopmentStatus, "Abandoned", StringComparison.OrdinalIgnoreCase))
        {
            states.Add("abandoned");
        }

        return states.Count == 0 ? "ok" : string.Join(", ", states);
    }

    /// <summary>
    /// Row accent for the History knowledge grid: matches snapshot timeline severity colors (Critical / High / Attention / Ok / None).
    /// </summary>
    private static string ComputeKnowledgeTimelineRowVisualBand(SurfaceHealthInfo? health, SurfaceCriticalityAssessment? criticality)
    {
        string? riskBand = criticality?.RiskBand;
        string? alertBand = criticality?.AlertBand;
        if (string.Equals(riskBand, "Critical", StringComparison.OrdinalIgnoreCase)
            || string.Equals(alertBand, "Critical", StringComparison.OrdinalIgnoreCase))
        {
            return "Critical";
        }

        if (string.Equals(riskBand, "High", StringComparison.OrdinalIgnoreCase)
            || string.Equals(alertBand, "High", StringComparison.OrdinalIgnoreCase))
        {
            return "High";
        }

        if (health is null)
        {
            return "None";
        }

        if (health.IsVulnerable)
        {
            return "Critical";
        }

        if (health.IsObsolete
            || string.Equals(health.DevelopmentStatus, "Removed", StringComparison.OrdinalIgnoreCase))
        {
            return "Critical";
        }

        if (health.IsDeprecated
            || health.IsOutdated
            || string.Equals(health.DevelopmentStatus, "Abandoned", StringComparison.OrdinalIgnoreCase))
        {
            return "Attention";
        }

        return "Ok";
    }

    private static string DescribeRemediation(SurfaceRemediationAdvice? remediation)
    {
        if (remediation is null)
        {
            return "-";
        }

        return remediation.Summary
               ?? remediation.RecommendedVersion
               ?? (!string.IsNullOrWhiteSpace(remediation.AlternatePackageId)
                   ? $"{remediation.AlternatePackageId} {remediation.AlternatePackageRange}".Trim()
                   : "-");
    }

    private sealed class SurfaceHealthInfo
    {
        public bool IsDeprecated { get; set; }
        public bool IsObsolete { get; set; }
        public bool IsOutdated { get; set; }
        public bool IsVulnerable { get; set; }
        public string? DevelopmentStatus { get; set; }
        public string? LatestStableVersion { get; set; }
        public string? MaxVulnerabilitySeverity { get; set; }
    }

    private sealed class SurfaceRemediationAdvice
    {
        public string? RecommendedVersion { get; set; }
        public string? AlternatePackageId { get; set; }
        public string? AlternatePackageRange { get; set; }
        public string? Summary { get; set; }
    }

    private sealed class SurfaceCriticalityAssessment
    {
        public double RiskScore { get; set; }
        public double AlertScore { get; set; }
        public string? RiskBand { get; set; }
        public string? AlertBand { get; set; }
    }

    private static SqliteConnection OpenReadOnlyConnection(string databasePath)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void ReportHandledFailure(string databasePath, string reason, string message)
    {
        string key = databasePath + "|" + reason;
        lock (ReportedFailuresLock)
        {
            if (!ReportedFailures.Add(key))
            {
                return;
            }
        }

        Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} [BG:T{Environment.CurrentManagedThreadId}] Sqlite SnapshotHistoryReader - {message} Database='{databasePath}'.");
    }

    private static bool HasRequiredSchema(SqliteConnection connection)
        => HasTable(connection, "SnapshotRun")
           && HasTable(connection, "ProjectSnapshot")
           && HasTable(connection, "PackageInstance")
           && HasTable(connection, "PackageKnowledgeChange")
           && HasTable(connection, "PackageKnowledge")
           && HasColumn(connection, "SnapshotRun", "SnapshotId")
           && HasColumn(connection, "SnapshotRun", "CapturedUtc")
           && HasColumn(connection, "SnapshotRun", "SolutionName")
           && HasColumn(connection, "SnapshotRun", "SolutionPath")
           && HasColumn(connection, "SnapshotRun", "AnalysisStatus")
           && HasColumn(connection, "SnapshotRun", "SnapshotJsonPath")
           && HasColumn(connection, "SnapshotRun", "MarkdownPath")
           && HasColumn(connection, "SnapshotRun", "DeltaJsonPath")
           && HasColumn(connection, "ProjectSnapshot", "SnapshotId")
           && HasColumn(connection, "ProjectSnapshot", "ProjectId")
           && HasColumn(connection, "PackageInstance", "SnapshotId")
           && HasColumn(connection, "PackageInstance", "ProjectId")
           && HasColumn(connection, "PackageInstance", "StablePackageInstanceKey")
           && HasColumn(connection, "PackageInstance", "HealthInfoJson")
           && HasColumn(connection, "PackageKnowledgeChange", "Id")
           && HasColumn(connection, "PackageKnowledgeChange", "SnapshotId")
           && HasColumn(connection, "PackageKnowledgeChange", "PackageId")
           && HasColumn(connection, "PackageKnowledgeChange", "ResolvedVersion")
           && HasColumn(connection, "PackageKnowledgeChange", "DeterminedUtc")
           && HasColumn(connection, "PackageKnowledgeChange", "PreviousHealthJson")
           && HasColumn(connection, "PackageKnowledgeChange", "CurrentHealthJson")
           && HasColumn(connection, "PackageKnowledgeChange", "RemediationJson")
           && HasColumn(connection, "PackageKnowledgeChange", "Summary")
           && HasColumn(connection, "PackageKnowledge", "PackageId")
           && HasColumn(connection, "PackageKnowledge", "ResolvedVersion")
           && HasColumn(connection, "PackageKnowledge", "CriticalityJson");

    private static bool HasTable(SqliteConnection connection, string tableName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table' AND name = $tableName
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$tableName", tableName);
        return command.ExecuteScalar() is not null;
    }

    private static bool HasColumn(SqliteConnection connection, string tableName, string columnName)
    {
        string escapedTable = tableName.Replace("'", "''");
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT 1
            FROM pragma_table_info('{escapedTable}')
            WHERE lower(name) = lower($columnName)
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$columnName", columnName);
        return command.ExecuteScalar() is not null;
    }
}
