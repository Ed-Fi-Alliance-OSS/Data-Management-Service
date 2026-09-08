// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.SchemaTools.Commands;

/// <summary>
/// Shared error handler that wraps command execution with a consistent try-catch pattern.
/// Handles <see cref="InvalidOperationException"/>, <see cref="ArgumentException"/>,
/// and general exceptions with appropriate logging and user-facing error messages.
/// </summary>
public static class CommandErrorHandler
{
    /// <summary>
    /// Executes the given command body within a standard error-handling wrapper.
    /// <paramref name="operationName"/> is a code literal describing the operation
    /// (e.g. "hash computation", "DDL emission") used in log messages.
    /// </summary>
    public static int Execute(ILogger logger, string operationName, Func<int> commandBody)
    {
        try
        {
            return commandBody();
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "{Operation} failed", operationName);
            Console.Error.WriteLine(
                $"Error: {operationName} failed: {LoggingSanitizer.SanitizeForConsole(ex.Message)}"
            );
            return 1;
        }
        catch (ArgumentException ex)
        {
            logger.LogError(ex, "Invalid argument during {Operation}", operationName);
            Console.Error.WriteLine(
                $"Error: Invalid argument: {LoggingSanitizer.SanitizeForConsole(ex.Message)}"
            );
            return 1;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "An unexpected error occurred during {Operation}", operationName);
            Console.Error.WriteLine(
                $"Error: An unexpected error occurred: {LoggingSanitizer.SanitizeForConsole(ex.Message)}"
            );
            return 1;
        }
    }
}
