using NuGetAudit.Intelligence;

namespace NuGetAudit.Core;

/// <summary>
/// Translates package health, remediation hints, and snapshot context into the ten 0–10 inputs
/// required by <see cref="CriticalityCalculator.Calculate"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this is not:</strong> It does not compute CVSS vectors, EPSS, or official CVE scores.
/// NuGet.org exposes simple severity labels (for example “Moderate”) on advisories; those labels are mapped to numbers here.
/// The goal is a consistent internal priority signal for triage, not a legal or compliance attestation.
/// </para>
/// <para>
/// Callers typically invoke <see cref="Assess"/> from <see cref="PackageKnowledgeTimelineBuilder.Merge"/> whenever
/// package knowledge is created or refreshed for a resolved version.
/// </para>
/// </remarks>
internal static class PackageCriticalityAssessor
{
    /// <summary>
    /// Builds a <see cref="CriticalityScorecard"/> from live audit inputs and returns the calculated assessment.
    /// </summary>
    /// <param name="healthInfo">
    /// NuGet.org-derived signals (vulnerabilities, deprecation, outdated, abandoned, removed) for this package version.
    /// </param>
    /// <param name="remediation">
    /// Best-known remediation hints (upgrade version, alternate package, free-text summary).
    /// </param>
    /// <param name="firstObservedUtc">
    /// When this package id and resolved version were first recorded in stored knowledge (used for <see cref="CriticalityFactor.Age"/>).
    /// </param>
    /// <param name="determinedUtc">
    /// When this assessment run occurred (paired with <paramref name="firstObservedUtc"/> for age scoring).
    /// </param>
    /// <param name="isDirectDependency">
    /// <see langword="true"/> if the reference is direct; <see langword="false"/> if transitive.
    /// </param>
    /// <param name="projectReach">
    /// Count of projects in the <em>current snapshot</em> that reference this package id and resolved version.
    /// </param>
    /// <param name="isProductionRelevant">
    /// Coarse flag: higher scores when the dependency is treated as production-relevant (see caller for heuristics).
    /// </param>
    /// <param name="isNewlyIntroduced">
    /// <see langword="true"/> the first time this package version appears in knowledge; <see langword="false"/> on later updates.
    /// </param>
    /// <returns>Risk score, alert score, bands, and per-factor explanations.</returns>
    public static CriticalityAssessment Assess(PackageHealthInfo healthInfo, PackageRemediationAdvice remediation, DateTimeOffset firstObservedUtc, DateTimeOffset determinedUtc, bool isDirectDependency, int projectReach, bool isProductionRelevant, bool isNewlyIntroduced)
    {
        CriticalityScorecard scorecard = new()
        {
            VulnerabilitySeverity = ScoreSeverity(healthInfo.MaxVulnerabilitySeverity),
            VulnerabilitySeverityExplanation = BuildSeverityExplanation(healthInfo),
            Exploitability = ScoreExploitability(healthInfo),
            ExploitabilityExplanation = healthInfo.IsVulnerable
                ? "Known vulnerability data exists, but exploitability evidence is limited to advisory severity and package health signals."
                : "No exploitability signals are currently known.",
            DependencyOwnership = isDirectDependency ? 8d : 4d,
            DependencyOwnershipExplanation = isDirectDependency
                ? "Package is referenced directly and is therefore explicitly owned by the consuming project."
                : "Package is transitive and inherited through the dependency graph.",
            Reach = ScoreReach(projectReach),
            ReachExplanation = $"Package knowledge currently applies across {projectReach} project instance(s).",
            ProductionRelevance = isProductionRelevant ? 8d : 3d,
            ProductionRelevanceExplanation = isProductionRelevant
                ? "Package appears in a production-relevant dependency path."
                : "Package appears to be lower-production-relevance or relevance is uncertain.",
            LifecycleState = ScoreLifecycle(healthInfo),
            LifecycleStateExplanation = BuildLifecycleExplanation(healthInfo),
            RemediationDifficulty = ScoreRemediationDifficulty(remediation),
            RemediationDifficultyExplanation = BuildRemediationDifficultyExplanation(remediation),
            Age = ScoreAge(firstObservedUtc, determinedUtc),
            AgeExplanation = $"Issue age is measured from first observation at {firstObservedUtc:O}.",
            FixAvailability = ScoreFixAvailability(remediation),
            FixAvailabilityExplanation = BuildFixAvailabilityExplanation(remediation),
            IntroductionRecency = isNewlyIntroduced ? 9d : 4d,
            IntroductionRecencyExplanation = isNewlyIntroduced
                ? "Issue was newly introduced in the current analysis window."
                : "Issue has existed across prior analyses."
        };

        return CriticalityCalculator.Calculate(scorecard);
    }

