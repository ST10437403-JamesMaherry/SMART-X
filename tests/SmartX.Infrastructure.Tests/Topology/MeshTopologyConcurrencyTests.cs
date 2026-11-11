using System.Collections.Concurrent;

using SmartX.Application.Sensors;
using SmartX.Application.Topology;
using SmartX.Domain.Sensors;
using SmartX.Domain.Topology;
using SmartX.Infrastructure.Sensors;
using SmartX.Infrastructure.Topology;

namespace SmartX.Infrastructure.Tests.Topology;

/// <summary>
/// P3-04 Commit 3: genuinely concurrent Task.Run workers exercise the shared
/// mesh gate rather than relying on synchronously completed Task.WhenAll calls.
/// No timing-based throughput assertions are used; every test checks graph
/// correctness and consistency at explicit operation boundaries.
/// </summary>
public sealed class MeshTopologyConcurrencyTests
{
    [Fact]
    public async Task ParallelIndependentAdds_PreserveEveryEdgeAndBothDirections()
    {
        const int sensorCount = 48;
        var fixture = new MeshTopologyScenario();
        var sensors = new List<SensorProfile>(sensorCount);
        for (int i = 1; i <= sensorCount; i++)
        {
            sensors.Add(await fixture.RegisterAsync(i));
        }

        Task[] writers = sensors.Select((sensor, index) => Task.Run(async () =>
        {
            await Task.Yield();
            await fixture.Store.AddLinkAsync(MeshTopologyScenario.Link(
                1000 + index, MeshNodeKey.Gateway,
                MeshTopologyScenario.Key(sensor), 1 + index));
        })).ToArray();

        await Task.WhenAll(writers);
        MeshGraphSnapshot snapshot = await fixture.Store.GetSnapshotAsync();
        MeshTopologyAssertions.AssertCoherent(snapshot);
        Assert.Equal(sensorCount + 1, snapshot.Nodes.Count);
        Assert.Equal(sensorCount, snapshot.Links.Count);
        Assert.Equal(2L * sensorCount, snapshot.Version);
        Assert.Equal(sensorCount, (await fixture.Store.GetIncidentLinksAsync(
            MeshNodeKey.Gateway)).Count);
        await MeshTopologyAssertions.AssertAdjacencyMatchesSnapshotAsync(fixture.Store);
    }

    [Fact]
    public async Task ParallelMirroredPairAdds_ExactlyOneOfManyUniqueIdsSucceeds()
    {
        var fixture = new MeshTopologyScenario();
        SensorProfile sensor = await fixture.RegisterAsync(1);
        MeshNodeKey key = MeshTopologyScenario.Key(sensor);

        Task<bool>[] submissions = Enumerable.Range(0, 64)
            .Select(index => Task.Run(async () =>
            {
                await Task.Yield();
                MeshNodeKey first = index % 2 == 0 ? key : MeshNodeKey.Gateway;
                MeshNodeKey second = index % 2 == 0 ? MeshNodeKey.Gateway : key;

                try
                {
                    await fixture.Store.AddLinkAsync(MeshTopologyScenario.Link(
                        2000 + index, first, second, 1 + index));
                    return true;
                }
                catch (InvalidMeshTopologyException)
                {
                    return false;
                }
            }))
            .ToArray();

        bool[] accepted = await Task.WhenAll(submissions);
        Assert.Equal(1, accepted.Count(result => result));
        MeshGraphSnapshot snapshot = await fixture.Store.GetSnapshotAsync();
        Assert.Equal(2L, snapshot.Version); // One sensor + one undirected edge.
        Assert.Single(snapshot.Links);
        await MeshTopologyAssertions.AssertAdjacencyMatchesSnapshotAsync(fixture.Store);
    }

    [Fact]
    public async Task ParallelSharedLinkIdAcrossDifferentPairs_ExactlyOneWins()
    {
        const int sensorCount = 24;
        var fixture = new MeshTopologyScenario();
        var sensors = new List<SensorProfile>(sensorCount);
        for (int i = 1; i <= sensorCount; i++)
        {
            sensors.Add(await fixture.RegisterAsync(i));
        }

        Guid sharedId = MeshTopologyScenario.LinkId(3000);
        Task<bool>[] writers = sensors.Select(sensor => Task.Run(async () =>
        {
            try
            {
                await fixture.Store.AddLinkAsync(new MeshLink(
                    sharedId, MeshNodeKey.Gateway,
                    MeshTopologyScenario.Key(sensor), 2));
                return true;
            }
            catch (InvalidMeshTopologyException)
            {
                return false;
            }
        })).ToArray();

        bool[] accepted = await Task.WhenAll(writers);
        Assert.Equal(1, accepted.Count(result => result));
        MeshGraphSnapshot snapshot = await fixture.Store.GetSnapshotAsync();
        Assert.Equal(sensorCount + 1, snapshot.Nodes.Count);
        Assert.Equal(sensorCount + 1L, snapshot.Version);
        Assert.Equal(sharedId, Assert.Single(snapshot.Links).Id);
        await MeshTopologyAssertions.AssertAdjacencyMatchesSnapshotAsync(fixture.Store);
    }

