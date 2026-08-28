// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers.Text;
using System.Globalization;
using System.Text;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Paging;

/// <summary>
/// Transport encoding for a <see cref="CursorRange"/>. A page token is opaque to clients and carries
/// nothing but the ordering-mode marker and the inclusive bounds.
/// </summary>
/// <remarks>
/// Internal to Core on purpose: token text belongs at the HTTP contract boundary, and backend
/// contracts, planners, and SQL compilers receive typed ranges only. A compiler that could see token
/// text would be one refactor away from making an authorization decision from client-supplied text.
/// Decoding grants no authority and makes no authorization decision. Reporting a rejected token to the
/// client belongs to request validation, so <see cref="TryDecode"/> returns a bool rather than a
/// message or an exception.
/// <para>
/// The marker is what makes the bounds interpretable. A token stores no request filters, so without it
/// the server could not tell a <c>ContentVersion</c> anchor from a <c>DocumentId</c> one when a client
/// changes <c>maxChangeVersion</c> mid-walk, and would replay the token's bounds against the wrong
/// column. Every token carries one — there is no unmarked legacy form, because cursor paging and this
/// marker ship in the same release and tokens are opaque, so nothing holds a two-field token.
/// </para>
/// </remarks>
internal static class PageTokenCodec
{
    private const char PaddingCharacter = '=';
    private const char FieldSeparator = ',';
    private const int FieldCount = 3;

    /// <summary>Marker for a <see cref="PageOrderingMode.DocumentId"/>-anchored range.</summary>
    private const string DocumentIdMarker = "d";

    /// <summary>Marker for a <see cref="PageOrderingMode.ContentVersion"/>-anchored range.</summary>
    private const string ContentVersionMarker = "c";

