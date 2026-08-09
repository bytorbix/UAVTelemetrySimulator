using System.Net.Sockets;
using TelemetrySimulator.Icd;
using TelemetrySimulator.Storage;


namespace TelemetrySimulator.Services
{
    public enum StartResult
    {
        Started,
        UploadNotFound,
        AlreadyRunning,
        InvalidEndpoint
    }
    public class SimulationService(Orchestrator orchestrator, UploadStore uploadStore, SimulationRegistry registry, IcdDocument icd) 
    {
        public StartResult Start(int tailNumber, string host, int port, int intervalMs, int startIndex, int? packetsCount)
        {
            if (port <= 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 0 and 65535");

            if (!uploadStore.TryGet(tailNumber, out PendingUpload upload)) return StartResult.UploadNotFound;
            CancellationTokenSource cts = new();

            if (!registry.TryRegister(tailNumber, cts)) return StartResult.AlreadyRunning;

            
            UdpClient socket = new();
            try
            {
                socket.Connect(host, port);
            }
            catch (SocketException)
            {
                socket.Dispose();
                registry.Unregister(tailNumber);
                return StartResult.InvalidEndpoint;
            }
            

            _ = Task.Run(async () =>
            {
                
                try
                {
                    await orchestrator.SimulateAsync(icd, upload.Mapping, upload.RawRecords, socket, intervalMs, tailNumber, startIndex, packetsCount, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error during simulation for tail number {tailNumber}: {ex.Message}");
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
