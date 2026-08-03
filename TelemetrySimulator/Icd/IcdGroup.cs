namespace TelemetrySimulator.Icd
{
    public class IcdGroup
    {
        public int Id { get; init; }
        public bool IsAlwaysSent { get; init; }
        public List<IcdField> Fields { get; init; }
    }
}
