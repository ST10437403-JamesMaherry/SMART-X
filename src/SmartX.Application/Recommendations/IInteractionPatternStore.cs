using SmartX.Domain.Recommendations;

namespace SmartX.Application.Recommendations;

/// <summary>
/// Process-local, bounded prediction evidence. Only a genuine server command
/// can append observations; reads and ranking never create new support.
/// </summary>
public interface IInteractionPatternStore
{
    /// <returns>True only when a previously unseen episode was counted.</returns>
    bool Observe(InteractionPatternObservation observation);

    /// <summary>Prunes expired evidence and returns detached, ordered statistics.</summary>
    IReadOnlyList<PatternStatistics> GetSnapshot();
}