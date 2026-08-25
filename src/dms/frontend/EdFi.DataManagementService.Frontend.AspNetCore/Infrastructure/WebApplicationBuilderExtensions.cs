// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.OAuth;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Serilog;
using CoreAppSettings = EdFi.DataManagementService.Core.Configuration.AppSettings;
using CoreAppSettingsValidator = EdFi.DataManagementService.Core.Configuration.AppSettingsValidator;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

public static class WebApplicationBuilderExtensions
{
    public static void AddServices(this WebApplicationBuilder webAppBuilder)
    {
        var logger = ConfigureLogging();

        // Debug logging
        logger.Information(
            "Current environment: {EnvironmentName}",
            webAppBuilder.Environment.EnvironmentName
        );

        webAppBuilder.Configuration.AddEnvironmentVariables();
        webAppBuilder
            .Services.AddDmsDefaultConfiguration(
                logger,
                webAppBuilder.Configuration.GetSection("CircuitBreaker"),
                webAppBuilder.Configuration.GetSection("DeadlockRetry"),
                webAppBuilder.Configuration.GetSection("AppSettings").GetValue<bool>("MaskRequestBodyInLogs")
            )
            .AddTransient<IApiSchemaAssetManifestProvider, ApiSchemaAssetManifestProvider>()
            .AddTransient<IContentProvider, ContentProvider>()
            .AddTransient<IVersionProvider, VersionProvider>()
            .AddTransient<ITenantValidator, TenantValidator>()
            .AddTransient<IOAuthManager, OAuthManager>()
            .Configure<DatabaseOptions>(webAppBuilder.Configuration.GetSection("DatabaseOptions"))
            .Configure<Frontend.AspNetCore.Configuration.AppSettings>(
                webAppBuilder.Configuration.GetSection("AppSettings")
            )
            .AddOptions<CoreAppSettings>()
            .Bind(webAppBuilder.Configuration.GetSection("AppSettings"))
            .ValidateOnStart()
            .Services.Configure<ReverseProxySettings>(
                webAppBuilder.Configuration.GetSection("AppSettings:ReverseProxy")
            )
            .Configure<ConfigurationServiceSettings>(
                webAppBuilder.Configuration.GetSection("ConfigurationServiceSettings")
            )
            .Configure<ResourceLinksOptions>(
                webAppBuilder.Configuration.GetSection("DataManagement:ResourceLinks")
            )
            .AddDmsDocumentCacheOptions(webAppBuilder.Configuration)
            .AddSingleton<IStartupStatusSignal, FileStartupStatusSignal>()
            .AddSingleton<IStartupProcessExit, EnvironmentStartupProcessExit>()
            .AddSingleton<StartupPhaseExecutor>()
            .AddSingleton<
                IValidateOptions<Frontend.AspNetCore.Configuration.AppSettings>,
                Frontend.AspNetCore.Configuration.AppSettingsValidator
            >()
            .AddSingleton<IValidateOptions<CoreAppSettings>, CoreAppSettingsValidator>()
            .AddSingleton<
                IValidateOptions<ConfigurationServiceSettings>,
                ConfigurationServiceSettingsValidator
            >()
            .AddSingleton<IValidateOptions<ReverseProxySettings>, ReverseProxySettingsValidator>()
            .AddSingleton<IValidateOptions<MappingSetProviderOptions>, MappingSetProviderOptionsValidator>();

        if (webAppBuilder.Configuration.GetSection(RateLimitOptions.RateLimit).Exists())
        {
            logger.Information("Injecting rate limiting");
            ConfigureRateLimit(webAppBuilder);
        }

        ConfigureDatastore(webAppBuilder, logger);

        webAppBuilder.Services.AddSingleton<DbHealthCheck>(serviceProvider =>
        {
            var connectionStringProvider = serviceProvider.GetRequiredService<IConnectionStringProvider>();
            var datastore =
                webAppBuilder.Configuration.GetSection("AppSettings:Datastore").Value ?? string.Empty;
            var logger = serviceProvider.GetRequiredService<ILogger<DbHealthCheck>>();

            string connectionString =
                connectionStringProvider.GetHealthCheckConnectionString() ?? string.Empty;
            return new DbHealthCheck(connectionString, datastore, logger);
        });

        webAppBuilder
            .Services.AddHealthChecks()
            .AddCheck<ApplicationHealthCheck>("ApplicationHealthCheck")
            .AddCheck<DbHealthCheck>("DbHealthCheck");

        Serilog.ILogger ConfigureLogging()
        {
            var configureLogging = LoggingConfigurator.ConfigureLogging(webAppBuilder.Configuration);
            webAppBuilder.Logging.ClearProviders();
            // dispose: true so host shutdown disposes the logger and flushes the OTLP sink's
            // pending batch; without it, buffered events are dropped on exit.
            webAppBuilder.Logging.AddSerilog(configureLogging, dispose: true);

            return configureLogging;
        }

        // MemoryCache backs claim-set caching. HybridCache is registered by the shared CMS data-store provider.
        webAppBuilder.Services.AddMemoryCache();
        webAppBuilder.Services.AddDmsConfigurationServiceDataStoreProvider(webAppBuilder.Configuration);

        // Register ConfigurationServiceClaimSetProvider as its interface
        webAppBuilder.Services.AddSingleton<
            IConfigurationServiceClaimSetProvider,
            ConfigurationServiceClaimSetProvider
        >();

        // Register CachedClaimSetProvider as IClaimSetProvider with in-process stampede protection
        webAppBuilder.Services.AddSingleton<CachedClaimSetProvider>();
        webAppBuilder.Services.AddSingleton<IClaimSetProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<CachedClaimSetProvider>()
        );

