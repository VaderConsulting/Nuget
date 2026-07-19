using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using NuGet.Packaging;
using NuGet.Packaging.Core;

namespace NuGetAudit.Core;

/// <summary>
/// Analyses individual projects to build package snapshot data using PackageReference, central <c>PackageVersion</c>,
/// <c>packages.config</c>, <c>packages.lock.json</c>, and <c>project.assets.json</c> when each is present.
/// </summary>
internal sealed class ProjectAnalyser
{
    private readonly MsBuildProjectEvaluator _msBuildProjectEvaluator = new();

    /// <summary>
    /// Analyses one project and returns its snapshot representation.
    /// </summary>
    /// <param name="descriptor">The project to analyse.</param>
    /// <param name="solutionPath">The owning solution path used for stable keys.</param>
    /// <returns>The project snapshot.</returns>
    public ProjectSnapshot Analyse(ProjectDescriptor descriptor, string solutionPath)
    {
        List<string> warnings = new();

        if (!File.Exists(descriptor.ProjectPath))
        {
            warnings.Add("Project file is not available on disk.");
            string SolutionDisplayName = AuditProjectDisplayName.GetAuditInputDisplayName(solutionPath);
            DebugLog.WriteTrace("BuildFormat", $"Solution='{SolutionDisplayName}' Project='{descriptor.ProjectName}': project file not on disk; skipping BuildFormat project lines. Path='{descriptor.ProjectPath}'.");
            return new ProjectSnapshot(CreateProjectId(descriptor.ProjectPath), descriptor.ProjectName, descriptor.ProjectPath, ProjectStyle.Unknown, ProjectLoadState.Unloaded, AnalysisStatus.Partial, null, null, null, Array.Empty<PackageReferenceRecord>(), Array.Empty<DependencyEdgeRecord>(), Array.Empty<AuditDiagnostic>(), warnings);
        }

        try
        {
            string SolutionDisplayName = AuditProjectDisplayName.GetAuditInputDisplayName(solutionPath);
            XDocument projectDocument = XDocument.Load(descriptor.ProjectPath, LoadOptions.PreserveWhitespace);
            string projectDirectory = Path.GetDirectoryName(descriptor.ProjectPath)!;
            DirectoryPackagesPropsContext centralContext = DirectoryPackagesPropsContext.Load(projectDirectory, projectDocument);
            ProjectStyle projectStyle = DetermineProjectStyle(projectDocument, projectDirectory);
            List<PackageReferenceRecord> packages = new();
            List<DependencyEdgeRecord> dependencyEdges = new();
            string? assetsFilePath = null;
            string? packagesLockFilePath = null;
            string? targetFrameworks = ReadTargetFrameworks(projectDocument);
            string ProjectJsonPath = Path.Combine(projectDirectory, "project.json");
            if (string.IsNullOrWhiteSpace(targetFrameworks) && LegacyProjectJsonReader.TryInferFrameworkList(ProjectJsonPath, out string? TargetFrameworksFromJson))
            {
                targetFrameworks = TargetFrameworksFromJson;
            }

            if (projectStyle == ProjectStyle.PackageReference)
            {
                MsBuildEvaluationResult evaluationResult = TryEvaluatePackageReferences(descriptor.ProjectPath, solutionPath, centralContext, warnings);

                if (evaluationResult.Packages.Count > 0)
                {
                    packages.AddRange(evaluationResult.Packages);

                    if (evaluationResult.TargetFrameworks.Count > 0)
                    {
                        targetFrameworks = string.Join(';', evaluationResult.TargetFrameworks);
                    }

                    warnings.AddRange(evaluationResult.Warnings);
                }
                else
                {
                    warnings.AddRange(evaluationResult.Warnings);
                    packages.AddRange(ParsePackageReferences(projectDocument, descriptor.ProjectPath, solutionPath, targetFrameworks, centralContext));
                }

                if (packages.Count == 0)
                {
                    string packagesConfigPath = Path.Combine(projectDirectory, "packages.config");
                    if (File.Exists(packagesConfigPath))
                    {
                        IReadOnlyList<PackageReferenceRecord> packagingFallback = ParsePackagesConfigViaNuGetPackaging(packagesConfigPath, descriptor.ProjectPath, solutionPath, targetFrameworks, warnings);
                        if (packagingFallback.Count > 0)
                        {
                            packages.AddRange(packagingFallback);
                            warnings.Add(
                                "No PackageReference items were resolved from MSBuild or project XML; package list was taken from packages.config using NuGet.Packaging.");
                        }
                    }
                }

                if (packages.Count == 0
                    && LegacyProjectJsonReader.TryReadPackages(ProjectJsonPath, descriptor.ProjectPath, solutionPath, targetFrameworks, out List<PackageReferenceRecord> ProjectJsonPackages, out string? InferredTargetFrameworksFromPackages))
                {
                    if (string.IsNullOrWhiteSpace(targetFrameworks) && !string.IsNullOrWhiteSpace(InferredTargetFrameworksFromPackages))
                    {
                        targetFrameworks = InferredTargetFrameworksFromPackages;
                    }

                    if (ProjectJsonPackages.Count > 0)
                    {
                        packages.AddRange(ProjectJsonPackages);
                        warnings.Add("Direct package references were read from legacy project.json.");
                    }
                }

                AppendPackageVersionItemsNotCoveredByReferences(projectDocument, descriptor.ProjectPath, solutionPath, targetFrameworks, packages);
                ApplyPackagesLockFileIfPresent(projectDirectory, descriptor.ProjectPath, solutionPath, packages, dependencyEdges, warnings, ref packagesLockFilePath);
                ApplyProjectAssetsFileIfPresent(projectDirectory, descriptor.ProjectPath, solutionPath, packages, dependencyEdges, warnings, ref assetsFilePath, packagesLockFilePath is not null);
            }
            else if (projectStyle == ProjectStyle.PackagesConfig)
            {
                packages.AddRange(ParsePackagesConfig(projectDirectory, descriptor.ProjectPath, solutionPath, targetFrameworks, warnings));
                warnings.Add("packages.config support is limited to direct package declarations in this implementation.");
                ApplyPackagesLockFileIfPresent(projectDirectory, descriptor.ProjectPath, solutionPath, packages, dependencyEdges, warnings, ref packagesLockFilePath);
                ApplyProjectAssetsFileIfPresent(projectDirectory, descriptor.ProjectPath, solutionPath, packages, dependencyEdges, warnings, ref assetsFilePath, packagesLockFilePath is not null);
            }
            else
            {
                if (!IsMsBuildProjectDocument(projectDocument))
                {
                    warnings.Add("Unsupported or unknown project package management style.");
                }
            }

            BuildInputFormatLogging.LogProjectFileFormat(SolutionDisplayName, descriptor.ProjectName, projectDocument, targetFrameworks);
            BuildInputFormatLogging.LogPackageConfiguration(SolutionDisplayName, descriptor.ProjectName, projectDirectory, projectStyle, centralContext, projectDocument);

            return new ProjectSnapshot(CreateProjectId(descriptor.ProjectPath), descriptor.ProjectName, descriptor.ProjectPath, projectStyle, descriptor.LoadState, DeriveProjectStatus(projectStyle, packages, warnings, projectDocument), targetFrameworks, assetsFilePath, packagesLockFilePath, packages.OrderBy(static package => package.PackageId, StringComparer.OrdinalIgnoreCase).ThenBy(static package => package.TargetFrameworkMoniker, StringComparer.OrdinalIgnoreCase).ToArray(), dependencyEdges.Distinct().OrderBy(static edge => edge.FromPackageId, StringComparer.OrdinalIgnoreCase).ThenBy(static edge => edge.ToPackageId, StringComparer.OrdinalIgnoreCase).ThenBy(static edge => edge.TargetFrameworkMoniker, StringComparer.OrdinalIgnoreCase).ToArray(), Array.Empty<AuditDiagnostic>(), warnings);
        }
        catch (Exception exception)
        {
            string failureSummary =
                $"Project analysis failed for '{descriptor.ProjectPath}'. Mitigation: returning a failed snapshot with no packages. {exception.GetType().Name}: {exception.Message}";
            DebugLog.WriteTrace("ProjectAnalysis", failureSummary);
            warnings.Add($"Project analysis failed: {exception.Message}");
            return new ProjectSnapshot(CreateProjectId(descriptor.ProjectPath), descriptor.ProjectName, descriptor.ProjectPath, ProjectStyle.Unknown, descriptor.LoadState, AnalysisStatus.Failed, null, null, null, Array.Empty<PackageReferenceRecord>(), Array.Empty<DependencyEdgeRecord>(), Array.Empty<AuditDiagnostic>(), warnings);
        }
    }

