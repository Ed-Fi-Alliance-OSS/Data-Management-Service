// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Core.Utilities;

/// <summary>
/// Utility class for sanitizing strings in logging to prevent log injection attacks.
/// Structured-log sanitization delegates to <see cref="LogSanitizer"/> (the canonical
/// implementation in Backend.External) so there is a single allowlist definition.
/// </summary>
public static class LoggingSanitizer
{
    /// <summary>
    /// Sanitizes input strings to prevent log injection attacks using an allowlist approach.
    /// Only allows alphanumeric characters, spaces, and safe punctuation (_-.:/\).
    /// Explicitly excludes every character <c>char.IsControl</c> reports, which is the whole of
    /// Unicode category Cc — U+0000–U+001F (including \r, \n, \t) and U+007F–U+009F.
    /// This prevents log forging, template injection, and other log-based attacks.
    /// </summary>
    /// <remarks>
    /// Never use this for a correlation ID or a <see cref="Core.External.Model.TraceId"/> - see
    /// the rule on <see cref="SanitizeCorrelationId"/>, and use that instead.
    /// </remarks>
    /// <param name="input">The input string to sanitize</param>
    /// <returns>A sanitized string safe for logging</returns>
    public static string SanitizeForLogging(string? input) => LogSanitizer.SanitizeForLog(input);

    /// <summary>
    /// Sanitizes a correlation ID for logging and for inclusion in an error response body, using
    /// the correlation-ID allowlist. <see cref="LogSanitizer.SanitizeCorrelationId"/> carries the
    /// canonical description of that allowlist; this is only the Core-side facade over it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the one place the rule for logging a correlation ID or a
    /// <see cref="Core.External.Model.TraceId"/> is stated. Everywhere else points here.</b>
    /// </para>
    /// <para>
    /// <b>What is removed:</b> control characters (Unicode category Cc), format characters
    /// (category Cf - the bidirectional overrides and isolates, the zero-width characters, SOFT
    /// HYPHEN and the BYTE ORDER MARK), and the Unicode line/paragraph separators U+2028 and
    /// U+2029. Everything else survives, including printable punctuation, non-ASCII letters and
    /// internal whitespace. <see cref="LogSanitizer.SanitizeCorrelationId"/> states the rule and
    /// the rationale in full; do not restate it here or anywhere else.
    /// </para>
    /// <para>
    /// 1. <b>A correlation ID is already normalized before it is logged</b>, once, at the
    /// frontend ingestion boundary (<c>AspNetCoreFrontend.ExtractTraceIdFrom</c>). Passing
    /// <c>traceId.Value</c> to a log event raw is therefore correct and is what most sites in
    /// this repository do. Nothing further is required of a new log site.
    /// </para>
    /// <para>
    /// 2. <b>Wrapping a correlation ID in this method is equally correct but adds nothing
    /// behaviorally</b>, because normalization is idempotent. The wrapper survives on the sites
    /// that already had one so that static log-injection analysis (CodeQL) keeps seeing a
    /// sanitizer marker there; it was not added as a second line of defense, and the two
    /// spellings are not a disagreement about correctness. Do not churn existing sites in either
    /// direction.
    /// </para>
    /// <para>
    /// 3. <b>Never use <see cref="SanitizeForLogging"/> for a correlation ID.</b> That strict
    /// <c>Method</c>/<c>Path</c> allowlist strips punctuation an upstream identifier scheme
    /// legitimately uses, so the logged value would no longer match the <c>correlationId</c> the
    /// client read from the response body for the same request - which is the single guarantee
    /// FR-LOG-6 makes. This is the only one of the three spellings that is a defect.
    /// </para>
    /// </remarks>
    /// <param name="input">The correlation ID to sanitize</param>
    /// <returns>A sanitized correlation ID safe for logging and for an error response body</returns>
    public static string SanitizeCorrelationId(string? input) => LogSanitizer.SanitizeCorrelationId(input);

    /// <summary>
    /// Sanitizes input for console/stderr output by stripping control characters,
    /// except newline (\n) and carriage return (\r) which are preserved for
    /// multi-line output readability (e.g., diff reports from SeedValidator).
    /// Unlike <see cref="SanitizeForLogging"/> which uses a strict allowlist to prevent
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
