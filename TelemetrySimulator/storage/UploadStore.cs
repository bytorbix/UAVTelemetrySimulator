using System.Collections.Concurrent;

namespace TelemetrySimulator.Storage
{
    public class UploadStore
    {
        private readonly ConcurrentDictionary<int, PendingUpload> _uploads = new();

        public void Save(int tailNumber, PendingUpload upload) => _uploads[tailNumber] = upload;

        public bool TryGet(int tailNumber, out PendingUpload upload) => _uploads.TryGetValue(tailNumber, out upload);
    }
}
