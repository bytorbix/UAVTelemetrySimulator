using Microsoft.AspNetCore.Mvc;
using TelemetrySimulator.Ingestion;
using TelemetrySimulator.Services;

namespace TelemetrySimulator.Controllers
{
    [ApiController]
    [Route ("api/uploads")]
    public class UploadsController(UploadService uploadService) : ControllerBase
    {
        [HttpPost("{tailNumber:int}")]
        public async Task<IActionResult> Upload(int tailNumber, FileType fileType,IFormFile mappingFile, IFormFile rawFile)
        {
            if (mappingFile is null || rawFile is null)
            {
                return BadRequest("Both mapping and raw files are invalid or not given.");
            }
            var (result, error) = await uploadService.SaveUploadAsync(tailNumber, fileType, mappingFile.OpenReadStream(), rawFile.OpenReadStream());
            return result switch
            {
                UploadResult.Success => Ok(),
                UploadResult.InvalidMapping => BadRequest(error),
                _ => StatusCode(500)
            };
        }
    }
}
