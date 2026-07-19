namespace NuGetAudit.Intelligence;

/// <summary>
/// Strategy interface for building a <see cref="CriticalityScorecard"/> from an arbitrary domain object.
/// </summary>
/// <typeparam name="T">The source type (for example, a reporting DTO).</typeparam>
/// <remarks>
/// The main audit pipeline uses <c>NuGetAudit.Core.PackageCriticalityAssessor</c> instead of this interface;
/// implementations can appear in extensions or tests.
/// </remarks>
public interface IScorecardBuilder<in T>
{
    /// <summary>
    /// Produces a scorecard suitable for <see cref="CriticalityCalculator.Calculate"/>.
    /// </summary>
    /// <param name="item">The object to translate into ten 0–10 inputs.</param>
    /// <returns>A filled scorecard with explanations.</returns>
    CriticalityScorecard Build(T item);
}
