namespace TelemetrySimulator.Timing
{
    // computes stamps from the recording's timeline; never reads the current time
    public class RecordingClock
    {
        // each row's position on the video's timeline, in ms (0 = the video's first frame)
        private readonly IReadOnlyList<double> _positionsMs;
        private readonly long _startAtUs;
        private readonly long _sendAnchorMs;

        public double LoopLengthMs { get; }

        // positionsMs: where each row sits relative to the video's first frame (e.g. pts_time * 1000). A row may sit
        //   slightly before 0 or start after 0; it is stamped where it really is, so telemetry and video stay aligned.
        // loopLengthMs: the run's shared loop length (same value the video gets); null = derive it from this recording
        // sendAnchor: the wall-clock moment position 0 is sent; null = startAt. A past startAt (archive replay) still paces in real time from the anchor
        public RecordingClock(IReadOnlyList<double> positionsMs, DateTimeOffset startAt, double? loopLengthMs = null, DateTimeOffset? sendAnchor = null)
        {
            if (positionsMs.Count == 0)
                throw new ArgumentException("Recording has no rows.", nameof(positionsMs));

            for (int i = 1; i < positionsMs.Count; i++)
                if (positionsMs[i] < positionsMs[i - 1])
                    throw new ArgumentException($"Row {i} is earlier than row {i - 1}; recording time must not go backwards.", nameof(positionsMs));

            if (loopLengthMs is <= 0)
                throw new ArgumentOutOfRangeException(nameof(loopLengthMs), loopLengthMs, "Loop length must be positive.");

            _positionsMs = positionsMs;
            _startAtUs = ToUnixMicroseconds(startAt);
            _sendAnchorMs = (sendAnchor ?? startAt).ToUnixTimeMilliseconds();

            LoopLengthMs = loopLengthMs ?? DeriveLoopLengthMs();
        }

        // rows given as absolute times: the first row is position 0
        public RecordingClock(IReadOnlyList<DateTimeOffset> rowTimes, DateTimeOffset startAt, double? loopLengthMs = null, DateTimeOffset? sendAnchor = null)
            : this(rowTimes.Select(t => (t - rowTimes[0]).TotalMilliseconds).ToList(), startAt, loopLengthMs, sendAnchor)
        {
        }

        private static long ToUnixMicroseconds(DateTimeOffset time) => (time - DateTimeOffset.UnixEpoch).Ticks / 10;

        // one average interval past the last row, so the next loop's first row doesn't share the last row's stamp
        private double DeriveLoopLengthMs()
        {
            int last = _positionsMs.Count - 1;
            double averageIntervalMs = last > 0 ? (_positionsMs[last] - _positionsMs[0]) / last : 0;
            return _positionsMs[last] + averageIntervalMs;
        }

        public double PositionMs(int rowIndex) => _positionsMs[rowIndex];

        // with a shared loop length shorter than the recording, rows past it would overlap the next loop, so they're skipped
        public bool IsWithinLoop(int rowIndex) => PositionMs(rowIndex) < LoopLengthMs;

        private double OffsetMs(int rowIndex, int loopIndex) => PositionMs(rowIndex) + loopIndex * LoopLengthMs;

        // MISB PrecisionTimeStamp: microseconds since the Unix epoch
        public long StampUs(int rowIndex, int loopIndex) => _startAtUs + (long)Math.Round(OffsetMs(rowIndex, loopIndex) * 1000);

        public long StampMs(int rowIndex, int loopIndex) => (long)Math.Round(StampUs(rowIndex, loopIndex) / 1000.0);

        // stamp and send share the same offset, so they stay a fixed distance apart (zero when the anchor is startAt)
        public DateTimeOffset SendAt(int rowIndex, int loopIndex) => DateTimeOffset.FromUnixTimeMilliseconds(_sendAnchorMs + (long)Math.Round(OffsetMs(rowIndex, loopIndex)));
    }
}
