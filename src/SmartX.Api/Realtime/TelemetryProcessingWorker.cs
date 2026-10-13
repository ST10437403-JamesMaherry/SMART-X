using SmartX.Application.Realtime;

namespace SmartX.Api.Realtime;

/// <summary>
/// Exactly one consumer. The buffer chooses the critical lane at each dequeue;
/// executing work cannot be preempted. Only the application stopping token is
/// supplied to processing, never the token from an ingestion HTTP request.
/// </summary>
public sealed class TelemetryProcessingWorker(
    ITelemetryWorkBuffer buffer,
    ITelemetryProcessingStore state,
    IServiceScopeFactory scopeFactory,
    IRealtimeNotifier notifier,
    TimeProvider timeProvider,
    ILogger<TelemetryProcessingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                state.MarkWaiting();
                var dispatch = await buffer.DequeueAsync(stoppingToken);
                state.Begin(dispatch, timeProvider.GetUtcNow());
                logger.LogInformation(
                    "Telemetry dispatch {DequeueSequence}: enqueue {EnqueueSequence}, lane {Lane}, sensor {SensorId}.",
                    dispatch.DequeueSequence, dispatch.WorkItem.ArrivalSequence,
                    dispatch.WorkItem.Lane, dispatch.WorkItem.SensorProfileId);
                await PublishSafelyAsync("TelemetryDispatched", dispatch.WorkItem.SensorProfileId, stoppingToken);

                // A singleton worker must not capture a scoped processor.
                await using (var scope = scopeFactory.CreateAsyncScope())
                {
                    var processor = scope.ServiceProvider.GetRequiredService<ITelemetryProcessor>();
                    await processor.ProcessAsync(dispatch, stoppingToken);
                }

                await PublishSafelyAsync("TelemetryProcessed", dispatch.WorkItem.SensorProfileId, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Pending work was not removed by a cancelled wait. If processing
            // was interrupted after dequeue, CurrentDispatch retains the packet.
        }
        catch (Exception exception)
        {
            state.MarkFaulted(timeProvider.GetUtcNow());
            logger.LogError(exception, "Telemetry worker faulted. Dispatch halted; uncompleted work retained.");
            await PublishSafelyAsync("TelemetryWorkerFaulted", null, stoppingToken);
            // Keep HTTP diagnostics alive with a visible Faulted status. Never
            // continue consuming after a failure or pretend it was processed.
        }
        finally
        {
            state.MarkStopped(timeProvider.GetUtcNow());
            var remaining = state.GetSnapshot();
            logger.LogInformation(
                "Telemetry worker ended as {Status}: {Processed} processed, {Pending} pending, {Outstanding} dispatched but uncompleted. Storage is process-local.",
                remaining.WorkerStatus, remaining.ProcessedCount,
                remaining.Buffer.PendingCount, remaining.OutstandingDispatchedCount);
            await PublishSafelyAsync("TelemetryWorkerStopped", null, stoppingToken);
        }
    }

    private async Task PublishSafelyAsync(string reason, Guid? sensorId, CancellationToken token)
    {
        try
        {
            await notifier.PublishAsync(reason, sensorId, token);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Real-time invalidation failed for {Reason}.", reason);
        }
    }
}