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
    public async Task SimulateAsync(IcdDocument icd, MappingConfig mapping, List<Dictionary<string, string>> rawRecords, UdpClient socket, IPEndPoint remoteEndPoint, int tailNumber, DateTimeOffset startAt, int startIndex = 0, int? packetsCount = null, bool loop = false, double? loopLengthMs = null, CancellationToken cancellationToken = default)
    {
        List<Dictionary<string, string>> rows = rawRecords.Skip(startIndex).Take(packetsCount ?? rawRecords.Count).ToList(); // cut rows to desired index and amount

        string timeSourceColumn = mapping.Entries.FirstOrDefault(e => e.Identifier == "time")?.SourceColumn 
            ?? throw new InvalidOperationException("Mapping has no 'time' entry; the recording clock needs row times.");

        List<DateTimeOffset> rowTimes = new();
        for (int i = 0; i < rows.Count; i++)
        {
            string text = rows[i][timeSourceColumn];
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset time))
                throw new InvalidOperationException($"Row {i}: could not read '{text}' as a time.");
            rowTimes.Add(time);
        }

        RecordingClock clock = new(rowTimes, startAt, loopLengthMs);

        _logger.LogInformation("Tail {TailNumber}: starting send to {RemoteEndPoint} ({PacketCount} packets, startAt={StartAt:O}, loop={Loop}, loopLengthMs={LoopLengthMs})", tailNumber, remoteEndPoint, rows.Count, startAt, loop, clock.LoopLengthMs);

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
                byte[] frame = _encoder.BuildFrame(icd, resolvedValues, groupMask, tailNumber, clock.StampMs(rowIndex, loopIndex));

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
