// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

var serviceCollection = new ServiceCollection();
ConfigureServices(serviceCollection);
var serviceProvider = serviceCollection.BuildServiceProvider();

var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
var fileLoader = serviceProvider.GetRequiredService<IApiSchemaFileLoader>();

try
{
    if (args.Length < 1)
    {
        PrintUsage();
        return 1;
    }

    string coreSchemaPath = args[0];
    var extensionSchemaPaths = args.Skip(1).ToList();

    logger.LogInformation(
        "Loading schemas: core={CorePath}, extensions={ExtensionCount}",
        LoggingSanitizer.SanitizeForLogging(coreSchemaPath),
        extensionSchemaPaths.Count
    );

    var result = fileLoader.Load(coreSchemaPath, extensionSchemaPaths);

    return result switch
    {
        ApiSchemaFileLoadResult.SuccessResult success => HandleSuccess(success),
        ApiSchemaFileLoadResult.FileNotFoundResult failure => HandleFileNotFound(failure),
        ApiSchemaFileLoadResult.FileReadErrorResult failure => HandleFileReadError(failure),
        ApiSchemaFileLoadResult.InvalidJsonResult failure => HandleInvalidJson(failure),
        ApiSchemaFileLoadResult.NormalizationFailureResult failure => HandleNormalizationFailure(failure),
        _ => HandleUnknownResult(result),
    };
}
catch (Exception ex)
{
    logger.LogCritical(ex, "An unexpected error occurred");
    return 1;
}

int HandleSuccess(ApiSchemaFileLoadResult.SuccessResult success)
{
    var nodes = success.NormalizedNodes;
    var coreEndpoint = nodes
        .CoreApiSchemaRootNode["projectSchema"]
        ?["projectEndpointName"]?.GetValue<string>();
    var extensionCount = nodes.ExtensionApiSchemaRootNodes.Length;

    logger.LogInformation(
        "Schema loaded and normalized successfully. Core: {CoreEndpoint}, Extensions: {ExtensionCount}",
        LoggingSanitizer.SanitizeForLogging(coreEndpoint),
        extensionCount
    );

    if (extensionCount > 0)
    {
        var extensionEndpoints = nodes
            .ExtensionApiSchemaRootNodes.Select(n =>
                n["projectSchema"]?["projectEndpointName"]?.GetValue<string>()
            )
            .Where(n => n != null);

        logger.LogInformation(
            "Extension endpoints: {Extensions}",
            string.Join(", ", extensionEndpoints.Select(LoggingSanitizer.SanitizeForLogging))
        );
    }

    Console.WriteLine("Schema normalization successful.");
    return 0;
}

int HandleFileNotFound(ApiSchemaFileLoadResult.FileNotFoundResult failure)
{
    logger.LogError("File not found: {FilePath}", LoggingSanitizer.SanitizeForLogging(failure.FilePath));
    Console.Error.WriteLine(
        $"Error: File not found: {LoggingSanitizer.SanitizeForLogging(failure.FilePath)}"
    );
    return 1;
}

int HandleFileReadError(ApiSchemaFileLoadResult.FileReadErrorResult failure)
{
    logger.LogError(
        "Failed to read file {FilePath}: {Error}",
        LoggingSanitizer.SanitizeForLogging(failure.FilePath),
        LoggingSanitizer.SanitizeForLogging(failure.ErrorMessage)
    );
    Console.Error.WriteLine(
        $"Error: Failed to read file {LoggingSanitizer.SanitizeForLogging(failure.FilePath)}: {LoggingSanitizer.SanitizeForLogging(failure.ErrorMessage)}"
    );
    return 1;
}

int HandleInvalidJson(ApiSchemaFileLoadResult.InvalidJsonResult failure)
{
    logger.LogError(
        "Invalid JSON in file {FilePath}: {Error}",
        LoggingSanitizer.SanitizeForLogging(failure.FilePath),
        LoggingSanitizer.SanitizeForLogging(failure.ErrorMessage)
    );
    Console.Error.WriteLine(
        $"Error: Invalid JSON in file {LoggingSanitizer.SanitizeForLogging(failure.FilePath)}: {LoggingSanitizer.SanitizeForLogging(failure.ErrorMessage)}"
    );
    return 1;
}

int HandleNormalizationFailure(ApiSchemaFileLoadResult.NormalizationFailureResult failure)
{
    var message = failure.FailureResult switch
    {
        ApiSchemaNormalizationResult.MissingOrMalformedProjectSchemaResult r =>
            $"Schema '{r.SchemaSource}' is malformed: {r.Details}",
        ApiSchemaNormalizationResult.ApiSchemaVersionMismatchResult r =>
            $"Version mismatch in '{r.SchemaSource}': expected {r.ExpectedVersion}, got {r.ActualVersion}",
        ApiSchemaNormalizationResult.ProjectEndpointNameCollisionResult r =>
            $"Endpoint name collision(s): {string.Join("; ", r.Collisions.Select(c => $"'{c.ProjectEndpointName}' in [{string.Join(", ", c.ConflictingSources)}]"))}",
        _ => "Unknown normalization failure",
    };

    logger.LogError("Schema normalization failed: {Message}", LoggingSanitizer.SanitizeForLogging(message));
    Console.Error.WriteLine($"Error: {LoggingSanitizer.SanitizeForLogging(message)}");
    return 1;
}

int HandleUnknownResult(ApiSchemaFileLoadResult result)
{
    logger.LogError("Unknown result type: {ResultType}", result.GetType().Name);
    Console.Error.WriteLine($"Error: Unknown result type: {result.GetType().Name}");
    return 1;
}

void PrintUsage()
{
    Console.WriteLine("Usage: dms-schema <coreSchemaPath> [extensionSchemaPath...]");
    Console.WriteLine();
    Console.WriteLine("Arguments:");
    Console.WriteLine("  coreSchemaPath       Path to the core ApiSchema.json file");
    Console.WriteLine("  extensionSchemaPath  Path(s) to extension ApiSchema.json file(s) (optional)");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  dms-schema core/ApiSchema.json");
    Console.WriteLine("  dms-schema core/ApiSchema.json extensions/tpdm/ApiSchema.json");
    Console.WriteLine(
        "  dms-schema core/ApiSchema.json extensions/tpdm/ApiSchema.json extensions/sample/ApiSchema.json"
    );
}

void ConfigureServices(IServiceCollection services)
{
    var logConfiguration = new LoggerConfiguration().MinimumLevel.Information();

    if (Console.IsOutputRedirected)
    {
        logConfiguration.WriteTo.File("logs/dms-schema.log", rollingInterval: RollingInterval.Day);
    }
    else
    {
        logConfiguration.WriteTo.Console();
        logConfiguration.WriteTo.File("logs/dms-schema.log", rollingInterval: RollingInterval.Day);
    }

    Log.Logger = logConfiguration.CreateLogger();

    services.AddLogging(config =>
    {
        config.ClearProviders();
        config.AddSerilog();
    });

    services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
    services.AddSingleton<IApiSchemaInputNormalizer, ApiSchemaInputNormalizer>();
    services.AddSingleton<IApiSchemaFileLoader, ApiSchemaFileLoader>();
}
