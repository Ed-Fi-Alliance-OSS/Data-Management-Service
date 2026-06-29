// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Linq;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Utilities;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using AppSettings = EdFi.DataManagementService.Frontend.AspNetCore.Configuration.AppSettings;
using ResponseCompressionDefaults = Microsoft.AspNetCore.ResponseCompression.ResponseCompressionDefaults;

// Disable reload to work around .NET file watcher bug on Linux. See:
// https://github.com/dotnet/runtime/issues/62869
// https://stackoverflow.com/questions/60295562/turn-reloadonchange-off-in-config-source-for-webapplicationfactory
Environment.SetEnvironmentVariable("DOTNET_hostBuilder:reloadConfigOnChange", "false");

var builder = WebApplication.CreateBuilder(args);
var bootstrapStartupStatusSignal = new FileStartupStatusSignal(
    builder.Configuration.GetValue<string>("AppSettings:StartupStatusFilePath"),
    Console.Error
);
bool enableAspNetCompression = false;
bool useReverseProxyHeaders = false;

RunBootstrapPhase(
    DmsStartupPhases.ConfigureServices,
    "Configuring DMS services and shared HTTP infrastructure.",
    "Configured DMS services.",
    "Configuring DMS services failed before the application host was built.",
    () =>
    {
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

        enableAspNetCompression = builder.Configuration.GetValue<bool>("AppSettings:EnableAspNetCompression");

        if (enableAspNetCompression)
        {
            builder.Services.AddResponseCompression(options =>
            {
                options.EnableForHttps = true;
                options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
                    new[] { "application/json" }
                );
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

        useReverseProxyHeaders = builder.Configuration.GetValue<bool>("AppSettings:UseReverseProxyHeaders");

        if (useReverseProxyHeaders)
        {
            var reverseProxySettings =
                builder.Configuration.GetSection("AppSettings:ReverseProxy").Get<ReverseProxySettings>()
                ?? new ReverseProxySettings();

            builder.Services.Configure<ForwardedHeadersOptions>(options =>
                ForwardedHeadersConfigurator.Configure(options, reverseProxySettings)
            );
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
    }
);

var app = RunBootstrapPhaseWithResult(
    DmsStartupPhases.BuildApplication,
    "Building the DMS web application host.",
    "Built DMS web application host.",
    "Building the DMS web application host failed before HTTP binding.",
    builder.Build
);
var startupPhaseExecutor = app.Services.GetRequiredService<StartupPhaseExecutor>();
app.Logger.LogInformation(
    "DMS startup status file path: {StartupStatusFilePath}",
    startupPhaseExecutor.StatusFilePath
);

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

app.UseMiddleware<LoggingMiddleware>();

var invalidConfigurationException = ReportInvalidConfiguration(app);

if (invalidConfigurationException is null)
{
    // Initialize data stores first to ensure connection strings are available
    await startupPhaseExecutor.RunFatalAsync(
        DmsStartupPhases.LoadDataStores,
        "Loading data stores from Configuration Service.",
        "Loaded data stores from Configuration Service.",
        "Unable to load data stores from Configuration Service. DMS cannot start without proper data store configuration.",
        () => InitializeDataStores(app)
    );
    await startupPhaseExecutor.RunFatalAsync(
        DmsStartupPhases.InitializeApiSchemas,
        "Loading API schemas and initializing effective schema metadata.",
        "API schema loading and effective-schema initialization completed successfully.",
        "API schema initialization failed. DMS cannot start with invalid schemas.",
        () => InitializeApiSchemas(app)
    );
    await startupPhaseExecutor.RunFatalAsync(
        DmsStartupPhases.InitializeBackendMappings,
        "Compiling backend mappings from initialized effective schemas.",
        "Backend mapping initialization completed successfully.",
        "Backend mapping initialization failed. DMS cannot start without compiled backend mappings.",
        () => InitializeBackendMappings(app)
    );
    await startupPhaseExecutor.RunFatalAsync(
        DmsStartupPhases.InitializeAuthMetadata,
        "Initializing authentication metadata caches (OIDC warm-up and claim sets).",
        "Authentication metadata initialization completed successfully.",
        "Authentication metadata initialization failed. JWT authentication will not function correctly.",
        () => InitializeAuthMetadata(app),
        exitCode: 1
    );

    if (enableAspNetCompression)
    {
        app.UseResponseCompression();
    }

    startupPhaseExecutor.WriteStarting(
        DmsStartupPhases.ConfigureEndpoints,
        "Configuring DMS middleware and endpoints."
    );

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

    startupPhaseExecutor.WriteReady("DMS startup completed successfully and HTTP endpoints are configured.");
}
else
{
    startupPhaseExecutor.WriteFailed(
        DmsStartupPhases.ConfigureEndpoints,
        "Configuration validation failed; requests are being short-circuited by invalid-configuration middleware.",
        invalidConfigurationException
    );
}

await app.RunAsync();

/// <summary>
/// Triggers configuration validation. If configuration is invalid, injects a short-circuit middleware to report.
/// Returns true if the middleware was injected.
/// </summary>
OptionsValidationException? ReportInvalidConfiguration(WebApplication app)
{
    try
    {
        // Accessing IOptions<T> forces validation
        _ = app.Services.GetRequiredService<IOptions<AppSettings>>().Value;
        _ = app.Services.GetRequiredService<IOptions<ConfigurationServiceSettings>>().Value;
        _ = app.Services.GetRequiredService<IOptions<MappingSetProviderOptions>>().Value;
        _ = app.Services.GetRequiredService<IOptions<ReverseProxySettings>>().Value;
    }
    catch (OptionsValidationException ex)
    {
        app.UseMiddleware<ReportInvalidConfigurationMiddleware>(ex.Failures);
        return ex;
    }
    return null;
}

async Task InitializeApiSchemas(WebApplication app)
{
    app.Logger.LogInformation("Initializing API schemas at startup");
    var orchestrator = app.Services.GetRequiredService<DmsStartupOrchestrator>();
    await orchestrator.RunByOrderRangeAsync(
        0,
        DmsStartupTaskOrderRanges.ApiSchemaInitializationMaximum,
        CancellationToken.None
    );
    app.Logger.LogInformation(
        "API schema loading and effective schema initialization completed successfully"
    );
}

async Task InitializeBackendMappings(WebApplication app)
{
    app.Logger.LogInformation("Initializing backend mappings at startup");
    var orchestrator = app.Services.GetRequiredService<DmsStartupOrchestrator>();
    await orchestrator.RunByOrderRangeAsync(
        DmsStartupTaskOrderRanges.BackendMappingMinimum,
        DmsStartupTaskOrderRanges.BackendMappingMaximum,
        CancellationToken.None
    );
    app.Logger.LogInformation("Backend mapping initialization completed successfully");
}

async Task InitializeAuthMetadata(WebApplication app)
{
    app.Logger.LogInformation("Initializing authentication metadata caches at startup");
    var orchestrator = app.Services.GetRequiredService<DmsStartupOrchestrator>();
    await orchestrator.RunByOrderRangeAsync(
        DmsStartupTaskOrderRanges.AuthInitializationMinimum,
        DmsStartupTaskOrderRanges.AuthInitializationMaximum,
        CancellationToken.None
    );
    app.Logger.LogInformation("Authentication metadata initialization completed successfully");
}

async Task InitializeDataStores(WebApplication app)
{
    app.Logger.LogInformation("Loading data stores from Configuration Service");
    var dataStoreProvider = app.Services.GetRequiredService<IDataStoreProvider>();
    var multiTenancyEnabled = app.Configuration.GetValue<bool>("AppSettings:MultiTenancy");

    if (multiTenancyEnabled)
    {
        await InitializeDataStoresForMultiTenancy(app, dataStoreProvider);
    }
    else
    {
        await InitializeDataStoresForSingleTenancy(app, dataStoreProvider);
    }
}

async Task InitializeDataStoresForMultiTenancy(WebApplication app, IDataStoreProvider dataStoreProvider)
{
    app.Logger.LogInformation("Multi-tenancy is enabled. Fetching tenants from Configuration Service.");

    IList<string> tenants = await dataStoreProvider.LoadTenants();

    if (tenants.Count == 0)
    {
        // When multi-tenancy is enabled, having 0 tenants at startup is not fatal.
        // Tenants may be created after DMS starts, and the cache-miss fallback will load instances on demand.
        app.Logger.LogWarning(
            "No tenants found in Configuration Service. Data stores will be loaded on-demand when tenants are created and requests arrive."
        );
        return;
    }

    app.Logger.LogInformation(
        "Found {TenantCount} tenants. Loading data stores for each tenant.",
        tenants.Count
    );

    int totalInstances = 0;
    foreach (string tenant in tenants)
    {
        app.Logger.LogInformation(
            "Loading data stores for tenant: {TenantName}",
            LoggingSanitizer.SanitizeForLogging(tenant)
        );

        IList<DataStore> instances = await dataStoreProvider.LoadDataStores(tenant);
        totalInstances += instances.Count;

        app.Logger.LogInformation(
            "Loaded {InstanceCount} data stores for tenant {TenantName}",
            instances.Count,
            LoggingSanitizer.SanitizeForLogging(tenant)
        );

        LogInstanceDetails(app, instances);
    }

    app.Logger.LogInformation(
        "Successfully loaded {TotalInstanceCount} total data stores across {TenantCount} tenants",
        totalInstances,
        tenants.Count
    );
}

async Task InitializeDataStoresForSingleTenancy(WebApplication app, IDataStoreProvider dataStoreProvider)
{
    IList<DataStore> instances = await dataStoreProvider.LoadDataStores();

    if (instances.Count == 0)
    {
        throw new InvalidOperationException(
            "No data stores were loaded from Configuration Service. DMS cannot start without data store configuration."
        );
    }

    app.Logger.LogInformation("Successfully loaded {InstanceCount} data stores", instances.Count);
    LogInstanceDetails(app, instances);
}

void LogInstanceDetails(WebApplication app, IList<DataStore> instances)
{
    foreach (var instance in instances)
    {
        var hasConnectionString = !string.IsNullOrWhiteSpace(instance.ConnectionString);
        app.Logger.LogInformation(
            "Data Store: ID={DataStoreId}, Name='{Name}', Type='{DataStoreType}', HasConnectionString={HasConnectionString}",
            instance.Id,
            instance.Name,
            instance.DataStoreType,
            hasConnectionString
        );
    }
}

void RunBootstrapPhase(
    string phase,
    string startingSummary,
    string successSummary,
    string failureSummary,
    Action action
)
{
    bootstrapStartupStatusSignal.WriteStarting(phase, startingSummary);

    try
    {
        action();
        bootstrapStartupStatusSignal.WriteCompleted(phase, successSummary);
    }
    catch (Exception ex)
    {
        bootstrapStartupStatusSignal.WriteFailed(phase, failureSummary, ex);
        throw;
    }
}

T RunBootstrapPhaseWithResult<T>(
    string phase,
    string startingSummary,
    string successSummary,
    string failureSummary,
    Func<T> action
)
{
    bootstrapStartupStatusSignal.WriteStarting(phase, startingSummary);

    try
    {
        T result = action();
        bootstrapStartupStatusSignal.WriteCompleted(phase, successSummary);
        return result;
    }
    catch (Exception ex)
    {
        bootstrapStartupStatusSignal.WriteFailed(phase, failureSummary, ex);
        throw;
    }
}

public partial class Program
{
    // Compliant solution for Sonar lint S1118
    private Program() { }
}
