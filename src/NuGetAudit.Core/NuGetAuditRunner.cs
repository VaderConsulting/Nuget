using System.Security.Cryptography;
using System.Text;

namespace NuGetAudit.Core;

/// <summary>
/// Coordinates project discovery, analysis, delta calculation, and report persistence.
/// </summary>
public sealed class NuGetAuditRunner
{
    private readonly ProjectAnalyser _projectAnalyser = new();
    private readonly PackageHealthEnricher _packageHealthEnricher = new();
    private readonly SnapshotStore _snapshotStore = new();

    /// <summary>
    /// Runs the audit pipeline for the requested solution or project input.
    /// </summary>
    /// <param name="options">The run options that control inputs and outputs.</param>
    /// <param name="cancellationToken">A token that cancels the run.</param>
    /// <returns>The completed audit result.</returns>
    public async Task<AuditRunResult> RunAsync(AuditCommandOptions options, CancellationToken cancellationToken)
    {
        string solutionPath = Path.GetFullPath(options.SolutionPath);
        string outputDirectory = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);

        string solutionName = AuditProjectDisplayName.GetAuditInputDisplayName(solutionPath);
        SourceCodeIdentity sourceIdentity = SourceCodeIdentityResolver.Resolve(solutionPath);
        SolutionInfo solution = new SolutionInfo(solutionName, solutionPath, sourceIdentity.IdentityKey, sourceIdentity);

        BuildInputFormatLogging.LogSolutionOrInputContainer(solutionName, solutionPath);

        IReadOnlyList<ProjectDescriptor> projectDescriptors = SolutionDiscovery.DiscoverProjects(solutionPath);
        List<ProjectSnapshot> projects = new();

        foreach (ProjectDescriptor descriptor in projectDescriptors.OrderBy(static project => project.ProjectPath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            projects.Add(_projectAnalyser.Analyse(descriptor, solutionPath));
        }

        PackageHealthEnricher.PackageHealthEnrichmentResult healthResult = await _packageHealthEnricher.EnrichAsync(projects, cancellationToken);
        projects = healthResult.Projects.ToList();

        List<string> warnings = projects.SelectMany(static project => project.Warnings)
            .Concat(healthResult.Warnings)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static warning => warning, StringComparer.Ordinal)
            .ToList();

        IReadOnlyList<AuditDiagnostic> diagnostics = projects
            .SelectMany(static project => project.Diagnostics)
            .ToArray();

        int errorCount = diagnostics.Count(static diagnostic => diagnostic.Severity == AuditDiagnosticSeverity.Error);
        int warningCount = diagnostics.Count(static diagnostic => diagnostic.Severity == AuditDiagnosticSeverity.Warning);

        if (errorCount > 0)
        {
            warnings.Add($"Attention required: {errorCount} package diagnostics are errors and should appear in the Visual Studio Error List as errors.");
        }

        if (warningCount > 0)
        {
            warnings.Add($"Attention required: {warningCount} package diagnostics are warnings.");
        }

        DateTimeOffset capturedUtc = DateTimeOffset.UtcNow;
        string snapshotId = $"{capturedUtc:yyyyMMddHHmmss}_{Guid.NewGuid():N}"[..23];

        SolutionSnapshot currentSnapshot = new SolutionSnapshot(snapshotId, capturedUtc, ComputeContentHash(projects), DeriveStatus(projects), solution, projects, warnings);

        SolutionSnapshot? previousSnapshot = await _snapshotStore.LoadLatestAcceptedAsync(outputDirectory, cancellationToken);
        PackageKnowledgeSnapshot? previousKnowledge = await _snapshotStore.LoadLatestKnowledgeAsync(outputDirectory, cancellationToken);
        SnapshotDelta baseDelta = DeltaCalculator.Calculate(previousSnapshot, currentSnapshot);
        (PackageKnowledgeSnapshot knowledgeSnapshot, IReadOnlyList<PackageKnowledgeChangeRecord> knowledgeChanges) = PackageKnowledgeTimelineBuilder.Merge(previousKnowledge, currentSnapshot);
        SnapshotDelta delta = baseDelta with
        {
            KnowledgeChanges = knowledgeChanges
        };
        GeneratedReportSet reportSet = await _snapshotStore.WriteAsync(outputDirectory, currentSnapshot, delta, knowledgeSnapshot, options, cancellationToken);

        return new AuditRunResult(currentSnapshot, previousSnapshot, delta, knowledgeSnapshot, reportSet);
    }

    /// <summary>
    /// Computes a deterministic hash over the change-significant project and package fields.
    /// </summary>
    /// <param name="projects">The projects to include in the hash.</param>
    /// <returns>The content hash.</returns>
    private static string ComputeContentHash(IReadOnlyList<ProjectSnapshot> projects)
    {
        StringBuilder builder = new();

        foreach (ProjectSnapshot project in projects.OrderBy(static project => project.ProjectPath, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(project.ProjectPath.ToUpperInvariant()).Append('|')
                .Append(project.ProjectStyle).Append('|')
                .Append(project.AnalysisStatus).Append('|')
                .Append(project.AssetsFilePath ?? string.Empty).Append('|')
                .Append(project.PackagesLockFilePath ?? string.Empty).AppendLine();

            foreach (PackageReferenceRecord package in project.Packages
                         .OrderBy(static package => package.StablePackageInstanceKey, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append(package.StablePackageInstanceKey).Append('|')
                    .Append(package.RequestedVersion).Append('|')
                    .Append(package.ResolvedVersion).Append('|')
                    .Append(package.ReferenceKind).Append('|')
                    .Append(package.HealthInfo.IsDeprecated).Append('|')
                    .Append(package.HealthInfo.IsObsolete).Append('|')
                    .Append(package.HealthInfo.IsOutdated).Append('|')
                    .Append(package.HealthInfo.IsVulnerable).Append('|')
                    .Append(package.HealthInfo.LatestStableVersion).Append('|')
                    .Append(package.HealthInfo.MaxVulnerabilitySeverity).Append('|')
                    .Append(package.HealthInfo.DevelopmentStatus).AppendLine();
            }

            foreach (DependencyEdgeRecord edge in project.DependencyEdges
                         .OrderBy(static edge => edge.FromStablePackageInstanceKey, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(static edge => edge.ToStablePackageInstanceKey, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append(edge.FromStablePackageInstanceKey).Append("->")
                    .Append(edge.ToStablePackageInstanceKey).Append('|')
                    .Append(edge.TargetFrameworkMoniker).AppendLine();
            }
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Derives the overall solution analysis status from individual project results.
    /// </summary>
    /// <param name="projects">The analysed projects.</param>
    /// <returns>The aggregate analysis status.</returns>
    private static AnalysisStatus DeriveStatus(IReadOnlyList<ProjectSnapshot> projects)
    {
        if (projects.Count == 0)
        {
            return AnalysisStatus.Failed;
        }

        if (projects.Any(static project => project.AnalysisStatus == AnalysisStatus.Failed))
        {
            return AnalysisStatus.Partial;
        }

        if (projects.Any(static project => project.AnalysisStatus != AnalysisStatus.Complete))
        {
            return AnalysisStatus.Partial;
        }

        return AnalysisStatus.Complete;
    }
}
