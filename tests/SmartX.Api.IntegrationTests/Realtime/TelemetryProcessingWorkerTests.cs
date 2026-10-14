using System.Collections.Concurrent;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using SmartX.Api.Realtime;
using SmartX.Application.Realtime;
using SmartX.Domain.Dashboard;
using SmartX.Domain.Realtime;
using SmartX.Domain.Sensors;
using SmartX.Domain.Telemetry;
using SmartX.Infrastructure.Realtime;

namespace SmartX.Api.IntegrationTests.Realtime;

public sealed class TelemetryProcessingWorkerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Worker_UsesOneFreshScopePerDispatch_AndDisposesEachScope()
    {
        var buffer = new InMemoryTelemetryWorkBuffer();
        var state = new InMemoryTelemetryProcessingStore(buffer);
        var instances = new ConcurrentQueue<ScopedProcessor>();
        await using var services = new ServiceCollection()
            .AddScoped<ITelemetryProcessor>(_ =>
            {
                var processor = new ScopedProcessor((dispatch, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    state.Complete(dispatch, Now);
                    return Task.CompletedTask;
                });
                instances.Enqueue(processor);
                return processor;
            }).BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifier = new TestNotifier(reason =>
        {
            if (reason == "TelemetryProcessed" && state.GetSnapshot().ProcessedCount == 2) completed.TrySetResult();
        });
        using var worker = CreateWorker(buffer, state, services, notifier);
        Enqueue(buffer, 1);
        Enqueue(buffer, 2);
        await worker.StartAsync(CancellationToken.None);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);
        Assert.Equal(2, instances.Count);
        Assert.All(instances, instance => Assert.True(instance.Disposed));
        Assert.Equal(new long[] { 1, 2 }, state.GetSnapshot().RecentDispatches.Select(r => r.Dispatch.WorkItem.ArrivalSequence));
        Assert.Equal(TelemetryWorkerStatus.Stopped, state.GetSnapshot().WorkerStatus);
    }

    [Fact]
    public async Task StopWhileWaiting_PreservesPausedPendingWork()
    {
        var buffer = new InMemoryTelemetryWorkBuffer();
        buffer.SetOrdinaryDispatchPaused(true);
        Enqueue(buffer, 1);
        var state = new InMemoryTelemetryProcessingStore(buffer);
        await using var services = new ServiceCollection().BuildServiceProvider();
        using var worker = CreateWorker(buffer, state, services, new TestNotifier());
        await worker.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => state.GetSnapshot().WorkerStatus == TelemetryWorkerStatus.Waiting);
        await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        var snapshot = state.GetSnapshot();
        Assert.Equal(TelemetryWorkerStatus.Stopped, snapshot.WorkerStatus);
        Assert.Equal(1, snapshot.Buffer.PendingCount);
        Assert.Equal(0, snapshot.Buffer.DispatchedCount);
    }

    [Fact]
    public async Task ApplicationShutdown_CancelsProcessing_RetainsInterruptedPacket_AndDisposesScope()
    {
        var buffer = new InMemoryTelemetryWorkBuffer();
        var state = new InMemoryTelemetryProcessingStore(buffer);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedCancellation = false;
        var processor = new ScopedProcessor(async (_, token) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                observedCancellation = true;
                throw;
            }
        });
        await using var services = new ServiceCollection().AddScoped<ITelemetryProcessor>(_ => processor).BuildServiceProvider();
        using var worker = CreateWorker(buffer, state, services, new TestNotifier());
        var accepted = Enqueue(buffer, 1);
        Enqueue(buffer, 2);
        await worker.StartAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        var snapshot = state.GetSnapshot();
        Assert.True(observedCancellation);
        Assert.True(processor.Disposed);
        Assert.Equal(TelemetryWorkerStatus.Stopped, snapshot.WorkerStatus);
        Assert.Same(accepted, snapshot.CurrentDispatch!.WorkItem);
        Assert.Equal(TelemetryDispatchOutcome.Interrupted, Assert.Single(snapshot.RecentDispatches).Outcome);
        Assert.Equal(0, snapshot.ProcessedCount);
        Assert.Equal(1, snapshot.OutstandingDispatchedCount);
        Assert.Equal(1, snapshot.Buffer.PendingCount);
    }

    [Fact]
    public async Task ProcessingFailure_IsVisibleAndHaltsFurtherDispatch_EvenWhenNotificationsFail()
    {
        var buffer = new InMemoryTelemetryWorkBuffer();
        var state = new InMemoryTelemetryProcessingStore(buffer);
        await using var services = new ServiceCollection()
            .AddScoped<ITelemetryProcessor>(_ => new ScopedProcessor((_, _) =>
                Task.FromException(new InvalidOperationException("Injected failure with private details."))))
            .BuildServiceProvider();
        using var worker = CreateWorker(buffer, state, services, new ThrowingNotifier());
        var accepted = Enqueue(buffer, 1);
        Enqueue(buffer, 2);
        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);
        var snapshot = state.GetSnapshot();
        Assert.Equal(TelemetryWorkerStatus.Faulted, snapshot.WorkerStatus);
        Assert.Same(accepted, snapshot.CurrentDispatch!.WorkItem);
        Assert.Equal(1, snapshot.Buffer.DispatchedCount);
        Assert.Equal(1, snapshot.Buffer.PendingCount);
        Assert.Equal(0, snapshot.ProcessedCount);
        Assert.NotNull(snapshot.Failure);
        Assert.DoesNotContain("private details", snapshot.Failure);
    }

    [Fact]
    public async Task NotificationFailure_DoesNotTurnCompletedProcessingIntoWorkerFailure()
    {
        var buffer = new InMemoryTelemetryWorkBuffer();
        var state = new InMemoryTelemetryProcessingStore(buffer);
        await using var services = new ServiceCollection().AddScoped<ITelemetryProcessor>(_ =>
            new ScopedProcessor((dispatch, _) =>
            {
                state.Complete(dispatch, Now);
                return Task.CompletedTask;
            })).BuildServiceProvider();
        using var worker = CreateWorker(buffer, state, services, new ThrowingNotifier());
        Enqueue(buffer, 1);
        await worker.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => state.GetSnapshot().ProcessedCount == 1);
        await worker.StopAsync(CancellationToken.None);
        Assert.Null(state.GetSnapshot().Failure);
        Assert.Null(state.GetSnapshot().CurrentDispatch);
    }

    private static TelemetryWorkItem<int> Enqueue(InMemoryTelemetryWorkBuffer buffer, int value)
        => buffer.Enqueue(Guid.NewGuid(), new TelemetryPacket<int>(
            DeviceIdentifier.FromUniqueIdentifier("worker-node"), value, Now), DashboardIssueSeverity.Information, Now);

    private static TelemetryProcessingWorker CreateWorker(InMemoryTelemetryWorkBuffer buffer,
        InMemoryTelemetryProcessingStore state, ServiceProvider services, IRealtimeNotifier notifier)
        => new(buffer, state, services.GetRequiredService<IServiceScopeFactory>(), notifier,
            TimeProvider.System, NullLogger<TelemetryProcessingWorker>.Instance);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private sealed class ScopedProcessor(Func<TelemetryDispatch, CancellationToken, Task> process)
        : ITelemetryProcessor, IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public Task ProcessAsync(TelemetryDispatch dispatch, CancellationToken cancellationToken)
            => process(dispatch, cancellationToken);
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestNotifier(Action<string>? callback = null) : IRealtimeNotifier
    {
        public Task PublishAsync(string reason, Guid? sensorProfileId = null, CancellationToken cancellationToken = default)
        {
            callback?.Invoke(reason);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingNotifier : IRealtimeNotifier
    {
        public Task PublishAsync(string reason, Guid? sensorProfileId = null, CancellationToken cancellationToken = default)
            => Task.FromException(new InvalidOperationException("Injected notification failure."));
    }
}