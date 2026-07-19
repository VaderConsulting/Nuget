using System.Xml.Linq;
using NuGetAudit.DbUtilities;

namespace NuGetAudit.Core;

/// <summary>
/// Resolves human-readable project and audit-input labels. Visual Studio <c>.vshistory</c> copies use
/// timestamp file names; discovery and UI must not treat those as the real project or solution name.
/// </summary>
public static class AuditProjectDisplayName
{
    /// <summary>
    /// Determines whether the path is inside Visual Studio local file history: any path segment named
    /// <c>.vshistory</c> (case-insensitive, including <c>.vsHistory</c>). All solutions, projects, sources, and
    /// other files under that tree should be ignored for auditing and file watching.
    /// </summary>
    public static bool IsUnderVsHistoryFolder(string path)
    {
        return VsHistoryPath.IsUnderVsHistoryFolder(path);
    }

    /// <summary>
    /// Reads <c>AssemblyName</c>, <c>ProjectName</c>, or <c>RootNamespace</c> from a project file when present and not MSBuild-expanded.
    /// </summary>
    public static string? TryReadPrimaryMsbuildLabel(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
        {
            return null;
        }

        try
        {
            XDocument document = XDocument.Load(projectPath, LoadOptions.PreserveWhitespace);
            XElement? root = document.Root;
            if (root is null || !string.Equals(root.Name.LocalName, "Project", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            foreach (XElement propertyGroup in root.Elements().Where(static element => string.Equals(element.Name.LocalName, "PropertyGroup", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (string localName in new[] { "AssemblyName", "ProjectName", "RootNamespace" })
                {
                    XElement? match = propertyGroup.Elements()
                        .FirstOrDefault(element => string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));
                    string value = match?.Value.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(value) || value.Contains('$', StringComparison.Ordinal))
                    {
                        continue;
                    }

                    return value;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    /// <summary>
    /// When the project file name is a history timestamp, derives a label from the parent folder
    /// (often <c>MyApp.csproj</c>) so the display name becomes <c>MyApp</c>.
    /// </summary>
    public static string RefineProjectFileName(string projectPath, string fileNameWithoutExtensionFallback)
    {
        if (!IsUnderVsHistoryFolder(projectPath))
        {
            return fileNameWithoutExtensionFallback;
        }

        string? directory = Path.GetDirectoryName(projectPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return fileNameWithoutExtensionFallback;
        }

        string parentName = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (parentName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || parentName.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)
            || parentName.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileNameWithoutExtension(parentName);
        }

        return fileNameWithoutExtensionFallback;
    }

    /// <summary>
    /// Resolves a display name for a project path, preferring MSBuild metadata, then <c>.vshistory</c> heuristics, then the supplied fallback.
    /// When the project file does not define a usable name, the solution fallback is blank, or the only candidate is a GUID (common in <c>.sln</c>),
    /// uses the project file name (without extension).
    /// </summary>
    public static string ResolveProjectDisplayName(string projectPath, string fallbackFromSolutionOrFile)
    {
        string? fromFile = TryReadPrimaryMsbuildLabel(projectPath);
        if (!string.IsNullOrWhiteSpace(fromFile) && !LooksLikeGuid(fromFile))
        {
            return fromFile;
        }

        string fileStem = Path.GetFileNameWithoutExtension(projectPath);
        if (string.IsNullOrWhiteSpace(fileStem))
        {
            fileStem = Path.GetFileName(projectPath);
        }

        string baseFallback = string.IsNullOrWhiteSpace(fallbackFromSolutionOrFile)
            ? fileStem
            : fallbackFromSolutionOrFile.Trim();

        if (string.IsNullOrWhiteSpace(baseFallback) || LooksLikeGuid(baseFallback))
        {
            baseFallback = fileStem;
        }

        string resolved = RefineProjectFileName(projectPath, baseFallback).Trim();
        if (string.IsNullOrWhiteSpace(resolved) || LooksLikeGuid(resolved))
        {
            resolved = fileStem;
        }

        if (string.IsNullOrWhiteSpace(resolved))
        {
            resolved = Path.GetFileName(projectPath);
        }

        return resolved;
    }

    /// <summary>
    /// True when <paramref name="value"/> parses as a <see cref="Guid"/> (including braced forms). Used to ignore solution display names that store a project id instead of a label.
    /// </summary>
    private static bool LooksLikeGuid(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return Guid.TryParse(value.Trim(), out _);
    }

    /// <summary>
    /// Display name for a catalog row or audit input path (solution, solution-xml, or project).
    /// </summary>
    public static string GetAuditInputDisplayName(string inputPath)
    {
        string extension = Path.GetExtension(inputPath);
        if (extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileNameWithoutExtension(inputPath);
        }

        if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveProjectDisplayName(inputPath, Path.GetFileNameWithoutExtension(inputPath));
        }

        return Path.GetFileName(inputPath);
    }
}