    /// <summary>
    /// Rejects invalid byte sequences instead of substituting replacement characters, so a token that
    /// was never produced by the encoder cannot decode to a different range than it names.
    /// </summary>
    private static readonly UTF8Encoding _strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    /// <summary>
    /// Encodes the anchor marker followed by both inclusive bounds as invariant signed decimals, joined
    /// by commas, UTF-8 encoded, in canonical unpadded base64url. All three fields are always emitted.
    /// </summary>
    internal static string Encode(CursorRange range, PageOrderingMode orderingMode)
    {
        ArgumentNullException.ThrowIfNull(range);

        string payload =
            MarkerFor(orderingMode)
            + FieldSeparator
            + range.InclusiveMinimum.ToString(CultureInfo.InvariantCulture)
            + FieldSeparator
            + range.InclusiveMaximum.ToString(CultureInfo.InvariantCulture);

        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload));
    }

    /// <summary>
    /// Decodes a client-supplied page token, accepting correctly padded or unpadded base64url. A null
    /// or empty token is rejected like any other malformed input, so callers can hand over an absent
    /// query-string value without a null check of their own.
    /// </summary>
    /// <remarks>
    /// Deliberately stricter than a permissive base64 reader: accepting forms the encoder never emits
    /// would create an undocumented input surface that a later change could not safely narrow. An
    /// empty maximum is the one accepted form the encoder does not produce, so a client or tool can
    /// express a range that is unbounded above. The marker has no such latitude: an unknown or missing
    /// one is rejected exactly as a malformed bound is.
    /// </remarks>
    /// <param name="orderingMode">
    /// The anchor the token's bounds are expressed in. Meaningful only when this method returns
    /// <see langword="true"/>; a rejected token names no anchor. Whether that anchor agrees with the
    /// anchor the request resolved is request validation's decision, not the codec's.
    /// </param>
    internal static bool TryDecode(
        string? pageToken,
        out CursorRange? range,
        out PageOrderingMode orderingMode
    )
    {
        range = null;
        orderingMode = PageOrderingMode.DocumentId;

        if (string.IsNullOrEmpty(pageToken))
        {
            return false;
        }

        int paddingLength = 0;
        while (
            paddingLength < pageToken.Length
            && pageToken[pageToken.Length - 1 - paddingLength] == PaddingCharacter
        )
        {
            paddingLength++;
        }

        int unpaddedLength = pageToken.Length - paddingLength;

        if (unpaddedLength == 0)
        {
            return false;
        }

        for (int index = 0; index < unpaddedLength; index++)
        {
            char character = pageToken[index];

            // Also rejects '+', '/', whitespace, and any padding before the trailing run.
            if (!char.IsAsciiLetterOrDigit(character) && character != '-' && character != '_')
            {
                return false;
            }
        }

        int lengthRemainder = unpaddedLength % 4;

        if (lengthRemainder == 1)
        {
            return false;
        }

        int requiredPaddingLength = lengthRemainder == 0 ? 0 : 4 - lengthRemainder;

        if (paddingLength != 0 && paddingLength != requiredPaddingLength)
        {
            return false;
        }

        ReadOnlySpan<char> unpadded = pageToken.AsSpan(0, unpaddedLength);
        byte[] payloadBytes = new byte[Base64Url.GetMaxDecodedLength(unpaddedLength)];

        if (!Base64Url.TryDecodeFromChars(unpadded, payloadBytes, out int payloadLength))
        {
            return false;
        }

        string payload;

        try
        {
            payload = _strictUtf8.GetString(payloadBytes, 0, payloadLength);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        string[] fields = payload.Split(FieldSeparator);

        if (fields.Length != FieldCount)
        {
            return false;
        }

        // Parsed into a local so a token that clears the marker but fails a bound still reports no
        // anchor, keeping the out parameter meaningful on success only.
        if (!TryParseMarker(fields[0], out PageOrderingMode decodedOrderingMode))
        {
            return false;
        }

        if (!TryParseBound(fields[1], out long inclusiveMinimum))
        {
            return false;
        }

        long inclusiveMaximum;

        if (fields[2].Length == 0)
        {
            inclusiveMaximum = long.MaxValue;
        }
        else if (!TryParseBound(fields[2], out inclusiveMaximum))
        {
            return false;
        }

        range = new CursorRange(inclusiveMinimum, inclusiveMaximum);
        orderingMode = decodedOrderingMode;
        return true;
    }

    /// <summary>
    /// Creates the token for the page after a non-empty selected keyset, retaining the request's
    /// maximum bound so a partition walk stays inside its slice, and stamping the anchor that page was
    /// selected on.
    /// </summary>
    /// <remarks>
    /// Both bounds are anchor values, so both arguments carry the units
    /// <paramref name="orderingMode"/> names. Advancing by one is what makes the inclusive bound shape
    /// work for either anchor: <c>ContentVersion &gt; anchor</c> and
    /// <c>ContentVersion &gt;= anchor + 1</c> are the same predicate over an integer sequence.
    /// Returns false at <see cref="long.MaxValue"/>: advancing would overflow, so no next token exists
    /// and the caller omits it. Callers whose request carried no maximum bound pass
    /// <see cref="long.MaxValue"/>, which keeps a walk entered from a traditional response unbounded
    /// above.
    /// </remarks>
    internal static bool TryCreateNextPageToken(
        long highestSelectedAnchor,
        long maximumAnchor,
        PageOrderingMode orderingMode,
        out string? nextPageToken
    )
    {
        if (highestSelectedAnchor == long.MaxValue)
        {
            nextPageToken = null;
            return false;
        }

        nextPageToken = Encode(new CursorRange(highestSelectedAnchor + 1, maximumAnchor), orderingMode);
        return true;
    }

    /// <summary>
    /// The wire marker for an anchor. Unsupported values throw rather than defaulting: a token stamped
    /// with the wrong anchor is rejected on replay, so silently choosing one here would turn a coding
    /// error into a broken walk a client cannot resume.
    /// </summary>
    private static string MarkerFor(PageOrderingMode orderingMode) =>
        orderingMode switch
        {
            PageOrderingMode.DocumentId => DocumentIdMarker,
            PageOrderingMode.ContentVersion => ContentVersionMarker,
            _ => throw new ArgumentOutOfRangeException(
                nameof(orderingMode),
                orderingMode,
                "Unsupported page ordering mode."
            ),
        };

    /// <summary>
    /// Parses the anchor marker, which must be exactly one of the emitted markers. The exact match
    /// rejects casing variants, padding, and surrounding whitespace without a separate rule.
    /// </summary>
    private static bool TryParseMarker(string field, out PageOrderingMode orderingMode)
    {
        switch (field)
        {
            case DocumentIdMarker:
                orderingMode = PageOrderingMode.DocumentId;
                return true;
            case ContentVersionMarker:
                orderingMode = PageOrderingMode.ContentVersion;
                return true;
            default:
                orderingMode = PageOrderingMode.DocumentId;
                return false;
        }
    }

    /// <summary>
    /// Parses one decimal bound, which must match <c>-?[0-9]+</c> exactly and fit <see cref="long"/>.
    /// </summary>
    private static bool TryParseBound(string field, out long bound)
    {
        bound = 0;

        if (field.Length == 0)
        {
            return false;
        }

        int firstDigitIndex = field[0] == '-' ? 1 : 0;

        if (field.Length == firstDigitIndex)
        {
            return false;
        }

        for (int index = firstDigitIndex; index < field.Length; index++)
        {
            // Rejects whitespace, a leading '+', grouping separators, and any other non-digit.
            if (!char.IsAsciiDigit(field[index]))
            {
                return false;
            }
        }

        // Only the Int64 range check remains once the grammar above has passed.
        return long.TryParse(field, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out bound);
    }
}
