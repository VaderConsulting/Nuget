using System.IO;
using System.Text.Json;
using NuGetAudit.Core;

namespace NuGetAudit.Workbench;

internal sealed partial class AuditCatalogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _catalogPath;

    public AuditCatalogStore()
    {
        string baseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NuGetAudit");
        Directory.CreateDirectory(baseDirectory);
        _catalogPath = Path.Combine(baseDirectory, "catalog.json");
    }

    internal IReadOnlyList<AuditCatalogEntry> Load()
    {
        return LoadAll().Targets;
    }

    internal IReadOnlyList<ScannedFolderRecord> LoadScannedFolders()
    {
        return LoadAll().ScannedFolders;
    }

    internal (IReadOnlyList<AuditCatalogEntry> Targets, IReadOnlyList<ScannedFolderRecord> ScannedFolders) LoadAll()
    {
        CatalogDocument document = LoadDocument();
        bool documentChanged = PruneVsHistoryFromDocument(document);

        AuditCatalogEntry[] dedupedTargets = DeduplicateTargetsByNormalizedInputPath(document.Targets);
        bool hadDuplicateNormalizedPaths = document.Targets
            .Where(static item => !string.IsNullOrWhiteSpace(item.InputPath))
            .GroupBy(static item => NormalizeCatalogInputPath(item.InputPath), StringComparer.OrdinalIgnoreCase)
            .Any(static group => group.Count() > 1);
        bool hadNonCanonicalPaths = document.Targets.Any(static item =>
            !string.IsNullOrWhiteSpace(item.InputPath)
            && !string.Equals(item.InputPath, NormalizeCatalogInputPath(item.InputPath), StringComparison.Ordinal));

        if (hadDuplicateNormalizedPaths || hadNonCanonicalPaths || dedupedTargets.Length != document.Targets.Length)
        {
            document.Targets = dedupedTargets
                .OrderBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.InputPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            documentChanged = true;
        }

        if (documentChanged)
        {
            Save(document);
        }

        IReadOnlyList<AuditCatalogEntry> targets = dedupedTargets
            .OrderBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.InputPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        IReadOnlyList<ScannedFolderRecord> scannedFolders = document.ScannedFolders
            .Where(static item => !string.IsNullOrWhiteSpace(item.FolderPath))
            .OrderBy(static item => item.FolderPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return (targets, scannedFolders);
    }

    /// <summary>
    /// Returns a stable, full path for catalog keys and deduplication (same file, one logical target).
    /// </summary>
    internal static string NormalizeCatalogInputPath(string? inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(inputPath.Trim());
        }
        catch
        {
            return inputPath.Trim();
        }
    }

    private static AuditCatalogEntry[] DeduplicateTargetsByNormalizedInputPath(IReadOnlyList<AuditCatalogEntry> targets)
    {
        return targets
            .Where(static item => !string.IsNullOrWhiteSpace(item.InputPath))
            .GroupBy(static item => NormalizeCatalogInputPath(item.InputPath), StringComparer.OrdinalIgnoreCase)
            .Select(static group => CanonicalizeCatalogEntry(group.OrderByDescending(static item => item.AddedUtc).First()))
            .ToArray();
    }

    /// <summary>
    /// One catalog row per audit input path for UI and queue (scope lists may aggregate overlapping targets).
    /// </summary>
    internal static AuditCatalogEntry[] DeduplicateCatalogEntries(IReadOnlyList<AuditCatalogEntry> entries)
    {
        return DeduplicateTargetsByNormalizedInputPath(entries);
    }

    private static AuditCatalogEntry CanonicalizeCatalogEntry(AuditCatalogEntry entry)
    {
        string normalized = NormalizeCatalogInputPath(entry.InputPath);
        if (string.Equals(normalized, entry.InputPath, StringComparison.Ordinal))
        {
            return entry;
        }

        return entry with { InputPath = normalized };
    }

    /// <summary>
    /// Removes Visual Studio local-history paths from the on-disk catalog when they are still present.
    /// </summary>
    internal void PersistPruneVsHistory()
    {
        CatalogDocument document = LoadDocument();
        if (PruneVsHistoryFromDocument(document))
        {
            Save(document);
        }
    }

    internal CatalogUpdateResult AddDiscoveredTargets(string rootPath, IReadOnlyList<string> targetPaths)
    {
        CatalogDocument document = LoadDocument();
        PruneVsHistoryFromDocument(document);

        document.Targets = DeduplicateTargetsByNormalizedInputPath(document.Targets);

        Dictionary<string, AuditCatalogEntry> existing = document.Targets
            .ToDictionary(static item => NormalizeCatalogInputPath(item.InputPath), StringComparer.OrdinalIgnoreCase);

        int addedCount = 0;
        string fullRootPath = Path.GetFullPath(rootPath);
        foreach (string targetPath in targetPaths)
        {
            string fullPath = Path.GetFullPath(targetPath);
            if (AuditProjectDisplayName.IsUnderVsHistoryFolder(fullPath))
            {
                continue;
            }

            if (existing.TryGetValue(fullPath, out AuditCatalogEntry? prior))
            {
                SourceCodeIdentity identity = SourceCodeIdentityResolver.Resolve(fullPath);
                AuditCatalogEntry refreshed = new AuditCatalogEntry(
                    identity.IdentityKey,
                    identity.IdentityKind,
                    identity.MachineName,
                    identity.RepositoryRootPath,
                    identity.GitRemoteUrl,
                    identity.GitRemoteKey,
                    fullPath,
                    AuditProjectDisplayName.GetAuditInputDisplayName(fullPath),
                    fullRootPath,
                    prior.OutputDirectory,
                    prior.AddedUtc);
                existing[fullPath] = refreshed;
                continue;
            }

            SourceCodeIdentity identityNew = SourceCodeIdentityResolver.Resolve(fullPath);

            existing[fullPath] = new AuditCatalogEntry(identityNew.IdentityKey, identityNew.IdentityKind, identityNew.MachineName, identityNew.RepositoryRootPath, identityNew.GitRemoteUrl, identityNew.GitRemoteKey, fullPath, AuditProjectDisplayName.GetAuditInputDisplayName(fullPath), fullRootPath, Path.Combine(Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory, ".nugetaudit"), DateTimeOffset.UtcNow);
            addedCount++;
        }

        document.Targets = existing.Values
            .OrderBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.InputPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (!AuditProjectDisplayName.IsUnderVsHistoryFolder(fullRootPath))
        {
            UpsertScannedFolder(document, rootPath, targetPaths.Count, addedCount);
        }

        Save(document);

        IReadOnlyList<AuditCatalogEntry> folderTargets = document.Targets
            .Where(entry => string.Equals(entry.DiscoveredFromPath, Path.GetFullPath(rootPath), StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return new CatalogUpdateResult(document.Targets, document.ScannedFolders, folderTargets, addedCount, targetPaths.Count);
    }

    private CatalogDocument LoadDocument()
    {
        if (!File.Exists(_catalogPath))
        {
            return new CatalogDocument();
        }

        CatalogDocument? document = JsonSerializer.Deserialize<CatalogDocument>(File.ReadAllText(_catalogPath), JsonOptions);
        return document ?? new CatalogDocument();
    }

    private static void UpsertScannedFolder(CatalogDocument document, string rootPath, int discoveredCount, int addedCount)
    {
        string fullRootPath = Path.GetFullPath(rootPath);
        Dictionary<string, ScannedFolderRecord> folders = document.ScannedFolders
            .ToDictionary(static item => item.FolderPath, StringComparer.OrdinalIgnoreCase);

        string status = discoveredCount == 0
            ? "No supported files found"
            : addedCount == 0
                ? "Scan complete; all discovered targets were already tracked"
                : $"Scan complete; added {addedCount} new target(s)";

        folders[fullRootPath] = new ScannedFolderRecord(fullRootPath, DateTimeOffset.UtcNow, discoveredCount, addedCount, status);

        document.ScannedFolders = folders.Values
            .OrderBy(static item => item.FolderPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void Save(CatalogDocument document)
    {
        File.WriteAllText(_catalogPath, JsonSerializer.Serialize(document, JsonOptions));
    }

    /// <summary>
    /// Drops catalog targets and scanned-folder records whose paths fall under a <c>.vshistory</c> segment.
    /// </summary>
    /// <returns><see langword="true"/> when the document was modified.</returns>
    private static bool PruneVsHistoryFromDocument(CatalogDocument document)
    {
        AuditCatalogEntry[] keptTargets = document.Targets
            .Where(static item => !string.IsNullOrWhiteSpace(item.InputPath)
                && !AuditProjectDisplayName.IsUnderVsHistoryFolder(item.InputPath))
            .ToArray();

        ScannedFolderRecord[] keptFolders = document.ScannedFolders
            .Where(static item => !string.IsNullOrWhiteSpace(item.FolderPath)
                && !AuditProjectDisplayName.IsUnderVsHistoryFolder(item.FolderPath))
            .ToArray();

        bool targetsChanged = keptTargets.Length != document.Targets.Length;
        bool foldersChanged = keptFolders.Length != document.ScannedFolders.Length;
        if (!targetsChanged && !foldersChanged)
        {
            return false;
        }

        document.Targets = keptTargets;
        document.ScannedFolders = keptFolders;
        return true;
    }

    internal sealed record CatalogUpdateResult(IReadOnlyList<AuditCatalogEntry> Targets, IReadOnlyList<ScannedFolderRecord> ScannedFolders, IReadOnlyList<AuditCatalogEntry> FolderTargets, int AddedCount, int DiscoveredCount);
}
