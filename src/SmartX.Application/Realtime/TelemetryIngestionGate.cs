using System.Collections.Concurrent;

namespace SmartX.Application.Realtime;

/// <summary>
/// One singleton gate per registered sensor serializes predecessor reads,
/// persistence, admission and the single Part 1 analysis for each request.
/// Different sensors remain independent. Entries share the registry lifetime.
/// </summary>
public sealed class TelemetryIngestionGate : IDisposable
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();

    public async Task<IDisposable> EnterAsync(Guid sensorProfileId, CancellationToken cancellationToken)
    {
        var gate = _gates.GetOrAdd(sensorProfileId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(gate);
    }

    /// <summary>Dispose only after the host has finished its active ingestion requests.</summary>
    public void Dispose()
    {
        foreach (var gate in _gates.Values)
        {
            gate.Dispose();
        }
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }
}