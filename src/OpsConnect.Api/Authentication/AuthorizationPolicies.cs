using Microsoft.AspNetCore.Authorization;
using OpsConnect.Domain.Administration;

namespace OpsConnect.Api.Authentication;

public static class AuthorizationPolicies
{
    public const string OrganisationRead = "OrganisationRead";
    public const string OrganisationManage = "OrganisationManage";

    public static void ConfigurePolicies(AuthorizationOptions options)
    {
        options.AddPolicy(OrganisationRead, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.RequireRole(
                SystemRoles.SuperAdmin,
                SystemRoles.Admin,
                SystemRoles.Viewer);
        });

        options.AddPolicy(OrganisationManage, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.RequireRole(
                SystemRoles.SuperAdmin,
                SystemRoles.Admin);
        });
    }
}
