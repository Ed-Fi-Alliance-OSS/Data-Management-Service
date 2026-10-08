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
    /// tenant or instance name, a schema hash - with a strict allowlist of letters, digits,
    /// spaces and the punctuation <c>_-.:/\</c>.
    /// </summary>
    /// <remarks>
    /// <b>Never for a correlation ID or a <c>TraceId</c></b>; use
    /// <see cref="SanitizeCorrelationId"/>. This allowlist strips punctuation an upstream
    /// identifier scheme legitimately uses, so a correlation ID sanitized here would stop
    /// matching the value echoed to the client for the same request, breaking FR-LOG-6 parity.
    /// </remarks>
    public static string SanitizeInternalValueForLog(string? input) => Sanitize(input, IsAllowedChar);

    /// <summary>
    /// Sanitizes a correlation ID for safe logging and for inclusion in an error response body.
    /// Removes Unicode categories Cc (control) and Cf (format), the line and paragraph separators
    /// U+2028 and U+2029, and every unpaired surrogate; keeps everything else, including
    /// printable punctuation, non-ASCII letters, emoji and category Zs whitespace. The result is
    /// always well-formed UTF-16.
    /// </summary>
    /// <remarks>
    /// The allowlist's breadth, the use of negative tests rather than a positive enumeration,
    /// and the unconditional removal of unpaired surrogates are all settled in the ADR at
    /// <c>reference/adr-correlation-id-normalization.md</c>. Length is the caller's concern -
    /// <c>CorrelationIdNormalizer.Normalize</c> applies the cap.
    ///
    /// Tested per Unicode code point rather than per UTF-16 code unit, which is why this runs on
    /// <see cref="SanitizeByCodePoint"/>. That is load-bearing: both halves of a non-BMP code
    /// point are surrogates, and <c>char.GetUnicodeCategory</c> reports Cs for a surrogate and
    /// never Cf, so a per-code-unit test cannot see the supplementary-plane Cf set at all -
    /// including the TAG block (U+E0001, U+E0020-U+E007F), which encodes arbitrary ASCII as
    /// characters that render as nothing.
    /// </remarks>
    public static string SanitizeCorrelationId(string? input) =>
        SanitizeByCodePoint(
            input,
            static r => !Rune.IsControl(r) && Rune.GetUnicodeCategory(r) != UnicodeCategory.Format
        );

    /// <summary>
    /// Sanitizes free-form text - an upstream service's error payload, an exception message, any
    /// value that is not an identifier - for safe inclusion in a structured log event. Applies
    /// the same allowlist as <see cref="SanitizeCorrelationId"/>, which describes it.
    /// </summary>
    /// <remarks>
    /// A separate entry point rather than a shared method because the two answer to different
    /// contracts: a correlation ID is also echoed to the client and must match the logged value
    /// character for character, so either rule can change without dragging the other along.
    ///
    /// Not <see cref="SanitizeInternalValueForLog"/>, which would strip the quotes, braces,
    /// commas and equals signs a JSON or form-encoded error payload is made of.
    ///
    /// <b>Length is the caller's responsibility</b>; no single cap suits every call site.
    /// Sanitize first and truncate second, so the budget is spent on characters that survive, and
    /// back the cut off a split surrogate pair.
    /// </remarks>
    public static string SanitizeFreeTextForLog(string? input) =>
        SanitizeByCodePoint(
            input,
            static r => !Rune.IsControl(r) && Rune.GetUnicodeCategory(r) != UnicodeCategory.Format
        );

    /// <summary>
    /// The two-pass filter behind <see cref="SanitizeInternalValueForLog"/>, applying its
    /// predicate once per UTF-16 code unit.
    /// </summary>
    /// <remarks>
    /// <paramref name="isAllowedChar"/> is invoked twice per character - once to count safe
    /// characters for the exact-size allocation, once to select them into the buffer - and must
    /// be pure: deterministic, no side effects, no dependence on call count or ordering. An
    /// impure predicate silently produces a buffer that is the wrong size or filled incorrectly.
    ///
    /// The per-code-unit iteration is deliberate, not a leftover from before
    /// <see cref="SanitizeCorrelationId"/> moved to <see cref="SanitizeByCodePoint"/>. Each half
    /// of a non-BMP code point is a surrogate and so neither a letter nor a digit, which strips
    /// every astral character from a <c>Method</c> or <c>Path</c>. Reunifying the two helpers on
    /// rune iteration would quietly reverse that, since <c>Rune.IsLetterOrDigit(U+1D400)</c> is
    /// true.
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
    /// The two-pass filter behind <see cref="SanitizeCorrelationId"/> and
    /// <see cref="SanitizeFreeTextForLog"/>, applying its predicate once per Unicode code point
    /// rather than once per UTF-16 code unit, and dropping every unpaired surrogate.
    /// </summary>
    /// <remarks>
    /// <paramref name="isAllowedRune"/> must be pure - deterministic, no side effects, no
    /// dependence on call count or ordering - because it is invoked once per code point in each
    /// of the two passes. The count pass accumulates <b>code units</b>, not code points, because
    /// that is what the <c>string.Create</c> buffer is measured in; counting runes would
    /// under-size it by one for every astral character that survives.
    ///
    /// The unpaired-surrogate drop lives here rather than in a predicate because an unpaired
    /// surrogate is not a code point and so never reaches <paramref name="isAllowedRune"/> as
    /// itself. <c>Rune.DecodeFromUtf16</c> reports InvalidData for one - or NeedMoreData for a
    /// high surrogate ending the string - and hands back U+FFFD; both statuses mean "remove", and
    /// neither the raw code unit nor the substitute is emitted. Both consume exactly one code
    /// unit, so each drop advances by one char and a well-formed pair immediately following an
    /// unpaired half still decodes and survives intact. Why the rule is unconditional rather
    /// than limited to the truncation cut: <c>reference/adr-correlation-id-normalization.md</c>.
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
