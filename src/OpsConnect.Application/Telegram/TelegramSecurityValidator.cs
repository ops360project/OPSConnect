using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace OpsConnect.Application.Telegram;

/// <summary>
/// Provides constant-time webhook secret verification and startup/runtime validation for TelegramBotOptions.
/// </summary>
public sealed partial class TelegramBotOptionsValidator : IValidateOptions<TelegramBotOptions>
{
    [GeneratedRegex(@"^\d{5,20}:[A-Za-z0-9_-]{20,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex BotTokenFormatRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{16,256}$", RegexOptions.CultureInvariant)]
    private static partial Regex WebhookSecretFormatRegex();

    /// <summary>
    /// Verifies the incoming X-Telegram-Bot-Api-Secret-Token header against the configured secret token
    /// using constant-time comparison to prevent timing side-channel attacks.
    /// </summary>
    public static bool VerifyWebhookSecretToken(string? providedSecretHeader, string? configuredSecretToken)
    {
        if (string.IsNullOrWhiteSpace(providedSecretHeader) ||
            TelegramSecretMasker.IsMissingOrPlaceholder(configuredSecretToken))
        {
            return false;
        }

        var providedBytes = Encoding.UTF8.GetBytes(providedSecretHeader);
        var configuredBytes = Encoding.UTF8.GetBytes(configuredSecretToken!.Trim());

        // Compare SHA-256 digests in constant time and verify exact byte-length match
        Span<byte> providedHash = stackalloc byte[32];
        Span<byte> configuredHash = stackalloc byte[32];
        SHA256.HashData(providedBytes, providedHash);
        SHA256.HashData(configuredBytes, configuredHash);

        bool hashesEqual = CryptographicOperations.FixedTimeEquals(providedHash, configuredHash);
        bool lengthsEqual = providedBytes.Length == configuredBytes.Length;

        return hashesEqual && lengthsEqual;
    }

    public static TelegramConfigurationValidationResult ValidateConfiguration(
        TelegramBotOptions? options,
        bool requireCredentialsEvenWhenDisabled = false)
    {
        if (options is null)
        {
            return new TelegramConfigurationValidationResult(
                IsValid: false,
                Errors: ["TelegramBot configuration section is missing."]);
        }

        var errors = new List<string>();

        if (options.MaxWebhookPayloadBytes < TelegramBotOptions.MinWebhookPayloadBytes ||
            options.MaxWebhookPayloadBytes > TelegramBotOptions.MaxAllowedWebhookPayloadBytes)
        {
            errors.Add(
                $"MaxWebhookPayloadBytes ({options.MaxWebhookPayloadBytes}) must be between {TelegramBotOptions.MinWebhookPayloadBytes} and {TelegramBotOptions.MaxAllowedWebhookPayloadBytes} bytes.");
        }

        if (options.AuthorizedSourceChatIds.Any(id => id == 0))
        {
            errors.Add("AuthorizedSourceChatIds cannot contain 0.");
        }

        if (options.DestinationChatIds.Any(id => id == 0))
        {
            errors.Add("DestinationChatIds cannot contain 0.");
        }

        bool mustValidateSecrets = options.IsEnabled || requireCredentialsEvenWhenDisabled;
        if (mustValidateSecrets)
        {
            if (TelegramSecretMasker.IsMissingOrPlaceholder(options.BotToken))
            {
                errors.Add("BotToken is required and cannot be a placeholder when TelegramBot is enabled.");
            }
            else if (!BotTokenFormatRegex().IsMatch(options.BotToken!.Trim()))
            {
                errors.Add("BotToken does not match the required Telegram Bot token format.");
            }

            if (TelegramSecretMasker.IsMissingOrPlaceholder(options.WebhookUrl))
            {
                errors.Add("WebhookUrl is required and cannot be a placeholder when TelegramBot is enabled.");
            }
            else if (!Uri.TryCreate(options.WebhookUrl!.Trim(), UriKind.Absolute, out var webhookUri) ||
                     !string.Equals(webhookUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("WebhookUrl must be a valid absolute HTTPS URI.");
            }

            if (TelegramSecretMasker.IsMissingOrPlaceholder(options.WebhookSecretToken))
            {
                errors.Add("WebhookSecretToken is required and cannot be a placeholder when TelegramBot is enabled.");
            }
            else if (!WebhookSecretFormatRegex().IsMatch(options.WebhookSecretToken!.Trim()))
            {
                errors.Add(
                    "WebhookSecretToken must be between 16 and 256 characters and contain only alphanumeric characters, underscores, or hyphens.");
            }
        }

        return new TelegramConfigurationValidationResult(
            IsValid: errors.Count == 0,
            Errors: errors);
    }

    public ValidateOptionsResult Validate(string? name, TelegramBotOptions options)
    {
        var result = ValidateConfiguration(options, requireCredentialsEvenWhenDisabled: false);
        return result.IsValid
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(result.Errors);
    }
}
