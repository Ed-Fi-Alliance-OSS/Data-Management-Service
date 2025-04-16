// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Interface;
using Microsoft.Extensions.DependencyInjection;
using EdFi.DataManagementService.Core.Security;
using Microsoft.AspNetCore.Builder;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using EdFi.DataManagementService.Frontend.BulkLoader.Model;
using EdFi.DataManagementService.Frontend.BulkLoader.Processor;
using Microsoft.Extensions.Logging;
using CommandLine;
using Serilog.Core;

namespace EdFi.DataManagementService.Frontend.BulkLoader
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddHttpClient();
            builder.AddServices();

            var serviceProvider = builder.Services.BuildServiceProvider();
            var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Program");

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
                    if (string.IsNullOrWhiteSpace(options.BulkLoadSourcePath))
                    {
                        logger.LogCritical("Error: SourceFilePath is required.");
                    }
                    IConfiguration config = builder.Configuration;
                    var apiService = serviceProvider.GetRequiredService<IApiService>();
                    var tokenHandler = serviceProvider.GetRequiredService<IConfigurationServiceTokenHandler>();
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

                    var jsonContent = await File.ReadAllTextAsync(options.BulkLoadSourcePath);
                    var model = JsonSerializer.Deserialize<SchoolYearTypesWrapper>(jsonContent);

                    if (model?.SchoolYearTypes == null || model.SchoolYearTypes.Count == 0)
                    {
                        logger.LogError("No school year types found in the JSON.");
                        throw new InvalidOperationException("No school year types found in the JSON.");
                    }

                    await SchoolYearProcessor.ProcessSchoolYearTypesAsync(logger, model, apiService, token);
                });

                return 0;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An error occurred while doing DMS BulkLoader.");
                Console.WriteLine(ex.Message, "An error occurred while doing DMS BulkLoader.");
                return 1;
            }
        }
    }
}
