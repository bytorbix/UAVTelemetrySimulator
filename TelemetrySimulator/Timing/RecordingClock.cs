namespace TelemetrySimulator.Timing
{
    // computes stamps from the recording's timeline; never reads the current time
    public class RecordingClock
    {
        private readonly IReadOnlyList<DateTimeOffset> _rowTimes;
        private readonly long _startAtMs;
        private readonly long _sendAnchorMs;

        public double LoopLengthMs { get; }

        // loopLengthMs: the run's shared loop length (same value the video gets); null = derive it from this recording
        // sendAnchor: the wall-clock moment row 0 is sent; null = startAt. A past startAt (archive replay) still paces in real time from the anchor
        public RecordingClock(IReadOnlyList<DateTimeOffset> rowTimes, DateTimeOffset startAt, double? loopLengthMs = null, DateTimeOffset? sendAnchor = null)
        {
            if (rowTimes.Count == 0)
                throw new ArgumentException("Recording has no rows.", nameof(rowTimes));

            for (int i = 1; i < rowTimes.Count; i++)
                if (rowTimes[i] < rowTimes[i - 1])
                    throw new ArgumentException($"Row {i} is earlier than row {i - 1}; recording time must not go backwards.", nameof(rowTimes));

            if (loopLengthMs is <= 0)
                throw new ArgumentOutOfRangeException(nameof(loopLengthMs), loopLengthMs, "Loop length must be positive.");

            _rowTimes = rowTimes;
            _startAtMs = startAt.ToUnixTimeMilliseconds();
            _sendAnchorMs = (sendAnchor ?? startAt).ToUnixTimeMilliseconds();

            LoopLengthMs = loopLengthMs ?? DeriveLoopLengthMs();
        }

        // one average interval past the last row, so the next loop's first row doesn't share the last row's stamp
        private double DeriveLoopLengthMs()
        {
            double spanMs = PositionMs(_rowTimes.Count - 1);
            double averageIntervalMs = _rowTimes.Count > 1 ? spanMs / (_rowTimes.Count - 1) : 0;
            return spanMs + averageIntervalMs;
        }

        public double PositionMs(int rowIndex) => (_rowTimes[rowIndex] - _rowTimes[0]).TotalMilliseconds;

        // with a shared loop length shorter than the recording, rows past it would overlap the next loop, so they're skipped
        public bool IsWithinLoop(int rowIndex) => PositionMs(rowIndex) < LoopLengthMs;

        private long OffsetMs(int rowIndex, int loopIndex) => (long)Math.Round(PositionMs(rowIndex) + loopIndex * LoopLengthMs);

        public long StampMs(int rowIndex, int loopIndex) => _startAtMs + OffsetMs(rowIndex, loopIndex);

        // stamp and send share the same offset, so they stay a fixed distance apart (zero when the anchor is startAt)
        public DateTimeOffset SendAt(int rowIndex, int loopIndex) => DateTimeOffset.FromUnixTimeMilliseconds(_sendAnchorMs + OffsetMs(rowIndex, loopIndex));
    }
}
