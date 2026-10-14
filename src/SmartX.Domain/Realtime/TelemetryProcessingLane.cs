namespace SmartX.Domain.Realtime;

/// <summary>Identifies the actual collection used to admit and dispatch work.</summary>
public enum TelemetryProcessingLane
{
    Ordinary = 1,
    Critical = 2
}