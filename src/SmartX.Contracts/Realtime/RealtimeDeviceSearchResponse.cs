namespace SmartX.Contracts.Realtime;

/// <summary>
/// Echoes the server-validated filters. Canonical MAC addresses and symbolic
/// enum values are returned in their normalized forms so callers can display
/// the exact search that was executed.
/// </summary>
public sealed record RealtimeDeviceSearchAppliedFiltersResponse(
    string? DeviceIdentifier,
    string? IdentifierKind,
    string? Text,
    string? Category,
    string? Status,
    bool? HasActiveCondition,
    string? ConditionType);

/// <summary>
/// One matching device, with read-only context from the existing active issue
/// collection. The issue ID denotes a specific activation episode, whereas
/// the device ID denotes a registered sensor.
/// </summary>
public sealed record RealtimeDeviceSearchItemResponse(
    RealtimeDeviceResponse Device,
    int ActiveConditionCount,
    Guid? HighestPriorityIssueId,
    string? HighestPriorityConditionType);

/// <summary>
/// A bounded, deterministic slice of search results. TotalMatches counts all
/// matches before paging. Search and filtering never create operator events:
/// deliberate user interaction recording will be added in P2-08 Commit 2.
/// </summary>
public sealed record RealtimeDeviceSearchResponse(
    DateTimeOffset GeneratedAtUtc,
    int TotalMatches,
    int Limit,
    int Offset,
    bool HasMore,
    RealtimeDeviceSearchAppliedFiltersResponse AppliedFilters,
    IReadOnlyList<RealtimeDeviceSearchItemResponse> Devices);