// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Interface;
using Microsoft.Extensions.DependencyInjection;
using EdFi.DataManagementService.Core.Security;
using Microsoft.Extensions.Configuration;
using EdFi.DataManagementService.Frontend.SchoolYearLoader.Processor;
using Microsoft.Extensions.Logging;
using CommandLine;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Serilog;
using EdFi.DataManagementService.Core;
using EdFi.DataManagementService.Backend.Deploy;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Backend.OpenSearch;
using Microsoft.Extensions.Options;
using EdFi.DataManagementService.Core.ApiSchema;

namespace EdFi.DataManagementService.Frontend.SchoolYearLoader
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {

            var host = new HostBuilder()
           .ConfigureAppConfiguration((hostingContext, config) =>
           {
               config.AddEnvironmentVariables();
               config.AddJsonFile("appsettings.json", optional: true);
           })
           .ConfigureServices((context, services) =>
           {
               AddServices(context.Configuration, services);
           })
           .ConfigureLogging(logging =>
           {
               logging.AddConsole();
           })
           .Build();
            await host.RunAsync();
            var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Program");

            try
            {
                // Parse command-line arguments
                var result = Parser.Default.ParseArguments<CommandLineOverrides>(args);
                // Handle parsing errors
                result.WithNotParsed(errors =>
                {
                    logger.LogError("Error parsing command-line arguments. Please provide valid parameters.");
                    Environment.Exit(1);
                });

                // Execute program logic if parsing is successful
                await result.WithParsedAsync(async options =>
                {
                    if (options.StartYear <= 0)
                    {
                        logger.LogCritical("Error: StartYear must be a positive integer.");
                    }
                    if (options.EndYear <= 0)
                    {
                        logger.LogCritical("Error: EndYear must be a positive integer.");
                    }
                    IConfiguration config = host.Services.GetRequiredService<IConfiguration>();
                    var _apiSchemaProvider = host.Services.GetRequiredService<IApiSchemaProvider>();
                    _apiSchemaProvider.GetApiSchemaNodes();


                    var apiService = host.Services.GetRequiredService<IApiService>();
                    var tokenHandler = host.Services.GetRequiredService<IConfigurationServiceTokenHandler>();
                    var configurationServiceSettings = config
                        .GetSection("ConfigurationServiceSettings")
                        .Get<ConfigurationServiceSettings>();

                    if (configurationServiceSettings == null)
                    {
                        logger.LogError("ConfigurationServiceSettings cannot be null.");
                        throw new InvalidOperationException("ConfigurationServiceSettings cannot be null.");
                    }

                    var token = await tokenHandler.GetTokenAsync(clientId: configurationServiceSettings.ClientId,
                                                                    clientSecret: configurationServiceSettings.ClientSecret,
                                                                    scope: configurationServiceSettings.Scope);

                    if (string.IsNullOrEmpty(token))
                    {
                        logger.LogError("Token cannot be null or empty.");
                        throw new InvalidOperationException("Token cannot be null or empty.");
                    }

                    await SchoolYearProcessor.ProcessSchoolYearTypesAsync(logger, apiService, token, options.StartYear, options.EndYear);
                });
                return 0;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An error occurred while doing DMS SchoolYearLoader.");
                Console.WriteLine(ex.Message, "An error occurred while doing DMS SchoolYearLoader.");
                return 1;
            }
        }


        private static void AddServices(IConfiguration configuration, IServiceCollection services)
        {
            // Initialize logging configuration
            var logger = ConfigureLogging(configuration);


            // Register services using the provided configuration
            services.AddDmsDefaultConfiguration(
                logger,
                configuration.GetSection("CircuitBreaker")
            );

            ConfigureDatastore(configuration, services, logger);
            ConfigureQueryHandler(configuration, services, logger);
            services.AddTransient<ISecurityMetadataProvider, SecurityMetadataProvider>();
            services.AddTransient<IClaimSetCacheService, ClaimSetCacheService>();

            services.Configure<ConfigurationServiceSettings>(configuration.GetSection("ConfigurationServiceSettings"));
            services.AddSingleton(sp => sp.GetRequiredService<IOptions<ConfigurationServiceSettings>>().Value);
            // For Token handling
            services.AddSingleton<IApiClientDetailsProvider, ApiClientDetailsProvider>();
            // Access Configuration service
            var configServiceSettings = configuration.GetSection("ConfigurationServiceSettings").Get<ConfigurationServiceSettings>();

            if (configServiceSettings == null)
            {
                logger.Error("Error reading ConfigurationServiceSettings");
                throw new InvalidOperationException(
                    "Unable to read ConfigurationServiceSettings from appsettings"
                );
            }
            services.AddTransient<ConfigurationServiceResponseHandler>();
            services.AddHttpClient<ConfigurationServiceApiClient>((serviceProvider, client) =>
            {
                var configServiceSettings = serviceProvider.GetRequiredService<ConfigurationServiceSettings>();
                client.BaseAddress = new Uri(configServiceSettings.BaseUrl);
                client.DefaultRequestHeaders.Add("Accept", "application/json");
                client.DefaultRequestHeaders.Add("Accept", "application/x-www-form-urlencoded");
            })
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
            services.AddTransient<ISecurityMetadataProvider, SecurityMetadataProvider>();
            services.AddTransient<IClaimSetCacheService, ClaimSetCacheService>();

            // Logging configuration setup
            Serilog.ILogger ConfigureLogging(IConfiguration config)
            {
                var configureLogging = new LoggerConfiguration()
                    .ReadFrom.Configuration(config)
                    .Enrich.FromLogContext()
                    .CreateLogger();

                // Clear default logging providers and add Serilog
                services.AddLogging(builder =>
                {
                    builder.ClearProviders();
                    builder.AddSerilog(configureLogging);
                });

                return configureLogging;
            }
        }
        private static void ConfigureDatastore(IConfiguration configuration, IServiceCollection services, Serilog.ILogger logger)
        {
            var datastore = configuration["AppSettings:Datastore"];

            if (string.Equals(datastore, "postgresql", StringComparison.OrdinalIgnoreCase))
            {
                logger.Information("Injecting PostgreSQL as the primary backend datastore");

                var connectionString = configuration.GetConnectionString("DatabaseConnection") ?? string.Empty;

                services.AddPostgresqlDatastore(connectionString);
                services.AddSingleton<IDatabaseDeploy, Backend.Postgresql.Deploy.DatabaseDeploy>();
            }
            else
            {
                logger.Information("Injecting MSSQL as the primary backend datastore");

                services.AddSingleton<IDatabaseDeploy, Backend.Mssql.Deploy.DatabaseDeploy>();
            }
        }

        private static void ConfigureQueryHandler(IConfiguration configuration, IServiceCollection services, Serilog.ILogger logger)
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
                    configuration.GetSection("ConnectionStrings:OpenSearchUrl").Value
                        ?? string.Empty
                );
            }
        }
        public class ConfigurationServiceSettings
        {
            public required string BaseUrl { get; set; }
            public required string ClientId { get; set; }
            public required string ClientSecret { get; set; }
            public required string Scope { get; set; }
            public required int CacheExpirationMinutes { get; set; }
        }

        public class ConfigurationServiceSettingsValidator : IValidateOptions<ConfigurationServiceSettings>
        {
            public ValidateOptionsResult Validate(string? name, ConfigurationServiceSettings options)
            {
                if (string.IsNullOrWhiteSpace(options.BaseUrl))
                {
                    return ValidateOptionsResult.Fail("Missing required ConfigurationServiceSettings value: BaseUrl");
                }

                if (string.IsNullOrWhiteSpace(options.ClientId))
                {
                    return ValidateOptionsResult.Fail(
                        "Missing required ConfigurationServiceSettings value: ClientId"
                    );
                }
                if (string.IsNullOrWhiteSpace(options.ClientSecret))
                {
                    return ValidateOptionsResult.Fail(
                        "Missing required ConfigurationServiceSettings value: ClientSecret"
                    );
                }
                if (string.IsNullOrWhiteSpace(options.Scope))
                {
                    return ValidateOptionsResult.Fail("Missing required ConfigurationServiceSettings value: Scope");
                }
                if (options.CacheExpirationMinutes > 0)
                {
                    return ValidateOptionsResult.Fail(
                        "Missing required ConfigurationServiceSettings value: CacheExpirationMinutes"
                    );
                }

                return ValidateOptionsResult.Success;
            }
        }


    }


}
