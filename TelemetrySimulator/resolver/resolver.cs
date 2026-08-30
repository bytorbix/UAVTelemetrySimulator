using System.Globalization;
using TelemetrySimulator.Mapping;

namespace TelemetrySimulator.Resolving
{
    public class Resolver
    {
        public Dictionary<string, double> Resolve(Dictionary<string, string> rawRow, MappingConfig mapping, double offsetMs)
        {
            Dictionary<string, double> res = new();
            foreach (MappingEntry entry in mapping.Entries)
            {
                string rawValue = rawRow[entry.SourceColumn];
                if (double.TryParse(rawValue, out double result))
                {
                    res.Add(entry.Identifier, result);
                }
                else if (DateTime.TryParse(rawValue, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsedDate))
                {
                    double milliseconds = parsedDate.TimeOfDay.TotalMilliseconds;
                    if (entry.Identifier == "time")
                    {
                        milliseconds -= offsetMs;
                    }
                    res.Add(entry.Identifier, milliseconds);
                }
                // TODO handle parsing case
            }
            return res;
        }
    }
}
