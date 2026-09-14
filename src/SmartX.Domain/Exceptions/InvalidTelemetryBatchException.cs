namespace SmartX.Domain.Exceptions;

/// <summary>
/// Raised when raw historical telemetry batches do not satisfy the
/// structural requirements of the Smart-X ingestion pipeline.
/// </summary>
public sealed class InvalidTelemetryBatchException
    : DomainValidationException
{
    /// <summary>
    /// Creates a telemetry batch validation exception.
    /// </summary>
    /// <param name="message">
    /// Description of the invalid batch structure.
    /// </param>
    /// <param name="batchIndex">
    /// Index of the affected batch when known.
    /// </param>
    /// <param name="sequenceIndex">
    /// Position of the affected telemetry packet within the batch when known.
    /// </param>
    public InvalidTelemetryBatchException(
        string message,
        int? batchIndex = null,
        int? sequenceIndex = null)
        : base(message)
    {
        BatchIndex = batchIndex;
        SequenceIndex = sequenceIndex;
    }

    /// <summary>
    /// Index of the invalid batch, when applicable.
    /// </summary>
    public int? BatchIndex { get; }

    /// <summary>
    /// Sequence position of the invalid packet, when applicable.
    /// </summary>
    public int? SequenceIndex { get; }
}