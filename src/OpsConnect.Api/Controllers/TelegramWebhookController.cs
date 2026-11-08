using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpsConnect.Api.Authentication;
using OpsConnect.Application.Telegram;
using OpsConnect.Domain.Administration;

namespace OpsConnect.Api.Controllers;

[ApiController]
[Route("api/telegram")]
[Produces("application/json")]
public class TelegramWebhookController : ControllerBase
{
    private readonly ITelegramWebhookReceiver _webhookReceiver;
    private readonly IOptionsMonitor<TelegramBotOptions> _optionsMonitor;

    public TelegramWebhookController(
        ITelegramWebhookReceiver webhookReceiver,
        IOptionsMonitor<TelegramBotOptions> optionsMonitor)
    {
        _webhookReceiver = webhookReceiver;
        _optionsMonitor = optionsMonitor;
    }

    /// <summary>
    /// HTTPS webhook endpoint receiving incoming updates from the Telegram Bot API.
    /// Authenticates requests using constant-time comparison of the X-Telegram-Bot-Api-Secret-Token header,
    /// enforces payload size limits, validates update structure, and checks Open Group Mode source-group authorisation.
    /// </summary>
    [HttpPost("webhook")]
    [AllowAnonymous]
    [Consumes("application/json")]
    [RequestSizeLimit(TelegramBotOptions.MaxAllowedWebhookPayloadBytes)]
    [ProducesResponseType(typeof(TelegramWebhookHandlingResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(TelegramWebhookHandlingResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(TelegramWebhookHandlingResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(TelegramWebhookHandlingResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(TelegramWebhookHandlingResult), StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<TelegramWebhookHandlingResult>> ReceiveWebhook(
        CancellationToken cancellationToken = default)
    {
        string? providedSecret = null;
        if (Request?.Headers != null &&
            Request.Headers.TryGetValue(TelegramBotOptions.SecretTokenHeaderName, out var headerValues))
        {
            providedSecret = headerValues.FirstOrDefault();
        }

        int maxAllowedBytes = _optionsMonitor.CurrentValue.MaxWebhookPayloadBytes > 0
            ? _optionsMonitor.CurrentValue.MaxWebhookPayloadBytes
            : TelegramBotOptions.DefaultMaxWebhookPayloadBytes;

        if (Request?.ContentLength is long contentLength && contentLength > maxAllowedBytes)
        {
            var oversizedByHeader = await _webhookReceiver.ReceiveWebhookAsync(
                providedSecret,
                new byte[maxAllowedBytes + 1],
                cancellationToken);

            return StatusCode(oversizedByHeader.RecommendedHttpStatusCode, oversizedByHeader);
        }

        byte[] payloadBytes = Array.Empty<byte>();
        if (Request?.Body != null)
        {
            payloadBytes = await ReadBoundedStreamAsync(Request.Body, maxAllowedBytes + 1, cancellationToken);
        }

        var result = await _webhookReceiver.ReceiveWebhookAsync(
            providedSecret,
            payloadBytes,
            cancellationToken);

        return StatusCode(result.RecommendedHttpStatusCode, result);
    }

    /// <summary>
    /// Retrieves masked Telegram Bot and Webhook configuration diagnostics and validation status.
    /// Secrets (BotToken, WebhookSecretToken) are always masked.
    /// </summary>
    [HttpGet("configuration-status")]
    [Authorize(Policy = AuthorizationPolicies.ActivityRead)]
    [ProducesResponseType(typeof(TelegramBotMaskedDiagnosticsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<TelegramBotMaskedDiagnosticsDto> GetConfigurationStatus()
    {
        if (User?.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(User.Identity.Name))
        {
            return Unauthorized(new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Authentication required.",
                Detail = "An authenticated user identity is required."
            });
        }

        if (!User.IsInRole(SystemRoles.SuperAdmin) &&
            !User.IsInRole(SystemRoles.Admin) &&
            !User.IsInRole(SystemRoles.Viewer))
        {
            return Forbid();
        }

        var diagnostics = _webhookReceiver.GetMaskedConfigurationDiagnostics();
        return Ok(diagnostics);
    }

    private static async Task<byte[]> ReadBoundedStreamAsync(
        Stream stream,
        int maxBytesToRead,
        CancellationToken cancellationToken)
    {
        using var memoryStream = new MemoryStream();
        var buffer = new byte[4096];
        int totalRead = 0;

        while (totalRead < maxBytesToRead)
        {
            int toRead = Math.Min(buffer.Length, maxBytesToRead - totalRead);
            int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            memoryStream.Write(buffer, 0, bytesRead);
            totalRead += bytesRead;
        }

        return memoryStream.ToArray();
    }
}
