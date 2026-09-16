// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Utilities;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// The single normalization applied to a correlation ID, client-supplied or server-generated,
/// before it is used in a log event or in an error response body.
/// </summary>
/// <remarks>
/// A frontend policy type, not a second sanitizer: character filtering belongs entirely to
/// <see cref="LoggingSanitizer.SanitizeCorrelationId"/>, which owns the allowlist. What lives
/// here is the part that depends on frontend configuration and on ordering - the truncate,
/// surrogate-guard, filter sequence, and the fallback to
/// <see cref="AppSettings.DefaultCorrelationIdMaxLength"/>, a constant the
/// <c>Backend.External</c> assembly hosting <c>LogSanitizer</c> deliberately knows nothing about.
/// The contract and its rationale: <c>reference/adr-correlation-id-normalization.md</c>.
/// </remarks>
public static class CorrelationIdNormalizer
{
    /// <summary>
    /// Truncates to <paramref name="maxLength"/>, backs the cut off a split surrogate pair, then
    /// applies <see cref="LoggingSanitizer.SanitizeCorrelationId"/>. Adjusts rather than rejects,
    /// so a request still succeeds or fails on its own merits, and is idempotent.
    /// </summary>
    /// <param name="value">The raw correlation ID. Null or empty yields an empty string.</param>
    /// <param name="maxLength">
    /// The configured maximum length. A non-positive value falls back to
    /// <see cref="AppSettings.DefaultCorrelationIdMaxLength"/> rather than throwing, so a
    /// misconfigured host still gets a bounded identifier.
    /// </param>
    /// <returns>
    /// The normalized correlation ID: never null, no longer than the effective maximum length,
    /// and holding only the characters <see cref="LoggingSanitizer.SanitizeCorrelationId"/>
    /// retains. It may be <em>shorter</em> than the cap - when characters were also removed, or
    /// when the cut landed inside a surrogate pair - and empty when nothing survives, as for a
    /// single astral character truncated to a maximum length of 1.
    ///
    /// <b>The result never contains an unpaired surrogate, whatever <paramref name="value"/>
    /// contained.</b> Why that guarantee is unconditional, and why the three steps run in this
    /// order: <c>reference/adr-correlation-id-normalization.md</c>.
    /// </returns>
    public static string Normalize(string? value, int maxLength) =>
        NormalizeWithDetail(value, maxLength).Value;

    /// <summary>
    /// <see cref="Normalize"/>, additionally reporting which of the two adjustments were applied.
    /// <see cref="Normalize"/> is a thin wrapper over this method, so the two cannot drift.
    /// </summary>
    /// <remarks>
    /// The flags let the request-logging middleware report <em>that</em> a client-supplied value
    /// was adjusted without reflecting the original into a log sink. They are derived facts about
    /// the transformation, carrying no part of <paramref name="value"/> but its normalized form.
    /// </remarks>
    public static NormalizationDetail NormalizeWithDetail(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return new NormalizationDetail(string.Empty, Truncated: false, CharactersRemoved: false);
        }

        // A non-positive cap falls back to the documented default rather than throwing, so a
        // misconfigured host still gets a bounded identifier. This is a policy choice, not the
        // guard that keeps the surrogate probe below in bounds - those guards are `retained > 0`
        // and `retained < value.Length`, both stated at the hazard so removing this fallback
        // cannot reintroduce an index-out-of-range.
        int effectiveMaxLength = maxLength > 0 ? maxLength : AppSettings.DefaultCorrelationIdMaxLength;

        string truncated = value;
        bool wasTruncated = value.Length > effectiveMaxLength;
        if (wasTruncated)
        {
            int retained = effectiveMaxLength;

            // The cut is on a UTF-16 code unit, so it can land between the halves of a
            // surrogate pair and manufacture an orphan that was never in the input. Back the cut
            // off by one when that is about to happen.
            //
            // Both halves of the test are required, and the guard is kept even though the
            // allowlist below would delete such an orphan anyway: truncation must not create one,
            // and an orphan the client actually sent has to be reported as a removal rather than
            // as a truncation effect. The ADR's normalization-order section has the reasoning.
            //
            // Both bounds are stated here, at the hazard, rather than inferred from distant
            // tests. `retained > 0` because a cap of zero would index value[-1] - the fallback
            // above makes that unreachable today, but it is documented as guarding a
            // misconfiguration, not an index, and could be simplified away. `retained <
            // value.Length` because value[retained] is the unit the cut discards, which holds
            // only because of a test twenty lines up that reads as a question about truncation.
            // The analyzer is right that both are currently redundant; that is why they are
            // written out rather than left to be re-derived.
#pragma warning disable S2589 // Conditions are redundant only because of distant, separately-motivated tests
            if (
                retained > 0
                && retained < value.Length
                && char.IsHighSurrogate(value[retained - 1])
                && char.IsLowSurrogate(value[retained])
            )
#pragma warning restore S2589
            {
                retained--;
            }

            truncated = value[..retained];
        }

        string filtered = LoggingSanitizer.SanitizeCorrelationId(truncated);

        // Length comparison rather than a string comparison: SanitizeCorrelationId only ever
        // removes characters, never substitutes them, so a shorter result is exactly "something
        // was removed" and an equal-length result is exactly "nothing was".
        return new NormalizationDetail(
            filtered,
            Truncated: wasTruncated,
            CharactersRemoved: filtered.Length != truncated.Length
        );
    }

    /// <summary>
    /// The normalized value together with the two derived facts about how it was reached.
    /// </summary>
    /// <param name="Value">The normalized correlation ID, as <see cref="Normalize"/> returns it.</param>
    /// <param name="Truncated">Whether the input was longer than the effective maximum length.</param>
    /// <param name="CharactersRemoved">
    /// Whether the allowlist removed at least one character from the (possibly truncated) input.
    /// </param>
    public readonly record struct NormalizationDetail(string Value, bool Truncated, bool CharactersRemoved);
}
