namespace SmartX.Domain.Interactions;

/// <summary>Process-local evidence of intentional actions, not user identities.</summary>
public enum OperatorInteractionKind
{
    SearchSubmitted,
    FiltersChanged,
    DeviceViewed,
    AlertViewed,
    CommandExecuted,
    CommandUndone
}

/// <summary>Immutable, server-timestamped interaction for the later predictive engine.</summary>
public sealed record OperatorInteraction(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    OperatorInteractionKind Kind,
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