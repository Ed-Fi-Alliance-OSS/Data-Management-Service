// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.SchemaTools.Commands;

/// <summary>
/// Shared error handler for <see cref="ApiSchemaFileLoadResult"/> failures.
/// Logs the error and writes a user-friendly message to stderr.
/// </summary>
public static class LoadResultErrorHandler
{
    /// <summary>
    /// Handles a non-success <see cref="ApiSchemaFileLoadResult"/> by logging the error
    /// and writing a message to stderr. Returns exit code 1.
    /// </summary>
    public static int Handle(ILogger logger, ApiSchemaFileLoadResult result)
    {
        switch (result)
        {
            case ApiSchemaFileLoadResult.FileNotFoundResult failure:
                logger.LogError(
                    "File not found: {FilePath}",
                    LoggingSanitizer.SanitizeForLogging(failure.FilePath)
                );
                Console.Error.WriteLine(
                    $"Error: File not found: {LoggingSanitizer.SanitizeForLogging(failure.FilePath)}"
                );
                return 1;

            case ApiSchemaFileLoadResult.FileReadErrorResult failure:
                logger.LogError(
                    "Failed to read file {FilePath}: {Error}",
                    LoggingSanitizer.SanitizeForLogging(failure.FilePath),
                    LoggingSanitizer.SanitizeForLogging(failure.ErrorMessage)
                );
                Console.Error.WriteLine(
                    $"Error: Failed to read file {LoggingSanitizer.SanitizeForLogging(failure.FilePath)}: {LoggingSanitizer.SanitizeForLogging(failure.ErrorMessage)}"
                );
                return 1;

            case ApiSchemaFileLoadResult.InvalidJsonResult failure:
                logger.LogError(
                    "Invalid JSON in file {FilePath}: {Error}",
                    LoggingSanitizer.SanitizeForLogging(failure.FilePath),
                    LoggingSanitizer.SanitizeForLogging(failure.ErrorMessage)
                );
                Console.Error.WriteLine(
                    $"Error: Invalid JSON in file {LoggingSanitizer.SanitizeForLogging(failure.FilePath)}: {LoggingSanitizer.SanitizeForLogging(failure.ErrorMessage)}"
                );
                return 1;

            case ApiSchemaFileLoadResult.NormalizationFailureResult failure:
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

            default:
                logger.LogError("Unknown result type: {ResultType}", result.GetType().Name);
                Console.Error.WriteLine($"Error: Unknown result type: {result.GetType().Name}");
                return 1;
        }
    }
}
