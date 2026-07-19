namespace NuGetAudit.Core;

/// <summary>
/// Represents a project discovered from a solution file.
/// </summary>
/// <param name="ProjectName">The project display name.</param>
/// <param name="ProjectPath">The full path to the project file.</param>
/// <param name="LoadState">The project load state inferred during discovery.</param>
internal sealed record ProjectDescriptor(string ProjectName, string ProjectPath, ProjectLoadState LoadState);
