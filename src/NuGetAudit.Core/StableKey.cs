namespace NuGetAudit.Core;

/// <summary>
/// Builds deterministic identity keys for projects and package instances.
/// </summary>
public static class StableKey
{
    /// <summary>
    /// Creates a deterministic package instance key.
    /// </summary>
    /// <param name="solutionPath">The solution path.</param>
    /// <param name="projectPath">The project path.</param>
    /// <param name="targetFramework">The target framework scope, if any.</param>
    /// <param name="runtimeIdentifier">The runtime identifier scope, if any.</param>
    /// <param name="packageId">The package identifier.</param>
    /// <returns>The stable package instance key.</returns>
    public static string Create(string solutionPath, string projectPath, string? targetFramework, string? runtimeIdentifier, string packageId)
    {
        return $"{Normalize(solutionPath)}|{Normalize(projectPath)}|{Normalize(targetFramework)}|{Normalize(runtimeIdentifier)}|{packageId.ToUpperInvariant()}";
    }

    /// <summary>
    /// Normalises a key segment into the canonical stable-key form.
    /// </summary>
    /// <param name="value">The raw value.</param>
    /// <returns>The normalised segment.</returns>
    private static string Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "NONE"
            : value.Replace('\\', '/').Trim().ToUpperInvariant();
    }
}
