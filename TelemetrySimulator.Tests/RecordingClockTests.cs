using TelemetrySimulator.Timing;

namespace TelemetrySimulator.Tests;

public class RecordingClockTests
{
    private static readonly DateTimeOffset StartAt = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    // recorded long ago, rows 100 ms apart
    private static List<DateTimeOffset> Rows(int count)
        => Enumerable.Range(0, count)
            .Select(i => new DateTimeOffset(2019, 8, 30, 17, 47, 13, TimeSpan.Zero).AddMilliseconds(i * 100))
            .ToList();

    private static long StartAtPlus(double ms) => StartAt.AddMilliseconds(ms).ToUnixTimeMilliseconds();

    [Fact]
    public void FirstRow_IsStampedWithStartAt()
    {
        RecordingClock clock = new(Rows(5), StartAt);

        Assert.Equal(StartAtPlus(0), clock.StampMs(0, loopIndex: 0));
    }

    [Fact]
    public void Stamp_IsStartAtPlusPositionInRecording()
    {
        RecordingClock clock = new(Rows(5), StartAt);

        Assert.Equal(StartAtPlus(300), clock.StampMs(3, loopIndex: 0));
    }

    [Fact]
    public void NextLoop_ContinuesOneIntervalAfterLastRow()
    {
        RecordingClock clock = new(Rows(5), StartAt); // span 400 ms + interval 100 ms = 500 ms loop

        Assert.Equal(StartAtPlus(500), clock.StampMs(0, loopIndex: 1));
        Assert.Equal(StartAtPlus(900), clock.StampMs(4, loopIndex: 1));
    }

    [Fact]
    public void SendAt_IsTheMomentTheStampNames()
    {
        RecordingClock clock = new(Rows(5), StartAt);

        Assert.Equal(StartAt.AddMilliseconds(200), clock.SendAt(2, loopIndex: 0));
    }

    [Fact]
    public void RecordingAcrossMidnight_KeepsCountingForward()
    {
        List<DateTimeOffset> rows = new()
        {
            new DateTimeOffset(2019, 8, 30, 23, 59, 59, 900, TimeSpan.Zero),
            new DateTimeOffset(2019, 8, 31, 0, 0, 0, 0, TimeSpan.Zero),
        };
        RecordingClock clock = new(rows, StartAt);

        Assert.Equal(StartAtPlus(100), clock.StampMs(1, loopIndex: 0));
    }

    [Fact]
    public void RecordingGoingBackwards_IsRejected()
    {
        List<DateTimeOffset> rows = Rows(3);
        rows.Reverse();

        Assert.Throws<ArgumentException>(() => new RecordingClock(rows, StartAt));
    }

    [Fact]
    public void EmptyRecording_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new RecordingClock(new List<DateTimeOffset>(), StartAt));
    }

    [Fact]
    public void SharedLoopLength_OverridesTheDerivedOne()
    {
        RecordingClock clock = new(Rows(5), StartAt, loopLengthMs: 1000);

        Assert.Equal(1000, clock.LoopLengthMs);
        Assert.Equal(StartAtPlus(1000), clock.StampMs(0, loopIndex: 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveLoopLength_IsRejected(double loopLengthMs)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecordingClock(Rows(5), StartAt, loopLengthMs));
    }

    [Fact]
    public void RowsPastASharedLoopLength_AreOutsideTheLoop()
    {
        RecordingClock clock = new(Rows(5), StartAt, loopLengthMs: 250);

        Assert.True(clock.IsWithinLoop(2));  // position 200
        Assert.False(clock.IsWithinLoop(3)); // position 300, would overlap the next loop's first row
    }
}
