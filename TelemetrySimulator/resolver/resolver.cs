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
<<<<<<< HEAD
                    double milliseconds = parsedDate.TimeOfDay.TotalMilliseconds;
                    if (entry.Identifier == "time")
                    {
                        milliseconds -= offsetMs;
                    }
                    res.Add(entry.Identifier, milliseconds);
=======
                    // full date+time as Unix epoch seconds (fractional), not just time-of-day -
                    // the ICD's "time" field is documented as Unix seconds, and truncating to
                    // time-of-day both loses the date and wraps every 24h.
                    double unixSeconds = new DateTimeOffset(DateTime.SpecifyKind(parsedDate, DateTimeKind.Utc)).ToUnixTimeMilliseconds() / 1000.0;
                    res.Add(entry.Identifier, unixSeconds);
>>>>>>> 84c457f (refactored packet to emit current utc timestamp)
                }
                // TODO handle parsing case
            }
            return res;
        }
    }
}
