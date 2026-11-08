using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpsConnect.Application.Telegram;

namespace OpsConnect.Infrastructure.Telegram;

/// <summary>
/// HTTP client adapter for the Telegram Bot API.
/// Ensures all logs and error strings sanitize BotToken and WebhookSecretToken via TelegramSecretMasker.
/// </summary>
public sealed class TelegramBotHttpClient : ITelegramBotClient
{
    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<TelegramBotOptions> _optionsMonitor;
    private readonly ILogger<TelegramBotHttpClient> _logger;

    public TelegramBotHttpClient(
        HttpClient httpClient,
        IOptionsMonitor<TelegramBotOptions> optionsMonitor,
        ILogger<TelegramBotHttpClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(optionsMonitor);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    public async Task<TelegramBotSendResult> SendMessageAsync(
        long chatId,
        string text,
        long? replyToMessageId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new TelegramBotSendResult(
                IsSuccess: false,
                SentTelegramMessageId: null,
                HttpStatusCode: null,
                ErrorMessage: "Message text cannot be empty.");
        }

        var options = _optionsMonitor.CurrentValue;
        if (TelegramSecretMasker.IsMissingOrPlaceholder(options.BotToken))
        {
            return new TelegramBotSendResult(
                IsSuccess: false,
                SentTelegramMessageId: null,
                HttpStatusCode: null,
                ErrorMessage: "Telegram BotToken is not configured.");
        }

        var endpoint = $"https://api.telegram.org/bot{options.BotToken!.Trim()}/sendMessage";
        var payload = new
        {
            chat_id = chatId,
            text = text.Trim(),
            reply_to_message_id = replyToMessageId,
            allow_sending_without_reply = replyToMessageId.HasValue ? true : (bool?)null
        };

        return await PostTelegramRequestAsync(endpoint, payload, options, cancellationToken);
    }

    public async Task<TelegramBotSendResult> SetWebhookAsync(
        string webhookUrl,
        string secretToken,
        CancellationToken cancellationToken = default)
    {
        var options = _optionsMonitor.CurrentValue;
        if (TelegramSecretMasker.IsMissingOrPlaceholder(options.BotToken))
        {
            return new TelegramBotSendResult(
                IsSuccess: false,
                SentTelegramMessageId: null,
                HttpStatusCode: null,
                ErrorMessage: "Telegram BotToken is not configured.");
        }

        var endpoint = $"https://api.telegram.org/bot{options.BotToken!.Trim()}/setWebhook";
        var payload = new
        {
            url = webhookUrl.Trim(),
            secret_token = secretToken.Trim(),
            allowed_updates = new[] { "message" }
        };

        return await PostTelegramRequestAsync(endpoint, payload, options, cancellationToken);
    }

    public async Task<TelegramBotSendResult> DeleteWebhookAsync(
        CancellationToken cancellationToken = default)
    {
        var options = _optionsMonitor.CurrentValue;
        if (TelegramSecretMasker.IsMissingOrPlaceholder(options.BotToken))
        {
            return new TelegramBotSendResult(
                IsSuccess: false,
                SentTelegramMessageId: null,
                HttpStatusCode: null,
                ErrorMessage: "Telegram BotToken is not configured.");
        }

        var endpoint = $"https://api.telegram.org/bot{options.BotToken!.Trim()}/deleteWebhook";
        var payload = new { drop_pending_updates = false };

        return await PostTelegramRequestAsync(endpoint, payload, options, cancellationToken);
    }

