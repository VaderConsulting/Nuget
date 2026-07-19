using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NuGetAudit.DbUtilities;

namespace NuGetAudit.Presentation;

public static class IssueRollupReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly HashSet<string> ReportedFailures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object ReportedFailuresLock = new();

    public static IReadOnlyList<IssueRollupItem> Load(string outputDirectory)
    {
        string fullOutputDirectory = Path.GetFullPath(outputDirectory);
        string sourceDisplayName = Path.GetFileName(Path.GetDirectoryName(fullOutputDirectory) ?? fullOutputDirectory);
        string sourceIdentityKey = sourceDisplayName;
        return LoadMany([new IssueSourceDescriptor(sourceIdentityKey, sourceDisplayName, fullOutputDirectory)]);
    }

    public static IReadOnlyList<IssueRollupItem> LoadMany(IEnumerable<IssueSourceDescriptor> sources)
    {
        IssueSourceDescriptor[] sourceRows = sources is IssueSourceDescriptor[] existingRows
            ? existingRows
            : sources.ToArray();

        List<(IssueSourceDescriptor Source, PackageKnowledgeRecordFile Package)> packages = new();

        foreach (IssueSourceDescriptor source in sourceRows)
        {
            if (string.IsNullOrWhiteSpace(source.OutputDirectory) || !Directory.Exists(source.OutputDirectory))
            {
                continue;
            }

            string fullOutputDirectory = Path.GetFullPath(source.OutputDirectory);
            string databasePath = Path.Combine(fullOutputDirectory, "knowledge.timeline.db");
            if (File.Exists(databasePath))
            {
                KnowledgeTimelineDbVsHistoryPurge.TryPurge(fullOutputDirectory);
                try
                {
                    using SqliteConnection connection = OpenReadOnlyConnection(databasePath);
                    if (!HasRequiredSchema(connection))
                    {
                        ReportHandledFailure(databasePath, "schema-mismatch", "Skipped issue rollup load because the SQLite schema is missing required tables or columns.");
                        continue;
                    }

                    packages.AddRange(LoadPackagesFromSqlite(connection, source));
                }
                catch (SqliteException exception)
                {
                    ReportHandledFailure(databasePath, "sqlite", $"Handled SQLite failure while loading issue rollups: {exception.Message}");
                }
                catch (IOException exception)
                {
                    ReportHandledFailure(databasePath, "io", $"Handled I/O failure while loading issue rollups: {exception.Message}");
                }
                catch (UnauthorizedAccessException exception)
                {
                    ReportHandledFailure(databasePath, "access", $"Handled access failure while loading issue rollups: {exception.Message}");
                }
                catch (JsonException exception)
                {
                    ReportHandledFailure(databasePath, "json", $"Handled JSON failure while loading issue rollups: {exception.Message}");
                }
            }
        }

        return packages
            .GroupBy(item => new
            {
                item.Source.SourceIdentityKey,
                item.Package.PackageId,
                item.Package.ResolvedVersion
            })
            .Select(group =>
            {
                (IssueSourceDescriptor Source, PackageKnowledgeRecordFile Package) newest = group
                    .OrderByDescending(static item => item.Package.LatestDeterminedUtc)
                    .ThenByDescending(static item => item.Package.Criticality?.AlertScore ?? double.MinValue)
                    .First();

                string currentHealth = DescribeHealth(newest.Package.LatestKnownHealth);
                return new IssueRollupItem(
                    newest.Source.SourceIdentityKey,
                    newest.Source.SourceDisplayName,
                    newest.Package.PackageId ?? "(unknown package)",
                    newest.Package.ResolvedVersion ?? "-",
                    currentHealth,
                    DescribeRemediation(newest.Package.Remediation),
                    newest.Package.Criticality?.RiskScore,
                    newest.Package.Criticality?.AlertScore,
                    newest.Package.Criticality?.RiskBand,
                    newest.Package.Criticality?.AlertBand,
                    group
                        .Select(static item => item.Source.OutputDirectory)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count(),
                    group.Count(),
                    newest.Package.LatestDeterminedUtc,
                    ComputeIssueRowVisualBand(newest.Package.Criticality?.RiskBand, newest.Package.Criticality?.AlertBand, currentHealth));
            })
            .OrderByDescending(static item => item.AlertScore ?? double.MinValue)
            .ThenByDescending(static item => item.RiskScore ?? double.MinValue)
            .ThenBy(static item => item.SourceDisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<(IssueSourceDescriptor Source, PackageKnowledgeRecordFile Package)> LoadPackagesFromSqlite(SqliteConnection connection, IssueSourceDescriptor source)
    {
        List<(IssueSourceDescriptor Source, PackageKnowledgeRecordFile Package)> packages = new();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                PackageId,
                ResolvedVersion,
                LatestDeterminedUtc,
                LatestKnownHealthJson,
                RemediationJson,
                CriticalityJson
            FROM PackageKnowledge
            ORDER BY PackageId, ResolvedVersion;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            PackageHealthFile latestKnownHealth = JsonSerializer.Deserialize<PackageHealthFile>(reader.GetString(3), JsonOptions) ?? new PackageHealthFile();
            if (!latestKnownHealth.IsVulnerable
                && !latestKnownHealth.IsDeprecated
                && !latestKnownHealth.IsObsolete
                && !latestKnownHealth.IsOutdated
                && !HealthIndicatesLifecycleAttention(latestKnownHealth.DevelopmentStatus))
            {
                continue;
            }

            packages.Add((source, new PackageKnowledgeRecordFile
            {
                PackageId = reader.GetString(0),
                ResolvedVersion = reader.GetString(1),
                LatestDeterminedUtc = DateTimeOffset.Parse(reader.GetString(2)),
                LatestKnownHealth = latestKnownHealth,
                Remediation = reader.IsDBNull(4) ? null : JsonSerializer.Deserialize<PackageRemediationFile>(reader.GetString(4), JsonOptions),
                Criticality = reader.IsDBNull(5) ? null : JsonSerializer.Deserialize<CriticalityFile>(reader.GetString(5), JsonOptions)
            }));
        }

        return packages;
    }

    /// <summary>
    /// Aligns with Issues grid row styling (criticality bands plus health summary text from <c>DescribeHealth</c>).
    /// </summary>
    private static string ComputeIssueRowVisualBand(string? riskBand, string? alertBand, string currentHealth)
    {
        return PackageHealthRowVisualBand.Compute(riskBand, alertBand, currentHealth);
    }

    private static string DescribeHealth(PackageHealthFile health)
    {
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

    private static bool HealthIndicatesLifecycleAttention(string developmentStatus)
    {
        return string.Equals(developmentStatus, "Abandoned", StringComparison.OrdinalIgnoreCase)
            || string.Equals(developmentStatus, "Removed", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeRemediation(PackageRemediationFile? remediation)
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

    private sealed class PackageKnowledgeRecordFile
    {
        public string? PackageId { get; set; }
        public string? ResolvedVersion { get; set; }
        public DateTimeOffset LatestDeterminedUtc { get; set; }
        public PackageHealthFile LatestKnownHealth { get; set; } = new();
        public PackageRemediationFile? Remediation { get; set; }
        public CriticalityFile? Criticality { get; set; }
    }

    private sealed class PackageHealthFile
    {
        public bool IsDeprecated { get; set; }
        public bool IsObsolete { get; set; }
        public bool IsOutdated { get; set; }
        public bool IsVulnerable { get; set; }
        public string DevelopmentStatus { get; set; } = "Unknown";
        public string? LatestStableVersion { get; set; }
        public string? MaxVulnerabilitySeverity { get; set; }
    }

    private sealed class PackageRemediationFile
    {
        public string? Summary { get; set; }
        public string? RecommendedVersion { get; set; }
        public string? AlternatePackageId { get; set; }
        public string? AlternatePackageRange { get; set; }
    }

    private sealed class CriticalityFile
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

        Debug.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} [BG:T{Environment.CurrentManagedThreadId}] Sqlite IssueRollupReader - {message} Database='{databasePath}'.");
    }

    private static bool HasRequiredSchema(SqliteConnection connection)
        => HasTable(connection, "PackageKnowledge")
           && HasColumn(connection, "PackageKnowledge", "LatestKnownHealthJson")
           && HasColumn(connection, "PackageKnowledge", "RemediationJson")
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
