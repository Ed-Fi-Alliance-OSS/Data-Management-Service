// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Deploy;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

// Disable reload to work around .NET file watcher bug on Linux. See:
// https://github.com/dotnet/runtime/issues/62869
// https://stackoverflow.com/questions/60295562/turn-reloadonchange-off-in-config-source-for-webapplicationfactory
Environment.SetEnvironmentVariable("DOTNET_hostBuilder:reloadConfigOnChange", "false");

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
builder.AddServices();

// Configure request size limits for schema upload
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.ValueLengthLimit = 10 * 1024 * 1024; // 10MB
    options.MultipartBodyLengthLimit = 10 * 1024 * 1024; // 10MB
});

builder.Services.Configure<KestrelServerOptions>(options =>
{
    options.Limits.MaxRequestBodySize = 10 * 1024 * 1024; // 10MB
});

// Add CORS policy to allow Swagger UI to access the API
string swaggerUiOrigin =
    builder.Configuration.GetValue<string>("Cors:SwaggerUIOrigin") ?? "http://localhost:8082";
builder.Services.AddCors(options =>
{
    options.AddPolicy(
        "AllowSwaggerUI",
        policy =>
        {
            policy.WithOrigins(swaggerUiOrigin).AllowAnyHeader().AllowAnyMethod();
        }
    );
});

var app = builder.Build();

var pathBase = app.Configuration.GetValue<string>("AppSettings:PathBase");
if (!string.IsNullOrEmpty(pathBase))
{
    app.UsePathBase($"/{pathBase.Trim('/')}");
}

var useReverseProxyHeaders = app.Configuration.GetValue<bool>("AppSettings:UseReverseProxyHeaders");
if (useReverseProxyHeaders)
{
    app.UseForwardedHeaders(
        new ForwardedHeadersOptions
        {
            ForwardedHeaders =
                ForwardedHeaders.All | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto,

            // Accept forwarded headers from any network and proxy
            KnownNetworks = { },
            KnownProxies = { },
        }
    );
}

app.UseMiddleware<LoggingMiddleware>();

if (!ReportInvalidConfiguration(app))
{
    InitializeDatabase(app);
    await RetrieveAndCacheClaimSets(app);
}

app.UseRouting();

if (app.Configuration.GetSection(RateLimitOptions.RateLimit).Exists())
{
    app.UseRateLimiter();
}

app.UseCors("AllowSwaggerUI");

app.MapRouteEndpoints();

app.MapHealthChecks("/health");

await app.RunAsync();

/// <summary>
/// Triggers configuration validation. If configuration is invalid, injects a short-circuit middleware to report.
/// Returns true if the middleware was injected.
/// </summary>
bool ReportInvalidConfiguration(WebApplication app)
{
    try
    {
        // Accessing IOptions<T> forces validation
        _ = app.Services.GetRequiredService<IOptions<AppSettings>>().Value;
        _ = app.Services.GetRequiredService<IOptions<ConnectionStrings>>().Value;
        _ = app.Services.GetRequiredService<IOptions<ConfigurationServiceSettings>>().Value;
    }
    catch (OptionsValidationException ex)
    {
        app.UseMiddleware<ReportInvalidConfigurationMiddleware>(ex.Failures);
        return true;
    }
    return false;
}

void InitializeDatabase(WebApplication app)
{
    var appSettings = app.Services.GetRequiredService<IOptions<AppSettings>>().Value;

    if (appSettings.DeployDatabaseOnStartup)
    {
        app.Logger.LogInformation("Running initial database deploy");
        try
        {
            var result = app
                .Services.GetRequiredService<IDatabaseDeploy>()
                .DeployDatabase(
                    app.Services.GetRequiredService<IOptions<ConnectionStrings>>().Value.DatabaseConnection,
                    appSettings.QueryHandler.Equals("postgresql", StringComparison.OrdinalIgnoreCase)
                );
            if (result is DatabaseDeployResult.DatabaseDeployFailure failure)
            {
                app.Logger.LogCritical(failure.Error, "Database Deploy Failure");
                Environment.Exit(-1);
            }
        }
        catch (Exception ex)
        {
            app.Logger.LogCritical(ex, "Database Deploy Failure");
            Environment.Exit(-1);
        }
    }
}
async Task RetrieveAndCacheClaimSets(WebApplication app)
{
    app.Logger.LogInformation("Retrieving and caching required claim sets");
    try
    {
        await app.Services.GetRequiredService<IClaimSetProvider>().GetAllClaimSets();
    }
    catch (Exception ex)
    {
        // Aim to cache the claim set list during the application's startup
        // process. However, if caching fails for any reason, we do not prevent
        // DMS from loading. This approach is intended to optimize the process
        // of loading claims set list from Configuration service without
        // impacting the application's availability.
        app.Logger.LogCritical(ex, "Retrieving and caching required claim sets failure");
    }
}

public partial class Program
{
    // Compliant solution for Sonar lint S1118
    private Program() { }
}
