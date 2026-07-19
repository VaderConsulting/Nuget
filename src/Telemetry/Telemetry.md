# Telemetry Producer SDK — Detailed Specification

## 1. Purpose

The package provides a **single, reusable .NET library** that application and integration teams can reference from any C# solution in order to emit monitoring telemetry consistently.

It must allow developers to:

• initialise monitoring with minimal code\
• emit heartbeat messages\
• emit operational outcome and status events\
• emit general reporting logs\
• emit exceptions\
• emit traces and correlated logs\
• enrich telemetry with standard monitoring attributes\
• export all telemetry using **OTLP over HTTP** to a **single REST endpoint**

The package is a **producer-side SDK**. It is not the monitoring backend, and it is not the event bus subscriber. It standardises how systems create and send telemetry into the monitoring ingestion path.

---

## 2. Design Goals

The package must:

• work in **any .NET application type**, including ASP.NET Core APIs, worker services, console applications, Azure Functions where feasible, Windows services, and scheduled executables  
• use **OpenTelemetry** as the underlying observability standard  
• export using **OTLP over HTTP** to a REST endpoint  
• integrate with standard .NET logging and tracing patterns  
• make common cases easy without requiring developers to understand all OpenTelemetry internals  
• enforce or strongly encourage required monitoring attributes such as environment, system, component, and correlation identifiers  
• support a **stable externally supplied identifier** for the logical monitored system or component  
• support a **stable externally supplied instance identifier** for the deployed instance that is sending telemetry  
• avoid identifiers that are generated per submission for system or instance identity  
• support structured monitoring events, not only free-text logs  
• keep producer code independent from downstream storage, alerting, or visualisation tools

---

## 3. Package Name

Recommended package name:

`Telemetry.Producer`

This specification assumes a **single NuGet package** that supports multiple .NET application types.

The package must be suitable for use from:

• ASP.NET Core APIs  
• worker services  
• console applications  
• scheduled executables  
• Windows services  
• Azure Functions where feasible

The design goal is to avoid splitting the producer SDK into hosting-model-specific packages unless a later technical need clearly justifies doing so.

---

## 4. Supported Telemetry Types

### 4.1 Traces

Use OpenTelemetry tracing and W3C Trace Context propagation for distributed tracing across HTTP and messaging.

### 4.2 Logs

Use structured logs for:

• reporting messages\
• exceptions\
• operational outcomes\
• explicit health and status messages

### 4.3 Metrics

Use metrics for:

• heartbeat cadence\
• request counts\
• retry counts\
• throughput\
• backlog\
• durations

---

## 5. Primary Usage Model

The package must support **three primary developer usage patterns**.

### 5.1 Minimal Bootstrap

A host application should be able to initialise the package with a small amount of setup code.

Example target experience:

```csharp
TelemetryProducerBuilder Builder = new TelemetryProducerBuilder();

Builder.UseServiceIdentity(
    ServiceName: "ApprovedLeaveWorker",
    Environment: "PRD",
    SystemName: "WFS",
    ComponentName: "ApprovedLeaveWorker",
    SystemId: "wfs-approved-leave",
    InstanceId: "3f2abcf3-2c0d-4a6c-bfd7-84a9638d2b5a");

Builder.UseOtlpHttpExporter(
    Endpoint: "https://coremonitor.example/api/otlp",
    ApiKey: null);

Builder.Build();
```

### 5.2 Logging and Exceptions

A developer should be able to write:

```csharp
ITelemetryClient Client = ServiceProvider.GetRequiredService<ITelemetryClient>();

Client.ReportInformation(
    Message: "Approved leave payload accepted for processing.",
    CorrelationId: CorrelationId);

Client.ReportException(
        Exception: Exception,
        Message: "Approved leave processing failed.",
        CorrelationId: CorrelationId,
        Options: new ExceptionTelemetryOptions
        {
            IncludeInnerExceptions = true,
            IncludeExceptionData = true,
            IncludeTargetSite = true,
            IncludeSource = true
        });
```

### 5.3 Explicit Monitoring Events

A developer should be able to write:

```csharp
await Client.PublishHealthReportAsync(
    Message: new HealthReportMessage
    {
        EventId = Guid.NewGuid().ToString(),
        EventTimestampUtc = DateTime.UtcNow,
        IntegrationId = "INT002",
        Environment = "PRD",
        System = "WFS",
        Component = "ApprovedLeaveWorker",
        Status = "Healthy",
        HealthCategory = "Heartbeat",
        Summary = "Worker is healthy."
    },
    CancellationToken: CancellationToken);
```

---

## 6. Functional Requirements

### 6.1 Initialisation

The package must provide a clear bootstrap mechanism that:

