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
/// nothing but the inclusive bounds.
/// </summary>
/// <remarks>
/// Internal to Core on purpose: token text belongs at the HTTP contract boundary, and backend
/// contracts, planners, and SQL compilers receive typed ranges only. A compiler that could see token
/// text would be one refactor away from making an authorization decision from client-supplied text.
/// Decoding grants no authority and makes no authorization decision. Reporting a rejected token to the
/// client belongs to request validation, so <see cref="TryDecode"/> returns a bool rather than a
/// message or an exception.
/// </remarks>
internal static class PageTokenCodec
{
    private const char PaddingCharacter = '=';
    private const char FieldSeparator = ',';
    private const int FieldCount = 2;

    /// <summary>
    /// Rejects invalid byte sequences instead of substituting replacement characters, so a token that
    /// was never produced by the encoder cannot decode to a different range than it names.
    /// </summary>
    private static readonly UTF8Encoding _strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    /// <summary>
    /// Encodes both inclusive bounds as invariant signed decimals joined by one comma, UTF-8 encoded,
    /// in canonical unpadded base64url. Both fields are always emitted.
    /// </summary>
    internal static string Encode(CursorRange range)
    {
        ArgumentNullException.ThrowIfNull(range);

        string payload =
            range.InclusiveMinimum.ToString(CultureInfo.InvariantCulture)
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
    /// express a range that is unbounded above.
    /// </remarks>
    internal static bool TryDecode(string? pageToken, out CursorRange? range)
    {
        range = null;

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

        if (!TryParseBound(fields[0], out long inclusiveMinimum))
        {
            return false;
        }

        long inclusiveMaximum;

        if (fields[1].Length == 0)
        {
            inclusiveMaximum = long.MaxValue;
        }
        else if (!TryParseBound(fields[1], out inclusiveMaximum))
        {
            return false;
        }

        range = new CursorRange(inclusiveMinimum, inclusiveMaximum);
        return true;
    }

    /// <summary>
    /// Creates the token for the page after a non-empty selected keyset, retaining the request's
    /// maximum bound so a partition walk stays inside its slice.
    /// </summary>
    /// <remarks>
    /// Returns false at <see cref="long.MaxValue"/>: advancing would overflow, so no next token exists
    /// and the caller omits it. Callers whose request carried no maximum bound pass
    /// <see cref="long.MaxValue"/>, which keeps a walk entered from a traditional response unbounded
    /// above.
    /// </remarks>
    internal static bool TryCreateNextPageToken(
        long highestSelectedDocumentId,
        long maximumDocumentId,
        out string? nextPageToken
    )
    {
        if (highestSelectedDocumentId == long.MaxValue)
        {
            nextPageToken = null;
            return false;
        }

        nextPageToken = Encode(new CursorRange(highestSelectedDocumentId + 1, maximumDocumentId));
        return true;
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
