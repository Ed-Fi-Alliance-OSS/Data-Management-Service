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

        // Behaviorally redundant with the whitelist below, which also rejects every
        // line-ending character. Kept because static log-injection analysis (CodeQL)
        // models ReplaceLineEndings as a sanitizer but not the custom whitelist loop.
        input = input.ReplaceLineEndings(string.Empty);

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

    /// <summary>
    /// Sanitizes a correlation ID for safe logging and for inclusion in an error response body.
    /// The allowlist is deliberately broader than <see cref="SanitizeForLog"/>: every printable
    /// character is preserved and only control characters are removed. A client-supplied
    /// correlation ID normally originates in an upstream system's own identifier scheme, so
    /// narrowing it to alphanumerics would defeat the purpose of accepting a client-supplied
    /// value. Removing control characters is what prevents log forging and corruption of
    /// structured log output.
    /// </summary>
    public static string SanitizeCorrelationIdForLog(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        // Behaviorally redundant with the allowlist below, which also rejects every
        // line-ending character. Kept because static log-injection analysis (CodeQL)
        // models ReplaceLineEndings as a sanitizer but not the custom allowlist loop.
        input = input.ReplaceLineEndings(string.Empty);

        // First pass: check if sanitization is needed and count safe characters
        int safeCount = 0;
        bool needsSanitization = false;

        foreach (char c in input)
        {
            if (IsAllowedCorrelationIdChar(c))
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
                    if (IsAllowedCorrelationIdChar(c))
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

    // The correlation ID allowlist is "all printable non-control characters" and is
    // deliberately a single negative test with no positive character enumeration.
    // char.IsControl is Unicode-aware, so the full printable Unicode range is preserved.
    private static bool IsAllowedCorrelationIdChar(char c) => !char.IsControl(c);
}
