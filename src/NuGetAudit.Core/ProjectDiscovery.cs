using System.Xml.Linq;
using SlnParser;
using SlnParser.Contracts;

namespace NuGetAudit.Core;

/// <summary>
/// Discovers supported project entries from solution or project inputs.
/// </summary>
internal static class SolutionDiscovery
{
    /// <summary>
    /// Discovers the supported projects contained in a solution or project file.
    /// </summary>
    /// <param name="solutionPath">The path to the solution or project file.</param>
    /// <returns>The discovered project descriptors.</returns>
    public static IReadOnlyList<ProjectDescriptor> DiscoverProjects(string solutionPath)
    {
        string extension = Path.GetExtension(solutionPath);

        if (IsSupportedProjectPath(solutionPath))
        {
            string displayName = AuditProjectDisplayName.ResolveProjectDisplayName(
                solutionPath,
                Path.GetFileNameWithoutExtension(solutionPath));
            return
            [
                new ProjectDescriptor(displayName, solutionPath, File.Exists(solutionPath) ? ProjectLoadState.Loaded : ProjectLoadState.Unloaded)
            ];
        }

        string rootDirectory = Path.GetDirectoryName(solutionPath)!;
        return extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            ? DiscoverFromSlnx(solutionPath, rootDirectory)
            : DiscoverFromSln(solutionPath, rootDirectory);
    }

    private static IReadOnlyList<ProjectDescriptor> DiscoverFromSlnx(string solutionPath, string rootDirectory)
    {
        try
        {
            List<ProjectDescriptor> nativeProjects = DiscoverFromSlnxNative(solutionPath, rootDirectory);
            if (nativeProjects.Count > 0)
            {
                return nativeProjects;
            }

            List<ProjectDescriptor>? parserProjects = TryMapSolutionWithSlnParser(solutionPath, rootDirectory);
            if (parserProjects is not null && parserProjects.Count > 0)
            {
                DebugLog.Write(
                    "SolutionDiscovery",
                    $"Native .slnx XML discovery found no supported projects; SlnParser recovered {parserProjects.Count} project(s) for '{solutionPath}'.");
                return parserProjects;
            }

            return nativeProjects;
        }
        catch (Exception primaryFailure)
        {
            List<ProjectDescriptor>? parserProjects = TryMapSolutionWithSlnParser(solutionPath, rootDirectory);
            if (parserProjects is not null && parserProjects.Count > 0)
            {
                DebugLog.Write(
                    "SolutionDiscovery",
                    $"Native .slnx discovery failed for '{solutionPath}'; SlnParser fallback succeeded ({parserProjects.Count} project(s)). {primaryFailure.GetType().Name}: {primaryFailure.Message}");
                return parserProjects;
            }

            throw;
        }
    }

    private static IReadOnlyList<ProjectDescriptor> DiscoverFromSln(string solutionPath, string rootDirectory)
    {
        try
        {
            List<ProjectDescriptor> nativeProjects = DiscoverFromSlnNative(solutionPath, rootDirectory);
            if (nativeProjects.Count > 0)
            {
                return nativeProjects;
            }

            List<ProjectDescriptor>? parserProjects = TryMapSolutionWithSlnParser(solutionPath, rootDirectory);
            if (parserProjects is not null && parserProjects.Count > 0)
            {
                DebugLog.Write(
                    "SolutionDiscovery",
                    $"Native .sln line parser found no supported projects; SlnParser recovered {parserProjects.Count} project(s) for '{solutionPath}'.");
                return parserProjects;
            }

            return nativeProjects;
        }
        catch (Exception primaryFailure)
        {
            List<ProjectDescriptor>? parserProjects = TryMapSolutionWithSlnParser(solutionPath, rootDirectory);
            if (parserProjects is not null && parserProjects.Count > 0)
            {
                DebugLog.Write(
                    "SolutionDiscovery",
                    $"Native .sln discovery failed for '{solutionPath}'; SlnParser fallback succeeded ({parserProjects.Count} project(s)). {primaryFailure.GetType().Name}: {primaryFailure.Message}");
                return parserProjects;
            }

            throw;
        }
    }

    /// <summary>
    /// Discovers projects from a Visual Studio <c>.slnx</c> file using inline XML parsing.
    /// </summary>
    private static List<ProjectDescriptor> DiscoverFromSlnxNative(string solutionPath, string rootDirectory)
    {
        XDocument document = XDocument.Load(solutionPath);
        List<ProjectDescriptor> projects = new();

        foreach (XElement projectElement in document.Descendants("Project"))
        {
            XAttribute? pathAttribute = projectElement.Attribute("Path");

            if (pathAttribute is null)
            {
                continue;
            }

            string relativePath = pathAttribute.Value.Replace('/', Path.DirectorySeparatorChar);
            string projectPath = Path.GetFullPath(Path.Combine(rootDirectory, relativePath));

            if (!IsSupportedProjectPath(projectPath))
            {
                continue;
            }

            string displayName = AuditProjectDisplayName.ResolveProjectDisplayName(
                projectPath,
                Path.GetFileNameWithoutExtension(projectPath));
            projects.Add(new ProjectDescriptor(displayName, projectPath, File.Exists(projectPath) ? ProjectLoadState.Loaded : ProjectLoadState.Unloaded));
        }

        return projects;
    }

