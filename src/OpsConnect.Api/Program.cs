using Microsoft.AspNetCore.Authentication;
using OpsConnect.Api.Authentication;
using OpsConnect.Application;
using OpsConnect.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

builder.Services
    .AddAuthentication(OpsConnectAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, OpsConnectAuthenticationHandler>(
        OpsConnectAuthenticationHandler.SchemeName,
        _ => { });

builder.Services.AddAuthorization(AuthorizationPolicies.ConfigurePolicies);

builder.Services.AddApplicationServices();
builder.Services.AddInfrastructureServices(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "OpsConnect API v1");
        options.RoutePrefix = "swagger";
    });
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

public partial class Program;