    private MsBuildEvaluationResult TryEvaluatePackageReferences(string projectPath, string solutionPath, DirectoryPackagesPropsContext centralContext, List<string> warnings)
    {
        try
        {
            return _msBuildProjectEvaluator.EvaluatePackageReferences(projectPath, solutionPath, centralContext);
        }
        catch (FileNotFoundException Failure)
        {
            bool IsMsBuildAssembly = Failure.Message.Contains("Microsoft.Build", StringComparison.OrdinalIgnoreCase);
            string Detail = IsMsBuildAssembly
                ? $"MSBuild evaluation for '{projectPath}' failed: the Microsoft.Build runtime could not be loaded (install Visual Studio or Build Tools; the host should call MsBuildHostBootstrap early). Mitigation: using XML parsing for PackageReference items instead. {Failure.Message}"
                : $"MSBuild evaluation for '{projectPath}' failed: a referenced file was not found (common causes: missing SDK, targets, or props under Program Files or the repo). Mitigation: using XML parsing for PackageReference items instead. {Failure.Message}";
            DebugLog.WriteTrace("ProjectAnalysis", Detail);
            warnings.Add(Detail);
            return new MsBuildEvaluationResult(Array.Empty<string>(), Array.Empty<PackageReferenceRecord>(), Array.Empty<string>());
        }
        catch (DirectoryNotFoundException exception)
        {
            string detail =
                $"MSBuild evaluation for '{projectPath}' failed: a directory on the evaluation path was missing. Mitigation: using XML parsing for PackageReference items instead. {exception.Message}";
            DebugLog.WriteTrace("ProjectAnalysis", detail);
            warnings.Add(detail);
            return new MsBuildEvaluationResult(Array.Empty<string>(), Array.Empty<PackageReferenceRecord>(), Array.Empty<string>());
        }
        catch (Exception exception)
        {
            string detail =
                $"MSBuild evaluation was unavailable for this project; XML parsing was used instead: {exception.Message}";
            DebugLog.WriteTrace("ProjectAnalysis", detail);
            warnings.Add(detail);
            return new MsBuildEvaluationResult(Array.Empty<string>(), Array.Empty<PackageReferenceRecord>(), Array.Empty<string>());
        }
    }

