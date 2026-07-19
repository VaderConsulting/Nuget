namespace NuGetAudit.Core;

/// <summary>
/// Merges newly observed package health into the persisted package-knowledge timeline.
/// </summary>
/// <remarks>
/// Each stored package version gets a <see cref="CriticalityAssessment"/> by calling
/// <see cref="PackageCriticalityAssessor.Assess"/> whenever knowledge is first created or refreshed.
/// </remarks>
internal static class PackageKnowledgeTimelineBuilder
{
    /// <summary>
    /// Updates or creates <see cref="PackageKnowledgeRecord"/> entries for every resolved package version in the snapshot
    /// and recalculates <see cref="CriticalityAssessment"/> using current health and remediation.
    /// </summary>
    /// <param name="previousKnowledge">Prior knowledge from SQLite, or <see langword="null"/> on first run.</param>
    /// <param name="currentSnapshot">The solution snapshot produced by the latest audit.</param>
    /// <returns>The merged snapshot and a list of notable changes for reporting.</returns>
    public static (PackageKnowledgeSnapshot Snapshot, IReadOnlyList<PackageKnowledgeChangeRecord> Changes) Merge(PackageKnowledgeSnapshot? previousKnowledge, SolutionSnapshot currentSnapshot)
    {
        Dictionary<string, PackageKnowledgeRecord> existing = (previousKnowledge?.Packages ?? Array.Empty<PackageKnowledgeRecord>())
            .ToDictionary(CreateKey, StringComparer.OrdinalIgnoreCase);
        List<PackageKnowledgeChangeRecord> changes = new();

        foreach (PackageReferenceRecord package in currentSnapshot.Projects.SelectMany(static project => project.Packages))
        {
            string resolvedVersion = package.ResolvedVersion ?? package.RequestedVersion ?? string.Empty;
            if (string.IsNullOrWhiteSpace(resolvedVersion))
            {
                continue;
            }

            string key = CreateKey(package.PackageId, resolvedVersion);
            PackageRemediationAdvice remediation = BuildRemediation(package.HealthInfo);
            int projectReach = currentSnapshot.Projects.Count(project => project.Packages.Any(existingPackage =>
                string.Equals(existingPackage.PackageId, package.PackageId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existingPackage.ResolvedVersion ?? existingPackage.RequestedVersion, resolvedVersion, StringComparison.OrdinalIgnoreCase)));
            bool isProductionRelevant = !string.Equals(package.TargetFrameworkMoniker, "test", StringComparison.OrdinalIgnoreCase);

            if (!existing.TryGetValue(key, out PackageKnowledgeRecord? previous))
            {
                var criticality = PackageCriticalityAssessor.Assess(
                    package.HealthInfo,
                    remediation,
                    currentSnapshot.CapturedUtc,
                    currentSnapshot.CapturedUtc,
                    package.ReferenceKind == PackageReferenceKind.Direct,
                    projectReach,
                    isProductionRelevant,
                    true);

                PackageKnowledgeRecord created = new PackageKnowledgeRecord(package.PackageId, resolvedVersion, currentSnapshot.CapturedUtc, currentSnapshot.CapturedUtc, package.HealthInfo.RequiresAttention ? currentSnapshot.CapturedUtc : null, package.HealthInfo.RequiresAttention ? currentSnapshot.CapturedUtc : null, currentSnapshot.CapturedUtc, package.HealthInfo, remediation, criticality);

                existing[key] = created;

                if (package.HealthInfo.RequiresAttention)
                {
                    changes.Add(new PackageKnowledgeChangeRecord(package.PackageId, resolvedVersion, currentSnapshot.CapturedUtc, null, package.HealthInfo, remediation, "Attention is now required for this package version."));
                }

                continue;
            }

            bool healthChanged = !AreHealthInfosEqual(previous.LatestKnownHealth, package.HealthInfo);
            bool remediationChanged = !Equals(previous.Remediation, remediation);
            var updatedCriticality = PackageCriticalityAssessor.Assess(
                package.HealthInfo,
                remediation,
                previous.FirstObservedUtc,
                currentSnapshot.CapturedUtc,
                package.ReferenceKind == PackageReferenceKind.Direct,
                projectReach,
                isProductionRelevant,
                false);

            PackageKnowledgeRecord updated = previous with
            {
                LastObservedUtc = currentSnapshot.CapturedUtc,
                FirstRequiresAttentionUtc = previous.FirstRequiresAttentionUtc ?? (package.HealthInfo.RequiresAttention ? currentSnapshot.CapturedUtc : null),
                LatestStatusChangedUtc = healthChanged || remediationChanged ? currentSnapshot.CapturedUtc : previous.LatestStatusChangedUtc,
                LatestDeterminedUtc = currentSnapshot.CapturedUtc,
                LatestKnownHealth = package.HealthInfo,
                Remediation = remediation,
                Criticality = updatedCriticality
            };

            existing[key] = updated;

            if (healthChanged || remediationChanged)
            {
                changes.Add(new PackageKnowledgeChangeRecord(package.PackageId, resolvedVersion, currentSnapshot.CapturedUtc, previous.LatestKnownHealth, package.HealthInfo, remediation, BuildChangeSummary(previous.LatestKnownHealth, package.HealthInfo, remediationChanged)));
            }
        }

        PackageKnowledgeSnapshot snapshot = new PackageKnowledgeSnapshot(currentSnapshot.CapturedUtc, existing.Values
                .OrderBy(static record => record.PackageId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static record => record.ResolvedVersion, StringComparer.OrdinalIgnoreCase)
                .ToArray());

        return (snapshot, changes
            .OrderByDescending(static change => change.DeterminedUtc)
            .ThenBy(static change => change.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static change => change.ResolvedVersion, StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    private static string CreateKey(PackageKnowledgeRecord record) => CreateKey(record.PackageId, record.ResolvedVersion);

    private static string CreateKey(string packageId, string resolvedVersion) => $"{packageId}|{resolvedVersion}".ToUpperInvariant();

    private static PackageRemediationAdvice BuildRemediation(PackageHealthInfo healthInfo)
    {
        string? recommendedVersion = healthInfo.IsOutdated ? healthInfo.LatestStableVersion : null;
        string? summary = null;

        if (!string.IsNullOrWhiteSpace(healthInfo.AlternatePackageId))
        {
            summary = $"Migrate to {healthInfo.AlternatePackageId}{(string.IsNullOrWhiteSpace(healthInfo.AlternatePackageRange) ? string.Empty : $" {healthInfo.AlternatePackageRange}")}.";
        }
        else if (!string.IsNullOrWhiteSpace(recommendedVersion))
        {
            summary = $"Upgrade to {recommendedVersion}.";
        }
        else if (healthInfo.DevelopmentStatus == PackageDevelopmentStatus.Removed)
        {
            summary = "Package id was not found on nuget.org; confirm private feeds or a replacement package.";
        }
        else if (healthInfo.DevelopmentStatus == PackageDevelopmentStatus.Abandoned)
        {
            summary = "Latest compatible stable release is more than a year old on nuget.org; evaluate maintenance and alternatives.";
        }
        else if (!string.IsNullOrWhiteSpace(healthInfo.DeprecationMessage))
        {
            summary = healthInfo.DeprecationMessage;
        }

        return new PackageRemediationAdvice(recommendedVersion, healthInfo.AlternatePackageId, healthInfo.AlternatePackageRange, summary);
    }

    private static string BuildChangeSummary(PackageHealthInfo previousHealth, PackageHealthInfo currentHealth, bool remediationChanged)
    {
        List<string> changes = new();

        if (previousHealth.IsVulnerable != currentHealth.IsVulnerable)
        {
            changes.Add(currentHealth.IsVulnerable ? "Package version is now known to be vulnerable." : "Package version is no longer marked vulnerable.");
        }

        if (previousHealth.IsDeprecated != currentHealth.IsDeprecated)
        {
            changes.Add(currentHealth.IsDeprecated ? "Package version is now deprecated." : "Package version is no longer marked deprecated.");
        }

        if (previousHealth.IsObsolete != currentHealth.IsObsolete)
        {
            changes.Add(currentHealth.IsObsolete ? "Package version is now marked obsolete." : "Package version is no longer marked obsolete.");
        }

        if (previousHealth.IsOutdated != currentHealth.IsOutdated
            || !string.Equals(previousHealth.LatestStableVersion, currentHealth.LatestStableVersion, StringComparison.OrdinalIgnoreCase))
        {
            changes.Add(currentHealth.IsOutdated
                ? $"Newer stable version available: {currentHealth.LatestStableVersion ?? "unknown"}."
                : "No newer stable version is currently required.");
        }

        if (previousHealth.DevelopmentStatus != currentHealth.DevelopmentStatus)
        {
            changes.Add($"Development status is now {currentHealth.DevelopmentStatus}.");
        }

        if (remediationChanged)
        {
            changes.Add("Remediation guidance changed.");
        }

        return changes.Count == 0
            ? "Package knowledge changed."
            : string.Join(" ", changes);
    }

    private static bool AreHealthInfosEqual(PackageHealthInfo left, PackageHealthInfo right)
    {
        return left.IsDeprecated == right.IsDeprecated
               && left.IsObsolete == right.IsObsolete
               && left.IsOutdated == right.IsOutdated
               && left.IsVulnerable == right.IsVulnerable
               && left.DevelopmentStatus == right.DevelopmentStatus
               && string.Equals(left.LatestStableVersion, right.LatestStableVersion, StringComparison.OrdinalIgnoreCase)
               && string.Equals(left.DeprecationMessage, right.DeprecationMessage, StringComparison.Ordinal)
               && string.Equals(left.AlternatePackageId, right.AlternatePackageId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(left.AlternatePackageRange, right.AlternatePackageRange, StringComparison.OrdinalIgnoreCase)
               && left.MaxVulnerabilitySeverity == right.MaxVulnerabilitySeverity
               && left.Vulnerabilities
                   .OrderBy(static vulnerability => vulnerability.AdvisoryUrl, StringComparer.OrdinalIgnoreCase)
                   .ThenBy(static vulnerability => vulnerability.Severity)
                   .SequenceEqual(
                       right.Vulnerabilities
                           .OrderBy(static vulnerability => vulnerability.AdvisoryUrl, StringComparer.OrdinalIgnoreCase)
                           .ThenBy(static vulnerability => vulnerability.Severity));
    }
}
