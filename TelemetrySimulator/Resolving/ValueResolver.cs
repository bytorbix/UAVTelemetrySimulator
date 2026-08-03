using TelemetrySimulator.Mapping;

namespace TelemetrySimulator.Resolving
{
    public class ValueResolver
    {
        public Dictionary<string, double> Resolve(Dictionary<string, string> rawRow, MappingConfig mapping)
        {
            Dictionary<string, double> res = new();
            foreach (MappingEntry entry in mapping.Entries)
            {
                string rawValue = rawRow[entry.SourceColumn];
                if (double.TryParse(rawValue, out double result))
                {
                    res.Add(entry.Identifier, result);
                }
            }
            return res;
        }
    }
}