    /// <summary>
    /// Parses direct <c>PackageReference</c> declarations from a project file.
    /// </summary>
    /// <param name="projectDocument">The loaded project document.</param>
    /// <param name="projectPath">The project path.</param>
    /// <param name="solutionPath">The owning solution path.</param>
    /// <param name="targetFrameworks">The declared target frameworks.</param>
    /// <param name="centralContext">The loaded central package context.</param>
    /// <returns>The discovered direct package records.</returns>
    private static IEnumerable<PackageReferenceRecord> ParsePackageReferences(XDocument projectDocument, string projectPath, string solutionPath, string? targetFrameworks, DirectoryPackagesPropsContext centralContext)
    {
        string[] tfmList = SplitTargetFrameworks(targetFrameworks);
        List<PackageReferenceRecord> packages = new();

        foreach (XElement packageReference in projectDocument.Descendants().Where(static element => string.Equals(element.Name.LocalName, "PackageReference", StringComparison.OrdinalIgnoreCase)))
        {
            string? packageId = GetAttributeValueIgnoreCase(packageReference, "Include")
                ?? GetAttributeValueIgnoreCase(packageReference, "Update");

            if (string.IsNullOrWhiteSpace(packageId))
            {
                continue;
            }

            string? requestedVersion =
                GetAttributeValueIgnoreCase(packageReference, "Version")
                ?? packageReference.Elements().FirstOrDefault(static element => string.Equals(element.Name.LocalName, "Version", StringComparison.OrdinalIgnoreCase))?.Value;

            bool isCentralManaged = string.IsNullOrWhiteSpace(requestedVersion)
                && centralContext.TryGetVersion(packageId, out requestedVersion);

            if (tfmList.Length == 0)
            {
                packages.Add(new PackageReferenceRecord(packageId, requestedVersion, null, PackageReferenceKind.Direct, isCentralManaged, null, Array.Empty<string>(), Array.Empty<string>(), PackageHealthInfo.None, StableKey.Create(solutionPath, projectPath, null, null, packageId)));

                continue;
            }

            foreach (string tfm in tfmList)
            {
                packages.Add(new PackageReferenceRecord(packageId, requestedVersion, null, PackageReferenceKind.Direct, isCentralManaged, tfm, Array.Empty<string>(), Array.Empty<string>(), PackageHealthInfo.None, StableKey.Create(solutionPath, projectPath, tfm, null, packageId)));
            }
        }

        return packages;
    }

