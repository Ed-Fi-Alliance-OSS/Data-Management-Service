// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Interface;
using Microsoft.Extensions.DependencyInjection;
using EdFi.DataManagementService.Core.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CommandLine;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Caching.Memory;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Frontend.SchoolYearLoader.Processor;

namespace EdFi.DataManagementService.Frontend.SchoolYearLoader
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var host = new HostBuilder()
           .ConfigureAppConfiguration((hostingContext, config) =>
           {
               config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
           })
           .ConfigureServices((context, services) =>
           {
               ConfigureServices.AddServices(context.Configuration, services);
           })
           .ConfigureLogging(logging =>
           {
               logging.AddConsole();
           })
           .Build();

            var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Program");

            try
            {
                var result = Parser.Default.ParseArguments<CommandLineOverrides>(args);
                result.WithNotParsed(errors =>
                {
                    logger.LogError("Error parsing command-line arguments. Please provide valid parameters.");
                    Environment.Exit(1);
                });

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
                await host.RunAsync();
                return 0;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An error occurred while doing DMS SchoolYearLoader.");
                Console.WriteLine(ex.Message, "An error occurred while doing DMS SchoolYearLoader.");
                return 1;
            }
        }
    }
}
