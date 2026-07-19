namespace NuGetAudit.Workbench;

internal sealed record AuditQueueRow(string AuditStatus, string DisplayName, string InputPath, string OutputDirectory, string FolderPath, string? FailureReason);
