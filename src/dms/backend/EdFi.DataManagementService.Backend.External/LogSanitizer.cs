// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers;
using System.Globalization;
using System.Text;

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Sanitizes strings for safe logging in backend components. Uses an allowlist approach
/// to prevent log injection and log forging attacks.
/// </summary>
public static class LogSanitizer
{
    /// <summary>
    /// Sanitizes an <b>internally-controlled</b> value - a request method or path, a resource,
    /// tenant or instance name, a schema hash - for safe logging, with a strict allowlist of
    /// letters, digits, spaces and the punctuation <c>_-.:/\</c>. <b>Never use this for a
    /// correlation ID or a <c>TraceId</c></b>; use <see cref="SanitizeCorrelationId"/> instead.
    /// </summary>
    /// <remarks>
    /// The prohibition is not stylistic. This allowlist strips punctuation an upstream identifier
    /// scheme legitimately uses, so a correlation ID sanitized here would stop matching the value
    /// echoed to the client for the same request, which is the parity guarantee FR-LOG-6 makes.
    /// <see cref="SanitizeCorrelationId"/> carries the canonical statement of that rule.
    /// </remarks>
    public static string SanitizeInternalValueForLog(string? input) => Sanitize(input, IsAllowedChar);

    /// <summary>
    /// Sanitizes a correlation ID for safe logging and for inclusion in an error response body.
    /// This is the canonical description of the correlation-ID allowlist; every other comment
    /// about it in this repository points here rather than restating it.
    /// </summary>
    /// <remarks>
    /// The allowlist is deliberately broader than <see cref="SanitizeInternalValueForLog"/>'s and is defined
    /// as a pair of negative tests, with no positive character enumeration: a client-supplied
    /// correlation ID normally originates in an upstream system's own identifier scheme, so
    /// narrowing it to alphanumerics would defeat the purpose of accepting a client-supplied
    /// value. Removing line-breaking characters is what prevents log forging and corruption of
    /// structured log output; the length cap, applied by the caller, is what bounds the value's
    /// size.
    ///
    /// The effective removed set is:
    ///
    /// - every <b>control</b> character, Unicode category Cc - U+0000-U+001F (including \r, \n,
    ///   \t and \0) and U+007F-U+009F - which is what <c>Rune.IsControl</c> reports;
    /// - every <b>format</b> character, Unicode category Cf. That covers the bidirectional
    ///   embeddings and overrides U+202A-U+202E (notably RIGHT-TO-LEFT OVERRIDE), the
    ///   bidirectional isolates U+2066-U+2069, the zero-width characters U+200B-U+200D and
    ///   U+2060 WORD JOINER, the directional marks U+200E and U+200F, U+00AD SOFT HYPHEN and
    ///   U+FEFF BYTE ORDER MARK. These are removed as a product decision, not because they can
    ///   forge a log line: a bidi override renders the remainder of a log line right-to-left in
    ///   a viewer, and a zero-width character makes two visually identical IDs distinct strings.
    ///   Both defeat the one guarantee this value carries - that an operator can search the logs
    ///   for the ID the client received. A category test is used rather than a code-point list
    ///   so the rule stays a single coherent negative test;
    /// - LINE SEPARATOR (U+2028) and PARAGRAPH SEPARATOR (U+2029), which neither test reports
    ///   (they are categories Zl and Zp) and which the <c>ReplaceLineEndings</c> call in
    ///   <see cref="SanitizeByCodePoint"/> removes instead, because they break a line-oriented
    ///   log consumer the same way a newline does;
    /// - every <b>unpaired surrogate</b>, Unicode category Cs - a high surrogate not followed by
    ///   a low one, or a low surrogate not preceded by a high one. This is not a category test
    ///   like the two above and cannot be written as one: a predicate over decoded code points
    ///   never sees an unpaired surrogate at all, because it is not a code point. It is
    ///   <see cref="SanitizeByCodePoint"/> that drops it, on the decoder's own report that the
    ///   input is not well-formed UTF-16. Removal is what makes the FR-LOG-6 parity guarantee
    ///   unconditional: <c>System.Text.Json</c> writes U+FFFD for an unpaired surrogate while a
    ///   structured-log sink receives the raw code unit, so any value carrying one reaches the
    ///   client and the log as two different strings. The two halves of a <em>well-formed</em>
    ///   pair are not surrogates for this purpose - they decode to a single astral code point
    ///   and are kept or removed on that code point's category, like any other character.
    ///
    /// Both category tests are applied <b>per Unicode code point</b>, not per UTF-16 code unit,
    /// which is why this method - unlike <see cref="SanitizeInternalValueForLog"/> - runs on
    /// <see cref="SanitizeByCodePoint"/>. The distinction is load-bearing rather than pedantic:
    /// the whole of the supplementary-plane Cf set is reachable in a header value, and a
    /// per-code-unit test cannot see any of it, because both halves of a non-BMP code point are
    /// surrogates and <c>char.GetUnicodeCategory</c> reports Cs for a surrogate, never Cf. The
    /// set that would otherwise survive is exactly the invisible-text-smuggling channel this
    /// rule exists to close: the TAG block U+E0001 and U+E0020-U+E007F, which encodes arbitrary
    /// ASCII as characters that render as nothing at all, plus U+110BD, U+110CD, U+13430-U+1343F,
    /// U+1BCA0-U+1BCA3 and the musical-notation controls U+1D173-U+1D17A. A tag-encoded payload
    /// appended to an otherwise ordinary ID round-trips intact through a per-code-unit filter
    /// while displaying as the bare ID, so the stored value and the displayed value differ - the
    /// same failure the zero-width rule above is written to prevent, in its most complete form.
    ///
    /// Everything else is preserved, and that deliberately includes printable punctuation and
    /// symbols, non-ASCII letters (Lu/Ll/Lo) whether BMP or astral, emoji and other supplementary
    /// symbols (So), and whitespace of category Zs - both SPACE and U+00A0 NO-BREAK SPACE - so
    /// internal spacing in an upstream identifier survives. The result is therefore always
    /// well-formed UTF-16, whatever the input was, which is the property
    /// <c>CorrelationIdNormalizer.Normalize</c> hands on to its own callers. The ingestion point
    /// separately falls back to the server-generated identifier when the supplied header is null,
    /// empty or entirely whitespace.
    /// </remarks>
    public static string SanitizeCorrelationId(string? input) =>
        SanitizeByCodePoint(
            input,
            static r => !Rune.IsControl(r) && Rune.GetUnicodeCategory(r) != UnicodeCategory.Format
        );

