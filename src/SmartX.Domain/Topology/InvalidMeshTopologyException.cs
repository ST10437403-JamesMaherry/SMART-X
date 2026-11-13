using SmartX.Domain.Exceptions;

namespace SmartX.Domain.Topology;

/// <summary>
/// A rejected mesh identity, link, cost or snapshot. Deriving from the
/// existing domain-validation base preserves Smart-X's HTTP 400 mapping.
/// </summary>
public sealed class InvalidMeshTopologyException : DomainValidationException
{
    public InvalidMeshTopologyException(string message)
        : base(message)
    {
    }
}