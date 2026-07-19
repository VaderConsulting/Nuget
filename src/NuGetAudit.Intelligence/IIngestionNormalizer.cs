namespace NuGetAudit.Intelligence;

public interface IIngestionNormalizer<out T>
{
    bool CanNormalize(IngestionEnvelope envelope);

    T Normalize(IngestionEnvelope envelope);
}
