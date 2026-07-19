namespace NuGetAudit.VisualStudioHost;

public sealed class CriticalityFile
{
    public double RiskScore { get; set; }
    public double AlertScore { get; set; }
    public string? RiskBand { get; set; }
    public string? AlertBand { get; set; }
}
