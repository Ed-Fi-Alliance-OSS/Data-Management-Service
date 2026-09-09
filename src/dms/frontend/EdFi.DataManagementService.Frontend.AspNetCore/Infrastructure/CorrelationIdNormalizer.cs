// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Utilities;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// The single normalization applied to a correlation ID, client-supplied or server-generated,
/// before it is used in a log event or in an error response body. Every place a
/// <see cref="Core.External.Model.TraceId"/> is constructed from request data calls this, so
/// the value a client reads from a failed request is always the value searchable in the logs.
/// </summary>
public static class CorrelationIdNormalizer
{
    /// <summary>
    /// Truncates to <paramref name="maxLength"/> and then removes every control character,
    /// plus LINE SEPARATOR (U+2028) and PARAGRAPH SEPARATOR (U+2029), which are not control
    /// characters but break a line-oriented log consumer the same way. Every other character
    /// is preserved. If truncation would split a surrogate pair, the orphaned high half is
    /// dropped as well, so the result is always well-formed UTF-16.
    /// </summary>
    /// <remarks>
    /// The order is deliberate and must not be swapped: truncating first means a long hostile
    /// value retains less trailing content than filtering first would, and it matches the order
    /// the request-logging middleware has always used. A consequence is that a value which is
    /// both over-length and contains control characters yields a result <em>shorter</em> than
    /// <paramref name="maxLength"/>; that is intended, not a defect to compensate for. The
    /// surrogate-pair guard has the same effect for a value with no disallowed characters at
    /// all: an over-length value cut between the halves of a pair yields
    /// <paramref name="maxLength"/> - 1 characters.
    ///
    /// A value that violates the allowlist or the length cap is adjusted, never rejected, so a
    /// request still succeeds or fails on its own merits rather than on the shape of an
    /// operational identifier. Normalization is idempotent: the result contains no control
    /// characters and is no longer than <paramref name="maxLength"/>, so re-applying it is a
    /// no-op.
    /// </remarks>
    /// <param name="value">The raw correlation ID. Null or empty yields an empty string.</param>
    /// <param name="maxLength">
    /// The configured maximum length. A non-positive value falls back to
    /// <see cref="AppSettings.DefaultCorrelationIdMaxLength"/> rather than throwing, so a
    /// misconfigured host still gets a bounded identifier.
    /// </param>
    /// <returns>
    /// The normalized correlation ID: no control characters, no U+2028 or U+2029, well-formed
    /// UTF-16, no longer than the effective maximum length, and never null. The result is an
    /// empty string when the input is null, empty, or made up entirely of removed characters -
    /// and also when the input's retained prefix is empty, as for a single astral character
    /// truncated to a maximum length of 1.
    /// </returns>
    public static string Normalize(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        int effectiveMaxLength = maxLength > 0 ? maxLength : AppSettings.DefaultCorrelationIdMaxLength;

        string truncated = value;
        if (value.Length > effectiveMaxLength)
        {
            int retained = effectiveMaxLength;

            // The cut is on a UTF-16 code unit, so it can land between the halves of a
            // surrogate pair. char.IsControl is false for surrogates, so the allowlist below
            // would keep the orphaned high half; System.Text.Json then writes U+FFFD into the
            // response body while a log sink receives the raw unpaired unit, and the two
            // values are no longer byte-identical - the one thing FR-LOG-6 guarantees. Drop
            // the orphan instead.
            if (char.IsHighSurrogate(value[retained - 1]))
            {
                retained--;
            }

            truncated = value[..retained];
        }

        return LoggingSanitizer.SanitizeCorrelationIdForLogging(truncated);
    }
}
