namespace NuGetAudit.Intelligence;

/// <summary>
/// Combines a filled <see cref="CriticalityScorecard"/> with <see cref="CriticalityWeights"/> to produce
/// <see cref="CriticalityAssessment"/> (risk score, alert score, and bands).
/// </summary>
/// <remarks>
/// <para>
/// This type does not call NuGet.org or read CVE databases. It only does arithmetic on numbers that
/// <c>NuGetAudit.Core.PackageCriticalityAssessor</c> already derived from audit context and feed metadata.
/// </para>
/// </remarks>
public static class CriticalityCalculator
{
    /// <summary>
    /// Computes risk and alert scores and assigns <see cref="CriticalityBand"/> values.
    /// </summary>
    /// <param name="scorecard">Ten raw factor scores (each conceptually 0–10) plus explanations.</param>
    /// <param name="weights">
    /// Optional custom weights; when <see langword="null"/>, <see cref="CriticalityWeights"/> defaults are used.
    /// </param>
    /// <returns>Rounded scores, bands, per-factor breakdown, and a one-line summary.</returns>
    public static CriticalityAssessment Calculate(CriticalityScorecard scorecard, CriticalityWeights? weights = null)
    {
        weights ??= new CriticalityWeights();

        FactorScore[] factors =
        [
            new(CriticalityFactor.VulnerabilitySeverity, Clamp(scorecard.VulnerabilitySeverity), weights.VulnerabilitySeverity, scorecard.VulnerabilitySeverityExplanation),
            new(CriticalityFactor.Exploitability, Clamp(scorecard.Exploitability), weights.Exploitability, scorecard.ExploitabilityExplanation),
            new(CriticalityFactor.DependencyOwnership, Clamp(scorecard.DependencyOwnership), weights.DependencyOwnership, scorecard.DependencyOwnershipExplanation),
            new(CriticalityFactor.Reach, Clamp(scorecard.Reach), weights.Reach, scorecard.ReachExplanation),
            new(CriticalityFactor.ProductionRelevance, Clamp(scorecard.ProductionRelevance), weights.ProductionRelevance, scorecard.ProductionRelevanceExplanation),
            new(CriticalityFactor.LifecycleState, Clamp(scorecard.LifecycleState), weights.LifecycleState, scorecard.LifecycleStateExplanation),
            new(CriticalityFactor.RemediationDifficulty, Clamp(scorecard.RemediationDifficulty), weights.RemediationDifficulty, scorecard.RemediationDifficultyExplanation),
            new(CriticalityFactor.Age, Clamp(scorecard.Age), weights.Age, scorecard.AgeExplanation),
            new(CriticalityFactor.FixAvailability, Clamp(scorecard.FixAvailability), weights.FixAvailability, scorecard.FixAvailabilityExplanation),
            new(CriticalityFactor.IntroductionRecency, Clamp(scorecard.IntroductionRecency), weights.IntroductionRecency, scorecard.IntroductionRecencyExplanation)
        ];

        double riskScore = Math.Round(factors.Sum(static factor => factor.RawScore * factor.Weight) / 10d, 2, MidpointRounding.AwayFromZero);
        double alertScore = Math.Round(CalculateAlertScore(factors), 2, MidpointRounding.AwayFromZero);

        return new CriticalityAssessment(riskScore, alertScore, ToBand(riskScore), ToBand(alertScore), factors, BuildSummary(riskScore, alertScore, factors));
    }

    /// <summary>
    /// Builds the alert score: same weighted base as risk, plus an extra boost from introduction recency,
    /// fix availability, and exploitability, capped at 100.
    /// </summary>
    /// <param name="factors">The clamped, weighted factor rows produced inside <see cref="Calculate"/>.</param>
    /// <returns>The alert score before final rounding in <see cref="Calculate"/>.</returns>
    private static double CalculateAlertScore(IReadOnlyList<FactorScore> factors)
    {
        double riskScore = factors.Sum(static factor => factor.RawScore * factor.Weight) / 10d;
        double introduction = factors.First(static factor => factor.Factor == CriticalityFactor.IntroductionRecency).RawScore;
        double fixAvailability = factors.First(static factor => factor.Factor == CriticalityFactor.FixAvailability).RawScore;
        double exploitability = factors.First(static factor => factor.Factor == CriticalityFactor.Exploitability).RawScore;

        double alertBoost = (introduction * 1.2d) + (fixAvailability * 0.7d) + (exploitability * 0.9d);
        return Math.Min(100d, riskScore + alertBoost);
    }

    /// <summary>
    /// Forces a factor value into the 0–10 range expected by the calculator.
    /// </summary>
    /// <param name="value">A raw score from the scorecard.</param>
    /// <returns><paramref name="value"/> limited to [0, 10].</returns>
    private static double Clamp(double value) => Math.Max(0d, Math.Min(10d, value));

    /// <summary>
    /// Maps a 0–100 style score to <see cref="CriticalityBand"/> using fixed thresholds.
    /// </summary>
    /// <param name="score">Typically <see cref="CriticalityAssessment.RiskScore"/> or <see cref="CriticalityAssessment.AlertScore"/>.</param>
    /// <returns>The matching band.</returns>
    private static CriticalityBand ToBand(double score) => score switch
    {
        < 25d => CriticalityBand.Low,
        < 50d => CriticalityBand.Medium,
        < 75d => CriticalityBand.High,
        _ => CriticalityBand.Critical
    };

    /// <summary>
    /// Picks the single factor with the largest <c>RawScore × Weight</c> product to explain the risk score.
    /// </summary>
    /// <param name="riskScore">The computed risk score.</param>
    /// <param name="alertScore">The computed alert score.</param>
    /// <param name="factors">All factor rows.</param>
    /// <returns>A short English sentence for logs or UI.</returns>
    private static string BuildSummary(double riskScore, double alertScore, IReadOnlyList<FactorScore> factors)
    {
        FactorScore primaryFactor = factors
            .OrderByDescending(factor => factor.RawScore * factor.Weight)
            .First();

        return $"Risk {riskScore:N2}, alert {alertScore:N2}. Primary driver: {primaryFactor.Factor} ({primaryFactor.Explanation}).";
    }
}