    [Fact]
    public async Task ParallelCostUpdatesAndReaders_ObserveWholeImmutableRevisions()
    {
        const int sensorCount = 8;
        const int writerCount = 6;
        const int updatesPerWriter = 30;
        const int readerCount = 6;
        const int snapshotsPerReader = 45;

        var fixture = new MeshTopologyScenario();
        for (int i = 1; i <= sensorCount; i++)
        {
            SensorProfile sensor = await fixture.RegisterAsync(i);
            await fixture.Store.AddLinkAsync(MeshTopologyScenario.Link(
                4000 + i, MeshNodeKey.Gateway,
                MeshTopologyScenario.Key(sensor), 1d));
        }

        Guid changingLink = MeshTopologyScenario.LinkId(4001);
        MeshGraphSnapshot before = await fixture.Store.GetSnapshotAsync();
        var collected = new ConcurrentBag<MeshGraphSnapshot>();

        Task[] writers = Enumerable.Range(0, writerCount).Select(writer =>
            Task.Run(async () =>
            {
                for (int step = 0; step < updatesPerWriter; step++)
                {
                    // Every cost differs from the initial weight and all
                    // other submitted costs; every successful write is real.
                    double cost = 1000 + writer * updatesPerWriter + step;
                    await fixture.Store.UpdateLinkCostAsync(changingLink, cost);
                    await Task.Yield();
                }
            })).ToArray();

        Task[] readers = Enumerable.Range(0, readerCount).Select(_ =>
            Task.Run(async () =>
            {
                long previousVersion = before.Version;
                for (int step = 0; step < snapshotsPerReader; step++)
                {
                    MeshGraphSnapshot snapshot =
                        await fixture.Store.GetSnapshotAsync();
                    MeshTopologyAssertions.AssertCoherent(snapshot);
                    Assert.Equal(sensorCount, snapshot.Links.Count);
                    Assert.True(snapshot.Version >= previousVersion);
                    previousVersion = snapshot.Version;
                    collected.Add(snapshot);
                    await Task.Yield();
                }
            })).ToArray();

        await Task.WhenAll(writers.Concat(readers));

        MeshGraphSnapshot after = await fixture.Store.GetSnapshotAsync();
        Assert.Equal(before.Version + writerCount * updatesPerWriter,
            after.Version);
        Assert.Equal(1d, before.Links.Single(link => link.Id == changingLink)
            .SimulatedEnergyCost);
        Assert.Equal(sensorCount, after.Links.Count);

        // Re-reading captured snapshots after all writers finish proves that
        // updates never changed objects already handed to a reader.
        foreach (MeshGraphSnapshot captured in collected)
        {
            MeshTopologyAssertions.AssertCoherent(captured);
        }

        await MeshTopologyAssertions.AssertAdjacencyMatchesSnapshotAsync(fixture.Store);
    }

    [Fact]
    public async Task ParallelAddRemoveReaddWithReaders_NeverPublishesHalfAnEdge()
    {
        const int sensorCount = 32;
        const int readerCount = 5;
        var fixture = new MeshTopologyScenario();
        var sensors = new List<SensorProfile>(sensorCount);
        for (int i = 1; i <= sensorCount; i++)
        {
            sensors.Add(await fixture.RegisterAsync(i));
        }

        Task[] writers = sensors.Select((sensor, index) => Task.Run(async () =>
        {
            MeshLink link = MeshTopologyScenario.Link(
                5000 + index, MeshNodeKey.Gateway,
                MeshTopologyScenario.Key(sensor), 1 + index);
            await fixture.Store.AddLinkAsync(link);
            await Task.Yield();
            Assert.True(await fixture.Store.RemoveLinkAsync(link.Id));
            await Task.Yield();
            await fixture.Store.AddLinkAsync(link);
        })).ToArray();

        Task[] readers = Enumerable.Range(0, readerCount).Select(_ =>
            Task.Run(async () =>
            {
                for (int read = 0; read < 60; read++)
                {
                    MeshGraphSnapshot snapshot =
                        await fixture.Store.GetSnapshotAsync();
                    MeshTopologyAssertions.AssertCoherent(snapshot);
                    Assert.InRange(snapshot.Links.Count, 0, sensorCount);
                    await Task.Yield();
                }
            })).ToArray();

        await Task.WhenAll(writers.Concat(readers));
        MeshGraphSnapshot final = await fixture.Store.GetSnapshotAsync();
        Assert.Equal(sensorCount, final.Links.Count);
        Assert.Equal(4L * sensorCount, final.Version);
        await MeshTopologyAssertions.AssertAdjacencyMatchesSnapshotAsync(fixture.Store);
    }

