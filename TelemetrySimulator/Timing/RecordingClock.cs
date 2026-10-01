namespace TelemetrySimulator.Timing
{
    // computes stamps from the recording's timeline; never reads the current time
    public class RecordingClock
    {
        private readonly IReadOnlyList<DateTimeOffset> _rowTimes;
        private readonly long _startAtMs;

        public double LoopLengthMs { get; }

        // loopLengthMs: the run's shared loop length (same value the video gets); null = derive it from this recording
        public RecordingClock(IReadOnlyList<DateTimeOffset> rowTimes, DateTimeOffset startAt, double? loopLengthMs = null)
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

        public long StampMs(int rowIndex, int loopIndex)
            => _startAtMs + (long)Math.Round(PositionMs(rowIndex) + loopIndex * LoopLengthMs);

        // rows are sent at the moment their stamp names, so the stamp and the send schedule can't disagree
        public DateTimeOffset SendAt(int rowIndex, int loopIndex) => DateTimeOffset.FromUnixTimeMilliseconds(StampMs(rowIndex, loopIndex));
    }
}
