namespace NuGetAudit.Presentation;

/// <summary>
/// Builds the same health summary string used by the Explorer package table and tooltips.
/// </summary>
internal static class PackageHealthSummary
{
    public static string Describe(SurfacePackageHealth health)
    {
        List<string> states = new();

        if (health.IsVulnerable)
        {
            states.Add($"vulnerable:{health.MaxVulnerabilitySeverity ?? "unknown"}");
        }

        if (health.IsObsolete)
        {
            states.Add("obsolete");
        }
        else if (health.IsDeprecated)
        {
            states.Add("deprecated");
        }

        if (health.IsOutdated)
        {
            states.Add($"outdated->{health.LatestStableVersion ?? "unknown"}");
        }

        if (string.Equals(health.DevelopmentStatus, "Removed", StringComparison.OrdinalIgnoreCase))
        {
            states.Add("removed");
        }
        else if (string.Equals(health.DevelopmentStatus, "Abandoned", StringComparison.OrdinalIgnoreCase))
        {
            states.Add("abandoned");
        }

        return states.Count == 0 ? "ok" : string.Join(", ", states);
    }
}
