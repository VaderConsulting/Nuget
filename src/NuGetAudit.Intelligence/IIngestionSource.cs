namespace NuGetAudit.Intelligence;

public interface IIngestionSource
{
    string Name { get; }

    Task<IReadOnlyList<IngestionEnvelope>> ReadAsync(CancellationToken cancellationToken);
}