• configures OpenTelemetry tracing\
• configures OpenTelemetry logging\
• configures OpenTelemetry metrics\
• configures OTLP/HTTP export\
• sets required resource attributes\
• wires up correlation propagation\
• registers a simple façade for application use

### 6.2 Standard Attributes

Every emitted telemetry item must include, either automatically or via validation, standard monitoring attributes:

• `environment`  
• `system.name`  
• `system.id`  
• `component.name`  
• `service.name`  
• `service.instance.id`  
• correlation identifiers where present

Rules:

• `system.name` and `component.name` are human-readable names  
• `system.id` is a **stable identifier for the logical monitored system or component family** and must not be generated per submission  
• `service.instance.id` is a **stable identifier for the deployed instance emitting telemetry** and must not be generated per submission  
• `service.instance.id` should be supplied from deployment or runtime configuration rather than from compiled assembly metadata  
• where multiple instances of the same code are deployed, each deployed instance must have its own distinct `service.instance.id`

### 6.3 Correlation

The package must support correlation across:

• internal operations\
• outbound HTTP calls\
• message handlers where manually wired if needed\
• logs associated with traces\
• explicit monitoring events associated with traces

### 6.4 Heartbeat Support

The package must provide a first-class heartbeat helper.

Required capabilities:

• publish heartbeat as a structured monitoring event\
• optionally emit a heartbeat metric\
• support expected interval\
• support next expected processing time\
• support workload state, backlog count, and last successful processing time

### 6.5 Exception Reporting

The package must provide a first-class exception reporting helper that:

• logs the exception as structured telemetry  
• includes correlation and system/component metadata  
• can optionally record exception details on the current active span  
• supports severity mapping  
• extracts rich exception information rather than only the textual representation or stack trace

Captured exception information should include, where available:

• exception type  
• exception message  
• stack trace  
• inner exception chain  
• source  
• target site / method  
• HResult  
• exception data entries  
• correlation id  
• trace id and span id  
• machine or host name where appropriate  
• environment, system, component, system id, and instance id

### 6.6 Reporting Logs

The package must support structured reporting logs with:

• message template or free-text message\
• severity\
• correlation id\
• optional business/reference identifiers\
• optional tags and properties

### 6.7 Explicit Monitoring Events

The package must support structured event publishing for:

• health reports\
• outcome reports\
• state transitions\
• audit and informational outcome events

These should be emitted as structured OTLP log records so they move through the same single pipeline.

---

## 7. Non-Functional Requirements

### 7.1 Ease of Adoption

A basic application should be able to start sending telemetry with:

• one package reference\
• one setup block\
• one injected interface for common publishing

### 7.2 Performance

The package must:

• avoid excessive allocations in hot paths\
• use batching where supported by OTel exporters\
• avoid blocking application threads unnecessarily\
• tolerate transient exporter failures gracefully

### 7.3 Reliability

The package must:

• fail safely if the monitoring endpoint is unavailable\
• never crash the host application merely because telemetry export fails\
• expose internal self-diagnostics via logging where practical

### 7.4 Security

The package must:

• support HTTPS OTLP/HTTP export\
• support bearer token and API key style headers if required\
• never log secrets\
• never include sensitive credentials in telemetry attributes

### 7.5 Compatibility

The package should support:

• .NET 8 and later as the primary target\
• C#-friendly DI patterns\
• `ILogger`\
• `ActivitySource`\
• `Meter`

---

## 8. Package Public API

### 8.1 Primary Façade

Recommended primary interface:

```csharp
public interface ITelemetryClient
{
    void ReportInformation(
        string Message,
        string? CorrelationId = null,
        IReadOnlyDictionary<string, object?>? Properties = null);

    void ReportWarning(
        string Message,
        string? CorrelationId = null,
        IReadOnlyDictionary<string, object?>? Properties = null);

    void ReportError(
        string Message,
        string? CorrelationId = null,
        IReadOnlyDictionary<string, object?>? Properties = null);

    void ReportException(
        Exception Exception,
        string? Message = null,
        string? CorrelationId = null,
        IReadOnlyDictionary<string, object?>? Properties = null,
        ExceptionTelemetryOptions? Options = null);

    Task PublishHealthReportAsync(
        HealthReportMessage Message,
        CancellationToken CancellationToken = default);

    Task PublishOutcomeEventAsync(
        OutcomeEventMessage Message,
        CancellationToken CancellationToken = default);

    IDisposable StartOperationScope(
        string OperationName,
        string? CorrelationId = null,
        IReadOnlyDictionary<string, object?>? Properties = null);

    void RecordHeartbeat(
        HeartbeatMessage Message);

    void IncrementCounter(
        string Name,
        long Value = 1,
        IReadOnlyDictionary<string, object?>? Tags = null);

    void RecordDuration(
        string Name,
        double Milliseconds,
        IReadOnlyDictionary<string, object?>? Tags = null);
}
```

