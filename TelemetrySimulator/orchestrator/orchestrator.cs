using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using TelemetrySimulator.Icd;
using TelemetrySimulator.Mapping;
using TelemetrySimulator.Resolving;

public class Orchestrator(Encoder _encoder, Resolver _resolver, ILogger<Orchestrator> _logger)
{
    public async Task SimulateAsync(IcdDocument icd, MappingConfig mapping, List<Dictionary<string, string>> rawRecords, UdpClient socket, IPEndPoint remoteEndPoint, int intervalMs, int tailNumber, int startIndex = 0, int? packetsCount = null, bool loop = false, CancellationToken cancellationToken = default)
    {
        List<Dictionary<string, string>> rows = rawRecords.Skip(startIndex).Take(packetsCount ?? rawRecords.Count).ToList(); // cut rows to desired index and amount

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
