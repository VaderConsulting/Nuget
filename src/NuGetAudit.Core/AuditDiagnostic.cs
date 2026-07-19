namespace NuGetAudit.Core;

/// <summary>
/// Represents a diagnostic that can be surfaced in reports or a Visual Studio Error List host.
/// </summary>
/// <param name="Code">The diagnostic code.</param>
/// <param name="Severity">The diagnostic severity.</param>
/// <param name="Message">The diagnostic message.</param>
/// <param name="ProjectName">The project name associated with the diagnostic.</param>
/// <param name="ProjectPath">The project path associated with the diagnostic.</param>
/// <param name="PackageId">The package identifier, if the diagnostic is package-specific.</param>
/// <param name="TargetFrameworkMoniker">The target framework moniker, if applicable.</param>
/// <param name="HelpLink">An advisory or help link, if available.</param>
public sealed record AuditDiagnostic(string Code, AuditDiagnosticSeverity Severity, string Message, string ProjectName, string ProjectPath, string? PackageId, string? TargetFrameworkMoniker, string? HelpLink);
