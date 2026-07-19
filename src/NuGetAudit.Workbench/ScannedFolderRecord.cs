namespace NuGetAudit.Workbench;

internal sealed record ScannedFolderRecord(string FolderPath, DateTimeOffset LastScannedUtc, int LastDiscoveredCount, int LastAddedCount, string LastScanStatus);
