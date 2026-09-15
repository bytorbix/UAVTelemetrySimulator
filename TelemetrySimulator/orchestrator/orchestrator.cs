using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using TelemetrySimulator.Icd;
using TelemetrySimulator.Mapping;
using TelemetrySimulator.Resolving;

public class Orchestrator(Encoder _encoder, Resolver _resolver, ILogger<Orchestrator> _logger)
{
<<<<<<< HEAD
    public async Task SimulateAsync(IcdDocument icd, MappingConfig mapping, List<Dictionary<string, string>> rawRecords, UdpClient socket, IPEndPoint remoteEndPoint, int intervalMs, int tailNumber, int startIndex = 0, int? packetsCount = null, bool loop = false, CancellationToken cancellationToken = default)
=======
    private const string TIME_IDENTIFIER = "time";

    public async Task SimulateAsync(IcdDocument icd, MappingConfig mapping, List<Dictionary<string, string>> rawRecords, UdpClient socket, IPEndPoint remoteEndPoint, int intervalMs, int tailNumber, int startIndex = 0, int? packetsCount = null, CancellationToken cancellationToken = default)
>>>>>>> 84c457f (refactored packet to emit current utc timestamp)
    {
        List<Dictionary<string, string>> rows = rawRecords.Skip(startIndex).Take(packetsCount ?? rawRecords.Count).ToList(); // cut rows to desired index and amount

<<<<<<< HEAD
        double offsetMs = 0;
        foreach (MappingEntry entry in mapping.Entries)
        {
            if (entry.Identifier == "time")
            {
                string row = rows.First()[entry.SourceColumn];
                if (!(DateTime.TryParse(row, out DateTime dateTime)))
                {
                    throw new InvalidOperationException($"Failed to parse time-of-day for calibration from row value '{row}'.");
                }
                offsetMs = dateTime.TimeOfDay.TotalMilliseconds;
            }
=======
        double? previousTimeSeconds = null;

        foreach (Dictionary<string, string> record in rows)
        {
            // 😜
            cancellationToken.ThrowIfCancellationRequested();

            // resolve and map values from raw record to ICD identifiers
            Dictionary<string, double> resolvedValues = _resolver.Resolve(record, mapping);

            int groupMask = ComputeDirtyGroupMask(icd, resolvedValues);
            byte[] frame = _encoder.BuildFrame(icd, resolvedValues, groupMask, tailNumber);

            await socket.SendAsync(frame, frame.Length, remoteEndPoint);

            // pace by the real gap between this row's and the previous row's timestamp, so
            // playback cadence matches how the telemetry was actually recorded. intervalMs is
            // only a fallback: for the first row (nothing to diff against) and for any row whose
            // delta comes out non-positive (out-of-order/duplicate timestamps in the source data).
            int delayMs = intervalMs;
            if (resolvedValues.TryGetValue(TIME_IDENTIFIER, out double currentTimeSeconds))
            {
                if (previousTimeSeconds is double previous)
                {
                    double deltaSeconds = currentTimeSeconds - previous;
                    if (deltaSeconds > 0)
                    {
                        delayMs = (int)Math.Round(deltaSeconds * 1000);
                    }
                }
                previousTimeSeconds = currentTimeSeconds;
            }

            await Task.Delay(delayMs, cancellationToken);
>>>>>>> 84c457f (refactored packet to emit current utc timestamp)
        }

        string? ptsSourceColumn = mapping.Entries.FirstOrDefault(e => e.Identifier == "pts_time")?.SourceColumn;

        _logger.LogInformation("Tail {TailNumber}: starting send to {RemoteEndPoint} ({PacketCount} packets, {IntervalMs}ms interval, loop={Loop}, pacedByRecordedPts={PacedByRecordedPts})", tailNumber, remoteEndPoint, rows.Count, intervalMs, loop, ptsSourceColumn is not null);

        int sentCount = 0;
        double? previousPtsSeconds = null;
        do
        {
            foreach (Dictionary<string, string> record in rows)
            {
                // 😜
                cancellationToken.ThrowIfCancellationRequested();

                // resolve and map values from raw record to ICD identifiers
                Dictionary<string, double> resolvedValues = _resolver.Resolve(record, mapping, offsetMs);

                int groupMask = ComputeDirtyGroupMask(icd, resolvedValues);
                byte[] frame = _encoder.BuildFrame(icd, resolvedValues, groupMask, tailNumber);

                await socket.SendAsync(frame, frame.Length, remoteEndPoint);
                sentCount++;
                _logger.LogInformation("Tail {TailNumber}: sent packet {SentCount} ({FrameLength} bytes) to {RemoteEndPoint}", tailNumber, sentCount, frame.Length, remoteEndPoint);

                TimeSpan delay = TimeSpan.FromMilliseconds(intervalMs);
                if (ptsSourceColumn is not null && double.TryParse(record[ptsSourceColumn], out double currentPtsSeconds))
                {
                    if (previousPtsSeconds is not null)
                    {
                        double deltaSeconds = currentPtsSeconds - previousPtsSeconds.Value;
                        delay = deltaSeconds > 0 ? TimeSpan.FromSeconds(deltaSeconds) : TimeSpan.Zero;
                    }
                    previousPtsSeconds = currentPtsSeconds;
                }

                await Task.Delay(delay, cancellationToken); // paced by the recording's own pts deltas when available, else fixed interval
            }
        } while (loop);

        _logger.LogInformation("Tail {TailNumber}: finished sending {SentCount} packets", tailNumber, sentCount);
    }

    private static int ComputeDirtyGroupMask(IcdDocument icd, Dictionary<string, double> resolvedValues)
    {
        int mask = 0;
        
        foreach (IcdParam param in icd.Params) 
        {
            if (param.CorrValue == 0) continue;
            if (!resolvedValues.TryGetValue(param.Identifier, out double value)) continue;

            bool dirty = param.PreviousValue is null || param.PreviousValue.Value != value;
            if (dirty) mask |= param.CorrValue;

            param.PreviousValue = value;
        }
        return mask;
    }
}
