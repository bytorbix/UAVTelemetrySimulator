using System.Net.Sockets;
using TelemetrySimulator.Icd;
using TelemetrySimulator.Storage;


namespace TelemetrySimulator.Services
{
    public enum StartResult
    {
        Started,
        UploadNotFound,
        AlreadyRunning
    }
    public class SimulationService(Orchestrator orchestrator, UploadStore uploadStore, SimulationRegistry registry, IcdDocument icd) 
    {
        public StartResult Start(int tailNumber, string host, int port, int intervalMs, int startIndex, int? packetsCount)
        {

            if (!uploadStore.TryGet(tailNumber, out PendingUpload upload)) return StartResult.UploadNotFound;
            CancellationTokenSource cts = new();

            if (!registry.TryRegister(tailNumber, cts)) return StartResult.AlreadyRunning;

            UdpClient socket = new();
            socket.Connect(host, port);

            _ = Task.Run(async () =>
            {
                
                try
                {
                    await orchestrator.SimulateAsync(icd, upload.Mapping, upload.RawRecords, socket, intervalMs, tailNumber, startIndex, packetsCount, cts.Token);
                }
                catch (OperationCanceledException)
                {

                }
                finally
                {
                    socket.Dispose();
                    registry.Unregister(tailNumber);
                }
            });

            return StartResult.Started;
        }

        public bool Stop(int tailNumber) => registry.TryCancel(tailNumber);
    }
}
