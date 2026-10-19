using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using SmartX.Application.Recommendations;
using SmartX.Contracts.Commands;
using SmartX.Contracts.Dashboard;
using SmartX.Contracts.Realtime;
using SmartX.Contracts.Sensors;
using SmartX.Domain.Dashboard;
using SmartX.Domain.Recommendations;

namespace SmartX.Api.IntegrationTests.Recommendations;

/// <summary>
/// Real HTTP proof: registered sensors -> genuine HashSet condition ->
/// validated alert view -> canonical searched actuator association -> accepted
/// command -> Dictionary pattern evidence. No pattern statistics are seeded.
/// </summary>
public sealed class PredictivePatternLearningEndpointTests : IDisposable
{
    private readonly string _storage = Path.Combine(Path.GetTempPath(),
        "smartx-p209-patterns", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public PredictivePatternLearningEndpointTests()
    {
        Directory.CreateDirectory(_storage);
        _factory = new Factory(_storage);
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
    }

    [Fact]
    public async Task GenuineAlertAssociationAndSuccessfulCommand_TeachOneEpisode()
    {
        SensorResponse source = await Register("Source", "Environmental", "Float");
        SensorResponse relay = await Register("Target Relay", "Actuator", "Boolean");
        await ChangeStatus(source.Id, "Online");
        await ChangeStatus(source.Id, "Offline");
        await ChangeStatus(relay.Id, "Online");

        DashboardSnapshotResponse dashboard = (await _client.GetFromJsonAsync<DashboardSnapshotResponse>(
            "/api/dashboard/issues"))!;
        DashboardIssueResponse issue = Assert.Single(dashboard.Issues, item =>
            item.SensorProfileId == source.Id && item.Type == "SensorDisconnected");

        // Merely fetching any of these endpoints cannot train a pattern.
        await _client.GetAsync("/api/realtime/devices/search");
        await _client.GetAsync("/api/realtime/snapshot");
        Assert.Empty(ReadPatterns());

        var viewed = await _client.PostAsJsonAsync("/api/realtime/interactions/view",
            new RecordOperatorViewRequest(source.Id, issue.Id));
        Assert.Equal(HttpStatusCode.Created, viewed.StatusCode);

        var associated = await _client.PostAsJsonAsync("/api/realtime/interactions/search",
            new RecordDeviceSearchRequest("SearchSubmitted", null, null,
                relay.Name, "Actuator", "Online", null, null,
                "25", "0", relay.Id, issue.Id));
        Assert.Equal(HttpStatusCode.Created, associated.StatusCode);
        Assert.Empty(ReadPatterns()); // Searching is not an executed command.

        string commandPath = $"/api/realtime/devices/{relay.Id}/commands";
        HttpResponseMessage commandResponse = await _client.PostAsJsonAsync(commandPath,
            new RelayCommandRequest("RelayOn"));
        Assert.Equal(HttpStatusCode.OK, commandResponse.StatusCode);
        RelayCommandResponse command = (await commandResponse.Content
            .ReadFromJsonAsync<RelayCommandResponse>())!;
        Assert.True(command.Command.StateChanged);

        PatternStatistics learned = Assert.Single(ReadPatterns());
        Assert.Equal(1, learned.SupportingEpisodes);
        Assert.Equal(source.Id, learned.Key.SourceDeviceId);
        Assert.Equal(relay.Id, learned.Key.TargetActuatorId);
        Assert.Equal(DashboardIssueType.SensorDisconnected, learned.Key.ConditionType);
        Assert.Equal(TelemetryMovementDirection.NotApplicable, learned.Key.Movement);
        Assert.Equal(issue.Id, Assert.Single(learned.Episodes).AlertEpisodeId);
        Assert.Equal(command.Command.CommandId, learned.Episodes[0].CommandId);
        Assert.Equal(TimeSpan.Zero, learned.Episodes[0].ObservedAtUtc.Offset);

        // Accepted no-op and successful Stack Undo remain in the P2-08 audit
        // stream, but neither manufactures a second training episode.
        Assert.Equal(HttpStatusCode.OK,
            (await _client.PostAsJsonAsync(commandPath,
                new RelayCommandRequest("RelayOn"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await _client.PostAsJsonAsync(commandPath + "/undo",
                new UndoRelayCommandRequest(command.Command.CommandId))).StatusCode);
        Assert.Equal(1, Assert.Single(ReadPatterns()).SupportingEpisodes);
    }

    [Fact]
    public async Task PlainManualCommandWithoutSourceContext_DoesNotTeachPattern()
    {
        SensorResponse relay = await Register("Unassociated Relay", "Actuator", "Boolean");
        await ChangeStatus(relay.Id, "Online");

        Assert.Equal(HttpStatusCode.OK,
            (await _client.PostAsJsonAsync($"/api/realtime/devices/{relay.Id}/commands",
                new RelayCommandRequest("RelayOn"))).StatusCode);
        Assert.Empty(ReadPatterns());
    }

    private IReadOnlyList<PatternStatistics> ReadPatterns() =>
        _factory.Services.GetRequiredService<IInteractionPatternStore>().GetSnapshot();

    private async Task<SensorResponse> Register(string name, string category, string type)
    {
        var request = new RegisterSensorRequest(name, "p209-" + Guid.NewGuid().ToString("N"),
            "UniqueIdentifier", new SensorDeploymentLocationRequest(
                "Predictive Lab", "Integration", "Node-1"), category, type);
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/sensors", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SensorResponse>())!;
    }

    private async Task ChangeStatus(Guid id, string status)
    {
        HttpResponseMessage response = await _client.PatchAsJsonAsync(
            $"/api/sensors/{id}/status", new ChangeSensorStatusRequest(status));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        if (Directory.Exists(_storage)) Directory.Delete(_storage, true);
    }

    private sealed class Factory(string storage) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Attachments:StoragePath", storage);
            builder.UseSetting("Attachments:EncryptionKeyBase64",
                Convert.ToBase64String(Enumerable.Range(1, 32)
                    .Select(value => (byte)value).ToArray()));
        }
    }
}