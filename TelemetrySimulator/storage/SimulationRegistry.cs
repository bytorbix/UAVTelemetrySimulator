using System.Collections.Concurrent;

namespace TelemetrySimulator.Storage
{
    public class SimulationRegistry
    {
        private readonly ConcurrentDictionary<int, CancellationTokenSource> _runs = new();

        public void Register(int tailNumber, CancellationTokenSource cts) => _runs[tailNumber] = cts;
        public bool TryRegister(int tailNumber, CancellationTokenSource cts) => _runs.TryAdd(tailNumber, cts);
        public bool TryCancel(int tailNumber)
        {
            if (!_runs.TryRemove(tailNumber, out CancellationTokenSource cts)) return false;

            cts.Cancel();
            return true;
        }

        public void Unregister(int tailNumber) => _runs.TryRemove(tailNumber, out _);
    }
}
