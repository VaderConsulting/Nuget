namespace NuGetAudit.Workbench;

internal sealed record FolderViewRow(string FolderPath, string AuditStatus, int SolutionCount, int ProjectCount, int PackageCount, int TrackedTargetCount, string LastScanStatus, DateTimeOffset LastScannedUtc);
