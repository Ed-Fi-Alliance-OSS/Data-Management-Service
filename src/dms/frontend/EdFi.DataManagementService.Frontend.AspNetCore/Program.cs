// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Linq;
using EdFi.DataManagementService.Backend.Deploy;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Utilities;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using AppSettings = EdFi.DataManagementService.Frontend.AspNetCore.Configuration.AppSettings;
using ResponseCompressionDefaults = Microsoft.AspNetCore.ResponseCompression.ResponseCompressionDefaults;

// Disable reload to work around .NET file watcher bug on Linux. See:
// https://github.com/dotnet/runtime/issues/62869
// https://stackoverflow.com/questions/60295562/turn-reloadonchange-off-in-config-source-for-webapplicationfactory
Environment.SetEnvironmentVariable("DOTNET_hostBuilder:reloadConfigOnChange", "false");

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
builder.AddServices();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Encoder = EdFi.DataManagementService
        .Frontend
        .AspNetCore
        .AspNetCoreFrontend
        .SharedSerializerOptions
        .Encoder;
    options.SerializerOptions.DefaultIgnoreCondition = EdFi.DataManagementService
        .Frontend
        .AspNetCore
        .AspNetCoreFrontend
        .SharedSerializerOptions
        .DefaultIgnoreCondition;
});

bool enableAspNetCompression = builder.Configuration.GetValue<bool>("AppSettings:EnableAspNetCompression");

if (enableAspNetCompression)
{
    builder.Services.AddResponseCompression(options =>
    {
        options.EnableForHttps = true;
        options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[] { "application/json" });
    });
}

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
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

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

if (useReverseProxyHeaders)
{
    app.UseForwardedHeaders();
}

app.UseMiddleware<LoggingMiddleware>();

if (!ReportInvalidConfiguration(app))
{
    // Initialize DMS instances first to ensure connection strings are available
    await InitializeDmsInstances(app);
    InitializeDatabase(app);
    await InitializeApiSchemas(app);
    await RetrieveAndCacheClaimSets(app);
    await WarmUpOidcMetadataCache(app);

    if (enableAspNetCompression)
    {
        app.UseResponseCompression();
    }
    app.UseRouting();

    if (app.Configuration.GetSection(RateLimitOptions.RateLimit).Exists())
    {
        app.UseRateLimiter();
    }

    app.UseCors("AllowSwaggerUI");

    app.MapRouteEndpoints();

    app.MapHealthChecks("/health");

    // Catch-all fallback for unmatched routes
    app.MapFallback(context =>
    {
        context.Response.StatusCode = 404;
        context.Response.ContentType = "application/problem+json";

        var traceId = context.Request.Headers.TryGetValue(
            app.Configuration.GetValue<string>("AppSettings:CorrelationIdHeader") ?? "correlationid",
            out var correlationId
        )
            ? correlationId.ToString()
            : context.TraceIdentifier;

        var response = FailureResponse.ForNotFound(
            "The specified data could not be found.",
            new TraceId(traceId)
        );
        return context.Response.WriteAsJsonAsync(response);
    });
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
        app.Logger.LogInformation("Running initial database deploy on all DMS instances");
        try
        {
            var dmsInstanceProvider = app.Services.GetRequiredService<IDmsInstanceProvider>();
            var connectionStringProvider = app.Services.GetRequiredService<IConnectionStringProvider>();
            var databaseDeploy = app.Services.GetRequiredService<IDatabaseDeploy>();

            // Get all loaded tenant keys and deploy to instances for each tenant
            var loadedTenantKeys = dmsInstanceProvider.GetLoadedTenantKeys();
            int totalInstancesDeployed = 0;

            foreach (var tenantKey in loadedTenantKeys)
            {
                // Convert empty string back to null for API calls
                string? tenant = string.IsNullOrEmpty(tenantKey) ? null : tenantKey;
                var instances = dmsInstanceProvider.GetAll(tenant);

                if (instances.Count == 0)
                {
                    app.Logger.LogDebug(
                        "No instances found for tenant '{Tenant}', skipping",
                        tenant ?? "(default)"
                    );
                    continue;
                }

                app.Logger.LogInformation(
                    "Deploying database schema to {InstanceCount} instances for tenant '{Tenant}'",
                    instances.Count,
                    tenant ?? "(default)"
                );

                foreach (var instance in instances)
                {
                    app.Logger.LogInformation(
                        "Deploying database schema to DMS instance '{InstanceName}' (ID: {InstanceId}) for tenant '{Tenant}'",
                        instance.InstanceName,
                        instance.Id,
                        tenant ?? "(default)"
                    );

                    string connectionString =
                        connectionStringProvider.GetConnectionString(instance.Id, tenant) ?? string.Empty;

                    var result = databaseDeploy.DeployDatabase(connectionString);

                    if (result is DatabaseDeployResult.DatabaseDeployFailure failure)
                    {
                        app.Logger.LogCritical(
                            failure.Error,
                            "Database Deploy Failure for instance '{InstanceName}' (ID: {InstanceId}) tenant '{Tenant}'",
                            instance.InstanceName,
                            instance.Id,
                            tenant ?? "(default)"
                        );
                        Environment.Exit(-1);
                    }

                    app.Logger.LogInformation(
                        "Successfully deployed database schema to DMS instance '{InstanceName}' (ID: {InstanceId})",
                        instance.InstanceName,
                        instance.Id
                    );

                    totalInstancesDeployed++;
                }
            }

            app.Logger.LogInformation(
                "Database deploy completed for {TotalInstanceCount} DMS instances across {TenantCount} tenants",
                totalInstancesDeployed,
                loadedTenantKeys.Count
            );
        }
        catch (Exception ex)
        {
            app.Logger.LogCritical(ex, "Database Deploy Failure");
            Environment.Exit(-1);
        }
    }
}

