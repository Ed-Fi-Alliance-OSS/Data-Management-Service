// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.SchemaTools.Commands;

/// <summary>
/// Defines the <c>hash</c> subcommand: loads schemas, normalizes, and prints the effective schema hash.
/// </summary>
public static class HashCommand
{
    public static Command Create(
        ILogger logger,
        IApiSchemaFileLoader fileLoader,
        IEffectiveSchemaHashProvider hashProvider
    )
    {
        var coreSchemaArg = new Argument<string>("coreSchemaPath")
        {
            Description = "Path to the core ApiSchema.json file",
        };

        var extensionSchemaArg = new Argument<string[]>("extensionSchemaPath")
        {
            Description = "Path(s) to extension ApiSchema.json file(s)",
            Arity = ArgumentArity.ZeroOrMore,
        };

        var command = new Command("hash", "Load schemas, normalize, and print the effective schema hash");
        command.Arguments.Add(coreSchemaArg);
        command.Arguments.Add(extensionSchemaArg);

        command.SetAction(parseResult =>
        {
            var corePath = parseResult.GetValue(coreSchemaArg)!;
            var extensionPaths = parseResult.GetValue(extensionSchemaArg) ?? [];
            return Execute(logger, fileLoader, hashProvider, corePath, extensionPaths);
        });

        return command;
    }

    private static int Execute(
        ILogger logger,
        IApiSchemaFileLoader fileLoader,
        IEffectiveSchemaHashProvider hashProvider,
        string coreSchemaPath,
        string[] extensionSchemaPaths
    )
    {
        logger.LogInformation(
            "Loading schemas: core={CorePath}, extensions={ExtensionCount}",
            LoggingSanitizer.SanitizeForLogging(coreSchemaPath),
            extensionSchemaPaths.Length
        );

        var result = fileLoader.Load(coreSchemaPath, extensionSchemaPaths);

        return result switch
        {
            ApiSchemaFileLoadResult.SuccessResult success => HandleSuccess(logger, hashProvider, success),
            ApiSchemaFileLoadResult.FileNotFoundResult failure => HandleFileNotFound(logger, failure),
            ApiSchemaFileLoadResult.FileReadErrorResult failure => HandleFileReadError(logger, failure),
            ApiSchemaFileLoadResult.InvalidJsonResult failure => HandleInvalidJson(logger, failure),
            ApiSchemaFileLoadResult.NormalizationFailureResult failure => HandleNormalizationFailure(
                logger,
                failure
            ),
            _ => HandleUnknownResult(logger, result),
        };
    }

    private static int HandleSuccess(
        ILogger logger,
        IEffectiveSchemaHashProvider hashProvider,
        ApiSchemaFileLoadResult.SuccessResult success
    )
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

        var effectiveSchemaHash = hashProvider.ComputeHash(nodes);
        logger.LogInformation("Effective schema hash: {Hash}", effectiveSchemaHash);

        Console.WriteLine("Schema normalization successful.");
        Console.WriteLine($"Effective schema hash: {effectiveSchemaHash}");
        return 0;
    }

    private static int HandleFileNotFound(ILogger logger, ApiSchemaFileLoadResult.FileNotFoundResult failure)
    {
        logger.LogError("File not found: {FilePath}", LoggingSanitizer.SanitizeForLogging(failure.FilePath));
        Console.Error.WriteLine(
            $"Error: File not found: {LoggingSanitizer.SanitizeForLogging(failure.FilePath)}"
        );
        return 1;
    }

    private static int HandleFileReadError(
        ILogger logger,
        ApiSchemaFileLoadResult.FileReadErrorResult failure
    )
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

    private static int HandleInvalidJson(ILogger logger, ApiSchemaFileLoadResult.InvalidJsonResult failure)
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

    private static int HandleNormalizationFailure(
        ILogger logger,
        ApiSchemaFileLoadResult.NormalizationFailureResult failure
    )
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

        logger.LogError(
            "Schema normalization failed: {Message}",
            LoggingSanitizer.SanitizeForLogging(message)
        );
        Console.Error.WriteLine($"Error: {LoggingSanitizer.SanitizeForLogging(message)}");
        return 1;
    }

    private static int HandleUnknownResult(ILogger logger, ApiSchemaFileLoadResult result)
    {
        logger.LogError("Unknown result type: {ResultType}", result.GetType().Name);
        Console.Error.WriteLine($"Error: Unknown result type: {result.GetType().Name}");
        return 1;
    }
}
