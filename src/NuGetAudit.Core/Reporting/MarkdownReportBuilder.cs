using System.Text;

namespace NuGetAudit.Core.Reporting;

/// <summary>
/// Renders solution snapshots and deltas as Markdown reports.
/// </summary>
public static class MarkdownReportBuilder
{
    /// <summary>
    /// Builds a Markdown report for the supplied snapshot and delta.
    /// </summary>
    /// <param name="snapshot">The current solution snapshot.</param>
    /// <param name="delta">The delta relative to the previous snapshot.</param>
    /// <returns>The generated Markdown document.</returns>
    public static string Build(SolutionSnapshot snapshot, SnapshotDelta delta, PackageKnowledgeSnapshot knowledgeSnapshot)
    {
        StringBuilder builder = new();
        Dictionary<string, PackageKnowledgeRecord> knowledgeByKey = knowledgeSnapshot.Packages
            .ToDictionary(static record => CreateKnowledgeKey(record.PackageId, record.ResolvedVersion), StringComparer.OrdinalIgnoreCase);
        int projectCount = snapshot.Projects.Count;
        int packageCount = snapshot.Projects.Sum(static project => project.Packages.Count);
        int directCount = snapshot.Projects.Sum(static project => project.Packages.Count(static package => package.ReferenceKind == PackageReferenceKind.Direct));
        int transitiveCount = snapshot.Projects.Sum(static project => project.Packages.Count(static package => package.ReferenceKind == PackageReferenceKind.Transitive));
        int dependencyEdgeCount = snapshot.Projects.Sum(static project => project.DependencyEdges.Count);
        IReadOnlyList<AuditDiagnostic> diagnostics = snapshot.Projects.SelectMany(static project => project.Diagnostics).ToArray();
        int errorCount = diagnostics.Count(static diagnostic => diagnostic.Severity == AuditDiagnosticSeverity.Error);
        int warningCount = diagnostics.Count(static diagnostic => diagnostic.Severity == AuditDiagnosticSeverity.Warning);

        builder.AppendLine($"# NuGet Audit Report - {snapshot.Solution.SolutionName}");
        builder.AppendLine();
        builder.AppendLine($"- Snapshot ID: `{snapshot.SnapshotId}`");
        builder.AppendLine($"- Captured UTC: `{snapshot.CapturedUtc:O}`");
        builder.AppendLine($"- Solution: `{snapshot.Solution.SolutionPath}`");
        builder.AppendLine($"- Source identity key: `{snapshot.Solution.SourceIdentity.IdentityKey}`");
        builder.AppendLine($"- Source identity kind: `{snapshot.Solution.SourceIdentity.IdentityKind}`");
        builder.AppendLine($"- Source machine: `{snapshot.Solution.SourceIdentity.MachineName}`");
        builder.AppendLine($"- Repository root: `{snapshot.Solution.SourceIdentity.RepositoryRootPath}`");
        builder.AppendLine($"- Git remote: `{snapshot.Solution.SourceIdentity.GitRemoteUrl ?? "-"}`");
        builder.AppendLine($"- Analysis status: `{snapshot.AnalysisStatus}`");
        builder.AppendLine($"- Projects analysed: `{projectCount}`");
        builder.AppendLine($"- Packages discovered: `{packageCount}`");
        builder.AppendLine($"- Direct packages: `{directCount}`");
        builder.AppendLine($"- Transitive packages: `{transitiveCount}`");
        builder.AppendLine($"- Dependency edges: `{dependencyEdgeCount}`");
        builder.AppendLine($"- Error diagnostics: `{errorCount}`");
        builder.AppendLine($"- Warning diagnostics: `{warningCount}`");
        builder.AppendLine($"- Delta changed: `{delta.IsChanged}`");
        builder.AppendLine($"- Knowledge snapshot generated: `{knowledgeSnapshot.GeneratedUtc:O}`");
        builder.AppendLine();

        builder.AppendLine("## Attention Required");
        builder.AppendLine();

        if (diagnostics.Count == 0)
        {
            builder.AppendLine("No package diagnostics require attention.");
        }
        else
        {
            builder.AppendLine("| Severity | Project | Package | Message | .NET version |");
            builder.AppendLine("|---|---|---|---|---|");

            foreach (AuditDiagnostic diagnostic in diagnostics
                         .OrderByDescending(static diagnostic => diagnostic.Severity)
                         .ThenBy(static diagnostic => diagnostic.ProjectName, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(static diagnostic => diagnostic.PackageId, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append("| ")
                    .Append(diagnostic.Severity).Append(" | ")
                    .Append(Escape(diagnostic.ProjectName)).Append(" | ")
                    .Append(Escape(diagnostic.PackageId ?? "-")).Append(" | ")
                    .Append(Escape(diagnostic.Message)).Append(" | ")
                    .Append(Escape(diagnostic.TargetFrameworkMoniker ?? "-")).AppendLine(" |");
            }
        }

        builder.AppendLine();

        builder.AppendLine("## Change Summary");
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(delta.ComparisonNote))
        {
            builder.AppendLine(delta.ComparisonNote);
        }
        else if (string.IsNullOrWhiteSpace(delta.PreviousSnapshotId))
        {
            builder.AppendLine("No previous accepted snapshot was available for comparison.");
        }
        else if (delta.Changes.Count == 0)
        {
            builder.AppendLine("No package changes were detected relative to the previous accepted snapshot.");
        }
        else
        {
            builder.AppendLine("| Project | Package | Change | Previous | Current | .NET version |");
            builder.AppendLine("|---|---|---|---|---|---|");

            foreach (PackageChangeRecord change in delta.Changes)
            {
                builder.Append("| ")
                    .Append(Escape(change.ProjectName)).Append(" | ")
                    .Append(Escape(change.PackageId)).Append(" | ")
                    .Append(change.ChangeType).Append(" | ")
                    .Append(Escape(change.PreviousResolvedVersion ?? change.PreviousRequestedVersion ?? "-")).Append(" | ")
                    .Append(Escape(change.CurrentResolvedVersion ?? change.CurrentRequestedVersion ?? "-")).Append(" | ")
                    .Append(Escape(change.TargetFrameworkMoniker ?? "-")).AppendLine(" |");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Knowledge Changes");
        builder.AppendLine();

        if (delta.KnowledgeChanges.Count == 0)
        {
            builder.AppendLine("No newly determined package-knowledge changes were recorded during this analysis.");
        }
        else
        {
            builder.AppendLine("| Package | Version | Determined UTC | Previous Health | Current Health | Risk | Alert | Remediation | Summary |");
            builder.AppendLine("|---|---|---|---|---|---|---|---|---|");

            foreach (PackageKnowledgeChangeRecord change in delta.KnowledgeChanges)
            {
                knowledgeByKey.TryGetValue(CreateKnowledgeKey(change.PackageId, change.ResolvedVersion), out PackageKnowledgeRecord? knowledgeRecord);

                builder.Append("| ")
                    .Append(Escape(change.PackageId)).Append(" | ")
                    .Append(Escape(change.ResolvedVersion)).Append(" | ")
                    .Append(change.DeterminedUtc.ToString("O")).Append(" | ")
                    .Append(Escape(DescribeHealth(change.PreviousHealthInfo))).Append(" | ")
                    .Append(Escape(DescribeHealth(change.CurrentHealthInfo))).Append(" | ")
                    .Append(Escape(DescribeRisk(knowledgeRecord))).Append(" | ")
                    .Append(Escape(DescribeAlert(knowledgeRecord))).Append(" | ")
                    .Append(Escape(DescribeRemediation(change.Remediation))).Append(" | ")
                    .Append(Escape(change.Summary)).AppendLine(" |");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Security and Health Transitions");
        builder.AppendLine();

        IReadOnlyList<PackageChangeRecord> healthChanges = delta.Changes
            .Where(static change => change.PreviousHealthInfo is not null || change.CurrentHealthInfo is not null)
            .Where(static change => HasHealthTransition(change.PreviousHealthInfo, change.CurrentHealthInfo))
            .ToArray();

        if (healthChanges.Count == 0)
        {
            builder.AppendLine("No package health transitions were detected relative to the previous accepted snapshot.");
        }
        else
        {
            builder.AppendLine("| Project | Package | Previous Health | Current Health | Previous Vulnerabilities | Current Vulnerabilities | .NET version |");
            builder.AppendLine("|---|---|---|---|---|---|---|");

            foreach (PackageChangeRecord change in healthChanges)
            {
                builder.Append("| ")
                    .Append(Escape(change.ProjectName)).Append(" | ")
                    .Append(Escape(change.PackageId)).Append(" | ")
                    .Append(Escape(DescribeHealth(change.PreviousHealthInfo))).Append(" | ")
                    .Append(Escape(DescribeHealth(change.CurrentHealthInfo))).Append(" | ")
                    .Append(Escape(DescribeVulnerabilities(change.PreviousHealthInfo))).Append(" | ")
                    .Append(Escape(DescribeVulnerabilities(change.CurrentHealthInfo))).Append(" | ")
                    .Append(Escape(change.TargetFrameworkMoniker ?? "-")).AppendLine(" |");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Dependency Relationship Changes");
        builder.AppendLine();

        if (delta.DependencyEdgeChanges.Count == 0)
        {
            builder.AppendLine("No dependency edge changes were detected relative to the previous accepted snapshot.");
        }
        else
        {
            builder.AppendLine("| Project | From Package | To Package | Change | .NET version |");
            builder.AppendLine("|---|---|---|---|---|");

            foreach (DependencyEdgeChangeRecord change in delta.DependencyEdgeChanges)
            {
                builder.Append("| ")
                    .Append(Escape(change.ProjectName)).Append(" | ")
                    .Append(Escape(change.FromPackageId)).Append(" | ")
                    .Append(Escape(change.ToPackageId)).Append(" | ")
                    .Append(change.ChangeType).Append(" | ")
                    .Append(Escape(change.TargetFrameworkMoniker ?? "-")).AppendLine(" |");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Project Details");
        builder.AppendLine();

        foreach (ProjectSnapshot project in snapshot.Projects.OrderBy(static project => project.ProjectName, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"### {project.ProjectName}");
            builder.AppendLine();
            builder.AppendLine($"- Path: `{project.ProjectPath}`");
            builder.AppendLine($"- Style: `{project.ProjectStyle}`");
            builder.AppendLine($"- Load state: `{project.LoadState}`");
            builder.AppendLine($"- Analysis status: `{project.AnalysisStatus}`");

            if (!string.IsNullOrWhiteSpace(project.TargetFrameworks))
            {
                builder.AppendLine($"- .NET version: `{project.TargetFrameworks}`");
            }

            if (project.Warnings.Count > 0)
            {
                builder.AppendLine("- Warnings:");
                foreach (string warning in project.Warnings)
                {
                    builder.AppendLine($"  - {warning}");
                }
            }

            if (project.Diagnostics.Count > 0)
            {
                builder.AppendLine("- Diagnostics:");
                foreach (AuditDiagnostic diagnostic in project.Diagnostics)
                {
                    builder.AppendLine($"  - [{diagnostic.Severity}] {diagnostic.Message}");
                }
            }

            builder.AppendLine();

            if (project.Packages.Count == 0)
            {
                builder.AppendLine("No packages detected.");
                builder.AppendLine();
                continue;
            }

            builder.AppendLine("| Package | Kind | Requested | Resolved | Health | Vulnerability Detail | Risk | Alert | Knowledge Determined | Status Changed | Remediation | Central | Parents | Path | .NET version |");
            builder.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");

            foreach (PackageReferenceRecord package in project.Packages
                         .OrderBy(static package => package.PackageId, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(static package => package.TargetFrameworkMoniker, StringComparer.OrdinalIgnoreCase))
            {
                string resolvedVersion = package.ResolvedVersion ?? package.RequestedVersion ?? string.Empty;
                knowledgeByKey.TryGetValue(CreateKnowledgeKey(package.PackageId, resolvedVersion), out PackageKnowledgeRecord? knowledgeRecord);

                builder.Append("| ")
                    .Append(Escape(package.PackageId)).Append(" | ")
                    .Append(package.ReferenceKind).Append(" | ")
                    .Append(Escape(package.RequestedVersion ?? "-")).Append(" | ")
                    .Append(Escape(package.ResolvedVersion ?? "-")).Append(" | ")
                    .Append(Escape(DescribeHealth(package.HealthInfo))).Append(" | ")
                    .Append(Escape(DescribeVulnerabilities(package.HealthInfo))).Append(" | ")
                    .Append(Escape(DescribeRisk(knowledgeRecord))).Append(" | ")
                    .Append(Escape(DescribeAlert(knowledgeRecord))).Append(" | ")
                    .Append(Escape(knowledgeRecord?.LatestDeterminedUtc.ToString("O") ?? "-")).Append(" | ")
                    .Append(Escape(knowledgeRecord?.LatestStatusChangedUtc?.ToString("O") ?? "-")).Append(" | ")
                    .Append(Escape(DescribeRemediation(knowledgeRecord?.Remediation))).Append(" | ")
                    .Append(package.IsCentralVersionManaged ? "Yes" : "No").Append(" | ")
                    .Append(Escape(package.DependencyParents.Count == 0 ? "-" : string.Join(", ", package.DependencyParents))).Append(" | ")
                    .Append(Escape(package.DependencyPath.Count == 0 ? "-" : string.Join(" -> ", package.DependencyPath))).Append(" | ")
                    .Append(Escape(package.TargetFrameworkMoniker ?? "-")).AppendLine(" |");
            }

            builder.AppendLine();

            builder.AppendLine("#### Dependency Graph Table");
            builder.AppendLine();

            if (project.DependencyEdges.Count == 0)
            {
                builder.AppendLine("No dependency edges were captured for this project.");
            }
            else
            {
                builder.AppendLine("| From Package | To Package | .NET version |");
                builder.AppendLine("|---|---|---|");

                foreach (DependencyEdgeRecord edge in project.DependencyEdges
                             .OrderBy(static edge => edge.FromPackageId, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(static edge => edge.ToPackageId, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(static edge => edge.TargetFrameworkMoniker, StringComparer.OrdinalIgnoreCase))
                {
                    builder.Append("| ")
                        .Append(Escape(edge.FromPackageId)).Append(" | ")
                        .Append(Escape(edge.ToPackageId)).Append(" | ")
                        .Append(Escape(edge.TargetFrameworkMoniker ?? "-")).AppendLine(" |");
                }
            }

            builder.AppendLine();
            builder.AppendLine("#### Dependency Graph Mermaid");
            builder.AppendLine();
            builder.AppendLine("```mermaid");
            builder.AppendLine("graph TD");

            foreach (string line in BuildProjectGraph(project))
            {
                builder.AppendLine(line);
            }

            builder.AppendLine("```");
            builder.AppendLine();
        }

        if (snapshot.Warnings.Count > 0)
        {
            builder.AppendLine("## Snapshot Warnings");
            builder.AppendLine();
            foreach (string warning in snapshot.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Escapes Markdown table delimiters in cell content.
    /// </summary>
    /// <param name="value">The cell value to escape.</param>
    /// <returns>The escaped value.</returns>
    private static string Escape(string value)
    {
        return value.Replace("|", "\\|", StringComparison.Ordinal);
    }

    private static string DescribeHealth(PackageHealthInfo? healthInfo)
    {
        if (healthInfo is null)
        {
            return "-";
        }

        List<string> states = new();

        if (healthInfo.IsVulnerable)
        {
            states.Add($"vulnerable:{healthInfo.MaxVulnerabilitySeverity.ToString().ToLowerInvariant()}");
        }

        if (healthInfo.IsObsolete)
        {
            states.Add("obsolete");
        }
        else if (healthInfo.IsDeprecated)
        {
            states.Add("deprecated");
        }

        if (healthInfo.IsOutdated)
        {
            states.Add($"outdated->{healthInfo.LatestStableVersion}");
        }

        if (healthInfo.DevelopmentStatus == PackageDevelopmentStatus.Removed)
        {
            states.Add("removed");
        }
        else if (healthInfo.DevelopmentStatus == PackageDevelopmentStatus.Abandoned)
        {
            states.Add("abandoned");
        }

        return states.Count == 0 ? "ok" : string.Join(", ", states);
    }

    private static string DescribeVulnerabilities(PackageHealthInfo? healthInfo)
    {
        if (healthInfo is null || healthInfo.Vulnerabilities.Count == 0)
        {
            return "-";
        }

        return string.Join("; ", healthInfo.Vulnerabilities
            .OrderBy(static vulnerability => vulnerability.Severity)
            .ThenBy(static vulnerability => vulnerability.AdvisoryUrl, StringComparer.OrdinalIgnoreCase)
            .Select(static vulnerability => $"{vulnerability.Severity}:{vulnerability.AdvisoryUrl}"));
    }

    private static string DescribeRemediation(PackageRemediationAdvice? remediation)
    {
        if (remediation is null)
        {
            return "-";
        }

        return remediation.Summary
               ?? remediation.RecommendedVersion
               ?? (!string.IsNullOrWhiteSpace(remediation.AlternatePackageId)
                   ? $"{remediation.AlternatePackageId} {remediation.AlternatePackageRange}".Trim()
                   : "-");
    }

    private static string DescribeRisk(PackageKnowledgeRecord? knowledgeRecord)
    {
        return knowledgeRecord is null
            ? "-"
            : $"{knowledgeRecord.Criticality.RiskScore:N2} ({knowledgeRecord.Criticality.RiskBand})";
    }

    private static string DescribeAlert(PackageKnowledgeRecord? knowledgeRecord)
    {
        return knowledgeRecord is null
            ? "-"
            : $"{knowledgeRecord.Criticality.AlertScore:N2} ({knowledgeRecord.Criticality.AlertBand})";
    }

    private static bool HasHealthTransition(PackageHealthInfo? previousHealth, PackageHealthInfo? currentHealth)
    {
        if (ReferenceEquals(previousHealth, currentHealth))
        {
            return false;
        }

        if (previousHealth is null || currentHealth is null)
        {
            return true;
        }

        return previousHealth.IsDeprecated != currentHealth.IsDeprecated
               || previousHealth.IsObsolete != currentHealth.IsObsolete
               || previousHealth.IsOutdated != currentHealth.IsOutdated
               || previousHealth.IsVulnerable != currentHealth.IsVulnerable
               || previousHealth.DevelopmentStatus != currentHealth.DevelopmentStatus
               || !string.Equals(previousHealth.LatestStableVersion, currentHealth.LatestStableVersion, StringComparison.OrdinalIgnoreCase)
               || !string.Equals(previousHealth.DeprecationMessage, currentHealth.DeprecationMessage, StringComparison.Ordinal)
               || !string.Equals(previousHealth.AlternatePackageId, currentHealth.AlternatePackageId, StringComparison.OrdinalIgnoreCase)
               || !string.Equals(previousHealth.AlternatePackageRange, currentHealth.AlternatePackageRange, StringComparison.OrdinalIgnoreCase)
               || previousHealth.MaxVulnerabilitySeverity != currentHealth.MaxVulnerabilitySeverity
               || !DescribeVulnerabilities(previousHealth).Equals(DescribeVulnerabilities(currentHealth), StringComparison.Ordinal);
    }

    private static IEnumerable<string> BuildProjectGraph(ProjectSnapshot project)
    {
        if (project.DependencyEdges.Count == 0)
        {
            yield return "    Empty[\"No dependency edges captured\"]";
            yield break;
        }

        foreach (PackageReferenceRecord package in project.Packages
                     .OrderBy(static package => package.PackageId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static package => package.TargetFrameworkMoniker, StringComparer.OrdinalIgnoreCase))
        {
            string nodeId = ToNodeId(package.StablePackageInstanceKey);
            string label = $"{package.PackageId}\\n{(package.ResolvedVersion ?? package.RequestedVersion ?? "unknown")}\\n{package.ReferenceKind}";
            yield return $"    {nodeId}[\"{EscapeMermaid(label)}\"]";
        }

        foreach (DependencyEdgeRecord edge in project.DependencyEdges
                     .OrderBy(static edge => edge.FromPackageId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static edge => edge.ToPackageId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static edge => edge.TargetFrameworkMoniker, StringComparer.OrdinalIgnoreCase))
        {
            yield return $"    {ToNodeId(edge.FromStablePackageInstanceKey)} --> {ToNodeId(edge.ToStablePackageInstanceKey)}";
        }
    }

    private static string ToNodeId(string value)
    {
        StringBuilder builder = new("N");

        foreach (char character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        return builder.ToString();
    }

    private static string EscapeMermaid(string value)
    {
        return value.Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static string CreateKnowledgeKey(string packageId, string resolvedVersion)
    {
        return $"{packageId}|{resolvedVersion}".ToUpperInvariant();
    }
}
