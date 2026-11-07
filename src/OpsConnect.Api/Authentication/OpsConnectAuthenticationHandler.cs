using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using OpsConnect.Domain.Administration;

namespace OpsConnect.Api.Authentication;

/// <summary>
/// Extensible authentication handler enforcing role-based access control on protected OpsConnect API endpoints.
/// Phase 7 will extend this foundation with database-backed UserMaster / JWT token issuance.
/// </summary>
public sealed class OpsConnectAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "OpsConnectAuth";
    public const string ApiKeyHeaderName = "X-OpsConnect-Api-Key";
    public const string UserHeaderName = "X-OpsConnect-User";
    public const string RoleHeaderName = "X-OpsConnect-Role";

    private readonly IConfiguration _configuration;

    public OpsConnectAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IConfiguration configuration)
        : base(options, logger, encoder)
    {
        _configuration = configuration;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyHeaderName, out var providedKeyValues))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var providedKey = providedKeyValues.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(providedKey))
        {
            return Task.FromResult(AuthenticateResult.Fail("API key header cannot be empty."));
        }

        var configuredKey = _configuration["Authentication:AdminApiKey"] ?? "OpsConnect-LocalDev-ApiKey";
        if (!string.Equals(providedKey, configuredKey, StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var username = Request.Headers.TryGetValue(UserHeaderName, out var userValues) &&
                       !string.IsNullOrWhiteSpace(userValues.FirstOrDefault())
            ? userValues.First()!.Trim()
            : "admin";

        var role = Request.Headers.TryGetValue(RoleHeaderName, out var roleValues) &&
                   !string.IsNullOrWhiteSpace(roleValues.FirstOrDefault())
            ? roleValues.First()!.Trim()
            : SystemRoles.Admin;

        if (!IsSupportedRole(role))
        {
            return Task.FromResult(AuthenticateResult.Fail($"Unsupported role '{role}'."));
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.Role, role)
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private static bool IsSupportedRole(string role) =>
        string.Equals(role, SystemRoles.SuperAdmin, StringComparison.Ordinal) ||
        string.Equals(role, SystemRoles.Admin, StringComparison.Ordinal) ||
        string.Equals(role, SystemRoles.Viewer, StringComparison.Ordinal);
}
