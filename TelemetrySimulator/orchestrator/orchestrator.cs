using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using TelemetrySimulator.Icd;
using TelemetrySimulator.Mapping;
using TelemetrySimulator.Resolving;
using TelemetrySimulator.Timing;

public class Orchestrator(Encoder _encoder, Resolver _resolver, ILogger<Orchestrator> _logger)
{
    // CSV column (not an ICD field) holding each row's position on the video: seconds since its first frame
    public const string PTS_COLUMN = "pts_time";

    // datasets come prepared and trimmed to their video (Datasets/Flights), so every run replays the whole CSV from its first row
    public async Task SimulateAsync(IcdDocument icd, MappingConfig mapping, List<Dictionary<string, string>> rawRecords, UdpClient socket, IPEndPoint remoteEndPoint, int tailNumber, DateTimeOffset startAt, int? packetsCount = null, bool loop = false, double? loopLengthMs = null, CancellationToken cancellationToken = default)
    {
        List<Dictionary<string, string>> rows = rawRecords.Take(packetsCount ?? rawRecords.Count).ToList();

        List<double> positionsMs = RowPositionsMs(rows, mapping);

        // a past startAt is an archive replay: keep its stamps, but send from now so it still plays in real time
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset sendAnchor = startAt > now ? startAt : now;
        RecordingClock clock = new(positionsMs, startAt, loopLengthMs, sendAnchor);

        _logger.LogInformation("Tail {TailNumber}: starting send to {RemoteEndPoint} ({PacketCount} packets, startAt={StartAt:O}, sendAnchor={SendAnchor:O}, loop={Loop}, loopLengthMs={LoopLengthMs})", tailNumber, remoteEndPoint, rows.Count, startAt, sendAnchor, loop, clock.LoopLengthMs);

        int sentCount = 0;
        int loopIndex = 0;
        do
        {
            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                if (!clock.IsWithinLoop(rowIndex)) continue;
                Dictionary<string, string> record = rows[rowIndex];
                // 😜
                cancellationToken.ThrowIfCancellationRequested();

                // resolve and map values from raw record to ICD identifiers
                Dictionary<string, double> resolvedValues = _resolver.Resolve(record, mapping);

                int groupMask = ComputeDirtyGroupMask(icd, resolvedValues);
                byte[] frame = _encoder.BuildFrame(icd, resolvedValues, groupMask, tailNumber, clock.StampUs(rowIndex, loopIndex));

                TimeSpan wait = clock.SendAt(rowIndex, loopIndex) - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);

                await socket.SendAsync(frame, frame.Length, remoteEndPoint);
                sentCount++;
                _logger.LogInformation("Tail {TailNumber}: sent packet {SentCount} ({FrameLength} bytes) to {RemoteEndPoint}", tailNumber, sentCount, frame.Length, remoteEndPoint);
            }
            loopIndex++;
        } while (loop);


        _logger.LogInformation("Tail {TailNumber}: finished sending {SentCount} packets", tailNumber, sentCount);
    }

    // Where each row sits on the video's timeline (ms, 0 = first video frame):
    //   the CSV's pts_time column when it has one, so telemetry that starts after the first frame is stamped where it is;
    //   otherwise the mapped PrecisionTimeStamp (µs since the epoch, or an ISO date-time) relative to the first row.
    public static List<double> RowPositionsMs(List<Dictionary<string, string>> rows, MappingConfig mapping)
    {
        if (rows.Count > 0 && rows[0].ContainsKey(PTS_COLUMN))
        {
            List<double> fromPts = new();
            for (int i = 0; i < rows.Count; i++)
            {
                string text = rows[i][PTS_COLUMN];
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
                    throw new InvalidOperationException($"Row {i}: could not read {PTS_COLUMN} '{text}' as seconds.");
                fromPts.Add(seconds * 1000);
            }
            return fromPts;
        }

        string timeColumn = mapping.Entries.FirstOrDefault(e => e.Identifier == Encoder.TIME_PARAM_NAME)?.SourceColumn
            ?? throw new InvalidOperationException($"Mapping has no '{Encoder.TIME_PARAM_NAME}' entry; the recording clock needs row times.");

        List<double> absoluteMs = new();
        for (int i = 0; i < rows.Count; i++)
        {
            string text = rows[i].GetValueOrDefault(timeColumn) ?? "";
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long microseconds))
                absoluteMs.Add(microseconds / 1000.0);
            else if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset time))
                absoluteMs.Add((time - DateTimeOffset.UnixEpoch).TotalMilliseconds);
            else
                throw new InvalidOperationException($"Row {i}: could not read '{text}' as a time (expected µs since the epoch or a date-time).");
        }
        return absoluteMs.Select(ms => ms - absoluteMs[0]).ToList();
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