    /// <summary>
    /// Maps NuGet’s worst-known advisory severity for this version to a 0–10 raw score for
    /// <see cref="CriticalityFactor.VulnerabilitySeverity"/>.
    /// </summary>
    /// <param name="severity">The maximum <see cref="PackageVulnerabilitySeverity"/> across advisories.</param>
    /// <returns>0 for none, 2 low, 5 moderate, 8 high, 10 critical.</returns>
    private static double ScoreSeverity(PackageVulnerabilitySeverity severity) => severity switch
    {
        PackageVulnerabilitySeverity.None => 0d,
        PackageVulnerabilitySeverity.Low => 2d,
        PackageVulnerabilitySeverity.Moderate => 5d,
        PackageVulnerabilitySeverity.High => 8d,
        PackageVulnerabilitySeverity.Critical => 10d,
        _ => 0d
    };

    /// <summary>
    /// Assigns a secondary 0–10 “exploitability emphasis” score when vulnerabilities exist; otherwise zero.
    /// </summary>
    /// <remarks>
    /// This is not a formal exploit prediction; it mirrors severity in a slightly different scale for the alert formula.
    /// </remarks>
    /// <param name="healthInfo">Health data including <see cref="PackageHealthInfo.IsVulnerable"/> and max severity.</param>
    /// <returns>0 if not vulnerable; otherwise 2–8 by severity, or 3 as a fallback.</returns>
    private static double ScoreExploitability(PackageHealthInfo healthInfo)
    {
        if (!healthInfo.IsVulnerable)
        {
            return 0d;
        }

        return healthInfo.MaxVulnerabilitySeverity switch
        {
            PackageVulnerabilitySeverity.Critical => 8d,
            PackageVulnerabilitySeverity.High => 6d,
            PackageVulnerabilitySeverity.Moderate => 4d,
            PackageVulnerabilitySeverity.Low => 2d,
            _ => 3d
        };
    }

    /// <summary>
    /// Converts “how many projects use this package version in this solution snapshot” into a 0–10 reach score.
    /// </summary>
    /// <param name="projectReach">Non-negative project count from the current snapshot.</param>
    /// <returns>2 for one project, rising to 10 for nine or more.</returns>
    private static double ScoreReach(int projectReach)
    {
        if (projectReach <= 1) return 2d;
        if (projectReach == 2) return 4d;
        if (projectReach <= 4) return 6d;
        if (projectReach <= 8) return 8d;
        return 10d;
    }

    /// <summary>
    /// Maps lifecycle and package-health flags into a single 0–10 raw score (first matching rule wins).
    /// </summary>
    /// <param name="healthInfo">NuGet.org-enriched health for the resolved version.</param>
    /// <returns>Higher when removed, obsolete+vulnerable, obsolete, abandoned, deprecated, or outdated; 0 when clean.</returns>
    private static double ScoreLifecycle(PackageHealthInfo healthInfo)
    {
        if (healthInfo.DevelopmentStatus == PackageDevelopmentStatus.Removed) return 10d;
        if (healthInfo.IsObsolete && healthInfo.IsVulnerable) return 10d;
        if (healthInfo.IsObsolete) return 8d;
        if (healthInfo.DevelopmentStatus == PackageDevelopmentStatus.Abandoned) return 7d;
        if (healthInfo.IsDeprecated) return 6d;
        if (healthInfo.IsOutdated) return 4d;
        return 0d;
    }

    /// <summary>
    /// Estimates remediation difficulty from which remediation fields are populated.
    /// </summary>
    /// <param name="remediation">Suggested upgrade, alternate package, and/or summary text.</param>
    /// <returns>9 when migration to an alternate package is indicated; 3 for a simple recommended version; 6 for summary-only; 7 default.</returns>
    private static double ScoreRemediationDifficulty(PackageRemediationAdvice remediation)
    {
        if (!string.IsNullOrWhiteSpace(remediation.AlternatePackageId)) return 9d;
        if (!string.IsNullOrWhiteSpace(remediation.RecommendedVersion)) return 3d;
        if (!string.IsNullOrWhiteSpace(remediation.Summary)) return 6d;
        return 7d;
    }

