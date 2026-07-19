namespace NuGetAudit.VisualStudioHost;

internal static class Guids
{
    public const string PackageString = "9B7DE47C-0A2D-4E13-9658-7E8C4E55A861";
    public const string CommandSetString = "81A6C9D8-561F-4622-96D8-155A4C910E1D";
    public const string DependencyExplorerWindowString = "3EAE0ACB-E37A-4C11-BD3E-1A4CF17B412A";

    public static readonly Guid Package = new(PackageString);
    public static readonly Guid CommandSet = new(CommandSetString);
    public static readonly Guid DependencyExplorerWindow = new(DependencyExplorerWindowString);
}
