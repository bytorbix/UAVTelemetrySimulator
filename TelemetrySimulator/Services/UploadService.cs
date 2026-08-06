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
    public class UploadService(IcdDocument icd, UploadStore uploadStore)
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
            } catch (InvalidOperationException ex)
            {
                return (UploadResult.InvalidMapping, ex.Message);
            }
            // initial records from raw file stream
            List<Dictionary<string, string>> rawRecords = new RecordReaderFactory().Create(fileType).ReadRecords(dataStream);
            uploadStore.Save(tailNumber, new PendingUpload { RawRecords= rawRecords, Mapping=mapping });

            return (UploadResult.Success, null);
        }   


    }
}
