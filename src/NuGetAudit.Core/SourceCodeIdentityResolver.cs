using System.Security.Cryptography;
using System.Text;

namespace NuGetAudit.Core;

/// <summary>
/// Resolves a stable source-code identity from local paths and optional Git metadata.
/// </summary>
public static class SourceCodeIdentityResolver
{
    public static SourceCodeIdentity Resolve(string inputPath)
    {
        string fullInputPath = Path.GetFullPath(inputPath);
        string machineName = Environment.MachineName;
        string repositoryRootPath = FindRepositoryRoot(fullInputPath) ?? Path.GetDirectoryName(fullInputPath) ?? fullInputPath;
        string? gitRemoteUrl = TryReadGitRemoteUrl(repositoryRootPath);
        string? gitRemoteKey = string.IsNullOrWhiteSpace(gitRemoteUrl) ? null : NormalizeRemoteUrl(gitRemoteUrl);
        string identityKind = string.IsNullOrWhiteSpace(gitRemoteKey) ? "disk-path" : "git-remote";
        string identitySource = gitRemoteKey ?? repositoryRootPath.ToUpperInvariant();
        string identityKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identitySource))[..8]);

        return new SourceCodeIdentity(identityKey, identityKind, machineName, fullInputPath, repositoryRootPath, gitRemoteUrl, gitRemoteKey);
    }

    private static string? FindRepositoryRoot(string inputPath)
    {
        string startDirectory = Directory.Exists(inputPath)
            ? inputPath
            : Path.GetDirectoryName(inputPath) ?? inputPath;

        DirectoryInfo? current = new(startDirectory);
        while (current is not null)
        {
            string gitPath = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string? TryReadGitRemoteUrl(string repositoryRootPath)
    {
        string? gitDirectory = ResolveGitDirectory(repositoryRootPath);
        if (string.IsNullOrWhiteSpace(gitDirectory))
        {
            return null;
        }

        string configPath = Path.Combine(gitDirectory, "config");
        if (!File.Exists(configPath))
        {
            return null;
        }

        string[] lines = File.ReadAllLines(configPath);
        bool inOrigin = false;

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.StartsWith("[remote ", StringComparison.OrdinalIgnoreCase))
            {
                inOrigin = line.Contains("\"origin\"", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inOrigin || !line.StartsWith("url", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int separatorIndex = line.IndexOf('=');
            if (separatorIndex < 0 || separatorIndex == line.Length - 1)
            {
                continue;
            }

            return line[(separatorIndex + 1)..].Trim();
        }

        return null;
    }

    private static string? ResolveGitDirectory(string repositoryRootPath)
    {
        string directGitPath = Path.Combine(repositoryRootPath, ".git");
        if (Directory.Exists(directGitPath))
        {
            return directGitPath;
        }

        if (!File.Exists(directGitPath))
        {
            return null;
        }

        string pointer = File.ReadAllText(directGitPath).Trim();
        const string prefix = "gitdir:";
        if (!pointer.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string relativePath = pointer[prefix.Length..].Trim();
        return Path.GetFullPath(Path.Combine(repositoryRootPath, relativePath));
    }

    private static string NormalizeRemoteUrl(string remoteUrl)
    {
        string normalized = remoteUrl.Trim();
        normalized = normalized.Replace('\\', '/');

        if (normalized.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            int colonIndex = normalized.IndexOf(':');
            if (colonIndex > 0)
            {
                normalized = $"ssh://{normalized[..colonIndex]}/{normalized[(colonIndex + 1)..]}";
            }
        }

        if (normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }

        return normalized.ToUpperInvariant();
    }
}
