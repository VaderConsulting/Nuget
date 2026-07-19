using Microsoft.Data.Sqlite;
using NuGetAudit.DbUtilities;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NuGetAudit.Core;

/// <summary>
/// Reads structural snapshot topology from the local SQLite operational store.
/// </summary>
public sealed class SnapshotTopologyStore
{
    private const string DatabaseFileName = "knowledge.timeline.db";
    private static readonly HashSet<string> ReportedFailures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock ReportedFailuresLock = new();
    private static readonly JsonSerializerOptions TopologyAuxiliaryJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public SqliteTopologySnapshot? LoadLatest(string outputDirectory)
    {
        string databasePath = Path.Combine(outputDirectory, DatabaseFileName);
        if (!File.Exists(databasePath))
        {
            return null;
        }

        KnowledgeTimelineDbVsHistoryPurge.TryPurge(outputDirectory);

        try
        {
            using SqliteConnection connection = new(CreateReadOnlyConnectionString(databasePath));
            connection.Open();
            if (!HasRequiredSchema(connection))
            {
                ReportHandledFailure(databasePath, "schema-mismatch", "Skipped topology load because the SQLite schema is missing required tables or columns.");
                return null;
            }

            SqliteTopologySnapshot? snapshot = LoadLatestSnapshot(connection);
            if (snapshot is null)
            {
                return null;
            }

            IReadOnlyList<SqliteTopologyProject> projects = LoadProjects(connection, snapshot.SnapshotId);
            IReadOnlyDictionary<string, IReadOnlyList<SqliteTopologyPackage>> packagesByProjectId = LoadPackages(connection, snapshot.SnapshotId);
            IReadOnlyDictionary<string, IReadOnlyList<SqliteTopologyDependencyEdge>> edgesByProjectId =
                HasTable(connection, "DependencyEdge")
                    ? LoadDependencyEdges(connection, snapshot.SnapshotId)
                    : new Dictionary<string, IReadOnlyList<SqliteTopologyDependencyEdge>>(StringComparer.OrdinalIgnoreCase);

            return snapshot with
            {
                Projects = projects.Select(project => project with
                {
                    Packages = packagesByProjectId.TryGetValue(project.ProjectId, out IReadOnlyList<SqliteTopologyPackage>? packages)
                        ? packages
                        : Array.Empty<SqliteTopologyPackage>(),
                    DependencyEdges = edgesByProjectId.TryGetValue(project.ProjectId, out IReadOnlyList<SqliteTopologyDependencyEdge>? edges)
                        ? edges
                        : Array.Empty<SqliteTopologyDependencyEdge>()
                }).ToArray()
            };
        }
        catch (SqliteException exception)
        {
            if (IsStalePackageInstanceSchemaError(exception))
            {
                ReportHandledFailure(databasePath, "schema-mismatch", "Skipped topology load because PackageInstance is missing ProjectId (stale knowledge.timeline.db).");
                return null;
            }

            if (exception.SqliteErrorCode == 1
                && exception.Message.Contains("no such column", StringComparison.OrdinalIgnoreCase))
            {
                ReportHandledFailure(databasePath, "schema-mismatch", $"Skipped topology load: SQLite schema is older than this reader expects ({exception.Message}).");
                return null;
            }

            ReportHandledFailure(databasePath, "sqlite", $"Handled SQLite failure while loading topology: {exception.Message}");
            return null;
        }
        catch (IOException exception)
        {
            ReportHandledFailure(databasePath, "io", $"Handled I/O failure while loading topology: {exception.Message}");
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            ReportHandledFailure(databasePath, "access", $"Handled access failure while loading topology: {exception.Message}");
            return null;
        }
        catch (JsonException exception)
        {
            ReportHandledFailure(databasePath, "json", $"Handled JSON failure while loading topology: {exception.Message}");
            return null;
        }
    }

    private static void ReportHandledFailure(string databasePath, string reason, string message)
    {
        string key = $"{databasePath}|{reason}";
        lock (ReportedFailuresLock)
        {
            if (!ReportedFailures.Add(key))
            {
                return;
            }
        }

        DebugLog.Write("Sqlite", $"{message} Database='{databasePath}'.");
    }

    private static string CreateReadOnlyConnectionString(string databasePath)
        => new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

