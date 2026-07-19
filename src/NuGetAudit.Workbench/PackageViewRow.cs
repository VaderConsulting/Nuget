namespace NuGetAudit.Workbench;

internal sealed record PackageViewRow(string FolderPath, string AuditStatus, string SolutionName, string SolutionPath, string ProjectName, string ProjectPath, string PackageId, string? RequestedVersion, string? ResolvedVersion, string ReferenceKind, string? TargetFramework, string HealthState);
