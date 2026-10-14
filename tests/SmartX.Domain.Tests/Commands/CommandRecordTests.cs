using SmartX.Domain.Commands;

namespace SmartX.Domain.Tests.Commands;

/// <summary>
/// Verifies immutable command metadata, state-change semantics, and
/// validation before command records reach application infrastructure.
/// </summary>
public sealed class CommandRecordTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CommandRecord_WhenStateChanges_RetainsActualPreviousState()
    {
        Guid commandId = Guid.NewGuid();
        Guid targetId = Guid.NewGuid();

        CommandRecord command = new(
            commandId,
            targetId,
            RelayCommandType.RelayOn,
            BaseTime,
            RelayState.Off,
            RelayState.On);

        Assert.Equal(commandId, command.Id);
        Assert.Equal(targetId, command.TargetDeviceId);
        Assert.Equal(RelayCommandType.RelayOn, command.Type);
        Assert.Equal(RelayState.Off, command.PreviousState);
        Assert.Equal(RelayState.On, command.ResultingState);
        Assert.True(command.StateChanged);
        Assert.False(command.IsReversal);
        Assert.Null(command.ReversesCommandId);
    }

    [Fact]
    public void CommandRecord_WhenNoOp_ReportsNoStateChange()
    {
        CommandRecord command = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            RelayCommandType.RelayOff,
            BaseTime,
            RelayState.Off,
            RelayState.Off);

        Assert.False(command.StateChanged);
        Assert.Equal(RelayState.Off, command.ResultingState);
    }

    [Fact]
    public void CommandRecord_WhenOffsetProvided_NormalisesTimestampToUtc()
    {
        CommandRecord command = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            RelayCommandType.RelayOn,
            BaseTime.ToOffset(TimeSpan.FromHours(2)),
            RelayState.Off,
            RelayState.On);

        Assert.Equal(BaseTime, command.ExecutedAtUtc);
        Assert.Equal(TimeSpan.Zero, command.ExecutedAtUtc.Offset);
    }

    [Fact]
    public void CommandRecord_WhenCommandTypeUnsupported_RejectsRecord()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CommandRecord(
            Guid.NewGuid(),
            Guid.NewGuid(),
            (RelayCommandType)999,
            BaseTime,
            RelayState.Off,
            RelayState.On));
    }

    [Fact]
    public void CommandRecord_WhenResultDoesNotMatchType_RejectsRecord()
    {
        Assert.Throws<ArgumentException>(() => new CommandRecord(
            Guid.NewGuid(),
            Guid.NewGuid(),
            RelayCommandType.RelayOn,
            BaseTime,
            RelayState.Off,
            RelayState.Off));
    }

    [Fact]
    public void CommandRecord_WhenIdentifierMissing_RejectsRecord()
    {
        Assert.Throws<ArgumentException>(() => new CommandRecord(
            Guid.Empty,
            Guid.NewGuid(),
            RelayCommandType.RelayOn,
            BaseTime,
            RelayState.Off,
            RelayState.On));

        Assert.Throws<ArgumentException>(() => new CommandRecord(
            Guid.NewGuid(),
            Guid.Empty,
            RelayCommandType.RelayOn,
            BaseTime,
            RelayState.Off,
            RelayState.On));
    }

    [Fact]
    public void CommandRecord_WhenReversalReferencesItself_RejectsRecord()
    {
        Guid commandId = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() => new CommandRecord(
            commandId,
            Guid.NewGuid(),
            RelayCommandType.RelayOff,
            BaseTime,
            RelayState.On,
            RelayState.Off,
            commandId));
    }

    [Fact]
    public void CommandRecord_WhenTimestampMissing_RejectsRecord()
    {
        Assert.Throws<ArgumentException>(() => new CommandRecord(
            Guid.NewGuid(),
            Guid.NewGuid(),
            RelayCommandType.RelayOn,
            default,
            RelayState.Off,
            RelayState.On));
    }
}