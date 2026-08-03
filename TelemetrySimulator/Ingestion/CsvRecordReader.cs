using CsvHelper;
using System.Globalization;

namespace TelemetrySimulator.Ingestion
{
    public class CsvRecordReader : IRawRecordReader
    {
        public List<Dictionary<string, string>> ReadRecords(Stream fileStream)
        {
            var records = new List<Dictionary<string, string>>();

            using var reader = new StreamReader(fileStream);
            using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);

            csv.Read();
            csv.ReadHeader();
            string[] headers = csv.HeaderRecord!;

            while (csv.Read())
            {
                var row = new Dictionary<string, string>();

                foreach (string header in headers)
                {
                    row[header] = csv.GetField(header) ?? string.Empty;
                }

                records.Add(row);
            }

            return records;
        }
    }
}
