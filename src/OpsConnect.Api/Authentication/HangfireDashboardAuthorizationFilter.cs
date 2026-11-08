using Hangfire.Dashboard;
using Microsoft.AspNetCore.Authentication;
using OpsConnect.Domain.Administration;

namespace OpsConnect.Api.Authentication;

/// <summary>
/// Authorizes access to the Hangfire Dashboard by requiring an authenticated principal
/// established by OpsConnect's authentication architecture with administrative privileges.
/// </summary>
public sealed class HangfireDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    private readonly string[] _allowedRoles;

    public HangfireDashboardAuthorizationFilter(params string[] allowedRoles)
    {
        _allowedRoles = allowedRoles.Length > 0
            ? allowedRoles
            : new[] { SystemRoles.SuperAdmin, SystemRoles.Admin };
    }

    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        if (httpContext == null)
        {
            return false;
        }

        var user = httpContext.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            try
            {
                // Attempt explicit authentication if identity has not yet been populated on HttpContext
                var authResult = httpContext.AuthenticateAsync(OpsConnectAuthenticationHandler.SchemeName)
                    .GetAwaiter()
                    .GetResult();

                if (authResult?.Succeeded == true && authResult.Principal != null)
                {
                    user = authResult.Principal;
                    httpContext.User = user;
                }
                else
                {
                    return false;
                }
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        if (_allowedRoles.Length == 0)
        {
            return true;
        }

        return _allowedRoles.Any(role => user.IsInRole(role));
    }
}
