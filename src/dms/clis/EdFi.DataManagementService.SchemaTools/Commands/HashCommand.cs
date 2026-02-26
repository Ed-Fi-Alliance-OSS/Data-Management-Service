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

        if (result is not ApiSchemaFileLoadResult.SuccessResult success)
        {
            return LoadResultErrorHandler.Handle(logger, result);
        }

        try
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
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Schema processing failed during hash computation");
            Console.Error.WriteLine(
                $"Error: Schema processing failed: {LoggingSanitizer.SanitizeForConsole(ex.Message)}"
            );
            return 1;
        }
        catch (ArgumentException ex)
        {
            logger.LogError(ex, "Invalid argument during hash computation");
            Console.Error.WriteLine(
                $"Error: Invalid argument: {LoggingSanitizer.SanitizeForConsole(ex.Message)}"
            );
            return 1;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "An unexpected error occurred during hash computation");
            Console.Error.WriteLine(
                $"Error: An unexpected error occurred: {LoggingSanitizer.SanitizeForConsole(ex.Message)}"
            );
            return 1;
        }
    }
}
