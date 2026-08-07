using Microsoft.AspNetCore.Mvc;
using TelemetrySimulator.Services;

namespace TelemetrySimulator.Controllers
{
    [ApiController]
    [Route("api/simulations")]
    public class SimulationsController(SimulationService simulationService) : ControllerBase
    {
        [HttpPost("{tailNumber:int}/start")]
        public IActionResult Start(int tailNumber, string host, int port, int intervalMs, int startIndex = 0, int? packetsCount = null)
        {
            StartResult result = simulationService.Start(tailNumber, host, port, intervalMs, startIndex, packetsCount);
            return result switch
            {
                StartResult.Started => Accepted(),
                StartResult.UploadNotFound => NotFound($"No pending upload found for tail number {tailNumber}."),
                StartResult.AlreadyRunning => Conflict($"A simulation is already running for tail number {tailNumber}."),
                _ => StatusCode(500)
            };
        }

        [HttpPost("{tailNumber:int}/stop")]
        public IActionResult Stop(int tailNumber)
        {
            return simulationService.Stop(tailNumber) ? Ok() : NotFound($"No running simulation for tail number {tailNumber}.");
        }
    }
}
