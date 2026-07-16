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
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.AddServices();
builder.Services.AddHttpClient();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

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

var reverseProxySettings =
    builder.Configuration.GetSection("AppSettings:ReverseProxy").Get<ReverseProxySettings>()
    ?? new ReverseProxySettings();
var useReverseProxyHeaders = reverseProxySettings.UseForwardedHeaders;
if (useReverseProxyHeaders)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
        ForwardedHeadersConfigurator.Configure(options, reverseProxySettings)
    );
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

app.UseMiddleware<SecurityHeadersMiddleware>();

app.UseMiddleware<RequestLoggingMiddleware>();

// When configuration is invalid, ReportInvalidConfiguration registers a middleware that short-circuits
// every request with a structured 500. In that case skip the rest of the pipeline: tenant resolution
// and several endpoint modules resolve validated options (AppSettings.Value), which would otherwise
// throw at startup (endpoint mapping) or upstream of the reporting middleware. Security headers and
// request logging are already registered, so they still apply to the error response.
if (!ReportInvalidConfiguration(app))
{
    InitializeDatabase(app);
    await InitializeClaimsData(app);

    // The global exception handler is registered before tenant resolution and routing so it wraps
    // them: exceptions thrown during tenant lookup (e.g. a database connection failure in
    // TenantRepository) are converted to the standardized Ed-Fi internal-server-error response
    // instead of escaping as a bare, unhandled 500.
    app.UseExceptionHandler(o => { });

    // Give framework-generated non-success responses that would otherwise be empty (binding-failure
    // 400, unmatched-route 404, method-not-allowed 405, unsupported-media-type 415, and a too-large
    // request body 413) the Ed-Fi Problem Details contract. The Knowledge Base defines no 413 type, so
    // that case uses the generic bad-request type with the 413 status preserved. UseStatusCodePages runs only when the response has no body, so it never
    // replaces bodies already produced (OAuth responses, the authorization handler's 401/403, endpoint
    // error responses including structured data-validation 400s) and does not touch successful/204
    // responses. Only the body is written, so the original status code and any framework headers (e.g.
    // Allow on 405, WWW-Authenticate) are preserved. The 400 detail is intentionally generic so that
    // invalid route/body input and framework exception text are never reflected into the response.
    app.UseStatusCodePages(async statusCodeContext =>
    {
        HttpContext context = statusCodeContext.HttpContext;
        IResult? problemDetails = FrameworkErrorResponse.ForEmptyStatusCode(
            context.Response.StatusCode,
            context.TraceIdentifier
        );

        if (problemDetails is not null)
        {
            await problemDetails.ExecuteAsync(context);
        }
    });

    app.UseMiddleware<TenantResolutionMiddleware>();

    app.UseRouting();
    app.UseCors("AllowSwaggerUI");
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapRouteEndpoints();
    app.MapOpenApi();
}

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
        _ = app.Services.GetRequiredService<IOptions<ReverseProxySettings>>().Value;
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