async Task InitializeApiSchemas(WebApplication app)
{
    app.Logger.LogInformation("Initializing API schemas at startup");
    try
    {
        var orchestrator = app.Services.GetRequiredService<DmsStartupOrchestrator>();
        // Intentionally not cancellable - initialization must complete or fail entirely
        await orchestrator.RunAllAsync(CancellationToken.None);
        app.Logger.LogInformation("API schema initialization completed successfully");
    }
    catch (Exception ex)
    {
        app.Logger.LogCritical(
            ex,
            "API schema initialization failed. DMS cannot start with invalid schemas."
        );
        Environment.Exit(-1);
    }
}

async Task RetrieveAndCacheClaimSets(WebApplication app)
{
    app.Logger.LogInformation("Retrieving and caching required claim sets");
    try
    {
        var claimSetProvider = app.Services.GetRequiredService<IClaimSetProvider>();
        var multiTenancyEnabled = app.Configuration.GetValue<bool>("AppSettings:MultiTenancy");

        if (multiTenancyEnabled)
        {
            var dmsInstanceProvider = app.Services.GetRequiredService<IDmsInstanceProvider>();
            IList<string> tenants = await dmsInstanceProvider.LoadTenants();

            foreach (string tenant in tenants)
            {
                app.Logger.LogInformation(
                    "Caching claim sets for tenant: {TenantName}",
                    LoggingSanitizer.SanitizeForLogging(tenant)
                );
                await claimSetProvider.GetAllClaimSets(tenant);
            }
        }
        else
        {
            await claimSetProvider.GetAllClaimSets();
        }
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

async Task InitializeDmsInstances(WebApplication app)
{
    app.Logger.LogInformation("Loading DMS instances from Configuration Service");
    try
    {
        var dmsInstanceProvider = app.Services.GetRequiredService<IDmsInstanceProvider>();
        var multiTenancyEnabled = app.Configuration.GetValue<bool>("AppSettings:MultiTenancy");

        if (multiTenancyEnabled)
        {
            await InitializeDmsInstancesForMultiTenancy(app, dmsInstanceProvider);
        }
        else
        {
            await InitializeDmsInstancesForSingleTenancy(app, dmsInstanceProvider);
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogCritical(
            ex,
            "Critical failure: Unable to load DMS instances from Configuration Service. DMS cannot start without proper instance configuration."
        );
        Environment.Exit(-1);
    }
}

async Task InitializeDmsInstancesForMultiTenancy(WebApplication app, IDmsInstanceProvider dmsInstanceProvider)
{
    app.Logger.LogInformation("Multi-tenancy is enabled. Fetching tenants from Configuration Service.");

    IList<string> tenants = await dmsInstanceProvider.LoadTenants();

    if (tenants.Count == 0)
    {
        // When multi-tenancy is enabled, having 0 tenants at startup is not fatal.
        // Tenants may be created after DMS starts, and the cache-miss fallback will load instances on demand.
        app.Logger.LogWarning(
            "No tenants found in Configuration Service. DMS instances will be loaded on-demand when tenants are created and requests arrive."
        );
        return;
    }

    app.Logger.LogInformation(
        "Found {TenantCount} tenants. Loading DMS instances for each tenant.",
        tenants.Count
    );

    int totalInstances = 0;
    foreach (string tenant in tenants)
    {
        app.Logger.LogInformation(
            "Loading DMS instances for tenant: {TenantName}",
            LoggingSanitizer.SanitizeForLogging(tenant)
        );

        IList<DmsInstance> instances = await dmsInstanceProvider.LoadDmsInstances(tenant);
        totalInstances += instances.Count;

        app.Logger.LogInformation(
            "Loaded {InstanceCount} DMS instances for tenant {TenantName}",
            instances.Count,
            LoggingSanitizer.SanitizeForLogging(tenant)
        );

        LogInstanceDetails(app, instances);
    }

    app.Logger.LogInformation(
        "Successfully loaded {TotalInstanceCount} total DMS instances across {TenantCount} tenants",
        totalInstances,
        tenants.Count
    );
}

async Task InitializeDmsInstancesForSingleTenancy(
    WebApplication app,
    IDmsInstanceProvider dmsInstanceProvider
)
{
    IList<DmsInstance> instances = await dmsInstanceProvider.LoadDmsInstances();

    if (instances.Count == 0)
    {
        app.Logger.LogCritical(
            "No DMS instances were loaded from Configuration Service. DMS cannot start without instance configuration."
        );
        Environment.Exit(-1);
    }

    app.Logger.LogInformation("Successfully loaded {InstanceCount} DMS instances", instances.Count);
    LogInstanceDetails(app, instances);
}

void LogInstanceDetails(WebApplication app, IList<DmsInstance> instances)
{
    foreach (var instance in instances)
    {
        var hasConnectionString = !string.IsNullOrWhiteSpace(instance.ConnectionString);
        app.Logger.LogInformation(
            "DMS Instance: ID={InstanceId}, Name='{InstanceName}', Type='{InstanceType}', HasConnectionString={HasConnectionString}",
            instance.Id,
            instance.InstanceName,
            instance.InstanceType,
            hasConnectionString
        );
    }
}

async Task WarmUpOidcMetadataCache(WebApplication app)
{
    var bypassAuthorizationEnabled = app.Configuration.GetValue<bool>("AppSettings:BypassAuthorization");
    if (bypassAuthorizationEnabled)
    {
        app.Logger.LogInformation("BypassAuthorization is enabled, skipping OIDC metadata cache warm-up");
        return;
    }

    app.Logger.LogInformation("Warming up OIDC metadata cache");
    try
    {
        var configManager = app.Services.GetRequiredService<
            IConfigurationManager<OpenIdConnectConfiguration>
        >();
        var config = await configManager.GetConfigurationAsync(CancellationToken.None);
        app.Logger.LogInformation(
            "OIDC metadata cache warmed up successfully. Issuer: {Issuer}, SigningKeys: {SigningKeyCount}",
            config.Issuer,
            config.SigningKeys.Count
        );
    }
    catch (Exception ex)
    {
        app.Logger.LogCritical(
            ex,
            "Critical failure: Unable to retrieve OIDC metadata from identity provider. JWT authentication will not function correctly."
        );
        Environment.Exit(1);
    }
}

public partial class Program
{
    // Compliant solution for Sonar lint S1118
    private Program() { }
}