    /// <summary>
    /// Discovers projects from a classic Visual Studio <c>.sln</c> file using line-based parsing.
    /// </summary>
    private static List<ProjectDescriptor> DiscoverFromSlnNative(string solutionPath, string rootDirectory)
    {
        List<ProjectDescriptor> projects = new();

        foreach (string line in File.ReadLines(solutionPath))
        {
            if (!line.StartsWith("Project(", StringComparison.Ordinal))
            {
                continue;
            }

            string[] parts = line.Split(',');

            if (parts.Length < 2)
            {
                continue;
            }

            string projectName = ExtractLastQuotedValue(parts[0]);
            string relativePath = ExtractQuotedValue(parts[1]);
            string projectPath = Path.GetFullPath(Path.Combine(rootDirectory, relativePath));

            if (!IsSupportedProjectPath(projectPath))
            {
                continue;
            }

            string displayName = AuditProjectDisplayName.ResolveProjectDisplayName(projectPath, projectName);
            projects.Add(new ProjectDescriptor(displayName, projectPath, File.Exists(projectPath) ? ProjectLoadState.Loaded : ProjectLoadState.Unloaded));
        }

        return projects;
    }

    /// <summary>
    /// Uses <see href="https://github.com/wgnf/SlnParser/">SlnParser</see> when native discovery throws or finds no supported projects.
    /// </summary>
    private static List<ProjectDescriptor>? TryMapSolutionWithSlnParser(string solutionPath, string rootDirectory)
    {
        SolutionParser parser = new();
        if (!parser.TryParse(solutionPath, out ISolution? parsed) || parsed is null)
        {
            return null;
        }

        return MapSlnParserToDescriptors(parsed, rootDirectory);
    }

    private static List<ProjectDescriptor> MapSlnParserToDescriptors(ISolution parsed, string rootDirectory)
    {
        List<ProjectDescriptor> result = new();
        foreach (IProject project in parsed.AllProjects)
        {
            if (project.Type == ProjectType.SolutionFolder)
            {
                continue;
            }

            if (project is not SolutionProject solutionProject)
            {
                continue;
            }

            string projectPath = solutionProject.File.FullName;
            if (!Path.IsPathRooted(projectPath))
            {
                projectPath = Path.GetFullPath(Path.Combine(rootDirectory, projectPath));
            }

            if (!IsSupportedProjectPath(projectPath))
            {
                continue;
            }

            string displayName = AuditProjectDisplayName.ResolveProjectDisplayName(
                projectPath,
                string.IsNullOrWhiteSpace(project.Name)
                    ? Path.GetFileNameWithoutExtension(projectPath)
                    : project.Name);

            result.Add(new ProjectDescriptor(displayName, projectPath, File.Exists(projectPath) ? ProjectLoadState.Loaded : ProjectLoadState.Unloaded));
        }

        return result;
    }

    /// <summary>
    /// Extracts the last quoted substring from the first segment of a <c>Project(...)</c> line (the human-readable name after <c>=</c>), not the project-type GUID.
    /// </summary>
    private static string ExtractLastQuotedValue(string input)
    {
        int lastQuote = input.LastIndexOf('"');
        if (lastQuote <= 0)
        {
            return input.Trim();
        }

        int previousQuote = input.LastIndexOf('"', lastQuote - 1);
        if (previousQuote < 0)
        {
            return input.Trim();
        }

        return input[(previousQuote + 1)..lastQuote];
    }

    /// <summary>
    /// Extracts the first quoted value from a solution token.
    /// </summary>
    /// <param name="input">The token to parse.</param>
    /// <returns>The extracted value or the trimmed input when quotes are absent.</returns>
    private static string ExtractQuotedValue(string input)
    {
        int firstQuote = input.IndexOf('"', StringComparison.Ordinal);
        int lastQuote = input.LastIndexOf('"');
        return firstQuote >= 0 && lastQuote > firstQuote
            ? input[(firstQuote + 1)..lastQuote]
            : input.Trim();
    }

    /// <summary>
    /// Determines whether the path points to a supported MSBuild project type.
    /// </summary>
    /// <param name="projectPath">The project path to inspect.</param>
    /// <returns><see langword="true"/> when the path is supported; otherwise, <see langword="false"/>.</returns>
    private static bool IsSupportedProjectPath(string projectPath)
    {
        string extension = Path.GetExtension(projectPath);
        return extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase);
    }
}
