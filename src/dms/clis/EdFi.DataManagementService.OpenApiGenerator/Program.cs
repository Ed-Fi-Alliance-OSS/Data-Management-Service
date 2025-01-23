// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.OpenApiGenerator.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

var serviceCollection = new ServiceCollection();
ConfigureServices(serviceCollection);
var serviceProvider = serviceCollection.BuildServiceProvider();

var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
var generator = serviceProvider.GetRequiredService<OpenApiGenerator>();

try
{
    // Get parameters from command-line arguments
    var argsDict = args.Select(arg => arg.Split(new[] { ':' }, 2))
        .Where(split => split.Length == 2)
        .ToDictionary(split => split[0].ToLower(), split => split[1].Trim());

    if (!argsDict.ContainsKey("core") || !argsDict.ContainsKey("ext") || !argsDict.ContainsKey("output"))
    {
        logger.LogError(
            "Insufficient arguments. Usage: core:<coreSchemaPath> ext:<extensionSchemaPath> output:<outputPath>"
        );
        return 1;
    }

    string coreSchemaPath = argsDict["core"];
    string extensionSchemaPath = argsDict["ext"];
    string outputPath = argsDict["output"];

    // Validate file paths
    if (!File.Exists(coreSchemaPath))
    {
        logger.LogError("Core schema file not found: {CoreSchemaPath}", coreSchemaPath);
        return 1;
    }

    if (!File.Exists(extensionSchemaPath))
    {
        logger.LogError("Extension schema file not found: {ExtensionSchemaPath}", extensionSchemaPath);
        return 1;
    }

    generator.Generate(coreSchemaPath, extensionSchemaPath, outputPath);
    logger.LogInformation("OpenAPI spec successfully generated at: {OutputPath}", outputPath);
    return 0;
}
catch (Exception ex)
{
    logger.LogCritical(ex, "An error occurred while generating the OpenAPI spec.");
    return 1;
}

void ConfigureServices(IServiceCollection services)
{
    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Debug()
        .WriteTo.Console()
        .WriteTo.File("logs/OpenApiGenerator.log", rollingInterval: RollingInterval.Day)
        .CreateLogger();

    services.AddLogging(config =>
    {
        config.ClearProviders();
        config.AddSerilog();
    });

    services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

    services.AddSingleton<OpenApiGenerator>();
}
