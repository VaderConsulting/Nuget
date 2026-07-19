namespace NuGetAudit.Workbench;

internal sealed partial class AuditCatalogStore
{
    private sealed class CatalogDocument
    {
        public AuditCatalogEntry[] Targets { get; set; } = Array.Empty<AuditCatalogEntry>();
        public ScannedFolderRecord[] ScannedFolders { get; set; } = Array.Empty<ScannedFolderRecord>();
    }
}