    public async Task<TelegramWebhookInfoResult> GetWebhookInfoAsync(
        CancellationToken cancellationToken = default)
    {
        var options = _optionsMonitor.CurrentValue;
        if (TelegramSecretMasker.IsMissingOrPlaceholder(options.BotToken))
        {
            return new TelegramWebhookInfoResult(
                IsSuccess: false,
                HttpStatusCode: null,
                ErrorMessage: "Telegram BotToken is not configured.",
                IsConfigured: false,
                Url: null,
                HasCustomCertificate: null,
                PendingUpdateCount: null,
                LastErrorDateUtc: null,
                LastErrorMessage: null);
        }

        var endpoint = $"https://api.telegram.org/bot{options.BotToken!.Trim()}/getWebhookInfo";

        try
        {
            using var response = await _httpClient.GetAsync(endpoint, cancellationToken);
            int statusCode = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var sanitizedBody = TelegramSecretMasker.SanitizeForLogging(body, options.BotToken, options.WebhookSecretToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Telegram getWebhookInfo API call returned HTTP {StatusCode}: {SanitizedResponse}",
                    statusCode,
                    sanitizedBody);

                return new TelegramWebhookInfoResult(
                    IsSuccess: false,
                    HttpStatusCode: statusCode,
                    ErrorMessage: $"Telegram Bot API returned HTTP {statusCode}.",
                    IsConfigured: false,
                    Url: null,
                    HasCustomCertificate: null,
                    PendingUpdateCount: null,
                    LastErrorDateUtc: null,
                    LastErrorMessage: null);
            }

            var parsed = JsonSerializer.Deserialize<TelegramWebhookInfoResponse>(body);
            if (parsed is null || !parsed.Ok || parsed.Result is null)
            {
                return new TelegramWebhookInfoResult(
                    IsSuccess: false,
                    HttpStatusCode: statusCode,
                    ErrorMessage: "Telegram Bot API response was malformed or indicated failure.",
                    IsConfigured: false,
                    Url: null,
                    HasCustomCertificate: null,
                    PendingUpdateCount: null,
                    LastErrorDateUtc: null,
                    LastErrorMessage: null);
            }

            var data = parsed.Result;
            bool isConfigured = !string.IsNullOrWhiteSpace(data.Url);
            DateTime? lastErrorUtc = data.LastErrorDate.HasValue && data.LastErrorDate.Value > 0
                ? DateTimeOffset.FromUnixTimeSeconds(data.LastErrorDate.Value).UtcDateTime
                : null;
            string? sanitizedLastError = string.IsNullOrWhiteSpace(data.LastErrorMessage)
                ? null
                : TelegramSecretMasker.SanitizeForLogging(data.LastErrorMessage, options.BotToken, options.WebhookSecretToken);

            return new TelegramWebhookInfoResult(
                IsSuccess: true,
                HttpStatusCode: statusCode,
                ErrorMessage: null,
                IsConfigured: isConfigured,
                Url: data.Url,
                HasCustomCertificate: data.HasCustomCertificate,
                PendingUpdateCount: data.PendingUpdateCount,
                LastErrorDateUtc: lastErrorUtc,
                LastErrorMessage: sanitizedLastError);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Telegram getWebhookInfo request timed out.");
            return new TelegramWebhookInfoResult(
                IsSuccess: false,
                HttpStatusCode: null,
                ErrorMessage: "Telegram Bot API request timed out.",
                IsConfigured: false,
                Url: null,
                HasCustomCertificate: null,
                PendingUpdateCount: null,
                LastErrorDateUtc: null,
                LastErrorMessage: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var sanitizedError = TelegramSecretMasker.SanitizeForLogging(ex.Message, options.BotToken, options.WebhookSecretToken);
            _logger.LogError(ex, "Failed to connect to Telegram getWebhookInfo: {SanitizedError}", sanitizedError);

            return new TelegramWebhookInfoResult(
                IsSuccess: false,
                HttpStatusCode: null,
                ErrorMessage: $"Connection to Telegram Bot API failed: {sanitizedError}",
                IsConfigured: false,
                Url: null,
                HasCustomCertificate: null,
                PendingUpdateCount: null,
                LastErrorDateUtc: null,
                LastErrorMessage: null);
        }
    }

    private async Task<TelegramBotSendResult> PostTelegramRequestAsync(
        string endpointUrl,
        object payload,
        TelegramBotOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(endpointUrl, payload, cancellationToken);
            int statusCode = (int)response.StatusCode;

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var sanitizedBody = TelegramSecretMasker.SanitizeForLogging(body, options.BotToken, options.WebhookSecretToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Telegram Bot API call returned non-success HTTP status {StatusCode}: {SanitizedResponse}",
                    statusCode,
                    sanitizedBody);

                int? retryAfterSeconds = TryExtractRetryAfterSeconds(body, response);
                bool isTransient = statusCode == 429 || statusCode == 408 || statusCode >= 500;
                string errorCode = statusCode == 429
                    ? "RateLimited"
                    : $"TelegramHttp{statusCode}";

                string errorMessage = statusCode == 429 && retryAfterSeconds.HasValue
                    ? $"Telegram API rate limit exceeded (HTTP 429); retry after {retryAfterSeconds.Value} seconds."
                    : $"Telegram API returned HTTP {statusCode}.";

                return new TelegramBotSendResult(
                    IsSuccess: false,
                    SentTelegramMessageId: null,
                    HttpStatusCode: statusCode,
                    ErrorMessage: errorMessage,
                    IsTransientFailure: isTransient,
                    RetryAfterSeconds: retryAfterSeconds,
                    ErrorCode: errorCode);
            }

