namespace TelemetrySimulator.Ingestion
{
    public enum FileType
    {
        Csv
        // Json later
    }

    public class RecordReaderFactory
    {
        public IRawRecordReader Create(FileType fileType)
        {
            return fileType switch
            {
                FileType.Csv => new CsvRecordReader(),
                _ => throw new ArgumentOutOfRangeException(nameof(fileType), $"Unsupported file type: {fileType}")
            };
        }
    }
}
