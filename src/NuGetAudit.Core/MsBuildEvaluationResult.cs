namespace NuGetAudit.Core;

/// <summary>
/// Represents the result of evaluating a project through MSBuild.
/// </summary>
/// <param name="TargetFrameworks">The evaluated target frameworks.</param>
/// <param name="Packages">The evaluated direct package references.</param>
/// <param name="Warnings">Warnings emitted while evaluating the project.</param>
internal sealed record MsBuildEvaluationResult(IReadOnlyList<string> TargetFrameworks, IReadOnlyList<PackageReferenceRecord> Packages, IReadOnlyList<string> Warnings);
