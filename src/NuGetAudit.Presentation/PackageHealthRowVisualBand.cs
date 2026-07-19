namespace NuGetAudit.Presentation;

/// <summary>
/// Derives a coarse row style band from criticality and health summary text, aligned across the Issues grid, Explorer package table, and history views.
/// </summary>
public static class PackageHealthRowVisualBand
{
    public static string Compute(string? riskBand, string? alertBand, string currentHealth)
    {
        if (string.Equals(riskBand, "Critical", StringComparison.OrdinalIgnoreCase)
            || string.Equals(alertBand, "Critical", StringComparison.OrdinalIgnoreCase))
        {
            return "Critical";
        }

        if (string.Equals(riskBand, "High", StringComparison.OrdinalIgnoreCase)
            || string.Equals(alertBand, "High", StringComparison.OrdinalIgnoreCase))
        {
            return "High";
        }

        if (currentHealth.StartsWith("vulnerable", StringComparison.OrdinalIgnoreCase))
        {
            return "Critical";
        }

        if (currentHealth.Contains("obsolete", StringComparison.OrdinalIgnoreCase)
            || currentHealth.Contains("removed", StringComparison.OrdinalIgnoreCase))
        {
            return "Critical";
        }

        if (currentHealth.Contains("deprecated", StringComparison.OrdinalIgnoreCase)
            || currentHealth.Contains("outdated", StringComparison.OrdinalIgnoreCase)
            || currentHealth.Contains("abandoned", StringComparison.OrdinalIgnoreCase))
        {
            return "Attention";
        }

        if (string.Equals(currentHealth, "ok", StringComparison.OrdinalIgnoreCase))
        {
            return "Ok";
        }

        return "None";
    }
}
