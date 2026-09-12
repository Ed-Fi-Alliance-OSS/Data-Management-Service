// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Sanitizes strings for safe logging in backend components. Uses an allowlist approach
/// to prevent log injection and log forging attacks.
/// </summary>
public static class LogSanitizer
{
    /// <summary>
    /// Sanitizes a string for safe logging with a strict allowlist of safe characters.
    /// Allows: letters, digits, spaces, and safe punctuation (_-.:/\)
    /// </summary>
    public static string SanitizeForLog(string? input) => Sanitize(input, IsAllowedChar);

    /// <summary>
    /// Sanitizes a correlation ID for safe logging and for inclusion in an error response body.
    /// This is the canonical description of the correlation-ID allowlist; every other comment
    /// about it in this repository points here rather than restating it.
    /// </summary>
    /// <remarks>
    /// The allowlist is deliberately broader than <see cref="SanitizeForLog"/>'s and is defined
    /// as a single negative test, <c>!char.IsControl(c)</c>, with no positive character
    /// enumeration: a client-supplied correlation ID normally originates in an upstream system's
    /// own identifier scheme, so narrowing it to alphanumerics would defeat the purpose of
    /// accepting a client-supplied value. Removing line-breaking characters is what prevents log
    /// forging and corruption of structured log output; the length cap, applied by the caller, is
    /// what bounds the value's size.
    ///
    /// The effective removed set is every character in Unicode category Cc - U+0000-U+001F
    /// (including \r, \n, \t and \0) and U+007F-U+009F - plus LINE SEPARATOR (U+2028) and
    /// PARAGRAPH SEPARATOR (U+2029), which <c>char.IsControl</c> does not report (they are
    /// categories Zl and Zp) and which the <c>ReplaceLineEndings</c> call in
    /// <see cref="Sanitize"/> removes instead, because they break a line-oriented log consumer
    /// the same way a newline does.
    ///
    /// Everything else is preserved, and that deliberately includes the Unicode format
    /// characters (category Cf): U+00AD SOFT HYPHEN, U+200B-U+200D, the bidirectional
    /// overrides and embeddings U+202A-U+202E, the isolates U+2066-U+2069, and U+FEFF.
    /// <c>char.IsControl</c> is true only for category Cc, so those survive normalization. They
    /// are invisible or direction-changing in a terminal but cannot forge a log line, and the
    /// sinks that receive this value - structured-log parameters and
    /// <c>JsonSerializer</c>/<c>JsonObject</c> - escape their own output.
    /// </remarks>
    public static string SanitizeCorrelationId(string? input) =>
        Sanitize(input, static c => !char.IsControl(c));

    /// <summary>
    /// The shared two-pass filter behind both public entry points.
    /// </summary>
    /// <remarks>
    /// <paramref name="isAllowedChar"/> is invoked twice per character - once to count safe
    /// characters for the exact-size allocation, once to select them into the buffer. It must
    /// be a pure function of only its input character: deterministic, with no side effects and
    /// no dependency on call count or ordering. A predicate that is not pure will silently
    /// produce a buffer that is the wrong size or filled incorrectly.
    /// </remarks>
    private static string Sanitize(string? input, Func<char, bool> isAllowedChar)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        // Required for the correlation-ID path, whose broader allowlist is only
        // !char.IsControl(c) and would otherwise preserve Unicode line/paragraph
        // separators that ReplaceLineEndings removes. Behaviorally redundant for the strict
        // SanitizeForLog allowlist, which independently rejects both - neither U+2028 nor
        // U+2029 is a letter or a digit. Also kept because static log-injection analysis
        // (CodeQL) models ReplaceLineEndings as a sanitizer but not the custom allowlist loop.
        input = input.ReplaceLineEndings(string.Empty);

        // First pass: check if sanitization is needed and count safe characters
        int safeCount = 0;
        bool needsSanitization = false;

        foreach (char c in input)
        {
            if (isAllowedChar(c))
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

        // Second pass: build the sanitized string with exact allocation. The predicate travels
        // with the source in a value tuple so the lambda stays static - no closure allocation,
        // and no boxing of the state.
#pragma warning disable S3267 // Loop intentionally avoids LINQ for performance - no intermediate allocations
        return string.Create(
            safeCount,
            (Input: input, IsAllowedChar: isAllowedChar),
            static (span, state) =>
            {
                int index = 0;
                foreach (char c in state.Input)
                {
                    if (state.IsAllowedChar(c))
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
