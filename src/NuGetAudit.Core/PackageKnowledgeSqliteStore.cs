using Microsoft.Data.Sqlite;
using NuGetAudit.DbUtilities;
using NuGetAudit.Intelligence;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NuGetAudit.Core;

/// <summary>
/// Persists evolving package knowledge and snapshot-run metadata in SQLite.
/// </summary>
internal sealed class PackageKnowledgeSqliteStore
{
    private const string DatabaseFileName = "knowledge.timeline.db";
    private static readonly HashSet<string> ReportedFailures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock ReportedFailuresLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<PackageKnowledgeSnapshot?> LoadLatestAsync(string outputDirectory, CancellationToken cancellationToken)
    {
        string databasePath = GetDatabasePath(outputDirectory);
        if (!File.Exists(databasePath))
        {
            return null;
        }

        KnowledgeTimelineDbVsHistoryPurge.TryPurge(outputDirectory);

        try
        {
            await using SqliteConnection connection = new(CreateReadOnlyConnectionString(databasePath));
            await connection.OpenAsync(cancellationToken);
            if (!await HasRequiredSchemaAsync(connection, cancellationToken))
            {
                ReportHandledFailure(databasePath, "schema-mismatch", "Skipped knowledge load because the SQLite schema is missing required tables or columns.");
                return null;
            }

            List<PackageKnowledgeRecord> packages = new();

            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT
                        PackageId,
                        ResolvedVersion,
                        FirstObservedUtc,
                        LastObservedUtc,
                        FirstRequiresAttentionUtc,
                        LatestStatusChangedUtc,
                        LatestDeterminedUtc,
                        LatestKnownHealthJson,
                        RemediationJson,
                        CriticalityJson
                    FROM PackageKnowledge
                    ORDER BY PackageId, ResolvedVersion;
                    """;

                await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    PackageHealthInfo health = DeserializeHealth(reader.GetString(7));
                    PackageRemediationAdvice remediation = DeserializeRemediation(reader.GetString(8));
                    CriticalityAssessment criticality = DeserializeCriticality(reader.GetString(9));

                    packages.Add(new PackageKnowledgeRecord(reader.GetString(0), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2)), DateTimeOffset.Parse(reader.GetString(3)), reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4)), reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)), DateTimeOffset.Parse(reader.GetString(6)), health, remediation, criticality));
                }
            }

            if (packages.Count == 0)
            {
                return null;
            }

            DateTimeOffset generatedUtc = packages.Max(static package => package.LatestDeterminedUtc);
            return new PackageKnowledgeSnapshot(generatedUtc, packages);
        }
        catch (SqliteException exception)
        {
            ReportHandledFailure(databasePath, "sqlite-load", $"Handled SQLite failure while loading knowledge: {exception.Message}");
            return null;
        }
        catch (IOException exception)
        {
            ReportHandledFailure(databasePath, "io-load", $"Handled I/O failure while loading knowledge: {exception.Message}");
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            ReportHandledFailure(databasePath, "access-load", $"Handled access failure while loading knowledge: {exception.Message}");
            return null;
        }
        catch (JsonException exception)
        {
            ReportHandledFailure(databasePath, "json-load", $"Handled JSON failure while loading knowledge: {exception.Message}");
            return null;
        }
    }

    public async Task<bool> PersistAsync(string outputDirectory, SolutionSnapshot snapshot, PackageKnowledgeSnapshot knowledgeSnapshot, IReadOnlyList<PackageKnowledgeChangeRecord> knowledgeChanges, string snapshotJsonPath, string? markdownPath, string? deltaJsonPath, string knowledgeJsonPath, CancellationToken cancellationToken)
        => await PersistAsync(
            outputDirectory,
            snapshot,
            knowledgeSnapshot,
            knowledgeChanges,
            snapshotJsonPath,
            markdownPath,
            deltaJsonPath,
            knowledgeJsonPath,
            allowReset: true,
            cancellationToken);

    private async Task<bool> PersistAsync(string outputDirectory, SolutionSnapshot snapshot, PackageKnowledgeSnapshot knowledgeSnapshot, IReadOnlyList<PackageKnowledgeChangeRecord> knowledgeChanges, string snapshotJsonPath, string? markdownPath, string? deltaJsonPath, string knowledgeJsonPath, bool allowReset, CancellationToken cancellationToken)
    {
        string databasePath = GetDatabasePath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        try
        {
            await using SqliteConnection connection = new(CreateConnectionString(databasePath));
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);
            await using SqliteTransaction transaction = connection.BeginTransaction();

            await UpsertSnapshotRunAsync(connection, transaction, snapshot, snapshotJsonPath, markdownPath, deltaJsonPath, knowledgeJsonPath, cancellationToken);
            await ReplaceSnapshotTopologyAsync(connection, transaction, snapshot, cancellationToken);

            foreach (PackageKnowledgeRecord record in knowledgeSnapshot.Packages)
            {
                await UpsertKnowledgeAsync(connection, transaction, record, cancellationToken);
            }

            foreach (PackageKnowledgeChangeRecord change in knowledgeChanges)
            {
                await InsertKnowledgeChangeAsync(connection, transaction, snapshot.SnapshotId, change, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            int orphanRowsRemoved = await CompactOrphanTimelineRowsAsync(connection, cancellationToken);
            if (orphanRowsRemoved > 0)
            {
                DebugLog.Write("Sqlite", $"Removed {orphanRowsRemoved} orphan timeline row(s) after persist. Database='{databasePath}'.");
            }

            return true;
        }
        catch (SqliteException ex) when (allowReset && ShouldResetStaleDatabase(ex))
        {
            ReportHandledFailure(databasePath, "stale-schema", $"Resetting stale SQLite artifacts after schema failure: {ex.Message}");
            if (!TryResetDatabaseArtifacts(databasePath))
            {
                return false;
            }

            return await PersistAsync(
                outputDirectory,
                snapshot,
                knowledgeSnapshot,
                knowledgeChanges,
                snapshotJsonPath,
                markdownPath,
                deltaJsonPath,
                knowledgeJsonPath,
                allowReset: false,
                cancellationToken);
        }
        catch (SqliteException exception) when (IsHandledPersistenceException(exception))
        {
            ReportHandledFailure(databasePath, $"sqlite-persist-{exception.SqliteErrorCode}", $"Handled SQLite persistence failure: {exception.Message}");
            return false;
        }
        catch (IOException exception)
        {
            ReportHandledFailure(databasePath, "io-persist", $"Handled filesystem failure while persisting SQLite artifacts: {exception.Message}");
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            ReportHandledFailure(databasePath, "access-persist", $"Handled access failure while persisting SQLite artifacts: {exception.Message}");
            return false;
        }
    }

    public string GetDatabasePath(string outputDirectory) => Path.Combine(outputDirectory, DatabaseFileName);

    /// <summary>
    /// Deletes topology and knowledge-change rows whose <c>SnapshotId</c> is missing from <c>SnapshotRun</c> (repair for partial writes or manual edits).
    /// </summary>
    /// <returns>Total rows deleted across all affected tables.</returns>
    internal static async Task<int> TryRemoveOrphanTimelineRowsForFileAsync(string databasePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
        {
            return 0;
        }

        try
        {
            await using SqliteConnection connection = new(CreateConnectionString(databasePath));
            await connection.OpenAsync(cancellationToken);
            if (!await HasOrphanCleanupSchemaAsync(connection, cancellationToken))
            {
                return 0;
            }

            return await CompactOrphanTimelineRowsAsync(connection, cancellationToken);
        }
        catch (SqliteException exception)
        {
            ReportHandledFailure(databasePath, "sqlite-orphan-repair", $"Handled SQLite failure while removing orphan timeline rows: {exception.Message}");
            return 0;
        }
        catch (IOException exception)
        {
            ReportHandledFailure(databasePath, "io-orphan-repair", $"Handled I/O failure while removing orphan timeline rows: {exception.Message}");
            return 0;
        }
        catch (UnauthorizedAccessException exception)
        {
            ReportHandledFailure(databasePath, "access-orphan-repair", $"Handled access failure while removing orphan timeline rows: {exception.Message}");
            return 0;
        }
    }

    private static async Task<int> CompactOrphanTimelineRowsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (!await HasOrphanCleanupSchemaAsync(connection, cancellationToken))
        {
            return 0;
        }

        await using SqliteTransaction cleanupTransaction = connection.BeginTransaction();
        int totalRemoved = 0;
        foreach (string commandText in OrphanTimelineDeleteStatements)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = cleanupTransaction;
            command.CommandText = commandText;
            int affected = await command.ExecuteNonQueryAsync(cancellationToken);
            totalRemoved += affected;
        }

        await cleanupTransaction.CommitAsync(cancellationToken);
        return totalRemoved;
    }

    private static readonly string[] OrphanTimelineDeleteStatements =
    [
        "DELETE FROM PackageKnowledgeChange WHERE SnapshotId NOT IN (SELECT SnapshotId FROM SnapshotRun);",
        "DELETE FROM DependencyEdge WHERE SnapshotId NOT IN (SELECT SnapshotId FROM SnapshotRun);",
        "DELETE FROM PackageInstance WHERE SnapshotId NOT IN (SELECT SnapshotId FROM SnapshotRun);",
        "DELETE FROM ProjectSnapshot WHERE SnapshotId NOT IN (SELECT SnapshotId FROM SnapshotRun);"
    ];

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS SnapshotRun (
                SnapshotId TEXT PRIMARY KEY,
                CapturedUtc TEXT NOT NULL,
                SolutionName TEXT NOT NULL,
                SolutionPath TEXT NOT NULL,
                SolutionKey TEXT NOT NULL,
                AnalysisStatus TEXT NOT NULL,
                NormalisedContentHash TEXT NOT NULL,
                SourceIdentityKey TEXT NOT NULL,
                SourceIdentityKind TEXT NOT NULL,
                MachineName TEXT NOT NULL,
                RepositoryRootPath TEXT NOT NULL,
                GitRemoteUrl TEXT NULL,
                GitRemoteKey TEXT NULL,
                SnapshotJsonPath TEXT NULL,
                MarkdownPath TEXT NULL,
                DeltaJsonPath TEXT NULL,
                KnowledgeJsonPath TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS ProjectSnapshot (
                SnapshotId TEXT NOT NULL,
                ProjectId TEXT NOT NULL,
                ProjectName TEXT NOT NULL,
                ProjectPath TEXT NOT NULL,
                ProjectStyle TEXT NOT NULL,
                LoadState TEXT NOT NULL,
                AnalysisStatus TEXT NOT NULL,
                TargetFrameworks TEXT NULL,
                AssetsFilePath TEXT NULL,
                PackagesLockFilePath TEXT NULL,
                PRIMARY KEY (SnapshotId, ProjectId)
            );

            CREATE TABLE IF NOT EXISTS PackageInstance (
                SnapshotId TEXT NOT NULL,
                ProjectId TEXT NOT NULL,
                StablePackageInstanceKey TEXT NOT NULL,
                PackageId TEXT NOT NULL,
                RequestedVersion TEXT NULL,
                ResolvedVersion TEXT NULL,
                ReferenceKind TEXT NOT NULL,
                IsCentralVersionManaged INTEGER NOT NULL,
                TargetFrameworkMoniker TEXT NULL,
                DependencyParentsJson TEXT NOT NULL,
                DependencyPathJson TEXT NOT NULL,
                HealthInfoJson TEXT NOT NULL,
                PRIMARY KEY (SnapshotId, ProjectId, StablePackageInstanceKey)
            );

            CREATE TABLE IF NOT EXISTS DependencyEdge (
                SnapshotId TEXT NOT NULL,
                ProjectId TEXT NOT NULL,
                FromStablePackageInstanceKey TEXT NOT NULL,
                ToStablePackageInstanceKey TEXT NOT NULL,
                FromPackageId TEXT NOT NULL,
                ToPackageId TEXT NOT NULL,
                TargetFrameworkMoniker TEXT NULL,
                PRIMARY KEY (SnapshotId, ProjectId, FromStablePackageInstanceKey, ToStablePackageInstanceKey)
            );

            CREATE TABLE IF NOT EXISTS PackageKnowledge (
                PackageId TEXT NOT NULL,
                ResolvedVersion TEXT NOT NULL,
                FirstObservedUtc TEXT NOT NULL,
                LastObservedUtc TEXT NOT NULL,
                FirstRequiresAttentionUtc TEXT NULL,
                LatestStatusChangedUtc TEXT NULL,
                LatestDeterminedUtc TEXT NOT NULL,
                LatestKnownHealthJson TEXT NOT NULL,
                RemediationJson TEXT NOT NULL,
                CriticalityJson TEXT NULL,
                PRIMARY KEY (PackageId, ResolvedVersion)
            );

            CREATE TABLE IF NOT EXISTS PackageKnowledgeChange (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SnapshotId TEXT NOT NULL,
                PackageId TEXT NOT NULL,
                ResolvedVersion TEXT NOT NULL,
                DeterminedUtc TEXT NOT NULL,
                PreviousHealthJson TEXT NULL,
                CurrentHealthJson TEXT NOT NULL,
                RemediationJson TEXT NOT NULL,
                Summary TEXT NOT NULL
            );
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureProjectSnapshotOptionalColumnsAsync(connection, cancellationToken);
    }

    private static async Task EnsureProjectSnapshotOptionalColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (!await HasColumnAsync(connection, "ProjectSnapshot", "PackagesLockFilePath", cancellationToken))
        {
            await using SqliteCommand alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE ProjectSnapshot ADD COLUMN PackagesLockFilePath TEXT NULL;";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task UpsertSnapshotRunAsync(SqliteConnection connection, SqliteTransaction transaction, SolutionSnapshot snapshot, string snapshotJsonPath, string? markdownPath, string? deltaJsonPath, string knowledgeJsonPath, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO SnapshotRun (
                SnapshotId,
                CapturedUtc,
                SolutionName,
                SolutionPath,
                SolutionKey,
                AnalysisStatus,
                NormalisedContentHash,
                SourceIdentityKey,
                SourceIdentityKind,
                MachineName,
                RepositoryRootPath,
                GitRemoteUrl,
                GitRemoteKey,
                SnapshotJsonPath,
                MarkdownPath,
                DeltaJsonPath,
                KnowledgeJsonPath
            )
            VALUES (
                $snapshotId,
                $capturedUtc,
                $solutionName,
                $solutionPath,
                $solutionKey,
                $analysisStatus,
                $normalisedContentHash,
                $sourceIdentityKey,
                $sourceIdentityKind,
                $machineName,
                $repositoryRootPath,
                $gitRemoteUrl,
                $gitRemoteKey,
                $snapshotJsonPath,
                $markdownPath,
                $deltaJsonPath,
                $knowledgeJsonPath
            )
            ON CONFLICT(SnapshotId) DO UPDATE SET
                CapturedUtc = excluded.CapturedUtc,
                SolutionName = excluded.SolutionName,
                SolutionPath = excluded.SolutionPath,
                SolutionKey = excluded.SolutionKey,
                AnalysisStatus = excluded.AnalysisStatus,
                NormalisedContentHash = excluded.NormalisedContentHash,
                SourceIdentityKey = excluded.SourceIdentityKey,
                SourceIdentityKind = excluded.SourceIdentityKind,
                MachineName = excluded.MachineName,
                RepositoryRootPath = excluded.RepositoryRootPath,
                GitRemoteUrl = excluded.GitRemoteUrl,
                GitRemoteKey = excluded.GitRemoteKey,
                SnapshotJsonPath = excluded.SnapshotJsonPath,
                MarkdownPath = excluded.MarkdownPath,
                DeltaJsonPath = excluded.DeltaJsonPath,
                KnowledgeJsonPath = excluded.KnowledgeJsonPath;
            """;

        command.Parameters.AddWithValue("$snapshotId", snapshot.SnapshotId);
        command.Parameters.AddWithValue("$capturedUtc", snapshot.CapturedUtc.ToString("O"));
        command.Parameters.AddWithValue("$solutionName", snapshot.Solution.SolutionName);
        command.Parameters.AddWithValue("$solutionPath", snapshot.Solution.SolutionPath);
        command.Parameters.AddWithValue("$solutionKey", snapshot.Solution.SolutionKey);
        command.Parameters.AddWithValue("$analysisStatus", snapshot.AnalysisStatus.ToString());
        command.Parameters.AddWithValue("$normalisedContentHash", snapshot.NormalisedContentHash);
        command.Parameters.AddWithValue("$sourceIdentityKey", snapshot.Solution.SourceIdentity.IdentityKey);
        command.Parameters.AddWithValue("$sourceIdentityKind", snapshot.Solution.SourceIdentity.IdentityKind);
        command.Parameters.AddWithValue("$machineName", snapshot.Solution.SourceIdentity.MachineName);
        command.Parameters.AddWithValue("$repositoryRootPath", snapshot.Solution.SourceIdentity.RepositoryRootPath);
        command.Parameters.AddWithValue("$gitRemoteUrl", (object?)snapshot.Solution.SourceIdentity.GitRemoteUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$gitRemoteKey", (object?)snapshot.Solution.SourceIdentity.GitRemoteKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$snapshotJsonPath", snapshotJsonPath);
        command.Parameters.AddWithValue("$markdownPath", (object?)markdownPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$deltaJsonPath", (object?)deltaJsonPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$knowledgeJsonPath", knowledgeJsonPath);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ReplaceSnapshotTopologyAsync(SqliteConnection connection, SqliteTransaction transaction, SolutionSnapshot snapshot, CancellationToken cancellationToken)
    {
        await DeleteSnapshotTopologyAsync(connection, transaction, snapshot.SnapshotId, cancellationToken);

        foreach (ProjectSnapshot project in snapshot.Projects)
        {
            await InsertProjectSnapshotAsync(connection, transaction, snapshot.SnapshotId, project, cancellationToken);

            foreach (PackageReferenceRecord package in project.Packages)
            {
                await InsertPackageInstanceAsync(connection, transaction, snapshot.SnapshotId, project.ProjectId, package, cancellationToken);
            }

            foreach (DependencyEdgeRecord edge in project.DependencyEdges)
            {
                await InsertDependencyEdgeAsync(connection, transaction, snapshot.SnapshotId, project.ProjectId, edge, cancellationToken);
            }
        }
    }

    private static async Task DeleteSnapshotTopologyAsync(SqliteConnection connection, SqliteTransaction transaction, string snapshotId, CancellationToken cancellationToken)
    {
        foreach (string tableName in new[] { "DependencyEdge", "PackageInstance", "ProjectSnapshot" })
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {tableName} WHERE SnapshotId = $snapshotId;";
            command.Parameters.AddWithValue("$snapshotId", snapshotId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertProjectSnapshotAsync(SqliteConnection connection, SqliteTransaction transaction, string snapshotId, ProjectSnapshot project, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO ProjectSnapshot (
                SnapshotId,
                ProjectId,
                ProjectName,
                ProjectPath,
                ProjectStyle,
                LoadState,
                AnalysisStatus,
                TargetFrameworks,
                AssetsFilePath,
                PackagesLockFilePath
            )
            VALUES (
                $snapshotId,
                $projectId,
                $projectName,
                $projectPath,
                $projectStyle,
                $loadState,
                $analysisStatus,
                $targetFrameworks,
                $assetsFilePath,
                $packagesLockFilePath
            );
            """;

        command.Parameters.AddWithValue("$snapshotId", snapshotId);
        command.Parameters.AddWithValue("$projectId", project.ProjectId);
        command.Parameters.AddWithValue("$projectName", project.ProjectName);
        command.Parameters.AddWithValue("$projectPath", project.ProjectPath);
        command.Parameters.AddWithValue("$projectStyle", project.ProjectStyle.ToString());
        command.Parameters.AddWithValue("$loadState", project.LoadState.ToString());
        command.Parameters.AddWithValue("$analysisStatus", project.AnalysisStatus.ToString());
        command.Parameters.AddWithValue("$targetFrameworks", (object?)project.TargetFrameworks ?? DBNull.Value);
        command.Parameters.AddWithValue("$assetsFilePath", (object?)project.AssetsFilePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$packagesLockFilePath", (object?)project.PackagesLockFilePath ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertPackageInstanceAsync(SqliteConnection connection, SqliteTransaction transaction, string snapshotId, string projectId, PackageReferenceRecord package, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO PackageInstance (
                SnapshotId,
                ProjectId,
                StablePackageInstanceKey,
                PackageId,
                RequestedVersion,
                ResolvedVersion,
                ReferenceKind,
                IsCentralVersionManaged,
                TargetFrameworkMoniker,
                DependencyParentsJson,
                DependencyPathJson,
                HealthInfoJson
            )
            VALUES (
                $snapshotId,
                $projectId,
                $stablePackageInstanceKey,
                $packageId,
                $requestedVersion,
                $resolvedVersion,
                $referenceKind,
                $isCentralVersionManaged,
                $targetFrameworkMoniker,
                $dependencyParentsJson,
                $dependencyPathJson,
                $healthInfoJson
            );
            """;

        command.Parameters.AddWithValue("$snapshotId", snapshotId);
        command.Parameters.AddWithValue("$projectId", projectId);
        command.Parameters.AddWithValue("$stablePackageInstanceKey", package.StablePackageInstanceKey);
        command.Parameters.AddWithValue("$packageId", package.PackageId);
        command.Parameters.AddWithValue("$requestedVersion", (object?)package.RequestedVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$resolvedVersion", (object?)package.ResolvedVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$referenceKind", package.ReferenceKind.ToString());
        command.Parameters.AddWithValue("$isCentralVersionManaged", package.IsCentralVersionManaged ? 1 : 0);
        command.Parameters.AddWithValue("$targetFrameworkMoniker", (object?)package.TargetFrameworkMoniker ?? DBNull.Value);
        command.Parameters.AddWithValue("$dependencyParentsJson", System.Text.Json.JsonSerializer.Serialize(package.DependencyParents));
        command.Parameters.AddWithValue("$dependencyPathJson", System.Text.Json.JsonSerializer.Serialize(package.DependencyPath));
        command.Parameters.AddWithValue("$healthInfoJson", SerializeHealth(package.HealthInfo));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertDependencyEdgeAsync(SqliteConnection connection, SqliteTransaction transaction, string snapshotId, string projectId, DependencyEdgeRecord edge, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO DependencyEdge (
                SnapshotId,
                ProjectId,
                FromStablePackageInstanceKey,
                ToStablePackageInstanceKey,
                FromPackageId,
                ToPackageId,
                TargetFrameworkMoniker
            )
            VALUES (
                $snapshotId,
                $projectId,
                $fromStablePackageInstanceKey,
                $toStablePackageInstanceKey,
                $fromPackageId,
                $toPackageId,
                $targetFrameworkMoniker
            );
            """;

        command.Parameters.AddWithValue("$snapshotId", snapshotId);
        command.Parameters.AddWithValue("$projectId", projectId);
        command.Parameters.AddWithValue("$fromStablePackageInstanceKey", edge.FromStablePackageInstanceKey);
        command.Parameters.AddWithValue("$toStablePackageInstanceKey", edge.ToStablePackageInstanceKey);
        command.Parameters.AddWithValue("$fromPackageId", edge.FromPackageId);
        command.Parameters.AddWithValue("$toPackageId", edge.ToPackageId);
        command.Parameters.AddWithValue("$targetFrameworkMoniker", (object?)edge.TargetFrameworkMoniker ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertKnowledgeAsync(SqliteConnection connection, SqliteTransaction transaction, PackageKnowledgeRecord record, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO PackageKnowledge (
                PackageId,
                ResolvedVersion,
                FirstObservedUtc,
                LastObservedUtc,
                FirstRequiresAttentionUtc,
                LatestStatusChangedUtc,
                LatestDeterminedUtc,
                LatestKnownHealthJson,
                RemediationJson,
                CriticalityJson
            )
            VALUES (
                $packageId,
                $resolvedVersion,
                $firstObservedUtc,
                $lastObservedUtc,
                $firstRequiresAttentionUtc,
                $latestStatusChangedUtc,
                $latestDeterminedUtc,
                $latestKnownHealthJson,
                $remediationJson,
                $criticalityJson
            )
            ON CONFLICT(PackageId, ResolvedVersion) DO UPDATE SET
                FirstObservedUtc = excluded.FirstObservedUtc,
                LastObservedUtc = excluded.LastObservedUtc,
                FirstRequiresAttentionUtc = excluded.FirstRequiresAttentionUtc,
                LatestStatusChangedUtc = excluded.LatestStatusChangedUtc,
                LatestDeterminedUtc = excluded.LatestDeterminedUtc,
                LatestKnownHealthJson = excluded.LatestKnownHealthJson,
                RemediationJson = excluded.RemediationJson,
                CriticalityJson = excluded.CriticalityJson;
            """;

        command.Parameters.AddWithValue("$packageId", record.PackageId);
        command.Parameters.AddWithValue("$resolvedVersion", record.ResolvedVersion);
        command.Parameters.AddWithValue("$firstObservedUtc", record.FirstObservedUtc.ToString("O"));
        command.Parameters.AddWithValue("$lastObservedUtc", record.LastObservedUtc.ToString("O"));
        command.Parameters.AddWithValue("$firstRequiresAttentionUtc", (object?)record.FirstRequiresAttentionUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$latestStatusChangedUtc", (object?)record.LatestStatusChangedUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$latestDeterminedUtc", record.LatestDeterminedUtc.ToString("O"));
        command.Parameters.AddWithValue("$latestKnownHealthJson", SerializeHealth(record.LatestKnownHealth));
        command.Parameters.AddWithValue("$remediationJson", SerializeRemediation(record.Remediation));
        command.Parameters.AddWithValue("$criticalityJson", SerializeCriticality(record.Criticality));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertKnowledgeChangeAsync(SqliteConnection connection, SqliteTransaction transaction, string snapshotId, PackageKnowledgeChangeRecord change, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO PackageKnowledgeChange (
                SnapshotId,
                PackageId,
                ResolvedVersion,
                DeterminedUtc,
                PreviousHealthJson,
                CurrentHealthJson,
                RemediationJson,
                Summary
            )
            VALUES (
                $snapshotId,
                $packageId,
                $resolvedVersion,
                $determinedUtc,
                $previousHealthJson,
                $currentHealthJson,
                $remediationJson,
                $summary
            );
            """;

        command.Parameters.AddWithValue("$snapshotId", snapshotId);
        command.Parameters.AddWithValue("$packageId", change.PackageId);
        command.Parameters.AddWithValue("$resolvedVersion", change.ResolvedVersion);
        command.Parameters.AddWithValue("$determinedUtc", change.DeterminedUtc.ToString("O"));
        command.Parameters.AddWithValue("$previousHealthJson", change.PreviousHealthInfo is null ? DBNull.Value : SerializeHealth(change.PreviousHealthInfo));
        command.Parameters.AddWithValue("$currentHealthJson", SerializeHealth(change.CurrentHealthInfo));
        command.Parameters.AddWithValue("$remediationJson", SerializeRemediation(change.Remediation));
        command.Parameters.AddWithValue("$summary", change.Summary);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string CreateConnectionString(string databasePath)
        => new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5
        }.ToString();

    private static string CreateReadOnlyConnectionString(string databasePath)
        => new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            DefaultTimeout = 2
        }.ToString();

    private static bool ShouldResetStaleDatabase(SqliteException exception)
        => exception.SqliteErrorCode == 1
           && (exception.Message.Contains("no such column", StringComparison.OrdinalIgnoreCase)
               || exception.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase)
               || exception.Message.Contains("has no column named", StringComparison.OrdinalIgnoreCase));

    private static bool IsHandledPersistenceException(SqliteException exception)
        => exception.SqliteErrorCode == 1
           || exception.SqliteErrorCode == 5
           || exception.SqliteErrorCode == 8
           || exception.SqliteErrorCode == 10
           || exception.SqliteErrorCode == 14;

    private static bool TryResetDatabaseArtifacts(string databasePath)
    {
        bool allDeleted = true;

        foreach (string path in GetDatabaseArtifactPaths(databasePath))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
            catch
            {
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ReportHandledFailure(databasePath, "reset-failed", $"Handled failure while deleting stale SQLite artifact '{path}': {exception.Message}");
                allDeleted = false;
            }
        }

        return allDeleted;
    }

    private static IEnumerable<string> GetDatabaseArtifactPaths(string databasePath)
    {
        yield return databasePath;
        yield return $"{databasePath}-wal";
        yield return $"{databasePath}-shm";
    }

    private static string SerializeHealth(PackageHealthInfo health) => JsonSerializer.Serialize(health, JsonOptions);
    private static PackageHealthInfo DeserializeHealth(string json) => PackageHealthJsonSerializer.DeserializeHealth(json);
    private static string SerializeRemediation(PackageRemediationAdvice remediation) => JsonSerializer.Serialize(remediation, JsonOptions);
    private static PackageRemediationAdvice DeserializeRemediation(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<PackageRemediationAdvice>(json, JsonOptions)!;
        }
        catch (JsonException)
        {
            return new PackageRemediationAdvice(null, null, null, null);
        }
    }

    private static string SerializeCriticality(CriticalityAssessment criticality) => JsonSerializer.Serialize(criticality, JsonOptions);
    private static CriticalityAssessment DeserializeCriticality(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<CriticalityAssessment>(json, JsonOptions)!;
        }
        catch (JsonException)
        {
            return new CriticalityAssessment(0d, 0d, CriticalityBand.Low, CriticalityBand.Low, Array.Empty<FactorScore>(), string.Empty);
        }
    }

    private static async Task<bool> HasOrphanCleanupSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
        => await HasTableAsync(connection, "SnapshotRun", cancellationToken)
           && await HasTableAsync(connection, "ProjectSnapshot", cancellationToken)
           && await HasTableAsync(connection, "PackageInstance", cancellationToken)
           && await HasTableAsync(connection, "DependencyEdge", cancellationToken)
           && await HasTableAsync(connection, "PackageKnowledgeChange", cancellationToken);

    private static async Task<bool> HasRequiredSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
        => await HasTableAsync(connection, "PackageKnowledge", cancellationToken)
           && await HasColumnAsync(connection, "PackageKnowledge", "LatestKnownHealthJson", cancellationToken)
           && await HasColumnAsync(connection, "PackageKnowledge", "RemediationJson", cancellationToken)
           && await HasColumnAsync(connection, "PackageKnowledge", "CriticalityJson", cancellationToken);

    private static async Task<bool> HasTableAsync(SqliteConnection connection, string tableName, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table' AND name = $tableName
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$tableName", tableName);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> HasColumnAsync(SqliteConnection connection, string tableName, string columnName, CancellationToken cancellationToken)
    {
        string escapedTable = tableName.Replace("'", "''", StringComparison.Ordinal);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT 1
            FROM pragma_table_info('{escapedTable}')
            WHERE lower(name) = lower($columnName)
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$columnName", columnName);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
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
}
