using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpsConnect.Application.Telegram;

namespace OpsConnect.Api.Controllers;

[ApiController]
[Produces("application/json")]
public class HealthController : ControllerBase
{
    private readonly ITelegramOperationalMonitoringService _monitoringService;

    public HealthController(ITelegramOperationalMonitoringService monitoringService)
    {
        _monitoringService = monitoringService;
    }

    /// <summary>
    /// Standard application and database readiness/liveness health probe.
    /// Returns 200 OK when the database and API are healthy, or 503 Service Unavailable when the database is unreachable.
    /// </summary>
    [HttpGet("health")]
    [HttpGet("api/health")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(TelegramHealthReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(TelegramHealthReportDto), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<TelegramHealthReportDto>> GetHealth(
        [FromQuery] bool verifyExternalApi = false,
        CancellationToken cancellationToken = default)
    {
        var report = await _monitoringService.CheckHealthAsync(verifyExternalApi, cancellationToken);
        return report.Status == IntegrationHealthStatus.Unhealthy
            ? StatusCode(StatusCodes.Status503ServiceUnavailable, report)
            : Ok(report);
    }
}
