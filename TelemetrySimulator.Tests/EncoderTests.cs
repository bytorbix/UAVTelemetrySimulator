using TelemetrySimulator.Icd;
using TelemetrySimulator.Ingestion;
using TelemetrySimulator.Mapping;
using TelemetrySimulator.Resolving;

namespace TelemetrySimulator.Tests;

public class EncoderTests
{
    private readonly Encoder _encoder = new();

    private static IcdDocument DocumentOf(params IcdParam[] parameters)
        => new() { Params = parameters.ToList() };

    [Fact]
    public void SingleByteAlignedInteger_IsWrittenAtOffsetZero()
    {
        IcdDocument icd = DocumentOf(new IcdParam
        {
            Identifier = "flight_state",
            Type = IcdDataType.INTEGER,
            Size = 8,
            Min = 0,
            Max = 255,
        });

        byte[] frame = _encoder.BuildFrame(icd, new() { ["flight_state"] = 42 }, groupMask: 0, tailNumber: 0);

        Assert.Equal(new byte[] { 42 }, frame);
    }

    [Fact]
    public void TwoByteAlignedFields_AreWrittenAtSequentialOffsets()
    {
        IcdDocument icd = DocumentOf(
            new IcdParam { Identifier = "sync", Type = IcdDataType.INTEGER, Size = 8, Min = 0, Max = 255 },
            new IcdParam { Identifier = "tail", Type = IcdDataType.INTEGER, Size = 16, Min = 0, Max = 65000 });

        byte[] frame = _encoder.BuildFrame(icd, new() { ["sync"] = 176, ["tail"] = 1234 }, groupMask: 0, tailNumber: 0);

        // tail = 1234 = 0x04D2, little-endian low byte first
        Assert.Equal(new byte[] { 176, 0xD2, 0x04 }, frame);
    }

    [Fact]
    public void ByteAlignedFloat_MatchesRawFloatBytes()
    {
        IcdDocument icd = DocumentOf(new IcdParam
        {
            Identifier = "latitude",
            Type = IcdDataType.FLOAT,
            Size = 32,
            Min = -90,
            Max = 90,
        });

        byte[] frame = _encoder.BuildFrame(icd, new() { ["latitude"] = 12.5 }, groupMask: 0, tailNumber: 0);

        Assert.Equal(BitConverter.GetBytes(12.5f), frame);
    }

    [Fact]
    public void TwoSubByteFields_SharedSingleByte()
    {
        // Mirrors the correlator (4 bits) + zero_space (4 bits) pair from MissionMapTable.json.
        IcdDocument icd = DocumentOf(
            new IcdParam { Identifier = "correlator", Type = IcdDataType.INTEGER, Size = 4, Min = 0, Max = 15 },
            new IcdParam { Identifier = "zero_space", Type = IcdDataType.INTEGER, Size = 4, Min = 0, Max = 0 });

        byte[] frame = _encoder.BuildFrame(icd, new(), groupMask: 5, tailNumber: 0);

        // correlator occupies the low nibble; zero_space has no resolved value so it's left at 0.
        Assert.Equal(new byte[] { 0b0000_0101 }, frame);
    }

    [Fact]
    public void CorrelatorIdentifier_UsesCorrelatorValueNotResolvedValues()
    {
        IcdDocument icd = DocumentOf(new IcdParam
        {
            Identifier = "correlator",
            Type = IcdDataType.INTEGER,
            Size = 4,
            Min = 0,
            Max = 15,
        });

        byte[] frame = _encoder.BuildFrame(icd, new() { ["correlator"] = 99 }, groupMask: 7, tailNumber: 0);

        Assert.Equal(new byte[] { 7 }, frame);
    }

    [Theory]
    [InlineData(0b0010, true)]  // bit 1 set -> included
    [InlineData(0b0001, false)] // bit 1 clear -> excluded
    public void CorrValueBitmask_GatesFieldInclusion(int groupMask, bool expectedIncluded)
    {
        IcdDocument icd = DocumentOf(new IcdParam
        {
            Identifier = "battery",
            Type = IcdDataType.INTEGER,
            Size = 8,
            CorrValue = 0b0010,
            Min = 0,
            Max = 100,
        });

        byte[] frame = _encoder.BuildFrame(icd, new() { ["battery"] = 50 }, groupMask, tailNumber: 0);

        Assert.Equal(expectedIncluded ? 50 : 0, frame[0]);
    }

