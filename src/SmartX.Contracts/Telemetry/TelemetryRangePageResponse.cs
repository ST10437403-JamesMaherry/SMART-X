namespace SmartX.Contracts.Telemetry;

/// <summary>
/// A composite keyset cursor. Sequence is zero-based within all accepted
/// packets captured at the exact same UTC instant, in ingestion order.
/// The client must replay both fields with unchanged sensor and date bounds.
/// </summary>
public sealed record TelemetryRangeCursor(
    DateTimeOffset CapturedAtUtc,
    int Sequence);

/// <summary>
/// Read-only, timestamp-ordered historical telemetry from the real AVL index.
/// No total-count scan is performed. NextCursor is present only if HasMore.
/// </summary>
public sealed record TelemetryRangePageResponse(
    Guid SensorProfileId,
    string TelemetryDataType,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int Limit,
    IReadOnlyList<TelemetryReadingResponse> Readings,
    bool HasMore,
    TelemetryRangeCursor? NextCursor);