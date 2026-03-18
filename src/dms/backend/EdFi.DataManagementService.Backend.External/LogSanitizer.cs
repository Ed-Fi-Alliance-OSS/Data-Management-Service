// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Sanitizes strings for safe logging in backend components. Uses a whitelist approach
/// to prevent log injection and log forging attacks.
/// </summary>
public static class LogSanitizer
{
    /// <summary>
    /// Sanitizes a string for safe logging by allowing only safe characters.
    /// Allows: letters, digits, spaces, and safe punctuation (_-.:/\)
    /// </summary>
    public static string SanitizeForLog(string? input)
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
            return input;
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

    // Explicitly reject control characters for defense in depth
    // Includes backslash for Windows file paths
    private static bool IsAllowedChar(char c) =>
        !char.IsControl(c)
        && (char.IsLetterOrDigit(c) || c is ' ' or '_' or '-' or '.' or ':' or '/' or '\\');
}
