using TelemetrySimulator.Icd;
using TelemetrySimulator.Ingestion;
using TelemetrySimulator.Mapping;
using TelemetrySimulator.Storage;

namespace TelemetrySimulator.Services
{
    public enum UploadResult
    {
        Success,
        InvalidMapping
    }
    public class UploadService(IcdDocument icd, UploadStore uploadStore, RecordReaderFactory recordReaderFactory)
    {
        public async Task<(UploadResult Result, string? Error)> SaveUploadAsync(int tailNumber, FileType fileType, Stream mappingStream, Stream dataStream) 
        {
            string mappingJson;
            MappingConfig mapping;
            using (StreamReader reader = new(mappingStream))
            try
            {
                // initial MappingConfig obj from raw stream
                mappingJson = await reader.ReadToEndAsync();
                mapping = MappingConfig.Load(mappingJson, icd);
            }
            catch (InvalidOperationException ex)
            {
                return (UploadResult.InvalidMapping, ex.Message);
            }
            catch (Exception ex) 
            {
                return (UploadResult.InvalidMapping, ex.Message);
            }

            // initial records from raw file stream
            List<Dictionary<string, string>> rawRecords = recordReaderFactory.Create(fileType).ReadRecords(dataStream);

            // a mapped column the file doesn't have would otherwise be sent as 0 in every packet
            if (rawRecords.Count == 0)
                return (UploadResult.InvalidMapping, "The telemetry file has no rows.");
            List<string> missing = mapping.Entries.Select(e => e.SourceColumn).Where(c => !rawRecords[0].ContainsKey(c)).ToList();
            if (missing.Count > 0)
                return (UploadResult.InvalidMapping, $"The telemetry file has no column(s) {string.Join(", ", missing.Select(c => $"'{c}'"))}, which the mapping uses.");

            uploadStore.Save(tailNumber, new PendingUpload { RawRecords= rawRecords, Mapping=mapping });

            return (UploadResult.Success, null);
        }   


    }
}
