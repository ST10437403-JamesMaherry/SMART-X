namespace SmartX.Contracts.Realtime;

/// <summary>
/// One server-generated device snapshot. Later Part 2 workflows extend this
/// response with their genuine processing and active-condition projections.
/// </summary>
public sealed record RealtimeSnapshotResponse(
    DateTimeOffset GeneratedAtUtc,
    int DeviceCount,
    IReadOnlyList<RealtimeDeviceResponse> Devices);