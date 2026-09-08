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
    if (args.Length < 1)
    {
        logger.LogError("Usage: <coreSchemaPath> [extensionSchemaPath]");
        return 1;
    }

    string coreSchemaPath = args[0];
    string? extensionSchemaPath = args.Length > 1 ? args[1] : null;

    // Validate file paths
    if (!File.Exists(coreSchemaPath))
    {
        logger.LogError("Core schema file not found: {CoreSchemaPath}", coreSchemaPath);
        return 1;
    }

    if (extensionSchemaPath != null && !File.Exists(extensionSchemaPath))
    {
        logger.LogError("Extension schema file not found: {ExtensionSchemaPath}", extensionSchemaPath);
        return 1;
    }

    string combinedSchema = generator.Generate(coreSchemaPath, extensionSchemaPath);

    if (Console.IsOutputRedirected)
    {
        Console.WriteLine(combinedSchema);
        logger.LogInformation("OpenAPI spec successfully generated and written to redirected output.");
    }
    else
    {
        Console.WriteLine(combinedSchema);
        logger.LogInformation("OpenAPI spec successfully generated and written to standard output.");
    }

    return 0;
}
catch (Exception ex)
{
    logger.LogCritical(ex, "An error occurred while generating the OpenAPI spec.");
    return 1;
}

void ConfigureServices(IServiceCollection services)
{
    var logConfiguration = new LoggerConfiguration().MinimumLevel.Debug();

    if (Console.IsOutputRedirected)
    {
        logConfiguration.WriteTo.File("logs/OpenApiGenerator.log", rollingInterval: RollingInterval.Day);
    }
    else
    {
        logConfiguration.WriteTo.Console();
        logConfiguration.WriteTo.File("logs/OpenApiGenerator.log", rollingInterval: RollingInterval.Day);
    }

    Log.Logger = logConfiguration.CreateLogger();

    services.AddLogging(config =>
    {
        config.ClearProviders();
        config.AddSerilog();
    });

    services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

    services.AddSingleton<OpenApiGenerator>();
}
