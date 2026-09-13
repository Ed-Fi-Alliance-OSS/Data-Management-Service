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
/// <remarks>
/// This is a frontend policy type, not a second sanitizer: the character filtering is delegated
/// in full to <see cref="LoggingSanitizer.SanitizeCorrelationId"/>, which owns the allowlist.
/// What lives here and cannot live there is the part that depends on frontend configuration and
/// on ordering: the truncate-then-surrogate-guard-then-filter sequence (whose order FR-LOG-6
/// depends on and which the sanitizer, being character-wise, cannot express), and the fallback
/// to <see cref="AppSettings.DefaultCorrelationIdMaxLength"/>. That constant is frontend
/// configuration; moving this logic into the <c>Backend.External</c> assembly that hosts
/// <c>LogSanitizer</c> would have to drag it across an assembly boundary that deliberately knows
/// nothing about ASP.NET Core host settings.
/// </remarks>
public static class CorrelationIdNormalizer
{
    /// <summary>
    /// Truncates to <paramref name="maxLength"/> and then applies
    /// <see cref="LoggingSanitizer.SanitizeCorrelationId"/>, which defines exactly which
    /// characters are removed. If truncation would split a surrogate pair, the orphaned high
    /// half is dropped as well, so truncation never introduces a lone surrogate.
    /// </summary>
    /// <remarks>
    /// The order is deliberate and must not be swapped: truncating first means a long hostile
    /// value retains less trailing content than filtering first would, and it matches the order
    /// the request-logging middleware has always used. A consequence is that a value which is
    /// both over-length and contains removed characters yields a result <em>shorter</em> than
    /// <paramref name="maxLength"/>; that is intended, not a defect to compensate for. The
    /// surrogate-pair guard has the same effect for a value with no disallowed characters at
    /// all: an over-length value cut between the halves of a pair yields
    /// <paramref name="maxLength"/> - 1 characters.
    ///
    /// A value that violates the allowlist or the length cap is adjusted, never rejected, so a
    /// request still succeeds or fails on its own merits rather than on the shape of an
    /// operational identifier. Normalization is idempotent: the result contains none of the
    /// characters <see cref="LoggingSanitizer.SanitizeCorrelationId"/> removes and is no longer
    /// than <paramref name="maxLength"/>, so re-applying it is a no-op.
    /// </remarks>
    /// <param name="value">The raw correlation ID. Null or empty yields an empty string.</param>
    /// <param name="maxLength">
    /// The configured maximum length. A non-positive value falls back to
    /// <see cref="AppSettings.DefaultCorrelationIdMaxLength"/> rather than throwing, so a
    /// misconfigured host still gets a bounded identifier.
    /// </param>
    /// <returns>
    /// The normalized correlation ID: no longer than the effective maximum length, never null,
    /// and holding only the characters
    /// <see cref="LoggingSanitizer.SanitizeCorrelationId"/> retains. The result is an empty
    /// string when the input is null, empty, or made up entirely of removed characters - and
    /// also when the input's retained prefix is empty, as for a single astral character
    /// truncated to a maximum length of 1. Surrogates are guaranteed only to the extent that
    /// truncation never splits a pair; a lone surrogate already present in
    /// <paramref name="value"/> is preserved rather than repaired, because a surrogate is
    /// Unicode category Cs and the allowlist removes only Cc, Cf, Zl and Zp. A caller whose
    /// correlation IDs can carry lone surrogates - which an HTTP header value cannot - must check for that
    /// itself.
    /// </returns>
    public static string Normalize(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // A non-positive cap falls back to the documented default rather than throwing, so a
        // misconfigured host still gets a bounded identifier. This is a policy choice, not the
        // guard that keeps the surrogate probe below in bounds - that guard is `retained > 0`,
        // stated at the hazard so removing this fallback cannot reintroduce an index-out-of-range.
        int effectiveMaxLength = maxLength > 0 ? maxLength : AppSettings.DefaultCorrelationIdMaxLength;

        string truncated = value;
        if (value.Length > effectiveMaxLength)
        {
            int retained = effectiveMaxLength;

            // The cut is on a UTF-16 code unit, so it can land between the halves of a
            // surrogate pair. A surrogate is Unicode category Cs, which the allowlist below
            // does not remove, so it would keep the orphaned high half; System.Text.Json then
            // writes U+FFFD into the response body while a log sink receives the raw unpaired
            // unit, and the two values are no longer byte-identical - the one thing FR-LOG-6
            // guarantees. Drop the orphan instead.
            //
            // `retained > 0` is load-bearing: an effective maximum length of zero would
            // otherwise index value[-1]. It is tested here, where the hazard is, rather than
            // relying on the fallback above to make zero unreachable.
            //
            // The analyzer is right that the fallback makes this condition true today - that is
            // precisely why it is written out. The bound and the fallback are two separate
            // concerns twenty lines apart, and a future simplification of the fallback (which its
            // own comment invites, since it is documented as guarding a misconfiguration rather
            // than an index) would turn an unreachable branch into an IndexOutOfRangeException on
            // a production request. The suppression is cheaper than that failure mode.
#pragma warning disable S2589 // Condition is redundant only because of a distant, separately-motivated clamp
            if (retained > 0 && char.IsHighSurrogate(value[retained - 1]))
#pragma warning restore S2589
            {
                retained--;
            }

            truncated = value[..retained];
        }

        return LoggingSanitizer.SanitizeCorrelationId(truncated);
    }
}
