namespace SmartX.Contracts.Commands;

/// <summary>
/// Transport request for issuing a manual simulated relay command.
///
/// Only RelayOn and RelayOff are accepted by the API. The controller
/// validates the string before passing a strongly typed enum into
/// the existing application command service.
/// </summary>
public sealed record RelayCommandRequest(
    string? CommandType);