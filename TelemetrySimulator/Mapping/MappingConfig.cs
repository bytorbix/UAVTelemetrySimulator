using System.Text.Json;
using TelemetrySimulator.Icd;

namespace TelemetrySimulator.Mapping
{
    public class MappingConfig
    {
        public List<MappingEntry> Entries { get; init; }

        public void ValidateConfig(IcdDocument doc)
        {
            foreach (MappingEntry mapEntry in Entries)
            {
                if (!doc.TryGetField(mapEntry.Identifier, out _))
                {
                    throw new InvalidOperationException(
                        $"Mapping entry references unknown ICD identifier: '{mapEntry.Identifier}' (source column '{mapEntry.SourceColumn}').");
                }
            }
        }
        public static MappingConfig Load(string configJson, IcdDocument document)
        {
            MappingConfig? mapConfig = JsonSerializer.Deserialize<MappingConfig>(configJson);

            if (mapConfig is null)
            {
                throw new InvalidOperationException("Failed to deserialize Mapping config");
            }
            if (mapConfig.Entries is null)
            {
                throw new InvalidOperationException("Failed to deserialize Mapping config entries");
            }

            mapConfig.ValidateConfig(document);
            return mapConfig;
        }
    }
}
