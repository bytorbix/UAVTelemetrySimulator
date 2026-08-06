using TelemetrySimulator.Mapping;

namespace TelemetrySimulator.Storage
{
    public class PendingUpload
    {
        public required List<Dictionary<string, string>> RawRecords { get; init; }
        public required MappingConfig Mapping { get; init; }
    }
}
