namespace NuGetAudit.Presentation;

public sealed class ReevaluationRequestEventArgs : EventArgs
{
    public ReevaluationRequestEventArgs(string scope, SurfaceProjectSnapshot? project, SurfacePackageReference? package)
    {
        Scope = scope;
        Project = project;
        Package = package;
    }

    public string Scope { get; }
    public SurfaceProjectSnapshot? Project { get; }
    public SurfacePackageReference? Package { get; }
}
