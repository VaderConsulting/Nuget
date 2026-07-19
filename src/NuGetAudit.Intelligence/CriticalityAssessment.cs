namespace NuGetAudit.Intelligence;

/// <summary>
/// The final outputs of criticality scoring: two numbers, their bands, per-factor detail, and a short narrative.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RiskScore"/> summarizes overall concern from all weighted factors.
/// <see cref="AlertScore"/> starts from the same base but adds extra emphasis on “act soon” signals
/// (new issues, known fixes, exploitability). Both are stored on package knowledge records for history and UI.
/// </para>
/// </remarks>
/// <param name="RiskScore">
/// Weighted combination of all factors, scaled to roughly 0–100 (see <see cref="CriticalityCalculator.Calculate"/>).
/// </param>
/// <param name="AlertScore">
/// Risk-oriented score with an additional boost from introduction recency, fix availability, and exploitability; capped at 100.
/// </param>
/// <param name="RiskBand">Coarse bucket for <paramref name="RiskScore"/>.</param>
/// <param name="AlertBand">Coarse bucket for <paramref name="AlertScore"/>.</param>
/// <param name="Factors">Per-factor raw scores, weights, and explanations for transparency.</param>
/// <param name="Summary">One-line overview naming the strongest driver of the risk score.</param>
public sealed record CriticalityAssessment(double RiskScore, double AlertScore, CriticalityBand RiskBand, CriticalityBand AlertBand, IReadOnlyList<FactorScore> Factors, string Summary);
