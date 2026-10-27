using System.Text;
using TelemetrySimulator.Icd;
using TelemetrySimulator.Ingestion;
using TelemetrySimulator.Services;
using TelemetrySimulator.Storage;

namespace TelemetrySimulator.Tests;

public class UploadServiceTests
{
    private static readonly IcdDocument Icd = IcdDocument.Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "MissionMapTable.json")));
    private static Stream Text(string s) => new MemoryStream(Encoding.UTF8.GetBytes(s));

    private const string Mapping = """
        { "Entries": [ { "SourceColumn": "PrecisionTimeStamp", "Identifier": "PrecisionTimeStamp" },
                       { "SourceColumn": "SensorLatitude", "Identifier": "SensorLatitude" } ] }
        """;

    [Fact]
    public async Task MappedColumnMissingFromTheCsv_IsRejectedAndNamed()
    {
        UploadStore store = new();
        UploadService service = new(Icd, store, new RecordReaderFactory());

        var (result, error) = await service.SaveUploadAsync(43, FileType.Csv, Text(Mapping), Text("PrecisionTimeStamp,lat\n1,24.0\n"));

        Assert.Equal(UploadResult.InvalidMapping, result);
        Assert.Contains("'SensorLatitude'", error);
        Assert.False(store.TryGet(43, out _));
    }

    [Fact]
    public async Task CsvWithEveryMappedColumn_IsStored()
    {
        UploadStore store = new();
        UploadService service = new(Icd, store, new RecordReaderFactory());

        var (result, _) = await service.SaveUploadAsync(43, FileType.Csv, Text(Mapping), Text("PrecisionTimeStamp,SensorLatitude,pts_time\n1,24.0,0.0\n"));

        Assert.Equal(UploadResult.Success, result);
        Assert.True(store.TryGet(43, out _));
    }
}