### 8.1.1 ExceptionTelemetryOptions

```csharp
public sealed class ExceptionTelemetryOptions
{
    public bool IncludeStackTrace { get; set; } = true;
    public bool IncludeInnerExceptions { get; set; } = true;
    public bool IncludeExceptionData { get; set; } = true;
    public bool IncludeTargetSite { get; set; } = true;
    public bool IncludeSource { get; set; } = true;
    public int MaxInnerExceptionDepth { get; set; } = 10;
}
```

### 8.2 Builder / Registration API

Recommended DI registration:

```csharp
public static class TelemetryServiceCollectionExtensions
{
    public static IServiceCollection AddTelemetryProducer(
        this IServiceCollection Services,
        Action<TelemetryOptions> Configure);
}
```

### 8.3 Options Class

```csharp
public sealed class TelemetryOptions
{
    public string Endpoint { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string SystemName { get; set; } = string.Empty;
    public string SystemId { get; set; } = string.Empty;
    public string ComponentName { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public string? ApiKeyHeaderName { get; set; }
    public string? ApiKeyValue { get; set; }
    public bool EnableTracing { get; set; } = true;
    public bool EnableMetrics { get; set; } = true;
    public bool EnableLogs { get; set; } = true;
    public bool EnableHttpClientInstrumentation { get; set; } = true;
    public bool EnableAspNetCoreInstrumentation { get; set; } = true;
    public bool EnableExceptionRecording { get; set; } = true;
}
```

---

## 9. Message Contracts

### 9.1 HealthReportMessage

```csharp
public sealed class HealthReportMessage
{
    public string SchemaVersion { get; set; } = "1.0";
    public string MessageType { get; set; } = "HealthReport";
    public DateTime EventTimestampUtc { get; set; }
    public string EventId { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string System { get; set; } = string.Empty;
    public string SystemId { get; set; } = string.Empty;
    public string Component { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string HealthCategory { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string? ReasonCode { get; set; }
    public string? CorrelationId { get; set; }
    public string? RunCorrelationId { get; set; }
    public string? TraceId { get; set; }
    public string? SpanId { get; set; }
    public DateTime? StartupTimestampUtc { get; set; }
    public DateTime? LastSuccessfulProcessingTimestampUtc { get; set; }
    public DateTime? LastAttemptedProcessingTimestampUtc { get; set; }
    public DateTime? NextExpectedProcessingTimestampUtc { get; set; }
    public int? HeartbeatIntervalSeconds { get; set; }
    public string? CurrentOperation { get; set; }
    public string? WorkloadState { get; set; }
    public int? BacklogCount { get; set; }
    public int? ConsecutiveFailureCount { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorSummary { get; set; }
    public List<DependencyHealthMessage>? DependencyStates { get; set; }
    public Dictionary<string, object?>? Data { get; set; }
    public Dictionary<string, string>? Tags { get; set; }
}
```

### 9.2 OutcomeEventMessage

```csharp
public sealed class OutcomeEventMessage
{
    public string SchemaVersion { get; set; } = "1.0";
    public string MessageType { get; set; } = "OutcomeEvent";
    public DateTime EventTimestampUtc { get; set; }
    public string EventId { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string System { get; set; } = string.Empty;
    public string SystemId { get; set; } = string.Empty;
    public string Component { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public string OutcomeState { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string? ReasonCode { get; set; }
    public string? CorrelationId { get; set; }
    public string? SubmissionReference { get; set; }
    public string? ExternalTransactionId { get; set; }
    public Dictionary<string, object?>? Data { get; set; }
    public Dictionary<string, string>? Tags { get; set; }
}
```

### 9.3 DependencyHealthMessage

```csharp
public sealed class DependencyHealthMessage
{
    public string DependencyName { get; set; } = string.Empty;
    public string DependencyType { get; set; } = string.Empty;
    public string DependencyState { get; set; } = string.Empty;
    public DateTime? LastCheckedTimestampUtc { get; set; }
    public string? ReasonCode { get; set; }
    public string? Summary { get; set; }
}
```

### 9.4 HeartbeatMessage

```csharp
public sealed class HeartbeatMessage
{
    public DateTime EventTimestampUtc { get; set; }
    public string Summary { get; set; } = "Heartbeat";
    public string? CorrelationId { get; set; }
    public DateTime? LastSuccessfulProcessingTimestampUtc { get; set; }
    public DateTime? NextExpectedProcessingTimestampUtc { get; set; }
    public int? HeartbeatIntervalSeconds { get; set; }
    public string? WorkloadState { get; set; }
    public int? BacklogCount { get; set; }
}
```

---

## 10. Internal Architecture

The package should wrap OpenTelemetry rather than replace it.

