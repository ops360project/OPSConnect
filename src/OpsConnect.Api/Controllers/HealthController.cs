using Microsoft.AspNetCore.Mvc;
using OpsConnect.Application.Common;

namespace OpsConnect.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    private readonly IBusinessClock _businessClock;

    public HealthController(IBusinessClock businessClock)
    {
        _businessClock = businessClock;
    }

    [HttpGet]
    public IActionResult GetHealth()
    {
        return Ok(new
        {
            Service = "OpsConnect.Api",
            Status = "Healthy",
            UtcNow = _businessClock.UtcNow,
            BusinessDateAsiaKolkata = _businessClock.GetBusinessDate("Asia/Kolkata")
        });
    }
}
