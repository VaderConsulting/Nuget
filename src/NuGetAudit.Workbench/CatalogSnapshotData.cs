namespace NuGetAudit.Workbench;

internal sealed record CatalogSnapshotData(string SolutionName, string SolutionPath, string AnalysisStatus, DateTimeOffset CapturedUtc, int ProjectCount, int PackageCount, IReadOnlyList<PackageViewRow> Packages);
