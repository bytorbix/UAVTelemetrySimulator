using System.Globalization;
using TelemetrySimulator.Mapping;

namespace TelemetrySimulator.Resolving
{
    public class Resolver
    {
        public Dictionary<string, double> Resolve(Dictionary<string, string> rawRow, MappingConfig mapping)
        {
            Dictionary<string, double> res = new();
            foreach (MappingEntry entry in mapping.Entries)
            {
                // columns are checked at upload; a blank cell stays unresolved and the encoder leaves that field at zero
                // (Flights rows are never blank). PrecisionTimeStamp is always the clock's stamp, never the cell.
                string rawValue = rawRow.GetValueOrDefault(entry.SourceColumn) ?? "";
                if (double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double result))
                {
                    res.Add(entry.Identifier, result);
                }
            }
            return res;
        }
    }
}
