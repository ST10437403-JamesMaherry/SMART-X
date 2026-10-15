namespace SmartX.Application.Interactions;

/// <summary>Invalid deliberate interaction submissions map to HTTP 400.</summary>
public sealed class InvalidOperatorInteractionException(string message) : Exception(message);