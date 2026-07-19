namespace NuGetAudit.Intelligence;

public sealed record IngestionEnvelope(string SourceId, IngestionMethod Method, IngestionPayloadKind PayloadKind, DateTimeOffset CapturedUtc, string? ContentType, string? Location, IReadOnlyDictionary<string, string> Metadata, object Payload);