    /// <summary>
    /// Sanitizes free-form text - an upstream service's error payload, an exception message, any
    /// value that is not an identifier - for safe inclusion in a structured log event.
    /// </summary>
    /// <remarks>
    /// Applies the same negative-test allowlist as <see cref="SanitizeCorrelationId"/> - remove
    /// Unicode categories Cc and Cf, the line/paragraph separators U+2028 and U+2029, and any
    /// unpaired surrogate, keep everything else - because the two have the same requirement:
    /// strip everything that can break or forge a log line, or smuggle invisible text past the
    /// operator reading it, while preserving the printable punctuation that makes the value worth
    /// logging at all. The canonical description of that allowlist lives on
    /// <see cref="SanitizeCorrelationId"/> and is deliberately not restated here.
    ///
    /// Unpaired-surrogate removal reaches this method because both entry points run on
    /// <see cref="SanitizeByCodePoint"/>, and it is wanted here on its own merits rather than
    /// merely tolerated: free-form text is not echoed to a client, so FR-LOG-6 parity is not at
    /// stake, but an unpaired surrogate in a log parameter still renders as U+FFFD in a
    /// JSON-formatted log event while a plain-text sink receives the raw unit, so two sinks
    /// reading the same event disagree about what the upstream service actually said.
    ///
    /// The two are separate entry points rather than one shared method because they answer to
    /// different contracts. A correlation ID is also echoed to the client in the error response
    /// body and has to match the logged value character for character (FR-LOG-6), so its rule is
    /// free to tighten or loosen without dragging free-form log text along with it - and free-form
    /// text is never echoed back, so the reverse is equally true.
    ///
    /// <b>Not <see cref="SanitizeInternalValueForLog"/>:</b> that Method/Path allowlist strips the quotes,
    /// braces, commas and equals signs a JSON or form-encoded error payload is made of, reducing
    /// it to a run of bare words that no longer identifies what the upstream service objected to.
    ///
    /// <b>Length is the caller's responsibility.</b> Free-form text is usually attacker-influenced
    /// in size as well as in content, and no single cap suits every call site. A caller that bounds
    /// length should sanitize first and truncate second, so the budget is spent on characters that
    /// survive rather than on padding the sanitizer is about to remove.
    /// </remarks>
    public static string SanitizeFreeTextForLog(string? input) =>
        SanitizeByCodePoint(
            input,
            static r => !Rune.IsControl(r) && Rune.GetUnicodeCategory(r) != UnicodeCategory.Format
        );

    /// <summary>
    /// The two-pass filter behind <see cref="SanitizeInternalValueForLog"/>, applying its predicate once per
    /// UTF-16 code unit.
    /// </summary>
    /// <remarks>
    /// <paramref name="isAllowedChar"/> is invoked twice per character - once to count safe
    /// characters for the exact-size allocation, once to select them into the buffer. It must
    /// be a pure function of only its input character: deterministic, with no side effects and
    /// no dependency on call count or ordering. A predicate that is not pure will silently
    /// produce a buffer that is the wrong size or filled incorrectly.
    ///
    /// The per-code-unit iteration is part of the strict allowlist's observable behavior, not an
    /// oversight left behind when <see cref="SanitizeCorrelationId"/> moved to
    /// <see cref="SanitizeByCodePoint"/>. Because each half of a non-BMP code point is a
    /// surrogate, and a surrogate is neither a letter nor a digit, every astral character -
    /// U+1D400 MATHEMATICAL BOLD CAPITAL A and emoji alike - is stripped from a <c>Method</c> or
    /// <c>Path</c>. Reunifying the two helpers on rune iteration would quietly reverse that, since
    /// <c>Rune.IsLetterOrDigit(U+1D400)</c> is true, so the strict path stays here.
    /// </remarks>
    private static string Sanitize(string? input, Func<char, bool> isAllowedChar)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        // Behaviorally redundant for the strict SanitizeInternalValueForLog allowlist, which independently
        // rejects both U+2028 and U+2029 - neither is a letter or a digit. Kept because static
        // log-injection analysis (CodeQL) models ReplaceLineEndings as a sanitizer but not the
        // custom allowlist loop. SanitizeByCodePoint carries the same call, where it is not
        // redundant.
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

