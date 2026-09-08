// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Core.Utilities;

/// <summary>
/// Utility class for sanitizing strings in logging to prevent log injection attacks.
/// Structured-log sanitization delegates to <see cref="LogSanitizer"/> (the canonical
/// implementation in Backend.External) so there is a single whitelist definition.
/// </summary>
public static class LoggingSanitizer
{
    /// <summary>
    /// Sanitizes input strings to prevent log injection attacks using a whitelist approach.
    /// Only allows alphanumeric characters, spaces, and safe punctuation (_-.:/).
    /// Explicitly excludes all control characters (ASCII &lt; 32, including \r, \n, \t, etc.)
    /// This prevents log forging, template injection, and other log-based attacks.
    /// </summary>
    /// <param name="input">The input string to sanitize</param>
    /// <returns>A sanitized string safe for logging</returns>
    public static string SanitizeForLogging(string? input) => LogSanitizer.SanitizeForLog(input);

    /// <summary>
    /// Sanitizes input for console/stderr output by stripping control characters,
    /// except newline (\n) and carriage return (\r) which are preserved for
    /// multi-line output readability (e.g., diff reports from SeedValidator).
    /// Unlike <see cref="SanitizeForLogging"/> which uses a strict whitelist to prevent
    /// structured-log template injection, this method preserves all printable characters
    /// (quotes, parentheses, brackets, etc.) so that file paths and exception messages
    /// remain readable in user-facing CLI output.
    /// </summary>
    public static string SanitizeForConsole(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        // Fast path: check if any control characters are present
        bool hasControl = false;
        int printableCount = 0;

        foreach (char c in input)
        {
            if (char.IsControl(c) && c != '\n' && c != '\r')
            {
                hasControl = true;
            }
            else
            {
                printableCount++;
            }
        }

        if (!hasControl)
        {
            return input;
        }

        if (printableCount == 0)
        {
            return string.Empty;
        }

#pragma warning disable S3267 // Loop intentionally avoids LINQ for performance - no intermediate allocations
        return string.Create(
            printableCount,
            input,
            static (span, source) =>
            {
                int index = 0;
                foreach (char c in source)
                {
                    if (!char.IsControl(c) || c == '\n' || c == '\r')
                    {
                        span[index++] = c;
                    }
                }
            }
        );
#pragma warning restore S3267
    }
}
