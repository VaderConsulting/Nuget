namespace NuGetAudit.Core;

/// <summary>
/// Inferred lifecycle posture for a package on the public NuGet.org registration feed.
/// </summary>
/// <remarks>
/// Used together with deprecation and outdated flags to populate <see cref="CriticalityFactor.LifecycleState"/> via
/// <see cref="PackageCriticalityAssessor"/>.
/// </remarks>
public enum PackageDevelopmentStatus
{
    /// <summary>
    /// The tool could not classify lifecycle (network errors, parse issues, or missing metadata).
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Registration loaded and the version is not treated as abandoned or removed.
    /// </summary>
    Active = 1,

    /// <summary>
    /// You are already on the newest compatible stable release, but that release is older than one year on the feed—treated as stale maintenance risk.
    /// </summary>
    Abandoned = 2,

    /// <summary>
    /// The package id has no registration on NuGet.org (HTTP 404), e.g. private-only id or delisted package.
    /// </summary>
    Removed = 3
}