    /// <summary>
    /// The two-pass filter behind <see cref="SanitizeCorrelationId"/>, applying its predicate once
    /// per Unicode code point rather than once per UTF-16 code unit.
    /// </summary>
    /// <remarks>
    /// <paramref name="isAllowedRune"/> is invoked once per code point in each of the two passes -
    /// the first counting the code units to emit for the exact-size allocation, the second
    /// selecting them into the buffer - and must be a pure function of only its input rune:
    /// deterministic, with no side effects and no dependency on call count or ordering. The count
    /// pass accumulates <b>code units</b>, not code points, because that is what the
    /// <c>string.Create</c> buffer is measured in; counting runes would under-size the buffer by
    /// one for every astral character that survives.
    ///
    /// An unpaired surrogate is dropped, and dropped here rather than in either predicate,
    /// because a predicate over runes cannot express the rule: an unpaired surrogate is not a
    /// code point, so it never reaches <paramref name="isAllowedRune"/> as itself.
    /// <c>Rune.DecodeFromUtf16</c> reports InvalidData for one (or NeedMoreData for a high
    /// surrogate that ends the string) and hands back U+FFFD; both statuses are treated as
    /// "remove", and neither the raw code unit nor the substituted U+FFFD is emitted. Emitting
    /// U+FFFD would be a silent repair that changes the value's length and content without
    /// saying so; emitting the raw unit would leave the result malformed UTF-16, which is the
    /// FR-LOG-6 parity break described on <see cref="SanitizeCorrelationId"/>. Both statuses
    /// consume exactly one code unit - NeedMoreData only arises when the remaining span is that
    /// single trailing surrogate - so each drop advances by exactly one char, and a well-formed
    /// pair immediately following an unpaired half still decodes and survives intact.
    /// </remarks>
    private static string SanitizeByCodePoint(string? input, Func<Rune, bool> isAllowedRune)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        // Load-bearing here, unlike in Sanitize: the correlation-ID allowlist excludes only
        // categories Cc and Cf, so without this it would preserve the Unicode line/paragraph
        // separators U+2028 and U+2029 (categories Zl and Zp), which break a line-oriented log
        // consumer the same way a newline does. Also kept because static log-injection analysis
        // (CodeQL) models ReplaceLineEndings as a sanitizer but not the custom allowlist loop.
        input = input.ReplaceLineEndings(string.Empty);

        // First pass: check if sanitization is needed and count the code units to emit.
        int safeUnitCount = 0;
        bool needsSanitization = false;

        ReadOnlySpan<char> remaining = input;
        while (!remaining.IsEmpty)
        {
            OperationStatus status = Rune.DecodeFromUtf16(remaining, out Rune rune, out int unitsConsumed);

            if (status != OperationStatus.Done)
            {
                // A decode failure is an unpaired surrogate, which is removed; see the remarks.
                needsSanitization = true;
            }
            else if (isAllowedRune(rune))
            {
                safeUnitCount += unitsConsumed;
            }
            else
            {
                needsSanitization = true;
            }

            remaining = remaining[unitsConsumed..];
        }

        if (!needsSanitization)
        {
            return input;
        }

        if (safeUnitCount == 0)
        {
            return string.Empty;
        }

        // Second pass: build the sanitized string with exact allocation. The predicate travels
        // with the source in a value tuple so the lambda stays static - no closure allocation,
        // and no boxing of the state.
        return string.Create(
            safeUnitCount,
            (Input: input, IsAllowedRune: isAllowedRune),
            static (span, state) =>
            {
                int index = 0;
                ReadOnlySpan<char> source = state.Input;

                while (!source.IsEmpty)
                {
                    OperationStatus status = Rune.DecodeFromUtf16(
                        source,
                        out Rune rune,
                        out int unitsConsumed
                    );

                    // A decode failure is an unpaired surrogate: emit nothing at all, neither
                    // the raw code unit nor the U+FFFD the decoder substituted for it.
                    if (status == OperationStatus.Done && state.IsAllowedRune(rune))
                    {
                        index += rune.EncodeToUtf16(span[index..]);
                    }

                    source = source[unitsConsumed..];
                }
            }
        );
    }

    // Explicitly reject control characters for defense in depth
    // Includes backslash for Windows file paths
    private static bool IsAllowedChar(char c) =>
        !char.IsControl(c)
        && (char.IsLetterOrDigit(c) || c is ' ' or '_' or '-' or '.' or ':' or '/' or '\\');
}
