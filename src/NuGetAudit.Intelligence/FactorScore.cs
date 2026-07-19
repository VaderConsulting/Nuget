namespace NuGetAudit.Intelligence;

/// <summary>
/// One factor’s contribution after combining a raw 0–10 rating with its weight.
/// </summary>
/// <param name="Factor">Which input dimension this row describes.</param>
/// <param name="RawScore">
/// The clamped 0–10 score before weighting (raw values are assigned in <c>NuGetAudit.Core.PackageCriticalityAssessor</c>).
/// </param>
/// <param name="Weight">
/// The multiplier from <see cref="CriticalityWeights"/>; higher means this factor moves the overall score more.
/// </param>
/// <param name="Explanation">Short human-readable reason for the raw score, for UI or reports.</param>
public sealed record FactorScore(CriticalityFactor Factor, double RawScore, double Weight, string Explanation);