    [Fact]
    public void MissingResolvedValue_LeavesFieldZeroWithoutThrowing()
    {
        IcdDocument icd = DocumentOf(new IcdParam
        {
            Identifier = "battery",
            Type = IcdDataType.INTEGER,
            Size = 8,
            Min = 0,
            Max = 100,
        });

        byte[] frame = _encoder.BuildFrame(icd, new(), groupMask: 0, tailNumber: 0);

        Assert.Equal(new byte[] { 0 }, frame);
    }

    [Fact]
    public void ValueAboveMax_IsClampedToMax()
    {
        IcdDocument icd = DocumentOf(new IcdParam
        {
            Identifier = "battery",
            Type = IcdDataType.INTEGER,
            Size = 8,
            Min = 0,
            Max = 100,
        });

        byte[] frame = _encoder.BuildFrame(icd, new() { ["battery"] = 150 }, groupMask: 0, tailNumber: 0);

        Assert.Equal(new byte[] { 100 }, frame);
    }

    [Fact]
    public void ValueBelowMin_IsClampedToMin()
    {
        IcdDocument icd = DocumentOf(new IcdParam
        {
            Identifier = "battery",
            Type = IcdDataType.INTEGER,
            Size = 8,
            Min = 10,
            Max = 100,
        });

        byte[] frame = _encoder.BuildFrame(icd, new() { ["battery"] = 0 }, groupMask: 0, tailNumber: 0);

        Assert.Equal(new byte[] { 10 }, frame);
    }

    [Fact]
    public void FrameSize_RoundsUpPartialByte()
    {
        IcdDocument icd = DocumentOf(new IcdParam
        {
            Identifier = "flag",
            Type = IcdDataType.INTEGER,
            Size = 3,
            Min = 0,
            Max = 7,
        });

        byte[] frame = _encoder.BuildFrame(icd, new() { ["flag"] = 5 }, groupMask: 0, tailNumber: 0);

        Assert.Single(frame);
    }

    [Fact]
    public void RealMissionMapTable_EncodesWithoutThrowingAndMatchesExpectedSize()
    {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "MissionMapTable.json"));
        IcdDocument icd = IcdDocument.Load(json);

        Dictionary<string, double> resolvedValues = icd.Params
            .Where(p => p.Identifier != "correlator")
            .ToDictionary(p => p.Identifier, p => (double)p.Min);

        byte[] frame = _encoder.BuildFrame(icd, resolvedValues, groupMask: 0, tailNumber: 0);

        int expectedBytes = (icd.Params.Sum(p => p.Size) + 7) / 8;
        Assert.Equal(expectedBytes, frame.Length);
    }

    [Fact]
    public void RealCsvAndMapping_ResolvesAndEncodesActualRow()
    {
        string icdJson = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "MissionMapTable.json"));
        IcdDocument icd = IcdDocument.Load(icdJson);

        string mappingJson = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "DjiMapping.json"));
        MappingConfig mapping = MappingConfig.Load(mappingJson, icd);

        using FileStream csvStream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "TestData", "DJI_0001-TxtLogToCsv.csv"));
        List<Dictionary<string, string>> rawRecords = new CsvRecordReader().ReadRecords(csvStream);

        Dictionary<string, double> resolvedValues = new Resolver().Resolve(rawRecords[0], mapping);

        byte[] frame = _encoder.BuildFrame(icd, resolvedValues, groupMask: 0, tailNumber: 42);

        int expectedBytes = (icd.Params.Sum(p => p.Size) + 7) / 8;
        Assert.Equal(expectedBytes, frame.Length);

        // sync words and tail number are always emitted, independent of groupMask/CSV content
        Assert.Equal(176, frame[0]);
        Assert.Equal(59, frame[1]);
        Assert.Equal(79, frame[2]);
        Assert.Equal(42, BitConverter.ToUInt16(frame, 3));

        // correlator low nibble reflects groupMask (0 here); latitude comes straight from the CSV row
        Assert.Equal(0, frame[5] & 0b0000_1111);
        Assert.Equal(24.013433f, BitConverter.ToSingle(frame, 10));
    }
}
