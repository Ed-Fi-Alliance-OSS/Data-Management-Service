// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.Deploy;
using EdFi.DataManagementService.Backend.OpenSearch;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core;
using EdFi.DataManagementService.Core.OAuth;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Frontend.SchoolYearLoader.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using CoreAppSettings = EdFi.DataManagementService.Core.Configuration.AppSettings;

namespace EdFi.DataManagementService.Frontend.SchoolYearLoader
{
    public static class HostBuilderExtensions
    {
        public static void AddServices(IConfiguration configuration, IServiceCollection services)
        {
            var logger = ConfigureLogging(configuration);

            services
                .AddDmsDefaultConfiguration(logger, configuration.GetSection("CircuitBreaker"), false)
                .AddTransient<IOAuthManager, OAuthManager>()
                .Configure<DatabaseOptions>(configuration.GetSection("DatabaseOptions"))
                .Configure<AppSettings>(configuration.GetSection("AppSettings"))
                .Configure<CoreAppSettings>(configuration.GetSection("AppSettings"))
                .AddSingleton<IValidateOptions<AppSettings>, AppSettingsValidator>()
                .Configure<ConnectionStrings>(configuration.GetSection("ConnectionStrings"))
                .AddSingleton<IValidateOptions<ConnectionStrings>, ConnectionStringsValidator>()
                .AddSingleton<IValidateOptions<IdentitySettings>, IdentitySettingsValidator>();

            ConfigureDatastore(configuration, services, logger);
            ConfigureQueryHandler(configuration, services, logger);

            services.Configure<ConfigurationServiceSettings>(
                configuration.GetSection("ConfigurationServiceSettings")
            );
            services.AddSingleton(sp =>
                sp.GetRequiredService<IOptions<ConfigurationServiceSettings>>().Value
            );
            // For Token handling
            services.AddSingleton<IApiClientDetailsProvider, ApiClientDetailsProvider>();
            // Access Configuration service
            var configServiceSettings = configuration
                .GetSection("ConfigurationServiceSettings")
                .Get<ConfigurationServiceSettings>();

            if (configServiceSettings == null)
            {
                logger.Error("Error reading ConfigurationServiceSettings");
                throw new InvalidOperationException(
                    "Unable to read ConfigurationServiceSettings from appsettings"
                );
            }
            services.AddTransient<ConfigurationServiceResponseHandler>();
            services
                .AddHttpClient<ConfigurationServiceApiClient>(
                    (serviceProvider, client) =>
                    {
                        var configServiceSettings =
                            serviceProvider.GetRequiredService<ConfigurationServiceSettings>();
                        client.BaseAddress = new Uri(configServiceSettings.BaseUrl);
                        client.DefaultRequestHeaders.Add("Accept", "application/json");
                        client.DefaultRequestHeaders.Add("Accept", "application/x-www-form-urlencoded");
                    }
                )
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler())
                .AddHttpMessageHandler<ConfigurationServiceResponseHandler>();

            services.AddSingleton(
                new ConfigurationServiceContext(
                    configServiceSettings.ClientId,
                    configServiceSettings.ClientSecret,
                    configServiceSettings.Scope
                )
            );

            services.AddMemoryCache();

            services.AddSingleton(serviceProvider =>
            {
                var memoryCache = serviceProvider.GetRequiredService<IMemoryCache>();
                var cacheExpiration = configServiceSettings.CacheExpirationMinutes;
                var defaultExpiration = TimeSpan.FromMinutes(cacheExpiration);

                return new ClaimSetsCache(memoryCache, defaultExpiration);
            });

            services.AddTransient<IConfigurationServiceTokenHandler, ConfigurationServiceTokenHandler>();

            // Register the inner claim set provider by its concrete type
            services.AddTransient<ConfigurationServiceClaimSetProvider>();

            // Register the cache decorator using a factory
            services.AddTransient<IClaimSetProvider>(provider =>
            {
                var innerProvider = provider.GetRequiredService<ConfigurationServiceClaimSetProvider>();
                var claimSetsCache = provider.GetRequiredService<ClaimSetsCache>();
                return new CachedClaimSetProvider(innerProvider, claimSetsCache);
            });

            Serilog.ILogger ConfigureLogging(IConfiguration config)
            {
                var configureLogging = new LoggerConfiguration()
                    .ReadFrom.Configuration(config)
                    .Enrich.FromLogContext()
                    .CreateLogger();

                services.AddLogging(builder =>
                {
                    builder.ClearProviders();
                    builder.AddSerilog(configureLogging);
                });
                return configureLogging;
            }
        }

        private static void ConfigureDatastore(
            IConfiguration configuration,
            IServiceCollection services,
            Serilog.ILogger logger
        )
        {
            var datastore = configuration["AppSettings:Datastore"];

            if (string.Equals(datastore, "postgresql", StringComparison.OrdinalIgnoreCase))
            {
                logger.Information("Injecting PostgreSQL as the primary backend datastore");
                var connectionString =
                    configuration.GetConnectionString("DatabaseConnection") ?? string.Empty;
                services.AddPostgresqlDatastore(connectionString);
                services.AddSingleton<IDatabaseDeploy, Backend.Postgresql.Deploy.DatabaseDeploy>();
            }
            else
            {
                logger.Information("Injecting MSSQL as the primary backend datastore");
                services.AddSingleton<IDatabaseDeploy, Backend.Mssql.Deploy.DatabaseDeploy>();
            }
        }

        private static void ConfigureQueryHandler(
            IConfiguration configuration,
            IServiceCollection services,
            Serilog.ILogger logger
        )
        {
            if (
                string.Equals(
                    configuration.GetSection("AppSettings:QueryHandler").Value,
                    "postgresql",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                logger.Information("Injecting PostgreSQL as the backend query handler");
                services.AddPostgresqlQueryHandler();
            }
            else
            {
                logger.Information("Injecting OpenSearch as the backend query handler");
                services.AddOpenSearchQueryHandler(
                    configuration.GetSection("ConnectionStrings:OpenSearchUrl").Value ?? string.Empty
                );
            }
        }
    }
}
