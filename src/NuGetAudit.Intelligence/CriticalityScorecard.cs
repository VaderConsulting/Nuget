namespace NuGetAudit.Intelligence;

/// <summary>
/// The ten numeric inputs (each intended to be on a 0–10 scale) passed into <see cref="CriticalityCalculator.Calculate"/>.
/// </summary>
/// <remarks>
/// <para>
/// A “scorecard” is just a structured form: each property is one question (for example, “how severe is the worst known issue?”)
/// answered as a number. <see cref="CriticalityCalculator"/> clamps values to 0–10, multiplies by weights, and produces
/// <see cref="CriticalityAssessment"/>. Raw values are normally filled by <c>NuGetAudit.Core.PackageCriticalityAssessor</c>.
/// </para>
/// </remarks>
public sealed class CriticalityScorecard
{
    /// <summary>
    /// Gets how severe the worst known security advisory for this package version is considered (0 = none, 10 = worst label).
    /// </summary>
    public required double VulnerabilitySeverity { get; init; }

    /// <summary>
    /// Gets a separate “ease or impact of exploitation” style score when vulnerabilities exist; 0 when none.
    /// </summary>
    public required double Exploitability { get; init; }

    /// <summary>
    /// Gets whether the team explicitly referenced the package (higher) or only inherited it transitively (lower).
    /// </summary>
    public required double DependencyOwnership { get; init; }

    /// <summary>
    /// Gets how broadly this package version appears across projects in the snapshot (more projects → higher).
    /// </summary>
    public required double Reach { get; init; }

    /// <summary>
    /// Gets a coarse production-vs-test relevance score for the dependency context.
    /// </summary>
    public required double ProductionRelevance { get; init; }

    /// <summary>
    /// Gets lifecycle stress: deprecation, outdated, abandoned, removed, etc.
    /// </summary>
    public required double LifecycleState { get; init; }

    /// <summary>
    /// Gets how difficult remediation appears (migration vs simple upgrade vs unclear).
    /// </summary>
    public required double RemediationDifficulty { get; init; }

    /// <summary>
    /// Gets how long the issue has been observed in stored knowledge (longer → higher).
    /// </summary>
    public required double Age { get; init; }

    /// <summary>
    /// Gets how clear a fix path is (recommended version, alternate package, summary only, or none).
    /// </summary>
    public required double FixAvailability { get; init; }

    /// <summary>
    /// Gets whether the issue is newly introduced in this analysis window (higher) or ongoing (lower).
    /// </summary>
    public required double IntroductionRecency { get; init; }

    /// <summary>
    /// Gets the explanation paired with <see cref="VulnerabilitySeverity"/>.
    /// </summary>
    public string VulnerabilitySeverityExplanation { get; init; } = string.Empty;

    /// <summary>
    /// Gets the explanation paired with <see cref="Exploitability"/>.
    /// </summary>
    public string ExploitabilityExplanation { get; init; } = string.Empty;

    /// <summary>
    /// Gets the explanation paired with <see cref="DependencyOwnership"/>.
    /// </summary>
    public string DependencyOwnershipExplanation { get; init; } = string.Empty;

    /// <summary>
    /// Gets the explanation paired with <see cref="Reach"/>.
    /// </summary>
    public string ReachExplanation { get; init; } = string.Empty;

    /// <summary>
    /// Gets the explanation paired with <see cref="ProductionRelevance"/>.
    /// </summary>
    public string ProductionRelevanceExplanation { get; init; } = string.Empty;

    /// <summary>
    /// Gets the explanation paired with <see cref="LifecycleState"/>.
    /// </summary>
    public string LifecycleStateExplanation { get; init; } = string.Empty;

    /// <summary>
    /// Gets the explanation paired with <see cref="RemediationDifficulty"/>.
    /// </summary>
    public string RemediationDifficultyExplanation { get; init; } = string.Empty;

    /// <summary>
    /// Gets the explanation paired with <see cref="Age"/>.
    /// </summary>
    public string AgeExplanation { get; init; } = string.Empty;

    /// <summary>
    /// Gets the explanation paired with <see cref="FixAvailability"/>.
    /// </summary>
    public string FixAvailabilityExplanation { get; init; } = string.Empty;

    /// <summary>
    /// Gets the explanation paired with <see cref="IntroductionRecency"/>.
    /// </summary>
    public string IntroductionRecencyExplanation { get; init; } = string.Empty;
}
