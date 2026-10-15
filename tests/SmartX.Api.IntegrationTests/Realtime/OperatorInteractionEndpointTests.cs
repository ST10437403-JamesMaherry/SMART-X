using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using SmartX.Contracts.Commands;
using SmartX.Contracts.Realtime;
using SmartX.Contracts.Sensors;

namespace SmartX.Api.IntegrationTests.Realtime;

/// <summary>Real HTTP proof that reads are passive and intentional posts are auditable.</summary>
public sealed class OperatorInteractionEndpointTests : IDisposable
{
    private readonly string _storage = Path.Combine(Path.GetTempPath(), "smartx-p208-interactions", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public OperatorInteractionEndpointTests()
    {
        Directory.CreateDirectory(_storage);
        _factory = new Factory(_storage);
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
    }

    [Fact]
    public async Task PassiveReads_DoNotRecord_ButSubmittedSearchDoes()
    {
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK,
                (await _client.GetAsync("/api/realtime/devices/search?text=absent")).StatusCode);
            await _client.GetAsync("/api/realtime/snapshot");
        }
        Assert.Equal(0, (await History()).TotalRecorded);

        var submission = new RecordDeviceSearchRequest("SearchSubmitted", null, null,
            "absent", null, null, null, null, null, null, null, null);
        var recorded = await _client.PostAsJsonAsync("/api/realtime/interactions/search", submission);
        Assert.Equal(HttpStatusCode.Created, recorded.StatusCode);
        OperatorInteractionResponse? item = await recorded.Content.ReadFromJsonAsync<OperatorInteractionResponse>();
        Assert.NotNull(item);
        Assert.Equal("SearchSubmitted", item.Kind);
        Assert.Equal(0, item.ResultCount);
        Assert.Equal(TimeSpan.Zero, item.OccurredAtUtc.Offset);
        Assert.Equal(1, (await History()).TotalRecorded);
        await History();
        Assert.Equal(1, (await History()).TotalRecorded);
    }

    [Fact]
    public async Task ViewsAndFilters_RequireRealServerContext()
    {
        var response = await _client.PostAsJsonAsync("/api/sensors", new RegisterSensorRequest(
            "Interaction Sensor", "interaction-" + Guid.NewGuid().ToString("N"),
            "UniqueIdentifier", new SensorDeploymentLocationRequest("Facility", "Zone", "Node"),
            "Environmental", "Float"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        SensorResponse sensor = (await response.Content.ReadFromJsonAsync<SensorResponse>())!;

        var view = await _client.PostAsJsonAsync("/api/realtime/interactions/view",
            new RecordOperatorViewRequest(sensor.Id, null));
        Assert.Equal(HttpStatusCode.Created, view.StatusCode);

        var invalid = await _client.PostAsJsonAsync("/api/realtime/interactions/view",
            new RecordOperatorViewRequest(Guid.NewGuid(), null));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var filter = new RecordDeviceSearchRequest("FiltersChanged", null, null,
            "Interaction", "Environmental", null, null, null, null, null, sensor.Id, null);
        var accepted = await _client.PostAsJsonAsync("/api/realtime/interactions/search", filter);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);

        var forgedSelection = filter with { SelectedDeviceId = Guid.NewGuid() };
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PostAsJsonAsync("/api/realtime/interactions/search", forgedSelection)).StatusCode);
        Assert.Equal(2, (await History()).TotalRecorded);
    }

    [Fact]
    public async Task InvalidInteractionsAndHistoryLimits_DoNotCreateEvidence()
    {
        var fakeCommand = await _client.PostAsJsonAsync("/api/realtime/interactions/search",
            new RecordDeviceSearchRequest("CommandExecuted", null, null, null,
                null, null, null, null, null, null, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, fakeCommand.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.GetAsync("/api/realtime/interactions?limit=201")).StatusCode);
        Assert.Equal(0, (await History()).TotalRecorded);
    }

    [Fact]
    public async Task Commands_RecordOnlyServerAcceptedOperations_NotFailuresOrGetReads()
    {
        var register = await _client.PostAsJsonAsync("/api/sensors", new RegisterSensorRequest(
            "Relay for training", "relay-" + Guid.NewGuid().ToString("N"),
            "UniqueIdentifier", new SensorDeploymentLocationRequest("Facility", "Zone", "Node"),
            "Actuator", "Boolean"));
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        SensorResponse device = (await register.Content.ReadFromJsonAsync<SensorResponse>())!;
        Assert.Equal(HttpStatusCode.OK, (await _client.PatchAsJsonAsync(
            $"/api/sensors/{device.Id}/status", new ChangeSensorStatusRequest("Online"))).StatusCode);

        string commandUrl = $"/api/realtime/devices/{device.Id}/commands";
        await _client.GetAsync(commandUrl);
        Assert.Equal(0, (await History()).TotalRecorded);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PostAsJsonAsync(commandUrl, new RelayCommandRequest("Invalid"))).StatusCode);
        Assert.Equal(0, (await History()).TotalRecorded);

        var command = await _client.PostAsJsonAsync(commandUrl,
            new RelayCommandRequest("RelayOn"));
        Assert.Equal(HttpStatusCode.OK, command.StatusCode);
        RelayCommandResponse accepted = (await command.Content.ReadFromJsonAsync<RelayCommandResponse>())!;

        // A no-op is still an accepted, auditable manual command.
        Assert.Equal(HttpStatusCode.OK,
            (await _client.PostAsJsonAsync(commandUrl, new RelayCommandRequest("RelayOn"))).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict,
            (await _client.PostAsJsonAsync(commandUrl + "/undo",
                new UndoRelayCommandRequest(Guid.NewGuid()))).StatusCode);
        Assert.Equal(2, (await History()).TotalRecorded);

        var undo = await _client.PostAsJsonAsync(commandUrl + "/undo",
            new UndoRelayCommandRequest(accepted.Command.CommandId));
        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        var records = await History();
        Assert.Equal(3, records.TotalRecorded);
        Assert.Equal(new[] { "CommandUndone", "CommandExecuted", "CommandExecuted" },
            records.Events.Select(x => x.Kind));
        Assert.Equal(device.Id, records.Events[0].DeviceId);
        Assert.All(records.Events, x => Assert.NotNull(x.CommandId));
        Assert.Equal(3, records.Events.Select(x => x.CommandId).Distinct().Count());

        await _client.GetAsync(commandUrl);
        await _client.GetAsync("/api/realtime/snapshot");
        Assert.Equal(3, (await History()).TotalRecorded);
    }

    private async Task<OperatorInteractionHistoryResponse> History() =>
        (await _client.GetFromJsonAsync<OperatorInteractionHistoryResponse>(
            "/api/realtime/interactions?limit=50"))!;

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        if (Directory.Exists(_storage)) Directory.Delete(_storage, true);
    }

    private sealed class Factory(string path) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Attachments:StoragePath", path);
            builder.UseSetting("Attachments:EncryptionKeyBase64",
                Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray()));
        }
    }
}