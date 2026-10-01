using Microsoft.AspNetCore.Mvc;
using TelemetrySimulator.Services;

namespace TelemetrySimulator.Controllers
{
    public record StartSimulationRequest(string Host, int Port, DateTimeOffset? StartAt, int StartIndex = 0, int? PacketsCount = null, bool Loop = false, double? LoopLengthMs = null);

    [ApiController]
    [Route("api/simulations")]
    public class SimulationsController(SimulationService simulationService) : ControllerBase
    {
        [HttpPost("{tailNumber:int}/start")]
        public IActionResult Start(int tailNumber, [FromBody] StartSimulationRequest request)
        {
            if (request.StartAt < DateTimeOffset.UtcNow) return BadRequest($"StartAt '{request.StartAt:O}' is already in the past.");
            if (request.LoopLengthMs <= 0) return BadRequest($"LoopLengthMs must be positive, got {request.LoopLengthMs}.");

            DateTimeOffset startAt = request.StartAt ?? DateTimeOffset.UtcNow;

            StartResult result = simulationService.Start(tailNumber, request.Host, request.Port, startAt, request.StartIndex, request.PacketsCount, request.Loop, request.LoopLengthMs);
            return result switch
            {
                StartResult.Started => Accepted(),
                StartResult.UploadNotFound => NotFound($"No pending upload found for tail number {tailNumber}."),
                StartResult.AlreadyRunning => Conflict($"A simulation is already running for tail number {tailNumber}."),
                StartResult.InvalidEndpoint => BadRequest($"Invalid host/port: '{request.Host}:{request.Port}'."),
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
