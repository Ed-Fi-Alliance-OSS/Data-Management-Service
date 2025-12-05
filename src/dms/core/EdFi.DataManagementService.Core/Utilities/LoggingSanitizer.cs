// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Utilities;

/// <summary>
/// Utility class for sanitizing strings in logging to prevent log injection attacks
/// </summary>
public static class LoggingSanitizer
{
    /// <summary>
    /// Sanitizes input strings to prevent log injection attacks using a whitelist approach.
    /// Only allows alphanumeric characters, spaces, and safe punctuation (_-.:/).
    /// This prevents log forging, template injection, and other log-based attacks.
    /// </summary>
    /// <param name="input">The input string to sanitize</param>
    /// <returns>A sanitized string safe for logging</returns>
    public static string SanitizeForLogging(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        // First pass: check if sanitization is needed and count safe characters
        int safeCount = 0;
        bool needsSanitization = false;

        foreach (char c in input)
        {
            if (IsAllowedChar(c))
            {
                safeCount++;
            }
            else
            {
                needsSanitization = true;
            }
        }

        if (!needsSanitization)
        {
            // All characters are safe - return a new string to avoid returning user input by reference
            return new string(input);
        }

        if (safeCount == 0)
        {
            return string.Empty;
        }

        // Second pass: build the sanitized string with exact allocation
#pragma warning disable S3267 // Loop intentionally avoids LINQ for performance - no intermediate allocations
        return string.Create(
            safeCount,
            input,
            static (span, source) =>
            {
                int index = 0;
                foreach (char c in source)
                {
                    if (IsAllowedChar(c))
                    {
                        span[index++] = c;
                    }
                }
            }
        );
#pragma warning restore S3267
    }

    private static bool IsAllowedChar(char c) =>
        char.IsLetterOrDigit(c) || c is ' ' or '_' or '-' or '.' or ':' or '/';
}
