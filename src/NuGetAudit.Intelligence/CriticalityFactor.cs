namespace NuGetAudit.Intelligence;

/// <summary>
/// Names one of the ten inputs that are combined to produce risk and alert scores.
/// </summary>
/// <remarks>
/// <para>
/// Each factor is first rated on a small scale (0–10) from project context and NuGet.org metadata.
/// Those ratings are multiplied by weights (see <see cref="CriticalityWeights"/>) and fed into
/// <see cref="CriticalityCalculator"/>. The names are meant to be self-explanatory for dashboards;
/// they do not map one-to-one to formal security frameworks.
/// </para>
/// </remarks>
public enum CriticalityFactor
{
    /// <summary>
    /// How serious the worst known security issue is for this package version (from the feed’s severity label).
    /// </summary>
    VulnerabilitySeverity,

    /// <summary>
    /// A simplified “how worried should we be about real-world misuse” score derived from severity when a vulnerability exists.
    /// </summary>
    Exploitability,

    /// <summary>
    /// Whether the reference is direct (you chose it) or transitive (pulled in indirectly).
    /// </summary>
    DependencyOwnership,

    /// <summary>
    /// How many projects in the current solution snapshot use this same package id and resolved version.
    /// </summary>
    Reach,

    /// <summary>
    /// A coarse guess whether the dependency is likely to matter in production builds versus test-only contexts.
    /// </summary>
    ProductionRelevance,

    /// <summary>
    /// Deprecated, outdated, abandoned, removed, or otherwise “lifecycle” stress from NuGet.org registration data.
    /// </summary>
    LifecycleState,

    /// <summary>
    /// How hard remediation looks based on known upgrade or migration hints.
    /// </summary>
    RemediationDifficulty,

    /// <summary>
    /// How long the tool has been seeing this package version as an issue.
    /// </summary>
    Age,

    /// <summary>
    /// Whether a concrete fix (new version or alternate package) is already known.
    /// </summary>
    FixAvailability,

    /// <summary>
    /// Whether the problem is newly seen in this audit versus something that persisted across earlier runs.
    /// </summary>
    IntroductionRecency
}
