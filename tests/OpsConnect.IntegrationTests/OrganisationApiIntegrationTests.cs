using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MiniExcelLibs;
using OpsConnect.Api.Authentication;
using OpsConnect.Api.Controllers;
using OpsConnect.Application.Common;
using OpsConnect.Application.Organisation;
using OpsConnect.Domain.Administration;
using OpsConnect.Infrastructure.Persistence;
using OpsConnect.Infrastructure.Services;

namespace OpsConnect.IntegrationTests;

public class OrganisationApiIntegrationTests
{
    private static readonly DateOnly Day1 = new(2026, 11, 1);
    private static readonly DateOnly Day5 = new(2026, 11, 5);
    private static readonly DateOnly Day10 = new(2026, 11, 10);

    private static OpsConnectDbContext CreateContext()
    {
        var factory = new OpsConnectDbContextFactory();
        return factory.CreateDbContext(Array.Empty<string>());
    }

    private static OrganisationController CreateController(
        OpsConnectDbContext dbContext,
        string username = "admin.api",
        string role = SystemRoles.Admin)
    {
        var service = new OrganisationMasterService(
            dbContext,
            new OrganisationExcelReader(),
            new OrganisationHierarchyImportValidator(),
            new SystemBusinessClock());

        var controller = new OrganisationController(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(
                        new ClaimsIdentity(
                            new[]
                            {
                                new Claim(ClaimTypes.Name, username),
                                new Claim(ClaimTypes.Role, role)
                            },
                            OpsConnectAuthenticationHandler.SchemeName))
                }
            }
        };

        return controller;
    }

    private static async Task<IFormFile> CreateExcelFormFileAsync(
        IEnumerable<Dictionary<string, object?>> rows,
        string fileName = "organisation-hierarchy.xlsx")
    {
        var stream = new MemoryStream();
        await stream.SaveAsAsync(rows);
        stream.Position = 0;

        return new FormFile(stream, 0, stream.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
        };
    }

    private static List<Dictionary<string, object?>> CreateValidSpreadsheetRows() =>
    [
        new()
        {
            ["ZoneCode"] = "EAST",
            ["ZoneName"] = "East Zone",
            ["StateCode"] = "OD",
            ["StateName"] = "Odisha",
            ["RegionCode"] = "REG-A",
            ["RegionName"] = "Region A",
            ["BranchCode"] = "36:01",
            ["BranchName"] = "Bhubaneswar Main",
            ["Status"] = "Active"
        },
        new()
        {
            ["ZoneCode"] = "EAST",
            ["ZoneName"] = "East Zone",
            ["StateCode"] = "OD",
            ["StateName"] = "Odisha",
            ["RegionCode"] = "REG-A",
            ["RegionName"] = "Region A",
            ["BranchCode"] = "36:02",
            ["BranchName"] = "Cuttack City",
            ["Status"] = "Active"
        },
        new()
        {
            ["ZoneCode"] = "EAST",
            ["ZoneName"] = "East Zone",
            ["StateCode"] = "OD",
            ["StateName"] = "Odisha",
            ["RegionCode"] = "REG-B",
            ["RegionName"] = "Region B",
            ["BranchCode"] = "36:03",
            ["BranchName"] = "Rourkela Sub-Branch",
            ["Status"] = "Inactive"
        }
    ];

    [Fact]
    public async Task AuthenticationAndAuthorization_ShouldEnforceUnauthorizedAndRolePolicies()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:AdminApiKey"] = "Test-Secret-Key-123"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(AuthorizationPolicies.ConfigurePolicies);
        await using var provider = services.BuildServiceProvider();
        var authzService = provider.GetRequiredService<IAuthorizationService>();

        var optionsMonitor = new StaticOptionsMonitor<AuthenticationSchemeOptions>(new AuthenticationSchemeOptions());
        var scheme = new AuthenticationScheme(
            OpsConnectAuthenticationHandler.SchemeName,
            OpsConnectAuthenticationHandler.SchemeName,
            typeof(OpsConnectAuthenticationHandler));

        // 1. Missing API key header -> NoResult (unauthenticated -> 401 Unauthorized on protected endpoints)
        var unauthContext = new DefaultHttpContext();
        var handler1 = new OpsConnectAuthenticationHandler(optionsMonitor, NullLoggerFactory.Instance, UrlEncoder.Default, config);
        await handler1.InitializeAsync(scheme, unauthContext);
        var unauthResult = await handler1.AuthenticateAsync();
        Assert.False(unauthResult.Succeeded);

        // 2. Invalid API key header -> Fail (401 Unauthorized)
        var badKeyContext = new DefaultHttpContext();
        badKeyContext.Request.Headers[OpsConnectAuthenticationHandler.ApiKeyHeaderName] = "Wrong-Key";
        var handler2 = new OpsConnectAuthenticationHandler(optionsMonitor, NullLoggerFactory.Instance, UrlEncoder.Default, config);
        await handler2.InitializeAsync(scheme, badKeyContext);
        var badKeyResult = await handler2.AuthenticateAsync();
        Assert.False(badKeyResult.Succeeded);

        // 3. Authenticated Viewer -> allowed on OrganisationRead (200 OK), forbidden on OrganisationManage (403 Forbidden)
        var viewerContext = new DefaultHttpContext();
        viewerContext.Request.Headers[OpsConnectAuthenticationHandler.ApiKeyHeaderName] = "Test-Secret-Key-123";
        viewerContext.Request.Headers[OpsConnectAuthenticationHandler.UserHeaderName] = "viewer.user";
        viewerContext.Request.Headers[OpsConnectAuthenticationHandler.RoleHeaderName] = SystemRoles.Viewer;

        var handler3 = new OpsConnectAuthenticationHandler(optionsMonitor, NullLoggerFactory.Instance, UrlEncoder.Default, config);
        await handler3.InitializeAsync(scheme, viewerContext);
        var viewerAuth = await handler3.AuthenticateAsync();
        Assert.True(viewerAuth.Succeeded);

        var readCheck = await authzService.AuthorizeAsync(viewerAuth.Principal!, null, AuthorizationPolicies.OrganisationRead);
        var manageCheck = await authzService.AuthorizeAsync(viewerAuth.Principal!, null, AuthorizationPolicies.OrganisationManage);
        Assert.True(readCheck.Succeeded);
        Assert.False(manageCheck.Succeeded);

        // 4. Authenticated Admin -> allowed on both OrganisationRead and OrganisationManage
        var adminContext = new DefaultHttpContext();
        adminContext.Request.Headers[OpsConnectAuthenticationHandler.ApiKeyHeaderName] = "Test-Secret-Key-123";
        adminContext.Request.Headers[OpsConnectAuthenticationHandler.UserHeaderName] = "admin.user";
        adminContext.Request.Headers[OpsConnectAuthenticationHandler.RoleHeaderName] = SystemRoles.Admin;

        var handler4 = new OpsConnectAuthenticationHandler(optionsMonitor, NullLoggerFactory.Instance, UrlEncoder.Default, config);
        await handler4.InitializeAsync(scheme, adminContext);
        var adminAuth = await handler4.AuthenticateAsync();
        Assert.True(adminAuth.Succeeded);

        var adminManageCheck = await authzService.AuthorizeAsync(adminAuth.Principal!, null, AuthorizationPolicies.OrganisationManage);
        Assert.True(adminManageCheck.Succeeded);
    }

    [Fact]
    public async Task ImportHierarchy_ValidateOnlyAndInvalidRequests_ShouldReturnExpectedStatusCodesAndNotPersist()
    {
        await using var context = CreateContext();
        if (!await context.Database.CanConnectAsync())
        {
            return;
        }

        var controller = CreateController(context);

        // 1. Missing file -> 400 Bad Request (ProblemDetails)
        var missingFileResponse = await controller.ImportHierarchy(file: null);
        var missingBadRequest = Assert.IsType<BadRequestObjectResult>(missingFileResponse.Result);
        var missingProblem = Assert.IsType<ProblemDetails>(missingBadRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, missingProblem.Status);

        // 2. Unsupported file extension (.csv) -> 400 Bad Request (ProblemDetails)
        var csvFile = await CreateExcelFormFileAsync(CreateValidSpreadsheetRows(), "hierarchy.csv");
        var badExtResponse = await controller.ImportHierarchy(file: csvFile);
        var badExtResult = Assert.IsType<BadRequestObjectResult>(badExtResponse.Result);
        Assert.IsType<ProblemDetails>(badExtResult.Value);

        // 3. Valid file with validateOnly = true -> 200 OK, WasPersisted = false, 0 rows in DB
        var validFile = await CreateExcelFormFileAsync(CreateValidSpreadsheetRows());
        var validateOnlyResponse = await controller.ImportHierarchy(
            file: validFile,
            validateOnly: true,
            effectiveFromDate: Day1);

        var okValidateResult = Assert.IsType<OkObjectResult>(validateOnlyResponse.Result);
        var validatePayload = Assert.IsType<OrganisationImportExecutionResult>(okValidateResult.Value);
        Assert.True(validatePayload.ValidateOnly);
        Assert.False(validatePayload.WasPersisted);
        Assert.True(validatePayload.ValidationReport.IsValid);
        Assert.Equal(3, validatePayload.ValidationReport.ValidRowCount);
        Assert.Equal(0, await context.Branches.CountAsync());

        // 4. Invalid spreadsheet (duplicate BranchCode 36:01) -> 400 Bad Request with ValidationReport, 0 partial writes
        var duplicateRows = CreateValidSpreadsheetRows();
        duplicateRows.Add(new Dictionary<string, object?>
        {
            ["ZoneCode"] = "EAST",
            ["ZoneName"] = "East Zone",
            ["StateCode"] = "OD",
            ["StateName"] = "Odisha",
            ["RegionCode"] = "REG-A",
            ["RegionName"] = "Region A",
            ["BranchCode"] = "36:01",
            ["BranchName"] = "Duplicate Branch 36:01",
            ["Status"] = "Active"
        });

        var invalidFile = await CreateExcelFormFileAsync(duplicateRows);
        var invalidImportResponse = await controller.ImportHierarchy(
            file: invalidFile,
            validateOnly: false,
            effectiveFromDate: Day1);

        var badImportResult = Assert.IsType<BadRequestObjectResult>(invalidImportResponse.Result);
        var badImportPayload = Assert.IsType<OrganisationImportExecutionResult>(badImportResult.Value);
        Assert.False(badImportPayload.WasPersisted);
        Assert.False(badImportPayload.ValidationReport.IsValid);
        Assert.Contains(
            badImportPayload.ValidationReport.Errors,
            e => e.ErrorCode == OrganisationImportErrorCode.DuplicateBranchCode);
        Assert.Equal(0, await context.Branches.CountAsync());
    }

    [Fact]
    public async Task OrganisationEndpoints_EndToEndImportFilterTreeAndHistoricalMapping_ShouldReturnExpectedResults()
    {
        await using var context = CreateContext();
        if (!await context.Database.CanConnectAsync())
        {
            return;
        }

        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await context.Database.BeginTransactionAsync();
            var controller = CreateController(context);

            // 1. POST /api/hierarchy/import (Day 1)
            var day1File = await CreateExcelFormFileAsync(CreateValidSpreadsheetRows());
            var importResponse = await controller.ImportHierarchy(
                file: day1File,
                validateOnly: false,
                effectiveFromDate: Day1,
                changeReason: "Initial rollout");

            var okImport = Assert.IsType<OkObjectResult>(importResponse.Result);
            var importResult = Assert.IsType<OrganisationImportExecutionResult>(okImport.Value);
            Assert.True(importResult.WasPersisted);
            Assert.Equal(3, importResult.BranchesCreated);

            // 2. GET /api/branches with filters
            var branchesResponse = await controller.GetBranches(
                isActive: true,
                zoneCode: "EAST",
                stateCode: "OD",
                regionCode: "REG-A",
                searchTerm: "36:01");

            var okBranches = Assert.IsType<OkObjectResult>(branchesResponse.Result);
            var branches = Assert.IsAssignableFrom<IReadOnlyList<BranchSummaryDto>>(okBranches.Value);
            var singleBranch = Assert.Single(branches);
            Assert.Equal("36:01", singleBranch.BranchCode);
            Assert.True(singleBranch.CanSubmitActivityUpdates);

            // 3. GET /api/hierarchy (includeInactive = false vs true)
            var activeTreeResponse = await controller.GetHierarchy(includeInactive: false);
            var activeTree = Assert.IsAssignableFrom<IReadOnlyList<ZoneHierarchyNodeDto>>(
                Assert.IsType<OkObjectResult>(activeTreeResponse.Result).Value);
            Assert.Equal(2, activeTree.Single().States.Single().Regions.SelectMany(r => r.Branches).Count());

            var fullTreeResponse = await controller.GetHierarchy(includeInactive: true);
            var fullTree = Assert.IsAssignableFrom<IReadOnlyList<ZoneHierarchyNodeDto>>(
                Assert.IsType<OkObjectResult>(fullTreeResponse.Result).Value);
            Assert.Equal(3, fullTree.Single().States.Single().Regions.SelectMany(r => r.Branches).Count());

            // 4. Move Branch 36:01 to REG-B on Day 10 via POST /api/hierarchy/import
            var movedRows = new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["ZoneCode"] = "EAST",
                    ["ZoneName"] = "East Zone",
                    ["StateCode"] = "OD",
                    ["StateName"] = "Odisha",
                    ["RegionCode"] = "REG-B",
                    ["RegionName"] = "Region B",
                    ["BranchCode"] = "36:01",
                    ["BranchName"] = "Bhubaneswar Main",
                    ["Status"] = "Active"
                }
            };

            var day10File = await CreateExcelFormFileAsync(movedRows);
            var day10Import = await controller.ImportHierarchy(
                file: day10File,
                validateOnly: false,
                effectiveFromDate: Day10,
                changeReason: "Moved 36:01 to Region B");
            Assert.IsType<OkObjectResult>(day10Import.Result);

            // 5. GET /api/branches/36:01/history?asOfReportingDate=2026-11-05 (resolves to REG-A)
            var historyDay5Response = await controller.GetBranchMappingHistory("36:01", asOfReportingDate: Day5);
            var okHistoryDay5 = Assert.IsType<OkObjectResult>(historyDay5Response.Result);
            var historyDay5 = Assert.IsType<BranchMappingHistoryResultDto>(okHistoryDay5.Value);
            Assert.Equal(2, historyDay5.HistoryEntries.Count);
            Assert.NotNull(historyDay5.ResolvedMappingAsOfDate);
            Assert.Equal("REG-A", historyDay5.ResolvedMappingAsOfDate.RegionCode);

            // 6. GET /api/branches/36:01/history?asOfReportingDate=2026-11-10 (resolves to REG-B)
            var historyDay10Response = await controller.GetBranchMappingHistory("36:01", asOfReportingDate: Day10);
            var okHistoryDay10 = Assert.IsType<OkObjectResult>(historyDay10Response.Result);
            var historyDay10 = Assert.IsType<BranchMappingHistoryResultDto>(okHistoryDay10.Value);
            Assert.NotNull(historyDay10.ResolvedMappingAsOfDate);
            Assert.Equal("REG-B", historyDay10.ResolvedMappingAsOfDate.RegionCode);

            // 7. GET /api/branches/99:99/history -> 404 Not Found
            var notFoundResponse = await controller.GetBranchMappingHistory("99:99");
            var notFoundResult = Assert.IsType<NotFoundObjectResult>(notFoundResponse.Result);
            var notFoundProblem = Assert.IsType<ProblemDetails>(notFoundResult.Value);
            Assert.Equal(StatusCodes.Status404NotFound, notFoundProblem.Status);

            await tx.RollbackAsync();
        });
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T currentValue) => CurrentValue = currentValue;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
