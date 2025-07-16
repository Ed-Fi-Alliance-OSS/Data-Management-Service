// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Threading.RateLimiting;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.Deploy;
using EdFi.DataManagementService.Backend.OpenSearch;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core;
using EdFi.DataManagementService.Core.OAuth;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using Microsoft.Extensions.Caching.Memory;
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

        // Add custom mapping for ENABLE_MANAGEMENT_ENDPOINTS environment variable
        var enableManagementEndpoints = Environment.GetEnvironmentVariable("ENABLE_MANAGEMENT_ENDPOINTS");
        if (!string.IsNullOrEmpty(enableManagementEndpoints))
        {
            webAppBuilder.Configuration["AppSettings:EnableManagementEndpoints"] = enableManagementEndpoints;
        }

        // Add custom mapping for JWT Authentication environment variables
        MapJwtEnvironmentVariables(webAppBuilder);

        webAppBuilder.Configuration.AddEnvironmentVariables();
        webAppBuilder
            .Services.AddDmsDefaultConfiguration(
                logger,
                webAppBuilder.Configuration.GetSection("CircuitBreaker"),
                webAppBuilder.Configuration.GetSection("AppSettings").GetValue<bool>("MaskRequestBodyInLogs")
            )
            .AddTransient<IAssemblyLoader, ApiSchemaAssemblyLoader>()
            .AddTransient<IContentProvider, ContentProvider>()
            .AddTransient<IVersionProvider, VersionProvider>()
            .AddTransient<IAssemblyProvider, AssemblyProvider>()
            .AddTransient<IOAuthManager, OAuthManager>()
            .Configure<DatabaseOptions>(webAppBuilder.Configuration.GetSection("DatabaseOptions"))
            .Configure<AppSettings>(webAppBuilder.Configuration.GetSection("AppSettings"))
            .Configure<CoreAppSettings>(webAppBuilder.Configuration.GetSection("AppSettings"))
            .AddSingleton<IValidateOptions<AppSettings>, AppSettingsValidator>()
            .Configure<ConnectionStrings>(webAppBuilder.Configuration.GetSection("ConnectionStrings"))
            .AddSingleton<IValidateOptions<ConnectionStrings>, ConnectionStringsValidator>();

        if (webAppBuilder.Configuration.GetSection(RateLimitOptions.RateLimit).Exists())
        {
            logger.Information("Injecting rate limiting");
            ConfigureRateLimit(webAppBuilder);
        }

        ConfigureDatastore(webAppBuilder, logger);
        ConfigureQueryHandler(webAppBuilder, logger);

        webAppBuilder.Services.AddSingleton(
            new DbHealthCheck(
                webAppBuilder.Configuration.GetSection("ConnectionStrings:DatabaseConnection").Value
                    ?? string.Empty,
                webAppBuilder.Configuration.GetSection("AppSettings:Datastore").Value ?? string.Empty,
                webAppBuilder.Services.BuildServiceProvider().GetRequiredService<ILogger<DbHealthCheck>>()
            )
        );

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

        IConfiguration config = webAppBuilder.Configuration;

        // For Token handling
        webAppBuilder.Services.AddMemoryCache();

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
                    client.BaseAddress = new Uri(configServiceSettings.BaseUrl);
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
        webAppBuilder.Services.AddSingleton(serviceProvider =>
        {
            var memoryCache = serviceProvider.GetRequiredService<IMemoryCache>();
            var cacheExpiration = configServiceSettings.CacheExpirationMinutes;
            var defaultExpiration = TimeSpan.FromMinutes(cacheExpiration);

            return new ClaimSetsCache(memoryCache, defaultExpiration);
        });
        webAppBuilder.Services.AddTransient<
            IConfigurationServiceTokenHandler,
            ConfigurationServiceTokenHandler
        >();

        // Register the inner claim set provider by its concrete type
        webAppBuilder.Services.AddTransient<ConfigurationServiceClaimSetProvider>();

        // Register the cache decorator using a factory
        webAppBuilder.Services.AddTransient<IClaimSetProvider>(provider =>
        {
            // Resolve the inner service
            var innerProvider = provider.GetRequiredService<ConfigurationServiceClaimSetProvider>();

            // Resolve the cache dependency
            var claimSetsCache = provider.GetRequiredService<ClaimSetsCache>();

            // Create and return the caching decorator
            return new CachedClaimSetProvider(innerProvider, claimSetsCache);
        });

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
            logger.Information("Injecting PostgreSQL as the primary backend datastore");
            webAppBuilder.Services.AddPostgresqlDatastore(
                webAppBuilder.Configuration.GetSection("ConnectionStrings:DatabaseConnection").Value
                    ?? string.Empty
            );
            webAppBuilder.Services.AddSingleton<IDatabaseDeploy, Backend.Postgresql.Deploy.DatabaseDeploy>();
        }
        else
        {
            logger.Information("Injecting MSSQL as the primary backend datastore");
            webAppBuilder.Services.AddSingleton<IDatabaseDeploy, Backend.Mssql.Deploy.DatabaseDeploy>();
        }
    }

    private static void ConfigureQueryHandler(WebApplicationBuilder webAppBuilder, Serilog.ILogger logger)
    {
        if (
            string.Equals(
                webAppBuilder.Configuration.GetSection("AppSettings:QueryHandler").Value,
                "postgresql",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            logger.Information("Injecting PostgreSQL as the backend query handler");
            webAppBuilder.Services.AddPostgresqlQueryHandler();
        }
        else
        {
            logger.Information("Injecting OpenSearch as the backend query handler");
            webAppBuilder.Services.AddOpenSearchQueryHandler(
                webAppBuilder.Configuration.GetSection("ConnectionStrings:OpenSearchUrl").Value
                    ?? string.Empty
            );
        }
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

    private static void MapJwtEnvironmentVariables(WebApplicationBuilder webAppBuilder)
    {
        // Map JWT Authentication environment variables to configuration
        var jwtEnvMappings = new Dictionary<string, string>
        {
            ["JWT_AUTHENTICATION_AUTHORITY"] = "JwtAuthentication:Authority",
            ["JWT_AUTHENTICATION_AUDIENCE"] = "JwtAuthentication:Audience",
            ["JWT_AUTHENTICATION_METADATA_ADDRESS"] = "JwtAuthentication:MetadataAddress",
            ["JWT_AUTHENTICATION_REQUIRE_HTTPS_METADATA"] = "JwtAuthentication:RequireHttpsMetadata",
            ["JWT_AUTHENTICATION_ROLE_CLAIM_TYPE"] = "JwtAuthentication:RoleClaimType",
            ["JWT_AUTHENTICATION_CLIENT_ROLE"] = "JwtAuthentication:ClientRole",
            ["JWT_AUTHENTICATION_CLOCK_SKEW_SECONDS"] = "JwtAuthentication:ClockSkewSeconds",
            ["JWT_AUTHENTICATION_REFRESH_INTERVAL_MINUTES"] = "JwtAuthentication:RefreshIntervalMinutes",
            ["JWT_AUTHENTICATION_AUTOMATIC_REFRESH_INTERVAL_HOURS"] =
                "JwtAuthentication:AutomaticRefreshIntervalHours",
        };

        foreach (var (envVar, configKey) in jwtEnvMappings)
        {
            var value = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrEmpty(value))
            {
                webAppBuilder.Configuration[configKey] = value;
            }
        }
    }
}