    /// <summary>
    /// Maps elapsed calendar time between first observation and this determination to a 0–10 age score.
    /// </summary>
    /// <param name="firstObservedUtc">When the package version was first stored in knowledge.</param>
    /// <param name="determinedUtc">Current audit timestamp.</param>
    /// <returns>Higher when the issue has been known longer.</returns>
    private static double ScoreAge(DateTimeOffset firstObservedUtc, DateTimeOffset determinedUtc)
    {
        double ageDays = Math.Max(0d, (determinedUtc - firstObservedUtc).TotalDays);
        if (ageDays < 1d) return 2d;
        if (ageDays < 7d) return 4d;
        if (ageDays < 30d) return 6d;
        if (ageDays < 90d) return 8d;
        return 10d;
    }

    /// <summary>
    /// Scores how actionable a fix is: known version bump vs migration vs narrative-only vs none.
    /// </summary>
    /// <param name="remediation">Structured remediation advice derived from health metadata.</param>
    /// <returns>9 recommended version, 8 alternate package, 6 summary only, 3 otherwise.</returns>
    private static double ScoreFixAvailability(PackageRemediationAdvice remediation)
    {
        if (!string.IsNullOrWhiteSpace(remediation.RecommendedVersion)) return 9d;
        if (!string.IsNullOrWhiteSpace(remediation.AlternatePackageId)) return 8d;
        if (!string.IsNullOrWhiteSpace(remediation.Summary)) return 6d;
        return 3d;
    }

    /// <summary>
    /// Builds the human-readable note for vulnerability severity on the scorecard.
    /// </summary>
    /// <param name="healthInfo">Package health including vulnerability flags.</param>
    /// <returns>Explanation string stored on the assessment factors.</returns>
    private static string BuildSeverityExplanation(PackageHealthInfo healthInfo)
        => healthInfo.IsVulnerable
            ? $"Highest known vulnerability severity is {healthInfo.MaxVulnerabilitySeverity}."
            : "No known vulnerability severity is currently recorded.";

    /// <summary>
    /// Builds the human-readable note for lifecycle scoring.
    /// </summary>
    /// <param name="healthInfo">Package health including lifecycle flags.</param>
    /// <returns>Explanation string stored on the assessment factors.</returns>
    private static string BuildLifecycleExplanation(PackageHealthInfo healthInfo)
    {
        if (healthInfo.DevelopmentStatus == PackageDevelopmentStatus.Removed)
        {
            return "Package id was not found on the NuGet.org registration feed.";
        }

        if (healthInfo.IsObsolete) return "Package is marked obsolete.";
        if (healthInfo.IsDeprecated) return "Package is marked deprecated.";
        if (healthInfo.IsOutdated) return $"Package has a newer stable version available: {healthInfo.LatestStableVersion ?? "unknown"}.";
        if (healthInfo.DevelopmentStatus == PackageDevelopmentStatus.Abandoned)
        {
            return "Latest compatible stable version was published more than a year ago on NuGet.org.";
        }

        return "No lifecycle warning is currently known.";
    }

    /// <summary>
    /// Builds the human-readable note for remediation difficulty.
    /// </summary>
    /// <param name="remediation">Remediation advice shown to users.</param>
    /// <returns>Explanation string stored on the assessment factors.</returns>
    private static string BuildRemediationDifficultyExplanation(PackageRemediationAdvice remediation)
    {
        if (!string.IsNullOrWhiteSpace(remediation.AlternatePackageId))
        {
            return $"Remediation requires migration to alternate package {remediation.AlternatePackageId}.";
        }

        if (!string.IsNullOrWhiteSpace(remediation.RecommendedVersion))
        {
            return $"Remediation appears to be a version upgrade to {remediation.RecommendedVersion}.";
        }

        return "Remediation path is unclear or may require manual investigation.";
    }

    /// <summary>
    /// Builds the human-readable note for fix availability.
    /// </summary>
    /// <param name="remediation">Remediation advice shown to users.</param>
    /// <returns>Explanation string stored on the assessment factors.</returns>
    private static string BuildFixAvailabilityExplanation(PackageRemediationAdvice remediation)
    {
        if (!string.IsNullOrWhiteSpace(remediation.RecommendedVersion))
        {
            return $"A recommended fix version is known: {remediation.RecommendedVersion}.";
        }

        if (!string.IsNullOrWhiteSpace(remediation.AlternatePackageId))
        {
            return $"A migration path is known through alternate package {remediation.AlternatePackageId}.";
        }

        return "No concrete fix version is currently known.";
    }
}
