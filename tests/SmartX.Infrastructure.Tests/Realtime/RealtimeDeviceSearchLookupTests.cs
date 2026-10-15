using SmartX.Application.Dashboard;
using SmartX.Application.Realtime;
using SmartX.Application.Sensors;
using SmartX.Domain.Dashboard;
using SmartX.Domain.Sensors;
using SmartX.Infrastructure.Commands;
using SmartX.Infrastructure.Realtime;
using SmartX.Infrastructure.Sensors;

namespace SmartX.Infrastructure.Tests.Realtime;

/// <summary>
/// Proves P2-08 exact queries take the existing canonical Dictionary path.
/// These tests count repository calls instead of assuming that a matching
/// response necessarily demonstrates O(1) lookup.
/// </summary>
public sealed class RealtimeDeviceSearchLookupTests
{
    [Fact]
    public async Task ExactMacSearch_NeverCallsRegistryScan()
    {
        var repository = new CountingSensorRepository();
        SensorProfile sensor = CreateSensor("AA:BB:CC:DD:EE:81", "Unit Pump");
        Assert.True(await repository.TryAddAsync(sensor));

        RealtimeDeviceSearchService service = CreateService(repository);

        var result = await service.SearchAsync(new RealtimeDeviceSearchQuery(
            DeviceIdentifier: "aabbccddee81",
            IdentifierKind: "MacAddress",
            Text: "pump"));

        Assert.Equal(sensor.Id, Assert.Single(result.Devices).Device.Id);
        Assert.Equal(1, repository.IdentifierLookupCalls);
        Assert.Equal(0, repository.GetAllCalls);
    }

    [Fact]
    public async Task MissingCanonicalIdentifier_ReturnsEmptyWithoutRegistryScan()
    {
        var repository = new CountingSensorRepository();
        RealtimeDeviceSearchService service = CreateService(repository);

        var result = await service.SearchAsync(new RealtimeDeviceSearchQuery(
            DeviceIdentifier: "aa-bb-cc-dd-ee-82",
            IdentifierKind: "MacAddress"));

        Assert.Empty(result.Devices);
        Assert.Equal(0, result.TotalMatches);
        Assert.Equal(1, repository.IdentifierLookupCalls);
        Assert.Equal(0, repository.GetAllCalls);
    }

    [Fact]
    public async Task TextSearch_ScansOnlyExistingOrderedRegistrySnapshot()
    {
        var repository = new CountingSensorRepository();
        SensorProfile first = CreateSensor("AA:BB:CC:DD:EE:83", "North Pump");
        SensorProfile second = CreateSensor("AA:BB:CC:DD:EE:84", "South Relay");
        Assert.True(await repository.TryAddAsync(first));
        Assert.True(await repository.TryAddAsync(second));

        RealtimeDeviceSearchService service = CreateService(repository);

        var result = await service.SearchAsync(new RealtimeDeviceSearchQuery(
            Text: "north",
            Category: "Environmental"));

        Assert.Equal(first.Id, Assert.Single(result.Devices).Device.Id);
        Assert.Equal(1, repository.GetAllCalls);
        Assert.Equal(0, repository.IdentifierLookupCalls);
    }

    [Fact]
    public async Task MalformedQuery_RejectsBeforeAnyRepositoryAccess()
    {
        var repository = new CountingSensorRepository();
        RealtimeDeviceSearchService service = CreateService(repository);

        await Assert.ThrowsAsync<InvalidRealtimeDeviceSearchQueryException>(() =>
            service.SearchAsync(new RealtimeDeviceSearchQuery(
                DeviceIdentifier: "broken-mac",
                IdentifierKind: "MacAddress")));

        Assert.Equal(0, repository.GetAllCalls);
        Assert.Equal(0, repository.IdentifierLookupCalls);
    }

    [Fact]
    public async Task CancelledSearch_DoesNotReadAnyRegistryOrConditionState()
    {
        var repository = new CountingSensorRepository();
        RealtimeDeviceSearchService service = CreateService(repository);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SearchAsync(new RealtimeDeviceSearchQuery(), cancellation.Token));

        Assert.Equal(0, repository.GetAllCalls);
        Assert.Equal(0, repository.IdentifierLookupCalls);
    }

    private static RealtimeDeviceSearchService CreateService(
        ISensorRepository repository)
    {
        var clock = TimeProvider.System;
        var buffer = new InMemoryTelemetryWorkBuffer();
        var processing = new InMemoryTelemetryProcessingStore(buffer);
        var commands = new InMemoryActuatorCommandStore();
        var issues = new DashboardIssueCollection();

        var snapshotService = new RealtimeSnapshotService(
            repository,
            clock,
            processing,
            commands);

        var dashboardService = new DashboardIssueService(issues, clock);

        return new RealtimeDeviceSearchService(
            snapshotService,
            dashboardService,
            clock);
    }

    private static SensorProfile CreateSensor(string mac, string name)
    {
        return SensorProfile.Create(
            name,
            DeviceIdentifier.FromMacAddress(mac),
            new DeploymentLocation("Lookup Lab", "Zone 2", "Node 1"),
            SensorCategory.Environmental,
            TelemetryDataType.Float,
            DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    private sealed class CountingSensorRepository : ISensorRepository
    {
        private readonly InMemorySensorRepository _inner = new();

        public int GetAllCalls { get; private set; }
        public int IdentifierLookupCalls { get; private set; }

        public Task<bool> TryAddAsync(
            SensorProfile sensor,
            CancellationToken cancellationToken = default) =>
            _inner.TryAddAsync(sensor, cancellationToken);

        public Task<IReadOnlyList<SensorProfile>> GetAllAsync(
            CancellationToken cancellationToken = default)
        {
            GetAllCalls++;
            return _inner.GetAllAsync(cancellationToken);
        }

        public Task<SensorProfile?> GetByIdAsync(
            Guid sensorProfileId,
            CancellationToken cancellationToken = default) =>
            _inner.GetByIdAsync(sensorProfileId, cancellationToken);

        public Task<SensorProfile?> GetByDeviceIdentifierAsync(
            DeviceIdentifier deviceIdentifier,
            CancellationToken cancellationToken = default)
        {
            IdentifierLookupCalls++;
            return _inner.GetByDeviceIdentifierAsync(deviceIdentifier, cancellationToken);
        }

        public Task<bool> UpdateAsync(
            SensorProfile sensor,
            CancellationToken cancellationToken = default) =>
            _inner.UpdateAsync(sensor, cancellationToken);
    }
}