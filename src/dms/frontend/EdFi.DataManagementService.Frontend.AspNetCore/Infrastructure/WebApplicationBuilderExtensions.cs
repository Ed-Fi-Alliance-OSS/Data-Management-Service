// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Threading.RateLimiting;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.OAuth;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Serilog;
using CoreAppSettings = EdFi.DataManagementService.Core.Configuration.AppSettings;

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
            .Configure<CoreAppSettings>(webAppBuilder.Configuration.GetSection("AppSettings"))
            .Configure<ReverseProxySettings>(
                webAppBuilder.Configuration.GetSection("AppSettings:ReverseProxy")
            )
            .Configure<ConfigurationServiceSettings>(
                webAppBuilder.Configuration.GetSection("ConfigurationServiceSettings")
            )
            .Configure<ResourceLinksOptions>(
                webAppBuilder.Configuration.GetSection("DataManagement:ResourceLinks")
            )
            .AddSingleton<IStartupStatusSignal, FileStartupStatusSignal>()
            .AddSingleton<IStartupProcessExit, EnvironmentStartupProcessExit>()
            .AddSingleton<StartupPhaseExecutor>()
            .AddSingleton<
                IValidateOptions<Frontend.AspNetCore.Configuration.AppSettings>,
                AppSettingsValidator
            >()
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
            var configureLogging = new LoggerConfiguration()
                .ReadFrom.Configuration(webAppBuilder.Configuration)
                .Enrich.FromLogContext()
                .CreateLogger();
            webAppBuilder.Logging.ClearProviders();
            webAppBuilder.Logging.AddSerilog(configureLogging);

            return configureLogging;
        }

        ConfigurationManager config = webAppBuilder.Configuration;

        // MemoryCache backs claim-set caching; HybridCache backs token, application-context, and profile caches.
        webAppBuilder.Services.AddMemoryCache();
        webAppBuilder.Services.AddHybridCache();

        // Access Configuration service
        var configServiceSettings = config
            .GetSection("ConfigurationServiceSettings")
            .Get<ConfigurationServiceSettings>();
        if (configServiceSettings == null)
        {
            logger.Error("Error reading ConfigurationServiceSettings");
            throw new InvalidOperationException(
                "Unable to read ConfigurationServiceSettings from appsettings"
            );
        }

        webAppBuilder.Services.AddTransient<ConfigurationServiceResponseHandler>();
        webAppBuilder
            .Services.AddHttpClient<ConfigurationServiceApiClient>(
                (serviceProvider, client) =>
                {
                    client.BaseAddress = new Uri($"{configServiceSettings.BaseUrl.Trim('/')}/");
                    client.DefaultRequestHeaders.Add("Accept", "application/json");
                    client.DefaultRequestHeaders.Add("Accept", "application/x-www-form-urlencoded");
                }
            )
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler())
            .AddHttpMessageHandler<ConfigurationServiceResponseHandler>();

        webAppBuilder.Services.AddSingleton(
            new ConfigurationServiceContext(
                configServiceSettings.ClientId,
                configServiceSettings.ClientSecret,
                configServiceSettings.Scope
            )
        );

        // Bind CacheSettings from configuration
        var cacheSettings = new CacheSettings();
        webAppBuilder.Configuration.GetSection("CacheSettings").Bind(cacheSettings);
        webAppBuilder.Services.AddSingleton(cacheSettings);

        webAppBuilder.Services.AddTransient<
            IConfigurationServiceTokenHandler,
            ConfigurationServiceTokenHandler
        >();

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

        // Register data store services
        webAppBuilder.Services.AddSingleton<IConnectionStringDecryptionService>(
            new ConnectionStringDecryptionService(configServiceSettings.EncryptionKey)
        );
        webAppBuilder.Services.AddSingleton<IDataStoreProvider, ConfigurationServiceDataStoreProvider>();
        webAppBuilder.Services.AddSingleton<IConnectionStringProvider, DmsConnectionStringProvider>();

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
            webAppBuilder.Services.AddPostgresqlDatastore(webAppBuilder.Configuration);
            logger.Information("Injecting PostgreSQL relational write runtime services");
            webAppBuilder.Services.AddPostgresqlReferenceResolver();
            webAppBuilder.Services.AddPostgresqlRelationalTokenInfoEducationOrganizationLookup();
            webAppBuilder.Services.AddSingleton<
                IDatabaseFingerprintReader,
                Backend.Postgresql.PostgresqlDatabaseFingerprintReader
            >();
            webAppBuilder.Services.AddSingleton<
                IResourceKeyRowReader,
                Backend.Postgresql.PostgresqlResourceKeyRowReader
            >();
        }
        else
        {
            logger.Information("Injecting MSSQL as the primary backend datastore");

            logger.Information("Injecting MSSQL relational write runtime services");
            AddMssqlRelationalRuntimeServices(webAppBuilder.Services, webAppBuilder.Configuration);
            webAppBuilder.Services.AddMssqlRelationalTokenInfoEducationOrganizationLookup();
            webAppBuilder.Services.AddSingleton<
                IDatabaseFingerprintReader,
                Backend.Mssql.MssqlDatabaseFingerprintReader
            >();
            webAppBuilder.Services.AddSingleton<
                IResourceKeyRowReader,
                Backend.Mssql.MssqlResourceKeyRowReader
            >();
        }

        logger.Information("Injecting relational document store repository surface");
        webAppBuilder.Services.AddScoped<IDocumentStoreRepository, RelationalDocumentStoreRepository>();
        webAppBuilder.Services.AddScoped<IQueryHandler, RelationalDocumentStoreRepository>();
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

    private static void AddMssqlRelationalRuntimeServices(
        IServiceCollection services,
        IConfiguration configuration
    )
    {
        services.AddRelationalMappingSetServices(configuration, SqlDialect.Mssql, new MssqlDialectRules());
        services.AddMssqlReferenceResolver();
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
