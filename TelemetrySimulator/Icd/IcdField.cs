namespace TelemetrySimulator.Icd
{

    public enum IcdDataType
    {
        Int,
        Float
    }

    public class IcdField
    {
        public string Identifier { get; init; }
        public IcdDataType Type { get; init; }
        public int Size { get; init; }
        public double DefaultValue { get; init; }
    }
}