        webAppBuilder.Services.Replace(
            ServiceDescriptor.Singleton<IDataStoreProvider, DocumentCacheRefreshNotifyingDataStoreProvider>()
        );
        webAppBuilder.Services.AddDmsDocumentCacheTargetRegistry(webAppBuilder.Configuration);
        webAppBuilder.Services.AddDocumentCacheProjectionSupervisor(registerHostedService: true);

        // Add JWT authentication services from Core
        webAppBuilder.Services.AddJwtAuthentication(webAppBuilder.Configuration);
    }

    private static void ConfigureDatastore(WebApplicationBuilder webAppBuilder, Serilog.ILogger logger)
    {
        if (
            string.Equals(
                webAppBuilder.Configuration.GetSection("AppSettings:Datastore").Value,
                "postgresql",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            logger.Information(
                "Injecting PostgreSQL as the primary backend datastore with per-request connection strings"
            );
            logger.Information("Injecting PostgreSQL relational write runtime services");
            webAppBuilder.Services.AddPostgresqlDocumentCacheRuntimeServices(webAppBuilder.Configuration);
        }
        else
        {
            logger.Information("Injecting MSSQL as the primary backend datastore");

            logger.Information("Injecting MSSQL relational write runtime services");
            webAppBuilder.Services.AddMssqlDocumentCacheRuntimeServices(webAppBuilder.Configuration);
        }

        logger.Information("Injecting relational document store repository surface");
        webAppBuilder.Services.AddScoped<IDocumentStoreRepository, RelationalDocumentStoreRepository>();
        webAppBuilder.Services.AddScoped<IQueryHandler, RelationalDocumentStoreRepository>();
        webAppBuilder.Services.AddScoped<IPartitionQueryHandler, RelationalDocumentStoreRepository>();
        webAppBuilder.Services.Replace(
            ServiceDescriptor.Singleton<IBackendMappingInitializer, RelationalBackendMappingInitializer>()
        );
    }

    private static void ConfigureRateLimit(WebApplicationBuilder webAppBuilder)
    {
        webAppBuilder.Services.Configure<RateLimitOptions>(
            webAppBuilder.Configuration.GetSection(RateLimitOptions.RateLimit)
        );
        var rateLimitOptions = new RateLimitOptions();
        webAppBuilder.Configuration.GetSection(RateLimitOptions.RateLimit).Bind(rateLimitOptions);

        webAppBuilder.Services.AddRateLimiter(limiterOptions =>
        {
            limiterOptions.RejectionStatusCode = (int)HttpStatusCode.TooManyRequests;
            limiterOptions.OnRejected = WriteRateLimitRejectionAsync;
            limiterOptions.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Request.Headers.Host.ToString(),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = rateLimitOptions.PermitLimit,
                        QueueLimit = rateLimitOptions.QueueLimit,
                        Window = TimeSpan.FromSeconds(rateLimitOptions.Window),
                    }
                )
            );
        });
    }

    /// <summary>
    /// Serves the rejection produced by the rate limiter middleware, which applies
    /// RejectionStatusCode before invoking this callback. Rejected requests never reach the DMS
    /// core pipeline, so the Retry-After header and the problem-details body are written at this
    /// boundary. The Retry-After value is the limiter's recommended retry delay rounded up to
    /// whole seconds so a client never retries sooner than recommended, and the body stays
    /// constant whether or not the limiter supplies retry-after metadata.
    /// </summary>
    internal static async ValueTask WriteRateLimitRejectionAsync(
        OnRejectedContext context,
        CancellationToken cancellationToken
    )
    {
        HttpContext httpContext = context.HttpContext;

        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
        {
            httpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(
                CultureInfo.InvariantCulture
            );
        }

        var appSettings = httpContext.RequestServices.GetRequiredService<
            IOptions<Frontend.AspNetCore.Configuration.AppSettings>
        >();
        var traceId = AspNetCoreFrontend.ExtractTraceIdFrom(httpContext.Request, appSettings);

        httpContext.Response.ContentType = "application/problem+json; charset=utf-8";
        await httpContext.Response.WriteAsync(
            JsonSerializer.Serialize(
                FailureResponse.ForTooManyRequests(traceId),
                AspNetCoreFrontend.SharedSerializerOptions
            ),
            cancellationToken
        );
    }
}

internal sealed class RelationalBackendMappingInitializer(
    IMappingSetProvider mappingSetProvider,
    IRuntimeMappingSetCompiler runtimeMappingSetCompiler,
    ILogger<RelationalBackendMappingInitializer> logger
) : IBackendMappingInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var key = runtimeMappingSetCompiler.GetCurrentKey();

        logger.LogInformation(
            "Initializing relational mapping set for EffectiveSchemaHash {EffectiveSchemaHash}, Dialect {Dialect}, RelationalMappingVersion {RelationalMappingVersion}",
            key.EffectiveSchemaHash,
            key.Dialect,
            key.RelationalMappingVersion
        );

        var mappingSet = await mappingSetProvider
            .GetOrCreateAsync(key, cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "Relational mapping set ready for EffectiveSchemaHash {EffectiveSchemaHash}, Dialect {Dialect}",
            mappingSet.Key.EffectiveSchemaHash,
            mappingSet.Key.Dialect
        );
    }
}
