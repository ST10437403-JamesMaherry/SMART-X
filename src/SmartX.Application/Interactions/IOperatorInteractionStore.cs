using SmartX.Domain.Interactions;

namespace SmartX.Application.Interactions;

/// <summary>Bounded process-local evidence. No authenticated operator identity is inferred.</summary>
public interface IOperatorInteractionStore
{
    OperatorInteraction Append(OperatorInteractionKind kind, Guid? deviceId = null,
        Guid? alertEpisodeId = null, Guid? commandId = null, string? searchText = null,
        string? canonicalIdentifier = null, string? category = null, string? status = null,
        bool? hasActiveCondition = null, string? conditionType = null, int? resultCount = null);

    OperatorInteractionSnapshot Read(int limit = 50);
}

public sealed record OperatorInteractionSnapshot(
    long TotalRecorded,
    int RetainedCount,
    int Capacity,
    IReadOnlyList<OperatorInteraction> Events);