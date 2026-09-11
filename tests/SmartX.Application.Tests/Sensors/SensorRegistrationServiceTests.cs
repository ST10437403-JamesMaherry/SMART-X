using SmartX.Application.Sensors;
using SmartX.Contracts.Sensors;
using SmartX.Domain.Sensors;

namespace SmartX.Application.Tests.Sensors;

public sealed class SensorRegistrationServiceTests
{
    private static readonly DateTimeOffset FixedUtcNow =
        new(
            2026,
            9,
            11,
            8,
            0,
            0,
            TimeSpan.Zero);

    [Fact]
    public async Task RegisterAsync_WhenValid_PersistsAndReturnsSensor()
    {
        var repository =
            new RecordingSensorRepository();

        var service =
            CreateService(repository);

        var response =
            await service.RegisterAsync(
                CreateValidRequest());

        Assert.Equal(
            1,
            repository.TryAddCallCount);

        Assert.NotNull(
            repository.LastSensor);

        Assert.Equal(
            repository.LastSensor.Id,
            response.Id);

        Assert.Equal(
            "AA:BB:CC:DD:EE:FF",
            response.DeviceIdentifier);

        Assert.Equal(
            FixedUtcNow,
            response.CreatedAtUtc);

        Assert.Equal(
            FixedUtcNow,
            response.UpdatedAtUtc);
    }

    [Fact]
    public async Task RegisterAsync_WhenDuplicateIdentifier_ThrowsDuplicateException()
    {
        var repository =
            new RecordingSensorRepository
            {
                TryAddResult = false
            };

        var service =
            CreateService(repository);

        var exception =
            await Assert.ThrowsAsync<
                DuplicateSensorIdentifierException>(
                () => service.RegisterAsync(
                    CreateValidRequest()));

        Assert.Equal(
            "AA:BB:CC:DD:EE:FF",
            exception.DeviceIdentifier);

        Assert.Contains(
            "already registered",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal(
            1,
            repository.TryAddCallCount);
    }

    [Fact]
    public async Task RegisterAsync_WhenMacFormattingDiffers_UsesNormalisedIdentifier()
    {
        var repository =
            new RecordingSensorRepository();

        var service =
            CreateService(repository);

        var request =
            CreateValidRequest() with
            {
                DeviceIdentifier =
                    "aabbccddeeff"
            };

        await service.RegisterAsync(request);

        Assert.NotNull(
            repository.LastSensor);

        Assert.Equal(
            "AA:BB:CC:DD:EE:FF",
            repository.LastSensor.DeviceIdentifier.Value);
    }

    [Fact]
    public async Task RegisterAsync_WhenCancellationAlreadyRequested_DoesNotCallRepository()
    {
        var repository =
            new RecordingSensorRepository();

        var service =
            CreateService(repository);

        using var cancellationSource =
            new CancellationTokenSource();

        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<
            OperationCanceledException>(
            () => service.RegisterAsync(
                CreateValidRequest(),
                cancellationSource.Token));

        Assert.Equal(
            0,
            repository.TryAddCallCount);
    }

    [Fact]
    public async Task RegisterAsync_PassesCancellationTokenToRepository()
    {
        var repository =
            new RecordingSensorRepository();

        var service =
            CreateService(repository);

        using var cancellationSource =
            new CancellationTokenSource();

        await service.RegisterAsync(
            CreateValidRequest(),
            cancellationSource.Token);

        Assert.Equal(
            cancellationSource.Token,
            repository.LastCancellationToken);
    }

    [Fact]
    public async Task RegisterAsync_WhenRepositoryCancels_PropagatesCancellation()
    {
        var repository =
            new RecordingSensorRepository();

        var service =
            CreateService(repository);

        using var cancellationSource =
            new CancellationTokenSource();

        repository.OnTryAddAsync =
            cancellationToken =>
            {
                cancellationSource.Cancel();

                return Task.FromCanceled<bool>(
                    cancellationToken);
            };

        await Assert.ThrowsAnyAsync<
            OperationCanceledException>(
            () => service.RegisterAsync(
                CreateValidRequest(),
                cancellationSource.Token));

        Assert.Equal(
            1,
            repository.TryAddCallCount);
    }

    [Fact]
    public async Task RegisterAsync_WhenRequestNull_RejectsBeforeRepository()
    {
        var repository =
            new RecordingSensorRepository();

        var service =
            CreateService(repository);

        var exception =
            await Assert.ThrowsAsync<
                SensorRegistrationValidationException>(
                () => service.RegisterAsync(
                    null!));

        Assert.Contains(
            "registration request",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal(
            0,
            repository.TryAddCallCount);
    }

    [Fact]
    public async Task RegisterAsync_WhenContractInvalid_DoesNotPersistSensor()
    {
        var repository =
            new RecordingSensorRepository();

        var service =
            CreateService(repository);

        var invalidRequest =
            CreateValidRequest() with
            {
                Category = "UnsupportedCategory"
            };

        await Assert.ThrowsAsync<
            SensorRegistrationValidationException>(
            () => service.RegisterAsync(
                invalidRequest));

        Assert.Equal(
            0,
            repository.TryAddCallCount);
    }

    private static SensorRegistrationService CreateService(
        RecordingSensorRepository repository)
    {
        return new SensorRegistrationService(
            repository,
            new FixedTimeProvider(
                FixedUtcNow));
    }

    private static RegisterSensorRequest CreateValidRequest()
    {
        return new RegisterSensorRequest(
            "Greenhouse Sensor",
            "aa-bb-cc-dd-ee-ff",
            "MacAddress",
            new SensorDeploymentLocationRequest(
                "Facility A",
                "Zone 1",
                "Node 14"),
            GetFirstDefinedEnumValue<SensorCategory>()
                .ToString(),
            GetFirstDefinedEnumValue<TelemetryDataType>()
                .ToString());
    }

    private static TEnum GetFirstDefinedEnumValue<TEnum>()
        where TEnum : struct, Enum
    {
        return Enum.GetValues<TEnum>()[0];
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(
            DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }
    }

    private sealed class RecordingSensorRepository
        : ISensorRepository
    {
        public bool TryAddResult
        {
            get;
            set;
        } = true;

        public int TryAddCallCount
        {
            get;
            private set;
        }

        public SensorProfile? LastSensor
        {
            get;
            private set;
        }

        public CancellationToken LastCancellationToken
        {
            get;
            private set;
        }

        public Func<
            CancellationToken,
            Task<bool>>? OnTryAddAsync
        {
            get;
            set;
        }

        public Task<bool> TryAddAsync(
            SensorProfile sensor,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(sensor);

            TryAddCallCount++;

            LastSensor =
                sensor;

            LastCancellationToken =
                cancellationToken;

            if (OnTryAddAsync is not null)
            {
                return OnTryAddAsync(
                    cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(
                TryAddResult);
        }
    }
}