    private static bool HasRequiredSchema(SqliteConnection connection)
        => HasTable(connection, "SnapshotRun")
           && HasTable(connection, "ProjectSnapshot")
           && HasTable(connection, "PackageInstance")
           && HasTable(connection, "PackageKnowledge")
           && HasColumn(connection, "SnapshotRun", "SnapshotId")
           && HasColumn(connection, "SnapshotRun", "CapturedUtc")
           && HasColumn(connection, "SnapshotRun", "SolutionName")
           && HasColumn(connection, "SnapshotRun", "SolutionPath")
           && HasColumn(connection, "SnapshotRun", "AnalysisStatus")
           && HasColumn(connection, "ProjectSnapshot", "SnapshotId")
           && HasColumn(connection, "ProjectSnapshot", "ProjectId")
           && HasColumn(connection, "ProjectSnapshot", "ProjectName")
           && HasColumn(connection, "ProjectSnapshot", "ProjectPath")
           && HasColumn(connection, "ProjectSnapshot", "AnalysisStatus")
           && HasColumn(connection, "ProjectSnapshot", "TargetFrameworks")
           && HasColumn(connection, "PackageInstance", "SnapshotId")
           && HasColumn(connection, "PackageInstance", "ProjectId")
           && HasColumn(connection, "PackageInstance", "PackageId")
           && HasColumn(connection, "PackageInstance", "RequestedVersion")
           && HasColumn(connection, "PackageInstance", "ResolvedVersion")
           && HasColumn(connection, "PackageInstance", "ReferenceKind")
           && HasColumn(connection, "PackageInstance", "TargetFrameworkMoniker")
           && HasColumn(connection, "PackageInstance", "HealthInfoJson")
           && HasColumn(connection, "PackageKnowledge", "PackageId")
           && HasColumn(connection, "PackageKnowledge", "ResolvedVersion")
           && HasColumn(connection, "PackageKnowledge", "LatestDeterminedUtc")
           && HasColumn(connection, "PackageKnowledge", "LatestStatusChangedUtc")
           && HasColumn(connection, "PackageKnowledge", "RemediationJson")
           && HasColumn(connection, "PackageKnowledge", "CriticalityJson");

    private static bool HasTable(SqliteConnection connection, string tableName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
            FROM main.sqlite_master
            WHERE type = 'table' AND name = $tableName
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$tableName", tableName);
        return command.ExecuteScalar() is not null;
    }

    private static bool HasColumn(SqliteConnection connection, string tableName, string columnName)
    {
        string escapedTable = tableName.Replace("'", "''", StringComparison.Ordinal);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA main.table_info('{escapedTable}');";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string name = reader.GetString(1);
            if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsStalePackageInstanceSchemaError(SqliteException exception)
        => exception.SqliteErrorCode == 1
           && exception.Message.Contains("no such column", StringComparison.OrdinalIgnoreCase)
           && exception.Message.Contains("ProjectId", StringComparison.OrdinalIgnoreCase);

    private static SqliteTopologySnapshot? LoadLatestSnapshot(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                SnapshotId,
                CapturedUtc,
                SolutionName,
                SolutionPath,
                AnalysisStatus
            FROM main.SnapshotRun
            ORDER BY CapturedUtc DESC
            LIMIT 1;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new SqliteTopologySnapshot(reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4), Array.Empty<SqliteTopologyProject>());
    }

