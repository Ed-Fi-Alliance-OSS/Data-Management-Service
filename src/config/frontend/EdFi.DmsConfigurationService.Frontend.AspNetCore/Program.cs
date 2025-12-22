// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.ClaimsDataLoader;
using EdFi.DmsConfigurationService.Backend.Deploy;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Middleware;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.AddServices();

// Add CORS policy to allow Swagger UI to access the Configuration Service
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

var useReverseProxyHeaders = builder.Configuration.GetValue<bool>("AppSettings:UseReverseProxyHeaders");
if (useReverseProxyHeaders)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders =
            ForwardedHeaders.XForwardedFor
            | ForwardedHeaders.XForwardedHost
            | ForwardedHeaders.XForwardedProto;

        // Accept forwarded headers from any network and proxy
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

var app = builder.Build();

var pathBase = app.Configuration.GetValue<string>("AppSettings:PathBase");
if (!string.IsNullOrEmpty(pathBase))
{
    app.UsePathBase($"/{pathBase.Trim('/')}");
}

if (useReverseProxyHeaders)
{
    app.UseForwardedHeaders();
}

app.UseMiddleware<RequestLoggingMiddleware>();
app.UseMiddleware<TenantResolutionMiddleware>();

if (!ReportInvalidConfiguration(app))
{
    InitializeDatabase(app);
    await InitializeClaimsData(app);
}

app.UseExceptionHandler(o => { });
app.UseRouting();
app.UseCors("AllowSwaggerUI");
app.UseAuthentication();
app.UseAuthorization();
app.MapRouteEndpoints();
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
        _ = app.Services.GetRequiredService<IOptions<IdentitySettings>>().Value;
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
    if (app.Services.GetRequiredService<IOptions<AppSettings>>().Value.DeployDatabaseOnStartup)
    {
        app.Logger.LogInformation("Running initial database deploy");
        try
        {
            var result = app
                .Services.GetRequiredService<IDatabaseDeploy>()
                .DeployDatabase(
                    app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.DatabaseConnection
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

/// <summary>
/// Initializes claims data at application startup if database deployment is enabled and tables are empty,
/// loading initial claim sets and hierarchy from the configured claims provider.
/// </summary>
async Task InitializeClaimsData(WebApplication app)
{
    if (app.Services.GetRequiredService<IOptions<AppSettings>>().Value.DeployDatabaseOnStartup)
    {
        app.Logger.LogInformation("Checking if initial claims data needs to be loaded");
        try
        {
            IClaimsDataLoader claimsLoader = app.Services.GetRequiredService<IClaimsDataLoader>();
            ClaimsDataLoadResult result = await claimsLoader.LoadInitialClaimsAsync();

            switch (result)
            {
                case ClaimsDataLoadResult.Success success:
                    app.Logger.LogInformation(
                        "Successfully loaded {ClaimSetCount} claim sets and hierarchy data",
                        success.ClaimSetsLoaded
                    );
                    break;
                case ClaimsDataLoadResult.AlreadyLoaded:
                    app.Logger.LogInformation("Claims data already exists, skipping initial load");
                    break;
                case ClaimsDataLoadResult.ValidationFailure validationFailure:
                    app.Logger.LogCritical(
                        "Claims data validation failed: {Errors}",
                        string.Join("; ", validationFailure.Errors)
                    );
                    Environment.Exit(-1);
                    break;
                case ClaimsDataLoadResult.DatabaseFailure databaseFailure:
                    app.Logger.LogCritical(
                        "Database error loading claims: {Error}",
                        databaseFailure.ErrorMessage
                    );
                    Environment.Exit(-1);
                    break;
                case ClaimsDataLoadResult.UnexpectedFailure unexpectedFailure:
                    app.Logger.LogCritical(
                        unexpectedFailure.Exception,
                        "Unexpected error loading claims: {Error}",
                        unexpectedFailure.ErrorMessage
                    );
                    Environment.Exit(-1);
                    break;
            }
        }
        catch (Exception ex)
        {
            app.Logger.LogCritical(ex, "Failed to initialize claims data");
            Environment.Exit(-1);
        }
    }
}

public partial class Program
{
    // Compliant solution for Sonar lint S1118
    private Program() { }
}
