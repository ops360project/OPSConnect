using System.Security.Claims;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpsConnect.Api.Authentication;
using OpsConnect.Domain.Administration;
using Xunit;

namespace OpsConnect.IntegrationTests;

public class HangfireDashboardIntegrationTests
{
    private static HttpContext CreateHttpContext(ClaimsPrincipal? principal = null)
    {
        var services = new ServiceCollection();
        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
        };

        if (principal != null)
        {
            context.User = principal;
        }

        return context;
    }

    [Fact]
    public void DashboardAuthorizationFilter_UnauthenticatedUser_ReturnsFalse()
    {
        var filter = new HangfireDashboardAuthorizationFilter();
        var httpContext = CreateHttpContext(new ClaimsPrincipal(new ClaimsIdentity())); // Unauthenticated
        var context = new AspNetCoreDashboardContext(new FakeJobStorage(), new DashboardOptions(), httpContext);

        var result = filter.Authorize(context);

        Assert.False(result);
    }

    [Fact]
    public void DashboardAuthorizationFilter_AuthenticatedAdmin_ReturnsTrue()
    {
        var filter = new HangfireDashboardAuthorizationFilter();
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.Name, "admin"),
                new Claim(ClaimTypes.Role, SystemRoles.Admin)
            },
            OpsConnectAuthenticationHandler.SchemeName);
        var httpContext = CreateHttpContext(new ClaimsPrincipal(identity));
        var context = new AspNetCoreDashboardContext(new FakeJobStorage(), new DashboardOptions(), httpContext);

        var result = filter.Authorize(context);

        Assert.True(result);
    }

    [Fact]
    public void DashboardAuthorizationFilter_AuthenticatedSuperAdmin_ReturnsTrue()
    {
        var filter = new HangfireDashboardAuthorizationFilter();
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.Name, "superadmin"),
                new Claim(ClaimTypes.Role, SystemRoles.SuperAdmin)
            },
            OpsConnectAuthenticationHandler.SchemeName);
        var httpContext = CreateHttpContext(new ClaimsPrincipal(identity));
        var context = new AspNetCoreDashboardContext(new FakeJobStorage(), new DashboardOptions(), httpContext);

        var result = filter.Authorize(context);

        Assert.True(result);
    }

    [Fact]
    public void DashboardAuthorizationFilter_AuthenticatedViewer_ReturnsFalse()
    {
        var filter = new HangfireDashboardAuthorizationFilter();
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.Name, "viewer"),
                new Claim(ClaimTypes.Role, SystemRoles.Viewer)
            },
            OpsConnectAuthenticationHandler.SchemeName);
        var httpContext = CreateHttpContext(new ClaimsPrincipal(identity));
        var context = new AspNetCoreDashboardContext(new FakeJobStorage(), new DashboardOptions(), httpContext);

        var result = filter.Authorize(context);

        Assert.False(result);
    }

    [Fact]
    public void DashboardAuthorizationFilter_CustomRoleSpecified_EnforcesCustomRole()
    {
        const string customRole = "ITAdministrator";
        var filter = new HangfireDashboardAuthorizationFilter(customRole);

        // 1. Admin without ITAdministrator role should be rejected
        var adminContext = CreateHttpContext(new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, SystemRoles.Admin) },
            OpsConnectAuthenticationHandler.SchemeName)));
        Assert.False(filter.Authorize(new AspNetCoreDashboardContext(new FakeJobStorage(), new DashboardOptions(), adminContext)));

        // 2. User with ITAdministrator role should be accepted
        var itAdminContext = CreateHttpContext(new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "itadmin"), new Claim(ClaimTypes.Role, customRole) },
            OpsConnectAuthenticationHandler.SchemeName)));
        Assert.True(filter.Authorize(new AspNetCoreDashboardContext(new FakeJobStorage(), new DashboardOptions(), itAdminContext)));
    }

    private sealed class FakeJobStorage : JobStorage
    {
        public override IStorageConnection GetConnection() => throw new NotImplementedException();
        public override IMonitoringApi GetMonitoringApi() => throw new NotImplementedException();
    }
}
