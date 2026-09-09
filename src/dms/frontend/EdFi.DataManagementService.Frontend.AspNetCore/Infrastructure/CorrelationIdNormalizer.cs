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
    /// Truncates to <paramref name="maxLength"/> and then removes every control character.
    /// </summary>
    /// <remarks>
    /// The order is deliberate and must not be swapped: truncating first means a long hostile
    /// value retains less trailing content than filtering first would, and it matches the order
    /// the request-logging middleware has always used. A consequence is that a value which is
    /// both over-length and contains control characters yields a result <em>shorter</em> than
    /// <paramref name="maxLength"/>; that is intended, not a defect to compensate for.
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
    public static string Normalize(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        int effectiveMaxLength = maxLength > 0 ? maxLength : AppSettings.DefaultCorrelationIdMaxLength;

        string truncated = value.Length > effectiveMaxLength ? value[..effectiveMaxLength] : value;

        return LoggingSanitizer.SanitizeCorrelationIdForLogging(truncated);
    }
}