    [Fact]
    public async Task ConcurrentRegistrationAndRead_OnlyProjectsRealVerticesWithoutLinks()
    {
        const int sensorCount = 40;
        var fixture = new MeshTopologyScenario();
        SensorProfile[] profiles = Enumerable.Range(1, sensorCount)
            .Select(MeshTopologyScenario.NewProfile)
            .ToArray();

        Task[] writers = profiles.Select(profile => Task.Run(async () =>
        {
            Assert.True(await fixture.Registry.TryAddAsync(profile));
            await Task.Yield();
        })).ToArray();

        Task[] readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            for (int read = 0; read < 40; read++)
            {
                MeshGraphSnapshot snapshot = await fixture.Store.GetSnapshotAsync();
                MeshTopologyAssertions.AssertCoherent(snapshot);
                Assert.Empty(snapshot.Links);
                await Task.Yield();
            }
        })).ToArray();

        await Task.WhenAll(writers.Concat(readers));
        MeshGraphSnapshot final = await fixture.Store.GetSnapshotAsync();
        Assert.Equal(sensorCount + 1, final.Nodes.Count);
        Assert.Equal(sensorCount, final.Version);
        Assert.Empty(final.Links);
        Assert.Equal(profiles.Select(profile => MeshTopologyScenario.Key(profile))
                .OrderBy(key => key).ToArray(),
            final.Nodes.Skip(1).Select(node => node.Key).ToArray());
    }

    [Fact]
    public async Task CancelledWriterWaitingForRegistryRead_DoesNotMutateOrDeadlock()
    {
        var registry = new OneTimeBlockingSensorRepository();
        SensorProfile sensor = MeshTopologyScenario.NewProfile(1);
        Assert.True(await registry.TryAddAsync(sensor));
        var store = new InMemoryMeshTopologyStore(registry);
        MeshNodeKey key = MeshTopologyScenario.Key(sensor);
        MeshLink link = MeshTopologyScenario.Link(6001, MeshNodeKey.Gateway, key);

        // The first operation holds the graph semaphore while waiting for a
        // simulated slow registry read. Cancellation must remove the queued
        // writer without observing or publishing a partially added link.
        Task<MeshGraphSnapshot> firstRead = store.GetSnapshotAsync();
        await registry.WaitUntilReadStartedAsync();

        using var cancelled = new CancellationTokenSource();
        Task<MeshLink> queuedWrite = store.AddLinkAsync(link, cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queuedWrite);

        registry.ReleaseRead();
        MeshGraphSnapshot baseline = await firstRead;
        Assert.Empty(baseline.Links);
        Assert.Equal(1L, baseline.Version);

        await store.AddLinkAsync(link);
        MeshGraphSnapshot after = await store.GetSnapshotAsync();
        Assert.Equal(2L, after.Version);
        Assert.Single(after.Links);
        await MeshTopologyAssertions.AssertAdjacencyMatchesSnapshotAsync(store);
    }

    /// <summary>
    /// A controlled fake for testing cancellation at the async registry
    /// boundary. All normal registry operations still use the real in-memory
    /// repository, including canonical IDs and synchronized registration.
    /// </summary>
    private sealed class OneTimeBlockingSensorRepository : ISensorRepository
    {
        private readonly ISensorRepository _inner = new InMemorySensorRepository();
        private readonly TaskCompletionSource<bool> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _shouldBlock = 1;

        public Task WaitUntilReadStartedAsync() =>
            _entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public void ReleaseRead() => _release.TrySetResult(true);

        public async Task<IReadOnlyList<SensorProfile>> GetAllAsync(
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _shouldBlock, 0) == 1)
            {
                _entered.TrySetResult(true);
                await _release.Task.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            return await _inner.GetAllAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        public Task<bool> TryAddAsync(
            SensorProfile sensor,
            CancellationToken cancellationToken = default) =>
            _inner.TryAddAsync(sensor, cancellationToken);

        public Task<SensorProfile?> GetByIdAsync(
            Guid sensorProfileId,
            CancellationToken cancellationToken = default) =>
            _inner.GetByIdAsync(sensorProfileId, cancellationToken);

        public Task<SensorProfile?> GetByDeviceIdentifierAsync(
            DeviceIdentifier deviceIdentifier,
            CancellationToken cancellationToken = default) =>
            _inner.GetByDeviceIdentifierAsync(deviceIdentifier, cancellationToken);

        public Task<bool> UpdateAsync(
            SensorProfile sensor,
            CancellationToken cancellationToken = default) =>
            _inner.UpdateAsync(sensor, cancellationToken);
    }
}