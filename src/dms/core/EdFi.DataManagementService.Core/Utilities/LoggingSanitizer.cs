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
    /// Core-side facade over <see cref="LogSanitizer.SanitizeInternalValueForLog"/>: the strict
    /// allowlist for an <b>internally-controlled</b> value - a request method or path, a
    /// resource, tenant or instance name, a schema hash.
    /// </summary>
    /// <remarks>
    /// <b>Never for a correlation ID or a <see cref="Core.External.Model.TraceId"/></b>; use
    /// <see cref="SanitizeCorrelationId"/>. This allowlist strips punctuation an upstream
    /// identifier scheme legitimately uses, so the logged value would stop matching the one
    /// echoed to the client for the same request.
    /// </remarks>
    /// <param name="input">The input string to sanitize</param>
    /// <returns>A sanitized string safe for logging</returns>
    public static string SanitizeInternalValueForLogging(string? input) =>
        LogSanitizer.SanitizeInternalValueForLog(input);

    /// <summary>
    /// Core-side facade over <see cref="LogSanitizer.SanitizeCorrelationId"/>, which defines the
    /// allowlist. Use this for a correlation ID or a
    /// <see cref="Core.External.Model.TraceId"/>.
    /// </summary>
    /// <remarks>
    /// A correlation ID is already normalized once, at the frontend ingestion boundary
    /// (<c>AspNetCoreFrontend.ExtractTraceIdFrom</c>), so passing <c>traceId.Value</c> to a log
    /// event raw is correct and is what most sites in this repository do. Wrapping it here is
    /// equally correct but behaviorally redundant, because normalization is idempotent; the
    /// wrapper survives on sites that already had one so that static log-injection analysis
    /// (CodeQL) keeps seeing a sanitizer marker there. Do not churn existing sites in either
    /// direction.
    ///
    /// The one spelling that is a defect is
    /// <see cref="SanitizeInternalValueForLogging"/>, whose stricter allowlist breaks the
    /// FR-LOG-6 parity between the logged value and the response body.
    /// </remarks>
    /// <param name="input">The correlation ID to sanitize</param>
    /// <returns>A sanitized correlation ID safe for logging and for an error response body</returns>
    public static string SanitizeCorrelationId(string? input) => LogSanitizer.SanitizeCorrelationId(input);

    /// <summary>
    /// Core-side facade over <see cref="LogSanitizer.SanitizeFreeTextForLog"/>: the method to
    /// reach for when logging free-form text derived from a client or an upstream service - an
    /// error payload, an exception message, any value that is not an identifier.
    /// </summary>
    /// <remarks>
    /// The other three are all wrong for that job.
    /// <see cref="SanitizeInternalValueForLogging"/> strips the punctuation a JSON payload is
    /// made of; <see cref="SanitizeCorrelationId"/> is for one specific value that is also echoed
    /// to the client, not a general-purpose text sanitizer; and <see cref="SanitizeForConsole"/>
    /// deliberately <b>preserves</b> newline and carriage return for multi-line CLI output, which
    /// is exactly what must not survive into a log line.
    ///
    /// Callers that log an attacker-influenceable payload must also bound its length; this method
    /// does not, because no single cap suits every call site.
    /// </remarks>
    /// <param name="input">The free-form text to sanitize</param>
    /// <returns>A sanitized string safe for a structured log event</returns>
    public static string SanitizeFreeTextForLogging(string? input) =>
        LogSanitizer.SanitizeFreeTextForLog(input);

    /// <summary>
    /// Sanitizes input for console/stderr output by stripping control characters,
    /// except newline (\n) and carriage return (\r) which are preserved for
    /// multi-line output readability (e.g., diff reports from SeedValidator).
    /// Unlike <see cref="SanitizeInternalValueForLogging"/> which uses a strict allowlist to prevent
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
