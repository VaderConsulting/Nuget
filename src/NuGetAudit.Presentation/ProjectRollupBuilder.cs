namespace NuGetAudit.Presentation;

/// <summary>
/// Rollup of health, risk, and alert signals for all package instances in a project, for tree and summary display.
/// </summary>
internal sealed class ProjectRollup
{
    internal ProjectRollup(string healthSummary, string riskSummary, string alertSummary, string rowVisualBand, int remediationPackageCount, int totalPackageCount)
    {
        HealthSummary = healthSummary;
        RiskSummary = riskSummary;
        AlertSummary = alertSummary;
        RowVisualBand = rowVisualBand;
        RemediationPackageCount = remediationPackageCount;
        TotalPackageCount = totalPackageCount;
    }

    internal string HealthSummary { get; }
    internal string RiskSummary { get; }
    internal string AlertSummary { get; }
    internal string RowVisualBand { get; }
    internal int RemediationPackageCount { get; }
    internal int TotalPackageCount { get; }
}

/// <summary>
/// Derives project-level health/risk/alert from package instances: at minimum the worst single-package row band,
/// with escalation when many instances need remediation.
/// </summary>
internal static class ProjectRollupBuilder
{
    public static ProjectRollup Compute(IReadOnlyList<SurfacePackageReference> packages)
    {
        int total = packages.Count;
        if (total == 0)
        {
            return new ProjectRollup("ok", "-", "-", "None", 0, 0);
        }

        int remediationCount = 0;
        int maxRowOrdinal = 0;
        string worstRowBand = "None";
        string worstHealthSummary = "ok";
        int worstRiskOrder = -1;
        string? worstRiskBand = null;
        double? maxRiskScore = null;
        int worstAlertOrder = -1;
        string? worstAlertBand = null;
        double? maxAlertScore = null;

        foreach (SurfacePackageReference package in packages)
        {
            string health = PackageHealthSummary.Describe(package.HealthInfo);
            string rowBand = PackageHealthRowVisualBand.Compute(package.RiskBand, package.AlertBand, health);
            int rowOrd = VisualBandOrdinal(rowBand);
            if (rowOrd > maxRowOrdinal)
            {
                maxRowOrdinal = rowOrd;
                worstRowBand = rowBand;
                worstHealthSummary = health;
            }

            if (CountsTowardRemediation(rowBand))
            {
                remediationCount++;
            }

            int riskOrder = CriticalityBandOrdinal(package.RiskBand);
            if (riskOrder > worstRiskOrder)
            {
                worstRiskOrder = riskOrder;
                worstRiskBand = package.RiskBand;
            }

            if (package.RiskScore.HasValue)
            {
                if (!maxRiskScore.HasValue || package.RiskScore.Value > maxRiskScore.Value)
                {
                    maxRiskScore = package.RiskScore;
                }
            }

            int alertOrder = CriticalityBandOrdinal(package.AlertBand);
            if (alertOrder > worstAlertOrder)
            {
                worstAlertOrder = alertOrder;
                worstAlertBand = package.AlertBand;
            }

            if (package.AlertScore.HasValue)
            {
                if (!maxAlertScore.HasValue || package.AlertScore.Value > maxAlertScore.Value)
                {
                    maxAlertScore = package.AlertScore;
                }
            }
        }

        string rowVisualBand = ApplyRemediationVolumeEscalation(worstRowBand, remediationCount);
        string healthSummary = BuildHealthSummaryLine(worstHealthSummary, remediationCount, total);
        string riskSummary = BuildScoreBandSummary(maxRiskScore, worstRiskBand, remediationCount, total);
        string alertSummary = BuildScoreBandSummary(maxAlertScore, worstAlertBand, remediationCount, total);

        return new ProjectRollup(healthSummary, riskSummary, alertSummary, rowVisualBand, remediationCount, total);
    }

    private static bool CountsTowardRemediation(string rowVisualBand)
    {
        return string.Equals(rowVisualBand, "Critical", StringComparison.OrdinalIgnoreCase)
            || string.Equals(rowVisualBand, "High", StringComparison.OrdinalIgnoreCase)
            || string.Equals(rowVisualBand, "Attention", StringComparison.OrdinalIgnoreCase);
    }

    private static int VisualBandOrdinal(string band)
    {
        if (string.Equals(band, "Critical", StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }

        if (string.Equals(band, "High", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (string.Equals(band, "Attention", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (string.Equals(band, "Ok", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 0;
    }

    private static int CriticalityBandOrdinal(string? band)
    {
        if (string.IsNullOrWhiteSpace(band))
        {
            return -1;
        }

        if (string.Equals(band, "Critical", StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }

        if (string.Equals(band, "High", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (string.Equals(band, "Medium", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (string.Equals(band, "Low", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Escalates the row band when many instances need remediation (never below the worst single instance).
    /// </summary>
    private static string ApplyRemediationVolumeEscalation(string worstPerInstanceBand, int remediationCount)
    {
        int b = VisualBandOrdinal(worstPerInstanceBand);
        if (b <= 0 || remediationCount <= 1)
        {
            return worstPerInstanceBand;
        }

        if (b == 2 && remediationCount >= 3)
        {
            return "High";
        }

        if (b == 3)
        {
            if (remediationCount >= 4)
            {
                return "Critical";
            }

            if (remediationCount >= 2)
            {
                return "Critical";
            }
        }

        return worstPerInstanceBand;
    }

    private static string BuildHealthSummaryLine(string worstHealthAmongWorstBand, int remediationCount, int total)
    {
        if (remediationCount == 0)
        {
            return "ok";
        }

        if (string.Equals(worstHealthAmongWorstBand, "ok", StringComparison.OrdinalIgnoreCase))
        {
            return $"needs attention ({remediationCount} of {total} package instance(s) need remediation)";
        }

        return $"{worstHealthAmongWorstBand} ({remediationCount} of {total} package instance(s) need remediation)";
    }

    private static string BuildScoreBandSummary(double? maxScore, string? worstBand, int remediationCount, int total)
    {
        string band = string.IsNullOrWhiteSpace(worstBand) ? "Unknown" : worstBand!;
        string core = maxScore.HasValue
            ? $"{maxScore.Value:N2} ({band})"
            : $"- ({band})";
        if (remediationCount == 0)
        {
            return core;
        }

        return $"{core} · {remediationCount}/{total} instance(s) to remediate";
    }
}
