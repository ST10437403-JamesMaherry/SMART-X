using Microsoft.AspNetCore.Mvc;

using SmartX.Application.Recommendations;
using SmartX.Contracts.Recommendations;

namespace SmartX.Api.Controllers;

/// <summary>
/// Read-only, explainable P2-09 recommendations. This controller is deliberately
/// NOT a command endpoint: an operator must call the existing validated relay
/// command API separately if they choose to follow a suggestion.
/// </summary>
[ApiController]
[Route("api/realtime/recommendations")]
public sealed class RealtimeRecommendationsController : ControllerBase
{
    private readonly PredictiveActionService _predictor;

    public RealtimeRecommendationsController(PredictiveActionService predictor)
    {
        _predictor = predictor ?? throw new ArgumentNullException(nameof(predictor));
    }

    /// <summary>
    /// Evaluates current active episodes and actuator eligibility against
    /// actual bounded operator patterns. GET never records interactions,
    /// updates commanded state or produces a SignalR notification.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<PredictedRecommendationsResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PredictedRecommendationsResponse>> Get(
        CancellationToken cancellationToken)
    {
        PredictedActionSnapshot snapshot = await _predictor.GetSnapshotAsync(
            cancellationToken);

        // An earlier positive result could become ineligible after recovery,
        // Undo, a new command, or the evidence window closing. Never let a
        // proxy/browser reuse stale predictive advice from an HTTP cache.
        Response.Headers["Cache-Control"] = "no-store";

        PredictedRecommendationResponse[] recommendations = snapshot.Actions
            .Select(action => new PredictedRecommendationResponse(
                action.SourceAlertEpisodeId,
                action.SourceDeviceId,
                action.SourceDeviceName,
                action.ConditionType.ToString(),
                action.Severity.ToString(),
                action.Movement.ToString(),
                action.TargetActuatorId,
                action.TargetActuatorName,
                action.Command.ToString(),
                action.SupportingEpisodes,
                action.LastLearnedAtUtc,
                action.Score,
                action.ConfidenceBand.ToString(),
                action.ExpiresAtUtc,
                action.Explanation))
            .ToArray();

        DateTimeOffset? nextRefresh = recommendations.Length == 0
            ? null
            : recommendations.Min(item => item.ExpiresAtUtc);

        // The response contains only what the application can prove from its
        // current process-local state; it does not claim an ML probability.
        return Ok(new PredictedRecommendationsResponse(
            snapshot.EvaluatedAtUtc,
            snapshot.MinimumSupportingEpisodes,
            snapshot.EvidenceWindow.TotalSeconds,
            nextRefresh,
            Array.AsReadOnly(recommendations)));
    }
}