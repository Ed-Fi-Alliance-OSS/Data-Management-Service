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
                    LoggingSanitizer.SanitizeInternalValueForLogging(failure.FilePath)
                );
                Console.Error.WriteLine(
                    $"Error: File not found: {LoggingSanitizer.SanitizeForConsole(failure.FilePath)}"
                );
                return 1;

            case ApiSchemaFileLoadResult.FileReadErrorResult failure:
                logger.LogError(
                    "Failed to read file {FilePath}: {Error}",
                    LoggingSanitizer.SanitizeInternalValueForLogging(failure.FilePath),
                    LoggingSanitizer.SanitizeInternalValueForLogging(failure.ErrorMessage)
                );
                Console.Error.WriteLine(
                    $"Error: Failed to read file {LoggingSanitizer.SanitizeForConsole(failure.FilePath)}: {LoggingSanitizer.SanitizeForConsole(failure.ErrorMessage)}"
                );
                return 1;

            case ApiSchemaFileLoadResult.InvalidJsonResult failure:
                logger.LogError(
                    "Invalid JSON in file {FilePath}: {Error}",
                    LoggingSanitizer.SanitizeInternalValueForLogging(failure.FilePath),
                    LoggingSanitizer.SanitizeInternalValueForLogging(failure.ErrorMessage)
                );
                Console.Error.WriteLine(
                    $"Error: Invalid JSON in file {LoggingSanitizer.SanitizeForConsole(failure.FilePath)}: {LoggingSanitizer.SanitizeForConsole(failure.ErrorMessage)}"
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
                    LoggingSanitizer.SanitizeInternalValueForLogging(message)
                );
                Console.Error.WriteLine($"Error: {LoggingSanitizer.SanitizeForConsole(message)}");
                return 1;

            default:
                logger.LogError("Unknown result type: {ResultType}", result.GetType().Name);
                Console.Error.WriteLine($"Error: Unknown result type: {result.GetType().Name}");
                return 1;
        }
    }

    /// <summary>
    /// Writes only typed input failure categories and sanitized paths for commands that
    /// suppress shared loader logging. Raw loader messages and schema values stay private.
    /// </summary>
    public static int HandleRedacted(ApiSchemaFileLoadResult result, TextWriter error)
    {
        static string PathForConsole(string path) =>
            LoggingSanitizer.SanitizeForConsole(path).Replace("\r", string.Empty).Replace("\n", string.Empty);

        var message = result switch
        {
            ApiSchemaFileLoadResult.FileNotFoundResult failure =>
                $"Schema file not found: {PathForConsole(failure.FilePath)}",
            ApiSchemaFileLoadResult.FileReadErrorResult failure =>
                $"Schema file could not be read: {PathForConsole(failure.FilePath)}. Check access and file permissions.",
            ApiSchemaFileLoadResult.InvalidJsonResult failure =>
                $"Invalid JSON in schema file: {PathForConsole(failure.FilePath)}.",
            ApiSchemaFileLoadResult.NormalizationFailureResult failure => failure.FailureResult switch
            {
                ApiSchemaNormalizationResult.MissingOrMalformedProjectSchemaResult =>
                    "Schema project metadata is missing or malformed.",
                ApiSchemaNormalizationResult.ApiSchemaVersionMismatchResult =>
                    "Schema input format versions do not match.",
                ApiSchemaNormalizationResult.ProjectEndpointNameCollisionResult =>
                    "Schema project endpoint names collide.",
                _ => "Schema input normalization failed.",
            },
            _ => "Schema inputs could not be loaded.",
        };
        error.WriteLine(message);
        return 1;
    }
}