            long? sentMessageId = TryExtractMessageId(body);
            return new TelegramBotSendResult(
                IsSuccess: true,
                SentTelegramMessageId: sentMessageId,
                HttpStatusCode: statusCode,
                ErrorMessage: null,
                IsTransientFailure: false,
                RetryAfterSeconds: null,
                ErrorCode: null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Telegram Bot API request timed out.");

            return new TelegramBotSendResult(
                IsSuccess: false,
                SentTelegramMessageId: null,
                HttpStatusCode: null,
                ErrorMessage: "Telegram Bot API request timed out.",
                IsTransientFailure: true,
                RetryAfterSeconds: null,
                ErrorCode: "NetworkTimeout");
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Telegram Bot API request timed out.");

            return new TelegramBotSendResult(
                IsSuccess: false,
                SentTelegramMessageId: null,
                HttpStatusCode: null,
                ErrorMessage: "Telegram Bot API request timed out.",
                IsTransientFailure: true,
                RetryAfterSeconds: null,
                ErrorCode: "NetworkTimeout");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var sanitizedError = TelegramSecretMasker.SanitizeForLogging(
                ex.Message,
                options.BotToken,
                options.WebhookSecretToken);

            _logger.LogError("Telegram Bot API request failed: {SanitizedError}", sanitizedError);

            return new TelegramBotSendResult(
                IsSuccess: false,
                SentTelegramMessageId: null,
                HttpStatusCode: null,
                ErrorMessage: sanitizedError,
                IsTransientFailure: true,
                RetryAfterSeconds: null,
                ErrorCode: "NetworkError");
        }
    }

    private static long? TryExtractMessageId(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<TelegramApiEnvelope>(responseBody);
            return envelope?.Result?.MessageId;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? TryExtractRetryAfterSeconds(string responseBody, HttpResponseMessage response)
    {
        if (!string.IsNullOrWhiteSpace(responseBody))
        {
            try
            {
                var envelope = JsonSerializer.Deserialize<TelegramApiEnvelope>(responseBody);
                if (envelope?.Parameters?.RetryAfter is > 0)
                {
                    return envelope.Parameters.RetryAfter.Value;
                }
            }
            catch (JsonException)
            {
                // Ignore malformed body and fallback to Retry-After header
            }
        }

        if (response.Headers.RetryAfter?.Delta is { TotalSeconds: > 0 } delta)
        {
            return (int)Math.Ceiling(delta.TotalSeconds);
        }

        return null;
    }

    private sealed class TelegramApiEnvelope
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("result")]
        public TelegramSentMessageResult? Result { get; set; }

        [JsonPropertyName("parameters")]
        public TelegramResponseParameters? Parameters { get; set; }
    }

    private sealed class TelegramSentMessageResult
    {
        [JsonPropertyName("message_id")]
        public long? MessageId { get; set; }
    }

    private sealed class TelegramResponseParameters
    {
        [JsonPropertyName("retry_after")]
        public int? RetryAfter { get; set; }
    }

    private sealed class TelegramWebhookInfoResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("result")]
        public TelegramWebhookInfoData? Result { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }
    }

    private sealed class TelegramWebhookInfoData
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("has_custom_certificate")]
        public bool HasCustomCertificate { get; set; }

        [JsonPropertyName("pending_update_count")]
        public int PendingUpdateCount { get; set; }

        [JsonPropertyName("last_error_date")]
        public long? LastErrorDate { get; set; }

        [JsonPropertyName("last_error_message")]
        public string? LastErrorMessage { get; set; }
    }
}