### 10.1 Internals

Use:

• `ActivitySource` for traces\
• `Meter` for metrics\
• OpenTelemetry logging integration for logs\
• OTLP/HTTP exporters for transmission

### 10.2 Export Path

The package must export to a REST endpoint using **OTLP over HTTP**.

Preferred configuration approach:

• endpoint URL from configuration\
• optional auth header from configuration\
• per-signal enable/disable flags\
• environment variable override support where sensible

### 10.3 Correlation Handling

The package must:

• attach current `Activity` trace/span context to emitted structured monitoring events when available\
• create an operation scope when requested\
• propagate W3C Trace Context on outbound HTTP calls where instrumentation is enabled

---

## 11. Validation Rules

The package must validate before publishing explicit monitoring events.

At minimum check:

• required fields present  
• UTC timestamps provided  
• schema version present  
• event id present where required  
• environment/system/component populated  
• system id populated  
• instance id populated  
• status/category values present  
• message size within configured limit

Validation failures should:

• not crash the host application  
• emit internal diagnostics  
• optionally throw only if strict mode is enabled

---

## 12. Configuration Model

Support configuration through:

• code-based options\
• `appsettings.json`\
• environment variables\
• dependency injection

Suggested keys:

```json
{
  "Telemetry": {
    "Endpoint": "https://coremonitor.example/api/otlp",
    "Environment": "PRD",
    "SystemName": "WFS",
    "SystemId": "wfs-approved-leave",
    "ComponentName": "ApprovedLeaveWorker",
    "InstanceId": "3f2abcf3-2c0d-4a6c-bfd7-84a9638d2b5a",
    "ServiceName": "ApprovedLeaveWorker",
    "EnableTracing": true,
    "EnableMetrics": true,
    "EnableLogs": true,
    "ApiKeyHeaderName": "X-Api-Key",
    "ApiKeyValue": "secret-not-in-source-control"
  }
}
```

---

## 13. Example Usage

### 13.1 Minimal Setup

```csharp
Services.AddTelemetryProducer(
    Configure: Options =>
    {
        Options.Endpoint = "https://coremonitor.example/api/otlp";
        Options.Environment = "PRD";
        Options.SystemName = "WFS";
        Options.SystemId = "wfs-approved-leave";
        Options.ComponentName = "ApprovedLeaveWorker";
        Options.InstanceId = "3f2abcf3-2c0d-4a6c-bfd7-84a9638d2b5a";
        Options.ServiceName = "ApprovedLeaveWorker";
    });
```

### 13.2 Exception Reporting

```csharp
try
{
    await Processor.ProcessAsync(CancellationToken);
}
catch (Exception Exception)
{
    Client.ReportException(
        Exception: Exception,
        Message: "Approved leave processing failed.",
        CorrelationId: CorrelationId);
    throw;
}
```

### 13.3 Heartbeat

```csharp
Client.RecordHeartbeat(
    Message: new HeartbeatMessage
    {
        EventTimestampUtc = DateTime.UtcNow,
        LastSuccessfulProcessingTimestampUtc = _LastSuccessUtc,
        NextExpectedProcessingTimestampUtc = _NextExpectedUtc,
        HeartbeatIntervalSeconds = 60,
        WorkloadState = "Idle",
        BacklogCount = 0
    });
```

---

## 14. Delivery Requirements

Initial release should include:

• package reference and documentation  
• DI registration helper  
• logging wrapper  
• exception helper  
• heartbeat helper  
• structured health/outcome event publishing  
• OTLP/HTTP export support  
• trace correlation support  
• support for multiple .NET hosting models within the same package

Later releases may add:

• Azure Service Bus helper integration  
• additional convenience extensions within the same package  
• testing harness or fake exporter

---

## 15. Acceptance Criteria

The package is successful if:

• a .NET service can reference one NuGet package and send OTLP telemetry to one REST endpoint\
• a developer can emit logs, exceptions, and heartbeat messages without dealing directly with raw OTel primitives\
• telemetry is correlated across traces and logs\
• structured monitoring events are exported in a consistent format\
• producer systems do not need bespoke monitoring code for each project

---

## Recommendation

This is a strong design direction. It provides a **single, reusable producer SDK** over OpenTelemetry rather than requiring each project team to become expert in raw OTel setup.

A stable **system id** and **instance id** should be treated as first-class requirements. These identifiers should come from deployment or runtime configuration, not from compiled assembly metadata and not from per-submission generation.

Exception handling should also be treated as a first-class feature of the SDK, with structured extraction of exception details rather than relying only on a textual exception representation.

A **single package for multiple codebase types** should remain the default design goal. Any future split into additional packages should occur only if a clear technical need emerges, such as dependency isolation or materially different hosting-model requirements.

