namespace NuGetAudit.Intelligence;

/// <summary>
/// Buckets a numeric criticality score into four coarse levels for display and filtering.
/// </summary>
/// <remarks>
/// <para>
/// Scores are produced on a continuous scale (roughly 0–100). Bands make that easier to read:
/// for example, "High" is easier to scan than "73.42". The cut-offs are fixed in
/// <see cref="CriticalityCalculator"/> and are not industry-standard CVSS tiers—they are specific to this tool.
/// </para>
/// </remarks>
public enum CriticalityBand
{
    /// <summary>
    /// Score is below 25 on the 0–100 scale used by <see cref="CriticalityCalculator"/>.
    /// </summary>
    Low = 0,

    /// <summary>
    /// Score is at least 25 and below 50.
    /// </summary>
    Medium = 1,

    /// <summary>
    /// Score is at least 50 and below 75.
    /// </summary>
    High = 2,

    /// <summary>
    /// Score is 75 or higher.
    /// </summary>
    Critical = 3
}
