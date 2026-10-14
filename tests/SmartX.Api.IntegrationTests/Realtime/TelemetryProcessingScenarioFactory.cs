using System.Collections.Concurrent;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using SmartX.Application.Realtime;
using SmartX.Domain.Realtime;

namespace SmartX.Api.IntegrationTests.Realtime;

/// <summary>
/// Runs the production worker, buffer, persistence and processor. A scoped
/// decorator records every completed dispatch independently of the public
/// 100-record diagnostics window. Its optional first-dispatch gate controls
/// scheduling in tests without adding any delay or test switch to production.
/// </summary>
internal sealed class TelemetryProcessingScenarioFactory(bool blockFirst = false, bool failFirst = false)
    : WebApplicationFactory<Program>
{
    private readonly string _storagePath = Path.Combine(Path.GetTempPath(), "smartx-priority-tests", Guid.NewGuid().ToString("N"));
    public TelemetryProcessingProbe Probe { get; } = new(blockFirst, failFirst);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Attachments:StoragePath", _storagePath);
        builder.UseSetting("Attachments:EncryptionKeyBase64", Convert.ToBase64String(new byte[32]));
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(Probe);
            services.AddScoped<TelemetryProcessingService>();
            services.RemoveAll<ITelemetryProcessor>();
            services.AddScoped<ITelemetryProcessor, RecordingTelemetryProcessor>();
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_storagePath)) Directory.Delete(_storagePath, recursive: true);
    }
}

internal sealed class TelemetryProcessingProbe(bool blockFirst, bool failFirst)
{
    private readonly ConcurrentQueue<TelemetryDispatch> _started = new();
    private readonly ConcurrentQueue<TelemetryDispatch> _completed = new();
    private int _active;
    private int _maximumActive;
    public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TelemetryDispatch[] Started => _started.ToArray();
    public TelemetryDispatch[] Completed => _completed.ToArray();
    public int MaximumActive => Volatile.Read(ref _maximumActive);

    public async Task BeforeAsync(TelemetryDispatch dispatch, CancellationToken token)
    {
        var active = Interlocked.Increment(ref _active);
        int previous;
        do
        {
            previous = Volatile.Read(ref _maximumActive);
            if (previous >= active) break;
        } while (Interlocked.CompareExchange(ref _maximumActive, active, previous) != previous);
        _started.Enqueue(dispatch);
        if (dispatch.DequeueSequence == 1)
        {
            FirstStarted.TrySetResult();
            if (blockFirst) await ReleaseFirst.Task.WaitAsync(token);
            if (failFirst) throw new InvalidOperationException("Injected processor failure with private test details.");
        }
    }

    public void RecordCompletion(TelemetryDispatch dispatch) => _completed.Enqueue(dispatch);
    public void Leave() => Interlocked.Decrement(ref _active);
}

internal sealed class RecordingTelemetryProcessor(TelemetryProcessingService processor, TelemetryProcessingProbe probe)
    : ITelemetryProcessor
{
    public async Task ProcessAsync(TelemetryDispatch dispatch, CancellationToken cancellationToken)
    {
        try
        {
            await probe.BeforeAsync(dispatch, cancellationToken);
            await processor.ProcessAsync(dispatch, cancellationToken);
            probe.RecordCompletion(dispatch);
        }
        finally
        {
            probe.Leave();
        }
    }
}