using SmartX.Domain.Interactions;
using SmartX.Infrastructure.Interactions;

namespace SmartX.Infrastructure.Tests.Interactions;

public sealed class InMemoryOperatorInteractionStoreTests
{
    [Fact]
    public void Append_EvictsOldestWithoutResettingSequence()
    {
        var store = new InMemoryOperatorInteractionStore(TimeProvider.System, capacity: 2);
        store.Append(OperatorInteractionKind.SearchSubmitted, searchText: "first");
        store.Append(OperatorInteractionKind.FiltersChanged, searchText: "second");
        store.Append(OperatorInteractionKind.DeviceViewed, deviceId: Guid.NewGuid());

        var page = store.Read();
        Assert.Equal(3, page.TotalRecorded);
        Assert.Equal(2, page.RetainedCount);
        Assert.Equal(2, page.Capacity);
        Assert.Equal(new long[] { 3, 2 }, page.Events.Select(x => x.Sequence));
        Assert.Equal(OperatorInteractionKind.DeviceViewed, page.Events[0].Kind);
    }

    [Fact]
    public void Read_IsPureAndRespectsBoundedLimit()
    {
        var store = new InMemoryOperatorInteractionStore(TimeProvider.System);
        store.Append(OperatorInteractionKind.CommandExecuted, commandId: Guid.NewGuid());
        Assert.Single(store.Read(1).Events);
        Assert.Single(store.Read(1).Events);
        Assert.Equal(1, store.Read().TotalRecorded);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Read(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Read(201));
    }

    [Fact]
    public async Task ConcurrentAppend_GeneratesUniqueMonotonicSequences()
    {
        var store = new InMemoryOperatorInteractionStore(TimeProvider.System, capacity: 1000);
        await Task.WhenAll(Enumerable.Range(0, 250).Select(_ => Task.Run(() =>
            store.Append(OperatorInteractionKind.FiltersChanged))));
        var events = store.Read(200);
        Assert.Equal(250, events.TotalRecorded);
        Assert.Equal(250, events.RetainedCount);
        Assert.Equal(200, events.Events.Count);
        Assert.Equal(200, events.Events.Select(x => x.Sequence).Distinct().Count());
        Assert.Equal(250, events.Events[0].Sequence);
        Assert.Equal(51, events.Events[^1].Sequence);
    }
}