    /// <summary>
    /// Parses direct package declarations from a <c>packages.config</c> file when present.
    /// Uses inline XML first; falls back to <see cref="PackagesConfigReader"/> from NuGet.Packaging when that yields nothing or throws.
    /// </summary>
    private static IEnumerable<PackageReferenceRecord> ParsePackagesConfig(string projectDirectory, string projectPath, string solutionPath, string? targetFrameworks, List<string> warnings)
    {
        string packagesConfigPath = Path.Combine(projectDirectory, "packages.config");
        if (!File.Exists(packagesConfigPath))
        {
            return Array.Empty<PackageReferenceRecord>();
        }

        List<PackageReferenceRecord> manual = new();
        try
        {
            manual.AddRange(ParsePackagesConfigViaXml(packagesConfigPath, projectPath, solutionPath, targetFrameworks));
        }
        catch (Exception exception)
        {
            warnings.Add($"packages.config XML read failed; trying NuGet.Packaging. {exception.GetType().Name}: {exception.Message}");
        }

        if (manual.Count > 0)
        {
            return manual;
        }

        IReadOnlyList<PackageReferenceRecord> packaging = ParsePackagesConfigViaNuGetPackaging(packagesConfigPath, projectPath, solutionPath, targetFrameworks, warnings);
        if (packaging.Count > 0)
        {
            warnings.Add("Package list for packages.config was taken using NuGet.Packaging (fallback).");
        }

        return packaging;
    }

    private static IEnumerable<PackageReferenceRecord> ParsePackagesConfigViaXml(string packagesConfigPath, string projectPath, string solutionPath, string? targetFrameworks)
    {
        XDocument document = XDocument.Load(packagesConfigPath);
        string? defaultTfm = SplitTargetFrameworks(targetFrameworks).FirstOrDefault();

        return document.Descendants()
            .Where(static element => string.Equals(element.Name.LocalName, "package", StringComparison.OrdinalIgnoreCase))
            .Select(static package => new
            {
                PackageId = GetAttributeValueIgnoreCase(package, "id"),
                Version = GetAttributeValueIgnoreCase(package, "version")
            })
            .Where(static package => !string.IsNullOrWhiteSpace(package.PackageId))
            .Select(package => new PackageReferenceRecord(package.PackageId!, package.Version, package.Version, PackageReferenceKind.Direct, false, defaultTfm, Array.Empty<string>(), Array.Empty<string>(), PackageHealthInfo.None, StableKey.Create(solutionPath, projectPath, defaultTfm, null, package.PackageId!)))
            .ToArray();
    }

