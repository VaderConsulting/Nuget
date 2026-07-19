using Microsoft.Data.Sqlite;

namespace NuGetAudit.DbUtilities;

/// <summary>
/// Removes persisted topology and timeline rows tied to Visual Studio <c>.vshistory</c> paths from
/// <c>knowledge.timeline.db</c> before readers hydrate in-memory models.
/// </summary>
public static class KnowledgeTimelineDbVsHistoryPurge
{
    private const string DatabaseFileName = "knowledge.timeline.db";

    /// <summary>
    /// Deletes snapshot runs, projects, package instances, dependency edges, and knowledge-change rows whose
    /// solution or project paths fall under a <c>.vshistory</c> segment. Also removes snapshot runs that
    /// have no projects left. Best-effort: failures are swallowed so read paths still run.
    /// </summary>
    public static void TryPurge(string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return;
        }

        string databasePath = Path.Combine(outputDirectory, DatabaseFileName);
        if (!File.Exists(databasePath))
        {
            return;
        }

        try
        {
            using SqliteConnection Connection = new(CreateReadWriteConnectionString(databasePath));
            Connection.Open();
            if (!HasMinimumSchema(Connection))
            {
                return;
            }

            using SqliteTransaction Transaction = Connection.BeginTransaction();

            List<string> SolutionHistorySnapshotIds = new();
            using (SqliteCommand SelectRunsCommand = Connection.CreateCommand())
            {
                SelectRunsCommand.Transaction = Transaction;
                SelectRunsCommand.CommandText =
                    """
                    SELECT SnapshotId, SolutionPath
                    FROM main.SnapshotRun;
                    """;
                using SqliteDataReader RunsReader = SelectRunsCommand.ExecuteReader();
                while (RunsReader.Read())
                {
                    string SnapshotId = RunsReader.GetString(0);
                    string SolutionPath = RunsReader.GetString(1);
                    if (VsHistoryPath.IsUnderVsHistoryFolder(SolutionPath))
                    {
                        SolutionHistorySnapshotIds.Add(SnapshotId);
                    }
                }
            }

            foreach (string SnapshotId in SolutionHistorySnapshotIds)
            {
                DeleteSnapshotAndChildren(Connection, Transaction, SnapshotId);
            }

            List<(string SnapshotId, string ProjectId)> ProjectHistoryRows = new();
            using (SqliteCommand SelectProjectsCommand = Connection.CreateCommand())
            {
                SelectProjectsCommand.Transaction = Transaction;
                SelectProjectsCommand.CommandText =
                    """
                    SELECT SnapshotId, ProjectId, ProjectPath
                    FROM main.ProjectSnapshot;
                    """;
                using SqliteDataReader ProjectsReader = SelectProjectsCommand.ExecuteReader();
                while (ProjectsReader.Read())
                {
                    string ProjectPath = ProjectsReader.GetString(2);
                    if (VsHistoryPath.IsUnderVsHistoryFolder(ProjectPath))
                    {
                        ProjectHistoryRows.Add((ProjectsReader.GetString(0), ProjectsReader.GetString(1)));
                    }
                }
            }

            foreach ((string SnapshotId, string ProjectId) Row in ProjectHistoryRows)
            {
                DeleteProjectSubtree(Connection, Transaction, Row.SnapshotId, Row.ProjectId);
            }

            List<string> OrphanSnapshotIds = new();
            using (SqliteCommand OrphansCommand = Connection.CreateCommand())
            {
                OrphansCommand.Transaction = Transaction;
                OrphansCommand.CommandText =
                    """
                    SELECT run.SnapshotId
                    FROM main.SnapshotRun AS run
                    LEFT JOIN main.ProjectSnapshot AS project
                        ON project.SnapshotId = run.SnapshotId
                    WHERE project.SnapshotId IS NULL;
                    """;
                using SqliteDataReader OrphansReader = OrphansCommand.ExecuteReader();
                while (OrphansReader.Read())
                {
                    OrphanSnapshotIds.Add(OrphansReader.GetString(0));
                }
            }

            foreach (string SnapshotId in OrphanSnapshotIds)
            {
                DeleteSnapshotAndChildren(Connection, Transaction, SnapshotId);
            }

            Transaction.Commit();
        }
        catch (SqliteException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteSnapshotAndChildren(SqliteConnection Connection, SqliteTransaction Transaction, string SnapshotId)
    {
        if (HasTable(Connection, Transaction, "PackageKnowledgeChange"))
        {
            using SqliteCommand DeleteChangesCommand = Connection.CreateCommand();
            DeleteChangesCommand.Transaction = Transaction;
            DeleteChangesCommand.CommandText = "DELETE FROM main.PackageKnowledgeChange WHERE SnapshotId = $snapshotId;";
            DeleteChangesCommand.Parameters.AddWithValue("$snapshotId", SnapshotId);
            DeleteChangesCommand.ExecuteNonQuery();
        }

        if (HasTable(Connection, Transaction, "DependencyEdge"))
        {
            using SqliteCommand DeleteEdgesCommand = Connection.CreateCommand();
            DeleteEdgesCommand.Transaction = Transaction;
            DeleteEdgesCommand.CommandText = "DELETE FROM main.DependencyEdge WHERE SnapshotId = $snapshotId;";
            DeleteEdgesCommand.Parameters.AddWithValue("$snapshotId", SnapshotId);
            DeleteEdgesCommand.ExecuteNonQuery();
        }

        using (SqliteCommand DeletePackagesCommand = Connection.CreateCommand())
        {
            DeletePackagesCommand.Transaction = Transaction;
            DeletePackagesCommand.CommandText = "DELETE FROM main.PackageInstance WHERE SnapshotId = $snapshotId;";
            DeletePackagesCommand.Parameters.AddWithValue("$snapshotId", SnapshotId);
            DeletePackagesCommand.ExecuteNonQuery();
        }

        using (SqliteCommand DeleteProjectsCommand = Connection.CreateCommand())
        {
            DeleteProjectsCommand.Transaction = Transaction;
            DeleteProjectsCommand.CommandText = "DELETE FROM main.ProjectSnapshot WHERE SnapshotId = $snapshotId;";
            DeleteProjectsCommand.Parameters.AddWithValue("$snapshotId", SnapshotId);
            DeleteProjectsCommand.ExecuteNonQuery();
        }

        using (SqliteCommand DeleteRunCommand = Connection.CreateCommand())
        {
            DeleteRunCommand.Transaction = Transaction;
            DeleteRunCommand.CommandText = "DELETE FROM main.SnapshotRun WHERE SnapshotId = $snapshotId;";
            DeleteRunCommand.Parameters.AddWithValue("$snapshotId", SnapshotId);
            DeleteRunCommand.ExecuteNonQuery();
        }
    }

    private static void DeleteProjectSubtree(SqliteConnection Connection, SqliteTransaction Transaction, string SnapshotId, string ProjectId)
    {
        if (HasTable(Connection, Transaction, "DependencyEdge"))
        {
            using SqliteCommand DeleteEdgesCommand = Connection.CreateCommand();
            DeleteEdgesCommand.Transaction = Transaction;
            DeleteEdgesCommand.CommandText =
                "DELETE FROM main.DependencyEdge WHERE SnapshotId = $snapshotId AND ProjectId = $projectId;";
            DeleteEdgesCommand.Parameters.AddWithValue("$snapshotId", SnapshotId);
            DeleteEdgesCommand.Parameters.AddWithValue("$projectId", ProjectId);
            DeleteEdgesCommand.ExecuteNonQuery();
        }

        using (SqliteCommand DeletePackagesCommand = Connection.CreateCommand())
        {
            DeletePackagesCommand.Transaction = Transaction;
            DeletePackagesCommand.CommandText =
                "DELETE FROM main.PackageInstance WHERE SnapshotId = $snapshotId AND ProjectId = $projectId;";
            DeletePackagesCommand.Parameters.AddWithValue("$snapshotId", SnapshotId);
            DeletePackagesCommand.Parameters.AddWithValue("$projectId", ProjectId);
            DeletePackagesCommand.ExecuteNonQuery();
        }

        using (SqliteCommand DeleteProjectCommand = Connection.CreateCommand())
        {
            DeleteProjectCommand.Transaction = Transaction;
            DeleteProjectCommand.CommandText =
                "DELETE FROM main.ProjectSnapshot WHERE SnapshotId = $snapshotId AND ProjectId = $projectId;";
            DeleteProjectCommand.Parameters.AddWithValue("$snapshotId", SnapshotId);
            DeleteProjectCommand.Parameters.AddWithValue("$projectId", ProjectId);
            DeleteProjectCommand.ExecuteNonQuery();
        }
    }

    private static bool HasMinimumSchema(SqliteConnection Connection)
        => HasTable(Connection, null, "SnapshotRun")
           && HasTable(Connection, null, "ProjectSnapshot")
           && HasTable(Connection, null, "PackageInstance")
           && HasColumn(Connection, null, "SnapshotRun", "SnapshotId")
           && HasColumn(Connection, null, "SnapshotRun", "SolutionPath")
           && HasColumn(Connection, null, "ProjectSnapshot", "SnapshotId")
           && HasColumn(Connection, null, "ProjectSnapshot", "ProjectId")
           && HasColumn(Connection, null, "ProjectSnapshot", "ProjectPath");

    private static bool HasTable(SqliteConnection Connection, SqliteTransaction? Transaction, string TableName)
    {
        using SqliteCommand TableCheckCommand = Connection.CreateCommand();
        TableCheckCommand.Transaction = Transaction;
        TableCheckCommand.CommandText =
            """
            SELECT 1
            FROM main.sqlite_master
            WHERE type = 'table' AND name = $tableName
            LIMIT 1;
            """;
        TableCheckCommand.Parameters.AddWithValue("$tableName", TableName);
        return TableCheckCommand.ExecuteScalar() is not null;
    }

    private static bool HasColumn(SqliteConnection Connection, SqliteTransaction? Transaction, string TableName, string ColumnName)
    {
        string EscapedTable = TableName.Replace("'", "''");
        using SqliteCommand PragmaCommand = Connection.CreateCommand();
        PragmaCommand.Transaction = Transaction;
        PragmaCommand.CommandText = $"PRAGMA main.table_info('{EscapedTable}');";
        using SqliteDataReader InfoReader = PragmaCommand.ExecuteReader();
        while (InfoReader.Read())
        {
            string Name = InfoReader.GetString(1);
            if (string.Equals(Name, ColumnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string CreateReadWriteConnectionString(string databasePath)
        => new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
}
