namespace NuGetAudit.Intelligence;

public interface IOutboundReportSink
{
    Task PublishAsync(ReportingBatch batch, CancellationToken cancellationToken);
}
