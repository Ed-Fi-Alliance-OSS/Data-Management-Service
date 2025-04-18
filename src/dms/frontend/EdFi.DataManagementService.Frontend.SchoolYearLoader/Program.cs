// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Interface;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CommandLine;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Caching.Memory;
using EdFi.DataManagementService.Frontend.SchoolYearLoader.Processor;
using EdFi.DataManagementService.Frontend.SchoolYearLoader.Configuration;

namespace EdFi.DataManagementService.Frontend.SchoolYearLoader
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var host = new HostBuilder()
           .ConfigureAppConfiguration((hostingContext, config) =>
           {
               config.SetBasePath(AppContext.BaseDirectory);
               config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
           })
           .ConfigureServices((context, services) =>
           {
               HostBuilderExtensions.AddServices(context.Configuration, services);
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
                        throw new InvalidOperationException("Error: StartYear must be a positive integer.");
                    }
                    if (options.EndYear <= 0)
                    {
                        logger.LogCritical("Error: EndYear must be a positive integer.");
                        throw new InvalidOperationException("Error: EndYear must be a positive integer.");
                    }
                    if (options.CurrentSchoolYear <= 0)
                    {
                        logger.LogCritical("Error: CurrentSchoolYear must be a positive integer.");
                        throw new InvalidOperationException("Error: CurrentSchoolYear must be a positive integer.");
                    }

                    await host.StartAsync();

                    IConfiguration config = host.Services.GetRequiredService<IConfiguration>();

                    var apiService = host.Services.GetRequiredService<IApiService>();
                    var configurationServiceSettings = config
                        .GetSection("ConfigurationServiceSettings")
                        .Get<ConfigurationServiceSettings>();

                    if (configurationServiceSettings == null)
                    {
                        logger.LogError("ConfigurationServiceSettings cannot be null.");
                        throw new InvalidOperationException("ConfigurationServiceSettings cannot be null.");
                    }

                    await SchoolYearProcessor.ProcessSchoolYearTypesAsync(logger, apiService, options.StartYear, options.EndYear, options.CurrentSchoolYear);
                });
                await host.StopAsync();
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
