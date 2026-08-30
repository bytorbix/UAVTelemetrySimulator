using System.Net.Sockets;
using System.Runtime.CompilerServices;
using TelemetrySimulator.Icd;
using TelemetrySimulator.Mapping;
using TelemetrySimulator.Resolving;

public class Orchestrator(Encoder _encoder, Resolver _resolver, ILogger<Orchestrator> _logger)
{
    public async Task SimulateAsync(IcdDocument icd, MappingConfig mapping, List<Dictionary<string, string>> rawRecords, UdpClient socket, int intervalMs, int tailNumber, int startIndex = 0, int? packetsCount = null, CancellationToken cancellationToken = default)
    {
        IEnumerable<Dictionary<string, string>> rows = rawRecords.Skip(startIndex).Take(packetsCount ?? rawRecords.Count); // cut rows to desired index and amount

        _logger.LogInformation("Tail {TailNumber}: starting send ({PacketCount} packets, {IntervalMs}ms interval)", tailNumber, rows.Count(), intervalMs);

        int sentCount = 0;
        foreach (Dictionary<string, string> record in rows)
        {
            // 😜
            cancellationToken.ThrowIfCancellationRequested();

            // resolve and map values from raw record to ICD identifiers
            Dictionary<string, double> resolvedValues = _resolver.Resolve(record, mapping);

            int groupMask = ComputeDirtyGroupMask(icd, resolvedValues);
            byte[] frame = _encoder.BuildFrame(icd, resolvedValues, groupMask, tailNumber);

            await socket.SendAsync(frame, frame.Length);
            sentCount++;
            _logger.LogInformation("Tail {TailNumber}: sent packet {SentCount} ({FrameLength} bytes)", tailNumber, sentCount, frame.Length);

            await Task.Delay(intervalMs, cancellationToken); // fixed interval ms between packets
        }

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
