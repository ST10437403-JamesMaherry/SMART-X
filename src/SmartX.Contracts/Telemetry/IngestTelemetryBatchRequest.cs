namespace SmartX.Contracts.Telemetry;

/// <summary>
/// Represents historical telemetry supplied as a jagged sequence of batches.
///
/// A jagged structure is intentional because individual device batches may
/// contain different numbers of readings. The Application layer converts
/// these transport records into strongly typed TelemetryPacket arrays and
/// then into HistoricalTelemetryBuffer&lt;T&gt;.
/// </summary>
public sealed record IngestTelemetryBatchRequest(
    IngestTelemetryRequest?[]?[]? Batches);