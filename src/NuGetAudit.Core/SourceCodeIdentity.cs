namespace NuGetAudit.Core;

/// <summary>
/// Describes the identity of the source code being audited, independent of any one local clone path.
/// </summary>
public sealed record SourceCodeIdentity(string IdentityKey, string IdentityKind, string MachineName, string InputPath, string RepositoryRootPath, string? GitRemoteUrl, string? GitRemoteKey);
