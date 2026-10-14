namespace SmartX.Contracts.Realtime;

/// <summary>
/// One server-generated device snapshot with actual processing diagnostics.
/// </summary>
public sealed record RealtimeSnapshotResponse(
    DateTimeOffset GeneratedAtUtc,
    int DeviceCount,
    IReadOnlyList<RealtimeDeviceResponse> Devices,
    TelemetryProcessingResponse Processing);