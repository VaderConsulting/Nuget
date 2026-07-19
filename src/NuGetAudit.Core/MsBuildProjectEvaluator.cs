using Microsoft.Build.Evaluation;
using Microsoft.Build.Locator;

namespace NuGetAudit.Core;

/// <summary>
/// Evaluates projects through MSBuild so conditional package references are resolved correctly.
/// </summary>
internal sealed class MsBuildProjectEvaluator
{
    private static readonly object SyncLock = new();

    /// <summary>
    /// Evaluates direct package references for the supplied project.
    /// </summary>
    /// <param name="projectPath">The project path to evaluate.</param>
    /// <param name="solutionPath">The owning solution path.</param>
    /// <param name="centralContext">The central package context used as a fallback for version discovery.</param>
    /// <returns>The evaluated package references and warnings.</returns>
    public MsBuildEvaluationResult EvaluatePackageReferences(string projectPath, string solutionPath, DirectoryPackagesPropsContext centralContext)
    {
        List<string> warnings = new();
        if (!TryEnsureMsBuildAvailable(out string? availabilityWarning))
        {
            warnings.Add(availabilityWarning ?? "MSBuild runtime is not available; XML parsing will be used instead.");
            return new MsBuildEvaluationResult(Array.Empty<string>(), Array.Empty<PackageReferenceRecord>(), warnings);
        }

        using ProjectCollection bootstrapCollection = new();
        Project bootstrapProject = new(projectPath, globalProperties: null, toolsVersion: null, bootstrapCollection);

        string? PrimaryTfm = TargetFrameworkMonikerResolution.ReadBestMonikerFromEvaluatedProject(bootstrapProject);
        string[] TargetFrameworkList = SplitTargetFrameworks(PrimaryTfm);

        if (TargetFrameworkList.Length == 0)
        {
            TargetFrameworkList = [string.Empty];
        }

        List<PackageReferenceRecord> packages = new();

        foreach (string TargetFramework in TargetFrameworkList)
        {
            using ProjectCollection projectCollection = new(CreateGlobalProperties(TargetFramework));
            Project evaluatedProject = new(projectPath, projectCollection.GlobalProperties, toolsVersion: null, projectCollection);

            foreach (ProjectItem packageReference in evaluatedProject.GetItems("PackageReference"))
            {
                string PackageId = packageReference.EvaluatedInclude;
                if (string.IsNullOrWhiteSpace(PackageId))
                {
                    continue;
                }

                string? RequestedVersion = packageReference.GetMetadataValue("Version");
                if (string.IsNullOrWhiteSpace(RequestedVersion))
                {
                    RequestedVersion = packageReference.GetMetadataValue("VersionOverride");
                }

                bool IsCentralManaged = string.IsNullOrWhiteSpace(RequestedVersion)
                    && centralContext.TryGetVersion(PackageId, out RequestedVersion);

                packages.Add(new PackageReferenceRecord(PackageId, RequestedVersion, null, PackageReferenceKind.Direct, IsCentralManaged, string.IsNullOrWhiteSpace(TargetFramework) ? null : TargetFramework, Array.Empty<string>(), Array.Empty<string>(), PackageHealthInfo.None, StableKey.Create(solutionPath, projectPath, string.IsNullOrWhiteSpace(TargetFramework) ? null : TargetFramework, null, PackageId)));
            }
        }

        if (packages.Count == 0)
        {
            warnings.Add("MSBuild evaluation found no PackageReference items.");
        }

        IReadOnlyList<PackageReferenceRecord> distinctPackages = packages
            .GroupBy(static package => package.StablePackageInstanceKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(static package => package.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static package => package.TargetFrameworkMoniker, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        IReadOnlyList<string> evaluatedFrameworks = TargetFrameworkList
            .Where(static framework => !string.IsNullOrWhiteSpace(framework))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new MsBuildEvaluationResult(evaluatedFrameworks, distinctPackages, warnings);
    }

    /// <summary>
    /// Ensures the current process can load MSBuild assemblies for project evaluation.
    /// </summary>
    /// <param name="warning">When this method returns, contains a fallback warning when MSBuild could not be made available.</param>
    /// <returns><see langword="true"/> when MSBuild is available; otherwise, <see langword="false"/>.</returns>
    private static bool TryEnsureMsBuildAvailable(out string? warning)
    {
        warning = null;

        lock (SyncLock)
        {
            MsBuildHostBootstrap.TryRegister();
            if (!MSBuildLocator.IsRegistered)
            {
                warning = "MSBuild runtime is not installed or could not be located; XML parsing will be used instead.";
                return false;
            }

            if (!IsMsBuildAssemblyLoadable())
            {
                warning = "MSBuild runtime assemblies could not be loaded after registration; XML parsing will be used instead.";
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Determines whether the core MSBuild assembly can currently be loaded.
    /// </summary>
    /// <returns><see langword="true"/> when the assembly is loadable; otherwise, <see langword="false"/>.</returns>
    private static bool IsMsBuildAssemblyLoadable()
    {
        try
        {
            _ = typeof(ProjectCollection).Assembly;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Creates the global properties used for per-target-framework evaluation.
    /// </summary>
    /// <param name="targetFramework">The target framework to evaluate.</param>
    /// <returns>The MSBuild global properties.</returns>
    private static IDictionary<string, string> CreateGlobalProperties(string targetFramework)
    {
        Dictionary<string, string> properties = new(StringComparer.OrdinalIgnoreCase)
        {
            ["DesignTimeBuild"] = "true",
            ["ExcludeRestorePackageImports"] = "true",
            ["SkipCompilerExecution"] = "true",
            ["ProvideCommandLineArgs"] = "false"
        };

        if (!string.IsNullOrWhiteSpace(targetFramework))
        {
            properties["TargetFramework"] = targetFramework;
        }

        return properties;
    }

    /// <summary>
    /// Splits a target framework declaration into individual target framework monikers.
    /// </summary>
    /// <param name="targetFrameworks">The raw target framework declaration.</param>
    /// <returns>The individual target frameworks.</returns>
    private static string[] SplitTargetFrameworks(string? targetFrameworks)
    {
        if (string.IsNullOrWhiteSpace(targetFrameworks))
        {
            return Array.Empty<string>();
        }

        return targetFrameworks
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
