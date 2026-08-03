namespace TelemetrySimulator.Ingestion
{
    public interface IRawRecordReader
    {
        List<Dictionary<string, string>> ReadRecords(Stream fileStream);
    }
}
