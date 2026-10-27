using TelemetrySimulator.Mapping;
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
    public void PastStartAt_KeepsItsStampsButSendsFromTheAnchor()
    {
        DateTimeOffset anchor = StartAt.AddYears(7);
        RecordingClock clock = new(Rows(5), StartAt, loopLengthMs: null, sendAnchor: anchor);

        Assert.Equal(StartAtPlus(300), clock.StampMs(3, loopIndex: 0));
        Assert.Equal(anchor.AddMilliseconds(300), clock.SendAt(3, loopIndex: 0));
        Assert.Equal(anchor.AddMilliseconds(700), clock.SendAt(2, loopIndex: 1)); // 500 ms loop + 200 ms
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

    [Fact]
    public void PtsPositions_StampEachRowWhereItSitsOnTheVideo()
    {
        // MISB telemetry often starts a little after the first video frame
        RecordingClock clock = new(new List<double> { 183.556, 383.6 }, StartAt);

        Assert.Equal(StartAtPlus(184), clock.StampMs(0, loopIndex: 0));
        Assert.Equal(StartAt.ToUnixTimeMilliseconds() * 1000 + 183_556, clock.StampUs(0, loopIndex: 0));
        Assert.Equal(StartAtPlus(384), clock.SendAt(1, loopIndex: 0).ToUnixTimeMilliseconds()); // sends are paced to the nearest ms
    }

    [Fact]
    public void RowPositions_UsePtsTimeColumnWhenPresent()
    {
        List<Dictionary<string, string>> rows = new()
        {
            new() { ["pts_time"] = "-0.016644", ["PrecisionTimeStamp"] = "1348087245793967" },
            new() { ["pts_time"] = "0.183556", ["PrecisionTimeStamp"] = "1348087245993999" },
        };
        // pts_time is a CSV column read directly, not a mapped ICD field
        MappingConfig mapping = new() { Entries = new() { new() { SourceColumn = "PrecisionTimeStamp", Identifier = "PrecisionTimeStamp" } } };

        Assert.Equal(new[] { -16.644, 183.556 }, Orchestrator.RowPositionsMs(rows, mapping).Select(p => Math.Round(p, 3)));
    }

    [Fact]
    public void RowPositions_RejectAnUnreadablePtsTime()
    {
        List<Dictionary<string, string>> rows = new()
        {
            new() { ["pts_time"] = "0.0", ["PrecisionTimeStamp"] = "1348087245793967" },
            new() { ["pts_time"] = "", ["PrecisionTimeStamp"] = "1348087245993999" },
        };
        MappingConfig mapping = new() { Entries = new() { new() { SourceColumn = "PrecisionTimeStamp", Identifier = "PrecisionTimeStamp" } } };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Orchestrator.RowPositionsMs(rows, mapping));
        Assert.Contains("Row 1", error.Message);
    }

    [Fact]
    public void RowPositions_FallBackToMicrosecondStamps()
    {
        List<Dictionary<string, string>> rows = new()
        {
            new() { ["t"] = "1348087245793967" },
            new() { ["t"] = "1348087245993999" },
        };
        MappingConfig mapping = new() { Entries = new() { new() { SourceColumn = "t", Identifier = "PrecisionTimeStamp" } } };

        Assert.Equal(new[] { 0.0, 200.032 }, Orchestrator.RowPositionsMs(rows, mapping).Select(p => Math.Round(p, 3)));
    }
}
