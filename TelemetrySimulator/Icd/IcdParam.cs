using System.Text.Json.Serialization;

namespace TelemetrySimulator.Icd
{

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum IcdDataType
    {
        INTEGER,
        FLOAT
    }

    public class IcdParam
    {
        public required string Identifier { get; init; }
        public IcdDataType Type { get; init; }
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int Size { get; init; }

        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int CorrValue { get; init; }

        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int Location { get; init; }

        public string Mask { get; init; } = "";

        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long Min { get; init; }

        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long Max { get; init; }

        [JsonIgnore]
        public double? PreviousValue { get; set; }
    }
}

