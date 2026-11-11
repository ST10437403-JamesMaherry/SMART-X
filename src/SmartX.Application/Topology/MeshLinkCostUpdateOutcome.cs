using SmartX.Domain.Topology;

namespace SmartX.Application.Topology;

/// <summary>
/// Immutable outcome of a simulated link cost mutation. A missing link has a
/// null Link; successful equal-cost patches return the link with Changed=false.
/// Only the authoritative mesh store performs graph mutation.
/// </summary>
public sealed record MeshLinkCostUpdateOutcome(
    MeshLink? Link,
    bool Changed);