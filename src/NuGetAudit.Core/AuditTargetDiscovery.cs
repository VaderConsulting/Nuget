namespace NuGetAudit.Core;

/// <summary>
/// Discovers supported audit targets beneath one or more directories.
/// </summary>
public static class AuditTargetDiscovery
{
    private static readonly string[] SupportedExtensions = [".sln", ".slnx", ".csproj", ".vbproj", ".fsproj"];
    private static readonly string[] SkippedDirectories = [".git", ".vs", ".vshistory", "bin", "obj", "node_modules", "packages"];

    public static IReadOnlyList<string> DiscoverTargets(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return Array.Empty<string>();
        }

        HashSet<string> targets = new(StringComparer.OrdinalIgnoreCase);
        DiscoverRecursive(Path.GetFullPath(rootPath), targets);
        return targets.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void DiscoverRecursive(string directoryPath, ISet<string> targets)
    {
        if (AuditProjectDisplayName.IsUnderVsHistoryFolder(directoryPath))
        {
            return;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directoryPath);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        foreach (string filePath in files)
        {
            if (IsSupportedTarget(filePath))
            {
                targets.Add(Path.GetFullPath(filePath));
            }
        }

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(directoryPath);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        foreach (string childDirectory in directories)
        {
            string name = Path.GetFileName(childDirectory);
            if (SkippedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            DiscoverRecursive(childDirectory, targets);
        }
    }

    private static bool IsSupportedTarget(string path)
    {
        if (AuditProjectDisplayName.IsUnderVsHistoryFolder(path))
        {
            return false;
        }

        return SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }
}
