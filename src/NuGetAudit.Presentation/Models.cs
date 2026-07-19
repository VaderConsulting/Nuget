namespace NuGetAudit.Presentation;

public sealed class PackageTableRow
{
    public PackageTableRow(string projectName, string packageId, string instanceKeyShort, string referenceKind, string requestedVersion, string resolvedVersion, string healthSummary, string riskSummary, string alertSummary, string remediationSummary, string parents, string path, string targetFrameworkMoniker, string stablePackageInstanceKey, string tooltip, string rowVisualBand)
    {
        ProjectName = projectName;
        PackageId = packageId;
        InstanceKeyShort = instanceKeyShort;
        ReferenceKind = referenceKind;
        RequestedVersion = requestedVersion;
        ResolvedVersion = resolvedVersion;
        VersionDisplay = FormatPackageVersionDisplay(requestedVersion, resolvedVersion);
        HealthSummary = healthSummary;
        RiskSummary = riskSummary;
        AlertSummary = alertSummary;
        RemediationSummary = remediationSummary;
        Parents = parents;
        Path = path;
        TargetFrameworkMoniker = targetFrameworkMoniker;
        TargetFrameworkMonikerDisplay = TargetFrameworkDisplay.FormatMergedRowMonikers(targetFrameworkMoniker);
        StablePackageInstanceKey = stablePackageInstanceKey;
        Tooltip = tooltip;
        RowVisualBand = rowVisualBand;
    }

    public string ProjectName { get; }
    public string PackageId { get; }
    public string InstanceKeyShort { get; }
    public string ReferenceKind { get; }
    public string RequestedVersion { get; }
    public string ResolvedVersion { get; }
    /// <summary>
    /// Resolved version for the grid; when it differs from requested, shows <c>resolved (requested)</c>.
    /// </summary>
    public string VersionDisplay { get; }
    public string HealthSummary { get; }
    public string RiskSummary { get; }
    public string AlertSummary { get; }
    public string RemediationSummary { get; }
    public string Parents { get; }
    public string Path { get; }
    public string TargetFrameworkMoniker { get; }
    /// <summary>
    /// Human-readable .NET target for grids (e.g. <c>.NET Framework 4.5.2</c>); row styling still uses health/risk bindings, not this text.
    /// </summary>
    public string TargetFrameworkMonikerDisplay { get; }
    public string StablePackageInstanceKey { get; }
    public string Tooltip { get; }
    /// <summary>
    /// Row background band for the Explorer package grid: <c>Critical</c>, <c>High</c>, <c>Attention</c>, <c>Ok</c>, or <c>None</c>.
    /// </summary>
    public string RowVisualBand { get; }

    private static string FormatPackageVersionDisplay(string Requested, string Resolved)
    {
        string Normalize(string? Value)
        {
            if (Value is null || string.IsNullOrWhiteSpace(Value) || Value == "-")
            {
                return string.Empty;
            }
            return Value.Trim();
        }

        string RequestedNormalized = Normalize(Requested);
        string ResolvedNormalized = Normalize(Resolved);
        if (RequestedNormalized.Length == 0 && ResolvedNormalized.Length == 0)
        {
            return "-";
        }

        if (RequestedNormalized.Length == 0)
        {
            return ResolvedNormalized;
        }

        if (ResolvedNormalized.Length == 0)
        {
            return RequestedNormalized;
        }

        if (string.Equals(RequestedNormalized, ResolvedNormalized, StringComparison.OrdinalIgnoreCase))
        {
            return ResolvedNormalized;
        }

        return $"{ResolvedNormalized} ({RequestedNormalized})";
    }
}

public sealed class GraphPayload
{
    public GraphPayload(string projectName, IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges, string projectPath)
    {
        ProjectName = projectName;
        Nodes = nodes;
        Edges = edges;
        ProjectPath = projectPath;
    }

    public string ProjectName { get; }
    public IReadOnlyList<GraphNode> Nodes { get; }
    public IReadOnlyList<GraphEdge> Edges { get; }
    public string ProjectPath { get; }
}

public sealed class GraphNode
{
    public GraphNode(string id, string label, string tfm, string health, string risk, string alert, bool selected, string referenceKind, string tooltip, string rowVisualBand, bool transitive = false)
    {
        Id = id;
        Label = label;
        Tfm = tfm;
        Health = health;
        Risk = risk;
        Alert = alert;
        Selected = selected;
        ReferenceKind = referenceKind;
        Tooltip = tooltip;
        RowVisualBand = rowVisualBand;
        Transitive = transitive;
    }

    public string Id { get; }
    public string Label { get; }
    public string Tfm { get; }
    public string Health { get; }
    public string Risk { get; }
    public string Alert { get; }
    public bool Selected { get; }
    public string ReferenceKind { get; }
    public string Tooltip { get; }
    /// <summary>
    /// Drives SVG fill/stroke using the same bands as the Explorer package table and Issues grid (<c>Critical</c>, <c>High</c>, <c>Attention</c>, <c>Ok</c>, <c>None</c>).
    /// </summary>
    public string RowVisualBand { get; }
    /// <summary>
    /// True when the node is a transitive package reference (hidden when Show Transient is off in the graph UI).
    /// </summary>
    public bool Transitive { get; }
}

public sealed class GraphEdge
{
    public GraphEdge(string from, string to, string label, bool transitive = false)
    {
        From = from;
        To = to;
        Label = label;
        Transitive = transitive;
    }

    public string From { get; }
    public string To { get; }
    public string Label { get; }
    /// <summary>
    /// When true, the dependency targets a transitively resolved package (dashed line in the graph).
    /// </summary>
    public bool Transitive { get; }
}
