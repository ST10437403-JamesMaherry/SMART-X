namespace SmartX.Contracts.Realtime;

/// <summary>Explicit submitted query; GET /search never records interactions.</summary>
public sealed record RecordDeviceSearchRequest(
    string? Kind,
    string? DeviceIdentifier,
    string? IdentifierKind,
    string? Text,
    string? Category,
    string? Status,
    string? HasActiveCondition,
    string? ConditionType,
    string? Limit,
    string? Offset,
    Guid? SelectedDeviceId,
    Guid? SourceAlertEpisodeId);

/// <summary>Explicitly viewed registered device or currently active alert episode.</summary>
public sealed record RecordOperatorViewRequest(Guid? DeviceId, Guid? AlertEpisodeId);

public sealed record OperatorInteractionResponse(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    string Kind,
    Guid? DeviceId,
    Guid? AlertEpisodeId,
    Guid? CommandId,
    string? SearchText,
    string? CanonicalIdentifier,
    string? Category,
    string? Status,
    bool? HasActiveCondition,
    string? ConditionType,
    int? ResultCount);

public sealed record OperatorInteractionHistoryResponse(
    long TotalRecorded, int RetainedCount, int Capacity,
    IReadOnlyList<OperatorInteractionResponse> Events);