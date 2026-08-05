using System.Net.Sockets;
using TelemetrySimulator.Icd;
using TelemetrySimulator.Mapping;
using TelemetrySimulator.Resolving;

public class Orchestrator(IcdDocument icd, MappingConfig mapping)
{
    const string CORRELATOR_PARAM_NAME = "correlator";

    Encoder _encoder = new();
    Resolver _resolver = new();

    public async Task SimulateAsync(List<Dictionary<string, string>> rawRecords, UdpClient socket, int intervalMs, int startIndex = 0, int? packetsCount = null)
    {
        IEnumerable<Dictionary<string, string>> rows = rawRecords.Skip(startIndex).Take(packetsCount ?? rawRecords.Count); // cut rows to desired index and amount

        int correlatorRange = 1 << icd.GetField(CORRELATOR_PARAM_NAME).Size;
        int correlatorValue = 0;

        

        foreach (Dictionary<string, string> record in rows)
        {
            // resolve and map values from raw record to ICD identifiers
            Dictionary<string, double> resolvedValues = _resolver.Resolve(record, mapping);

            byte[] frame = _encoder.BuildFrame(icd, resolvedValues, correlatorValue);

            await socket.SendAsync(frame, frame.Length);

            correlatorValue = (correlatorValue + 1) % correlatorRange; // inc correaltor 

            await Task.Delay(intervalMs); // fixed interval ms between packets
        }
    }

}
