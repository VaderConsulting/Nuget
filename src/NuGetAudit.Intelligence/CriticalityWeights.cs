namespace NuGetAudit.Intelligence;

/// <summary>
/// Default importance of each <see cref="CriticalityFactor"/> when turning raw 0–10 ratings into an overall score.
/// </summary>
/// <remarks>
/// <para>
/// Each weight multiplies one raw score. The default weights sum to 100 so that “everything at 10” would yield a
/// weighted sum of 1000 before the calculator divides by 10 (see <see cref="CriticalityCalculator"/>).
/// You can supply a custom instance to <see cref="CriticalityCalculator.Calculate"/> to experiment with sensitivity.
/// </para>
/// </remarks>
public sealed class CriticalityWeights
{
    /// <summary>
    /// Gets or sets the weight for <see cref="CriticalityFactor.VulnerabilitySeverity"/> (default 20).
    /// </summary>
    public double VulnerabilitySeverity { get; init; } = 20d;

    /// <summary>
    /// Gets or sets the weight for <see cref="CriticalityFactor.Exploitability"/> (default 15).
    /// </summary>
    public double Exploitability { get; init; } = 15d;

    /// <summary>
    /// Gets or sets the weight for <see cref="CriticalityFactor.DependencyOwnership"/> (default 8).
    /// </summary>
    public double DependencyOwnership { get; init; } = 8d;

    /// <summary>
    /// Gets or sets the weight for <see cref="CriticalityFactor.Reach"/> (default 12).
    /// </summary>
    public double Reach { get; init; } = 12d;

    /// <summary>
    /// Gets or sets the weight for <see cref="CriticalityFactor.ProductionRelevance"/> (default 10).
    /// </summary>
    public double ProductionRelevance { get; init; } = 10d;

    /// <summary>
    /// Gets or sets the weight for <see cref="CriticalityFactor.LifecycleState"/> (default 8).
    /// </summary>
    public double LifecycleState { get; init; } = 8d;

    /// <summary>
    /// Gets or sets the weight for <see cref="CriticalityFactor.RemediationDifficulty"/> (default 8).
    /// </summary>
    public double RemediationDifficulty { get; init; } = 8d;

    /// <summary>
    /// Gets or sets the weight for <see cref="CriticalityFactor.Age"/> (default 6).
    /// </summary>
    public double Age { get; init; } = 6d;

    /// <summary>
    /// Gets or sets the weight for <see cref="CriticalityFactor.FixAvailability"/> (default 7).
    /// </summary>
    public double FixAvailability { get; init; } = 7d;

    /// <summary>
    /// Gets or sets the weight for <see cref="CriticalityFactor.IntroductionRecency"/> (default 6).
    /// </summary>
    public double IntroductionRecency { get; init; } = 6d;
}
