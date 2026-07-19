using System.Text.Json.Serialization;

namespace NuGetAudit.Core;

/// <summary>
/// Describes the overall completeness of an analysis result.
/// </summary>
public enum AnalysisStatus
{
    /// <summary>
    /// The analysis completed with no known gaps.
    /// </summary>
    Complete,

    /// <summary>
    /// The analysis completed, but some data was unavailable or inferred.
    /// </summary>
    Partial,

    /// <summary>
    /// The target could not be analysed by the current implementation.
    /// </summary>
    Unsupported,

    /// <summary>
    /// The analysis failed.
    /// </summary>
    Failed
}

/// <summary>
/// Identifies the package management style used by a project.
/// </summary>
public enum ProjectStyle
{
    /// <summary>
    /// The project uses <c>PackageReference</c> and/or central <c>PackageVersion</c> items (including from <c>Directory.Packages.props</c>).
    /// </summary>
    PackageReference,

    /// <summary>
    /// The project uses <c>packages.config</c>.
    /// </summary>
    PackagesConfig,

    /// <summary>
    /// The project style could not be determined.
    /// </summary>
    Unknown
}

/// <summary>
/// Describes whether a project is loaded and analysable.
/// </summary>
public enum ProjectLoadState
{
    /// <summary>
    /// The project is present and loaded.
    /// </summary>
    Loaded,

    /// <summary>
    /// The project exists in the solution but is not currently loaded.
    /// </summary>
    Unloaded,

    /// <summary>
    /// The project is known but not supported by the analyser.
    /// </summary>
    Unsupported
}

/// <summary>
/// Indicates whether a package is directly referenced or transitively resolved.
/// </summary>
public enum PackageReferenceKind
{
    /// <summary>
    /// The package is declared directly by the project.
    /// </summary>
    Direct,

    /// <summary>
    /// The package is introduced by another dependency.
    /// </summary>
    Transitive
}

/// <summary>
/// Describes how a package changed between two snapshots.
/// </summary>
public enum PackageChangeType
{
    /// <summary>
    /// The package is present only in the current snapshot.
    /// </summary>
    Added,

    /// <summary>
    /// The package is present only in the previous snapshot.
    /// </summary>
    Removed,

    /// <summary>
    /// The resolved package version increased.
    /// </summary>
    Upgraded,

    /// <summary>
    /// The resolved package version decreased.
    /// </summary>
    Downgraded,

    /// <summary>
    /// Package metadata changed without a clear version direction.
    /// </summary>
    MetadataChanged
}

/// <summary>
/// Describes how a dependency relationship changed between two snapshots.
/// </summary>
public enum DependencyEdgeChangeType
{
    /// <summary>
    /// The dependency relationship is present only in the current snapshot.
    /// </summary>
    Added,

    /// <summary>
    /// The dependency relationship is present only in the previous snapshot.
    /// </summary>
    Removed
}

/// <summary>
/// Describes how serious NuGet.org labels the worst known security advisory affecting a specific package version.
/// </summary>
/// <remarks>
/// <para>
/// These values come from the public NuGet API’s vulnerability list for that version, not from a full CVE scoring workshop.
/// Labels such as “High” are vendor-supplied categories. <see cref="NuGetAudit.Core.PackageCriticalityAssessor"/> maps each label
/// to a 0–10 input for <see cref="NuGetAudit.Intelligence.CriticalityCalculator"/>.
/// </para>
/// </remarks>
[JsonConverter(typeof(LenientPackageVulnerabilitySeverityJsonConverter))]
public enum PackageVulnerabilitySeverity
{
    /// <summary>
    /// No advisory is recorded, or the feed did not provide a recognized severity string.
    /// </summary>
    None,

    /// <summary>
    /// The feed reported low impact; treated as a small bump in criticality scoring.
    /// </summary>
    Low,

    /// <summary>
    /// The feed reported moderate impact; a middle tier in scoring.
    /// </summary>
    Moderate,

    /// <summary>
    /// The feed reported high impact; a strong bump in scoring.
    /// </summary>
    High,

    /// <summary>
    /// The feed reported critical impact; the largest severity bump in scoring.
    /// </summary>
    Critical
}

/// <summary>
/// Describes the severity of an audit diagnostic.
/// </summary>
public enum AuditDiagnosticSeverity
{
    /// <summary>
    /// Informational diagnostic.
    /// </summary>
    Info,

    /// <summary>
    /// Warning diagnostic.
    /// </summary>
    Warning,

    /// <summary>
    /// Error diagnostic.
    /// </summary>
    Error
}
