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
    /// half is dropped as well, so truncation never introduces a lone surrogate; and the
    /// allowlist removes any unpaired surrogate the value already carried, so the result never
    /// contains one however it got there.
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
    /// truncated to a maximum length of 1.
    ///
    /// <b>The result never contains an unpaired surrogate, whatever
    /// <paramref name="value"/> contained.</b> The guarantee is unconditional and holds for both
    /// ways one can arise: truncation cutting a well-formed pair in half, which the guard below
    /// prevents, and a half already present in the input, which
    /// <see cref="LoggingSanitizer.SanitizeCorrelationId"/> removes. A well-formed pair - a
    /// genuine astral character - is not touched by either and survives intact.
    ///
    /// The guarantee is unconditional because FR-LOG-6 is. An unpaired surrogate reaches the
    /// response body as U+FFFD, written by <c>System.Text.Json</c>, and reaches a log sink as
    /// the raw code unit, so the client and the operator hold two different strings for the same
    /// request - the one failure the parity guarantee exists to exclude. A previous version of
    /// this contract instead promised to preserve a pre-existing lone surrogate, on the argument
    /// that an HTTP header value cannot carry one. That argument was correct in fact but rested
    /// on a Kestrel default rather than on anything this method does: a
    /// <c>RequestHeaderEncodingSelector</c> configured with a non-replacement fallback reopens
    /// it, as does any caller that is not an HTTP header at all. A guarantee conditional on host
    /// configuration is not one a caller can rely on, so it was made absolute instead.
    /// </returns>
    public static string Normalize(string? value, int maxLength) =>
        NormalizeWithDetail(value, maxLength).Value;

    /// <summary>
    /// <see cref="Normalize"/>, additionally reporting which of the two adjustments were applied.
    /// The result's <see cref="NormalizationDetail.Value"/> is byte-for-byte what
    /// <see cref="Normalize"/> returns for the same arguments - the latter is a thin wrapper over
    /// this method, so the two cannot drift.
    /// </summary>
    /// <remarks>
    /// This exists so the request-logging middleware can report <em>that</em> a client-supplied
    /// correlation ID was adjusted without reflecting the original value into a log sink. The
    /// flags are derived facts about the transformation, not content: nothing here carries any
    /// part of <paramref name="value"/> other than its normalized form, which is already the
    /// value every log event and error response body carries.
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
            // surrogate pair - turning a well-formed astral character into an orphaned high
            // half that was never in the input. Back the cut off by one when that is what is
            // about to happen.
            //
            // This guard is deliberately kept even though the allowlist below now removes every
            // unpaired surrogate and would therefore delete such an orphan anyway. The two are
            // different events and only one of them is this method's doing:
            //
            //   - Truncation manufacturing an orphan is an artifact of where the cut landed.
            //     Not creating it in the first place keeps the truncation step independently
            //     well-formed, so the guarantee does not depend on the allowlist running
            //     afterwards. The ADR fixes the order as truncate-then-filter; a guard that is
            //     only correct in that order would be a silent trap for anyone revisiting it.
            //   - An orphan the client actually sent is a character the allowlist removes, like
            //     any other. Letting the allowlist do that - rather than widening this guard to
            //     cover it - is what keeps `CharactersRemoved` below honest: it reports that the
            //     allowlist removed something from the value, not that truncation broke a
            //     character in half. The two show up differently on the CorrelationIdModified
            //     event, and they are genuinely different facts about the request.
            //
            // Hence both halves of the test. `IsHighSurrogate(value[retained - 1])` alone would
            // also back off from a lone high surrogate sitting at the cut, spending a code unit
            // of the budget to remove something the allowlist is about to remove for free and
            // reporting it as a truncation effect rather than as a removal.
            //
            // Both bounds are stated here, at the hazard, rather than inferred from conditions
            // elsewhere in the method:
            //
            //   - `retained > 0` - an effective maximum length of zero would otherwise index
            //     value[-1]. The fallback above makes zero unreachable today; the bound does not
            //     rely on that, because the fallback is documented as guarding a
            //     misconfiguration rather than an index and could be simplified away.
            //   - `retained < value.Length` - value[retained] is the code unit the cut discards.
            //     It exists because this block only runs when value.Length > effectiveMaxLength,
            //     but that test is twenty lines up and reads as a question about truncation, not
            //     about indexing.
            //
            // The analyzer is right that both conditions are true today for exactly those
            // distant reasons - which is precisely why they are written out. The suppression is
            // cheaper than turning an unreachable branch into an IndexOutOfRangeException on a
            // production request.
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