    private static string? GetAttributeValueIgnoreCase(XElement Element, string LocalName)
    {
        return Element.Attributes().FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, LocalName, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    /// <summary>
    /// Reads <c>packages.config</c> using NuGet.Packaging's <see cref="PackagesConfigReader"/>.
    /// </summary>
    private static IReadOnlyList<PackageReferenceRecord> ParsePackagesConfigViaNuGetPackaging(string packagesConfigPath, string projectPath, string solutionPath, string? targetFrameworks, List<string> warnings)
    {
        try
        {
            using FileStream stream = new(packagesConfigPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            PackagesConfigReader reader = new(stream, leaveStreamOpen: false);
            string? defaultTfm = SplitTargetFrameworks(targetFrameworks).FirstOrDefault();
            List<PackageReferenceRecord> result = new();

            foreach (PackageReference reference in reader.GetPackages())
            {
                PackageIdentity identity = reference.PackageIdentity;
                string packageId = identity.Id;
                string? versionString = identity.Version?.ToString();
                string? tfmMoniker = null;
                if (reference.TargetFramework is { } framework && framework.IsSpecificFramework)
                {
                    tfmMoniker = framework.GetShortFolderName();
                }

                if (string.IsNullOrWhiteSpace(tfmMoniker))
                {
                    tfmMoniker = defaultTfm;
                }

                result.Add(new PackageReferenceRecord(packageId, versionString, versionString, PackageReferenceKind.Direct, false, tfmMoniker, Array.Empty<string>(), Array.Empty<string>(), PackageHealthInfo.None, StableKey.Create(solutionPath, projectPath, tfmMoniker, null, packageId)));
            }

            return result;
        }
        catch (Exception exception)
        {
            warnings.Add($"NuGet.Packaging could not read packages.config '{packagesConfigPath}': {exception.GetType().Name}: {exception.Message}");
            return Array.Empty<PackageReferenceRecord>();
        }
    }

    /// <summary>
    /// Determines the package management style for the supplied project.
    /// </summary>
    /// <param name="projectDocument">The loaded project document.</param>
    /// <param name="projectDirectory">The project directory.</param>
    /// <returns>The detected project style.</returns>
    private static ProjectStyle DetermineProjectStyle(XDocument projectDocument, string projectDirectory)
    {
        if (projectDocument.Descendants().Any(static element => string.Equals(element.Name.LocalName, "PackageReference", StringComparison.OrdinalIgnoreCase)))
        {
            return ProjectStyle.PackageReference;
        }

        if (projectDocument.Descendants().Any(static element =>
                string.Equals(element.Name.LocalName, "PackageVersion", StringComparison.OrdinalIgnoreCase)
                && (!string.IsNullOrWhiteSpace(GetAttributeValueIgnoreCase(element, "Include"))
                    || !string.IsNullOrWhiteSpace(GetAttributeValueIgnoreCase(element, "Update")))))
        {
            return ProjectStyle.PackageReference;
        }

        if (LegacyProjectJsonReader.IsLikelyProjectJsonManifest(Path.Combine(projectDirectory, "project.json")))
        {
            return ProjectStyle.PackageReference;
        }

        if (File.Exists(Path.Combine(projectDirectory, "packages.config")))
        {
            return ProjectStyle.PackagesConfig;
        }

        return ProjectStyle.Unknown;
    }

    private static void AppendPackageVersionItemsNotCoveredByReferences(XDocument projectDocument, string projectPath, string solutionPath, string? targetFrameworks, List<PackageReferenceRecord> packages)
    {
        HashSet<string> referencedPackageIds = new(packages.Select(static package => package.PackageId), StringComparer.OrdinalIgnoreCase);
        string[] tfmList = SplitTargetFrameworks(targetFrameworks);

        foreach (XElement element in projectDocument.Descendants().Where(static e => string.Equals(e.Name.LocalName, "PackageVersion", StringComparison.OrdinalIgnoreCase)))
        {
            string? packageId = GetAttributeValueIgnoreCase(element, "Include") ?? GetAttributeValueIgnoreCase(element, "Update");
            string? version = GetAttributeValueIgnoreCase(element, "Version");
            if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(version) || referencedPackageIds.Contains(packageId))
            {
                continue;
            }

            if (tfmList.Length == 0)
            {
                packages.Add(new PackageReferenceRecord(packageId, version, null, PackageReferenceKind.Direct, true, null, Array.Empty<string>(), Array.Empty<string>(), PackageHealthInfo.None, StableKey.Create(solutionPath, projectPath, null, null, packageId)));
                continue;
            }

            foreach (string tfm in tfmList)
            {
                packages.Add(new PackageReferenceRecord(packageId, version, null, PackageReferenceKind.Direct, true, tfm, Array.Empty<string>(), Array.Empty<string>(), PackageHealthInfo.None, StableKey.Create(solutionPath, projectPath, tfm, null, packageId)));
            }
        }
    }

    private static void ApplyPackagesLockFileIfPresent(string projectDirectory, string projectPath, string solutionPath, List<PackageReferenceRecord> packages, List<DependencyEdgeRecord> dependencyEdges, List<string> warnings, ref string? packagesLockFilePath)
    {
        packagesLockFilePath = null;
        string lockPath = Path.Combine(projectDirectory, "packages.lock.json");
        if (!File.Exists(lockPath))
        {
            return;
        }

        packagesLockFilePath = lockPath;
        ProjectAssetsResult lockResult = PackagesLockFileParser.Parse(lockPath, projectPath, solutionPath, packages);
        MergeResolvedPackages(packages, lockResult.Packages);
        dependencyEdges.AddRange(lockResult.DependencyEdges);
        warnings.AddRange(lockResult.Warnings);
    }

    private static void ApplyProjectAssetsFileIfPresent(string projectDirectory, string projectPath, string solutionPath, List<PackageReferenceRecord> packages, List<DependencyEdgeRecord> dependencyEdges, List<string> warnings, ref string? assetsFilePath, bool packagesLockFilePresent)
    {
        string candidatePath = Path.Combine(projectDirectory, "obj", "project.assets.json");
        if (!File.Exists(candidatePath))
        {
            assetsFilePath = null;
            if (!packagesLockFilePresent)
            {
                warnings.Add(
                    "Neither obj/project.assets.json nor packages.lock.json was found; the resolved package graph may be incomplete.");
            }

            return;
        }

        assetsFilePath = candidatePath;
        ProjectAssetsResult assetsResult = ProjectAssetsParser.ParseWithCapabilityFallbacks(candidatePath, projectPath, solutionPath, packages);

        MergeResolvedPackages(packages, assetsResult.Packages);
        dependencyEdges.AddRange(assetsResult.DependencyEdges);
        warnings.AddRange(assetsResult.Warnings);
    }

    private static AnalysisStatus DeriveProjectStatus(ProjectStyle projectStyle, IReadOnlyList<PackageReferenceRecord> packages, IReadOnlyList<string> warnings, XDocument projectDocument)
    {
        if (projectStyle == ProjectStyle.Unknown && packages.Count == 0 && IsMsBuildProjectDocument(projectDocument) && warnings.Count == 0)
        {
            return AnalysisStatus.Complete;
        }

        return warnings.Count == 0 ? AnalysisStatus.Complete : AnalysisStatus.Partial;
    }

    private static bool IsMsBuildProjectDocument(XDocument projectDocument)
    {
        return string.Equals(projectDocument.Root?.Name.LocalName, "Project", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the target framework declaration from a project file.
    /// </summary>
    /// <param name="projectDocument">The loaded project document.</param>
    /// <returns>The declared target framework string, if any.</returns>
    private static string? ReadTargetFrameworks(XDocument projectDocument)
    {
        return TargetFrameworkMonikerResolution.ReadBestMonikerFromProjectDocument(projectDocument);
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

    /// <summary>
    /// Merges resolved package information into existing direct package records.
    /// </summary>
    /// <param name="existingPackages">The current project package list.</param>
    /// <param name="resolvedPackages">The resolved packages parsed from the assets file.</param>
    private static void MergeResolvedPackages(List<PackageReferenceRecord> existingPackages, IReadOnlyList<PackageReferenceRecord> resolvedPackages)
    {
        Dictionary<string, PackageReferenceRecord> indexed = existingPackages.ToDictionary(static package => package.StablePackageInstanceKey, StringComparer.OrdinalIgnoreCase);

        foreach (PackageReferenceRecord resolved in resolvedPackages)
        {
            if (indexed.TryGetValue(resolved.StablePackageInstanceKey, out PackageReferenceRecord? current))
            {
                int index = existingPackages.IndexOf(current);
                existingPackages[index] = current with
                {
                    ResolvedVersion = resolved.ResolvedVersion ?? current.ResolvedVersion,
                    DependencyParents = resolved.DependencyParents,
                    DependencyPath = resolved.DependencyPath
                };
                continue;
            }

            existingPackages.Add(resolved);
        }
    }

    /// <summary>
    /// Creates a stable project identifier from the project path.
    /// </summary>
    /// <param name="projectPath">The project path.</param>
    /// <returns>The project identifier.</returns>
    private static string CreateProjectId(string projectPath)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(projectPath.ToUpperInvariant()));
        return Convert.ToHexString(bytes[..8]);
    }
}