    private static IReadOnlyList<SqliteTopologyProject> LoadProjects(SqliteConnection connection, string snapshotId)
    {
        List<SqliteTopologyProject> projects = new();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                ProjectId,
                ProjectName,
                ProjectPath,
                AnalysisStatus,
                TargetFrameworks
            FROM main.ProjectSnapshot
            WHERE SnapshotId = $snapshotId
            ORDER BY ProjectName, ProjectPath;
            """;
        command.Parameters.AddWithValue("$snapshotId", snapshotId);

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            projects.Add(new SqliteTopologyProject(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                Array.Empty<SqliteTopologyPackage>(),
                Array.Empty<SqliteTopologyDependencyEdge>()));
        }

        return projects;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<SqliteTopologyPackage>> LoadPackages(SqliteConnection connection, string snapshotId)
    {
        Dictionary<string, List<SqliteTopologyPackage>> packagesByProjectId = new(StringComparer.OrdinalIgnoreCase);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                package.ProjectId,
                package.PackageId,
                package.RequestedVersion,
                package.ResolvedVersion,
                package.ReferenceKind,
                package.TargetFrameworkMoniker,
                package.HealthInfoJson,
                knowledge.LatestDeterminedUtc,
                knowledge.LatestStatusChangedUtc,
                knowledge.RemediationJson,
                knowledge.CriticalityJson
            FROM main.PackageInstance AS package
            LEFT JOIN main.PackageKnowledge AS knowledge
                ON knowledge.PackageId = package.PackageId
               AND knowledge.ResolvedVersion = COALESCE(package.ResolvedVersion, package.RequestedVersion, '')
            WHERE package.SnapshotId = $snapshotId
            ORDER BY package.ProjectId, package.PackageId, package.ResolvedVersion;
            """;
        command.Parameters.AddWithValue("$snapshotId", snapshotId);

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string projectId = reader.GetString(0);
            if (!packagesByProjectId.TryGetValue(projectId, out List<SqliteTopologyPackage>? packages))
            {
                packages = new List<SqliteTopologyPackage>();
                packagesByProjectId[projectId] = packages;
            }

            PackageHealthInfo health = PackageHealthJsonSerializer.DeserializeHealth(reader.GetString(6));
            PackageRemediationAdvice? remediation = TryDeserializeRemediation(reader, 9);
            SqliteTopologyCriticality? criticality = TryDeserializeTopologyCriticality(reader, 10);
            string? remediationSummary = remediation?.Summary
                ?? remediation?.RecommendedVersion
                ?? (!string.IsNullOrWhiteSpace(remediation?.AlternatePackageId)
                    ? $"{remediation.AlternatePackageId} {remediation.AlternatePackageRange}".Trim()
                    : null);

            packages.Add(new SqliteTopologyPackage(reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), health, reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)), reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)), remediationSummary, criticality?.RiskScore, criticality?.AlertScore, criticality?.RiskBand, criticality?.AlertBand));
        }

        return packagesByProjectId.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<SqliteTopologyPackage>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<SqliteTopologyDependencyEdge>> LoadDependencyEdges(SqliteConnection connection, string snapshotId)
    {
        Dictionary<string, List<SqliteTopologyDependencyEdge>> edgesByProjectId = new(StringComparer.OrdinalIgnoreCase);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                ProjectId,
                FromStablePackageInstanceKey,
                ToStablePackageInstanceKey,
                FromPackageId,
                ToPackageId,
                TargetFrameworkMoniker
            FROM main.DependencyEdge
            WHERE SnapshotId = $snapshotId
            ORDER BY ProjectId, FromPackageId, ToPackageId, TargetFrameworkMoniker;
            """;
        command.Parameters.AddWithValue("$snapshotId", snapshotId);

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string projectId = reader.GetString(0);
            if (!edgesByProjectId.TryGetValue(projectId, out List<SqliteTopologyDependencyEdge>? list))
            {
                list = new List<SqliteTopologyDependencyEdge>();
                edgesByProjectId[projectId] = list;
            }

            list.Add(new SqliteTopologyDependencyEdge(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return edgesByProjectId.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<SqliteTopologyDependencyEdge>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private static PackageRemediationAdvice? TryDeserializeRemediation(SqliteDataReader Reader, int Ordinal)
    {
        if (Reader.IsDBNull(Ordinal))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PackageRemediationAdvice>(Reader.GetString(Ordinal), TopologyAuxiliaryJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static SqliteTopologyCriticality? TryDeserializeTopologyCriticality(SqliteDataReader Reader, int Ordinal)
    {
        if (Reader.IsDBNull(Ordinal))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SqliteTopologyCriticality>(Reader.GetString(Ordinal), TopologyAuxiliaryJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record SqliteTopologySnapshot(string SnapshotId, DateTimeOffset CapturedUtc, string SolutionName, string SolutionPath, string AnalysisStatus, IReadOnlyList<SqliteTopologyProject> Projects);

public sealed record SqliteTopologyProject(
    string ProjectId,
    string ProjectName,
    string ProjectPath,
    string AnalysisStatus,
    string? TargetFrameworks,
    IReadOnlyList<SqliteTopologyPackage> Packages,
    IReadOnlyList<SqliteTopologyDependencyEdge> DependencyEdges);

/// <summary>
/// One directed dependency edge as persisted for a project snapshot (parent package depends on child package).
/// </summary>
public sealed record SqliteTopologyDependencyEdge(
    string FromStablePackageInstanceKey,
    string ToStablePackageInstanceKey,
    string FromPackageId,
    string ToPackageId,
    string? TargetFrameworkMoniker);

public sealed record SqliteTopologyPackage(string PackageId, string? RequestedVersion, string? ResolvedVersion, string ReferenceKind, string? TargetFrameworkMoniker, PackageHealthInfo HealthInfo, DateTimeOffset? KnowledgeDeterminedUtc, DateTimeOffset? KnowledgeStatusChangedUtc, string? RemediationSummary, double? RiskScore, double? AlertScore, string? RiskBand, string? AlertBand);

public sealed record SqliteTopologyCriticality(double RiskScore, double AlertScore, string? RiskBand, string? AlertBand);
