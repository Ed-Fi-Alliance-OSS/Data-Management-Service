// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.EducationOrganizationProjection;

/// <summary>
/// Transport encoding and acceptance rules for <see cref="ProjectionCursor"/>.
/// </summary>
/// <remarks>
/// Format version <c>1</c> is the unpadded base64url encoding of the UTF-8 text
/// <c>1,&lt;dataStoreId&gt;,&lt;lastEducationOrganizationId&gt;,&lt;digest&gt;,&lt;walkIssuedAtUnixSeconds&gt;,&lt;bindingHash&gt;</c>.
/// The cursor is not signed: altering it can only move the position or fail the digest check
/// within a set the caller is already authorized to read in full.
/// <para>
/// Decoding accepts exactly one representation of a cursor: the unpadded base64url text the
/// encoder emits, in strict UTF-8, with every field in the one form the encoder writes. Padding is
/// refused even when correct, and <c>+1</c>, <c>01</c> and <c>-0</c> are refused rather than read as
/// the number they resemble. This is stricter than <see cref="Paging.PageTokenCodec"/>, which keeps
/// accepting padded page tokens for compatibility; a projection cursor is a new contract, so there
/// is no earlier form to stay compatible with, and accepting forms the encoder never emits would
/// create an input surface that could not later be narrowed.
/// </para>
/// </remarks>
internal static class ProjectionCursorCodec
{
    /// <summary>How far ahead of the server clock a walk timestamp may be before it is refused.</summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromSeconds(60);

    private const string FormatVersion = "1";
    private const char FieldSeparator = ',';
    private const int FieldCount = 6;
    private const int BindingHashLength = 32;

    private static readonly int _digestFieldLength = Base64Url.GetEncodedLength(ProjectionDigest.ByteLength);

    private static readonly UTF8Encoding _strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    /// <summary>
    /// Encodes a cursor. Throws when a field is outside what <see cref="TryDecode"/> accepts, so the
    /// server can never issue a cursor it would itself refuse.
    /// </summary>
    public static string Encode(ProjectionCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        if (cursor.DataStoreId <= 0)
        {
            throw new ArgumentException("A cursor's data store id is positive.", nameof(cursor));
        }

        if (cursor.WalkIssuedAtUnixSeconds < 0)
        {
            throw new ArgumentException("A cursor's walk timestamp is not negative.", nameof(cursor));
        }

        if (!IsCanonicalDigest(cursor.Digest))
        {
            throw new ArgumentException("A cursor's digest is unpadded base64url.", nameof(cursor));
        }

        if (!IsBindingHash(cursor.BindingHash))
        {
            throw new ArgumentException(
                "A cursor's binding hash is 32 lower-case hex digits.",
                nameof(cursor)
            );
        }

        string payload = string.Join(
            FieldSeparator,
            FormatVersion,
            cursor.DataStoreId.ToString(CultureInfo.InvariantCulture),
            cursor.LastEducationOrganizationId.ToString(CultureInfo.InvariantCulture),
            cursor.Digest,
            cursor.WalkIssuedAtUnixSeconds.ToString(CultureInfo.InvariantCulture),
            cursor.BindingHash
        );

        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload));
    }

    /// <summary>
    /// Decodes cursor text without judging it against a request. A null or empty value is refused
    /// like any other malformed input.
    /// </summary>
    public static bool TryDecode(string? text, out ProjectionCursor? cursor)
    {
        cursor = null;

        if (!TryDecodePayload(text, out string payload))
        {
            return false;
        }

        string[] fields = payload.Split(FieldSeparator);

        if (fields.Length != FieldCount || fields[0] != FormatVersion)
        {
            return false;
        }

        if (
            !TryParseCanonicalInt64(fields[1], allowNegative: false, out long dataStoreId)
            || dataStoreId is <= 0 or > int.MaxValue
        )
        {
            return false;
        }

        if (!TryParseCanonicalInt64(fields[2], allowNegative: true, out long lastEducationOrganizationId))
        {
            return false;
        }

        if (!IsCanonicalDigest(fields[3]))
        {
            return false;
        }

        if (!TryParseCanonicalInt64(fields[4], allowNegative: false, out long walkIssuedAtUnixSeconds))
        {
            return false;
        }

        if (!IsBindingHash(fields[5]))
        {
            return false;
        }

        cursor = new ProjectionCursor(
            (int)dataStoreId,
            lastEducationOrganizationId,
            fields[3],
            walkIssuedAtUnixSeconds,
            fields[5]
        );
        return true;
    }

    /// <summary>
    /// Decodes cursor text and accepts it only for this request: the same data store, the same
    /// binding, and a walk that began no more than <paramref name="cursorLifetime"/> ago and no more
    /// than <see cref="FutureTolerance"/> ahead of <paramref name="now"/>. Both bounds are inclusive.
    /// </summary>
    public static bool TryAccept(
        string? text,
        int requestDataStoreId,
        string requestBindingHash,
        DateTimeOffset now,
        TimeSpan cursorLifetime,
        out ProjectionCursor? cursor,
        out ProjectionCursorRejection rejection
    )
    {
        cursor = null;
        rejection = ProjectionCursorRejection.Malformed;

        if (!TryDecode(text, out ProjectionCursor? decoded))
        {
            return false;
        }

        ProjectionCursor accepted = decoded!;
        long nowUnixSeconds = now.ToUnixTimeSeconds();

        if (accepted.DataStoreId != requestDataStoreId)
        {
            rejection = ProjectionCursorRejection.DataStoreMismatch;
            return false;
        }

        if (!string.Equals(accepted.BindingHash, requestBindingHash, StringComparison.Ordinal))
        {
            rejection = ProjectionCursorRejection.BindingMismatch;
            return false;
        }

        // Compared without subtracting from the client value, so an extreme timestamp cannot
        // overflow; the server-side operands are bounded by the clock and the configured limits.
        if (accepted.WalkIssuedAtUnixSeconds < nowUnixSeconds - (long)cursorLifetime.TotalSeconds)
        {
            rejection = ProjectionCursorRejection.Expired;
            return false;
        }

        if (accepted.WalkIssuedAtUnixSeconds > nowUnixSeconds + (long)FutureTolerance.TotalSeconds)
        {
            rejection = ProjectionCursorRejection.FutureDated;
            return false;
        }

        cursor = accepted;
        return true;
    }

    /// <summary>
    /// The first 32 lower-case hex characters of SHA-256 over
    /// <c>&lt;tenant&gt;|&lt;contractVersion&gt;|&lt;key1&gt;=&lt;value1&gt;;&lt;key2&gt;=&lt;value2&gt;...</c>, where
    /// the tenant is lower-cased (empty in single-tenant mode), the contract version is used as sent,
    /// and the route-qualifier pairs are lower-cased and sorted ordinally by key.
    /// </summary>
    public static string ComputeBindingHash(
        string? tenant,
        string contractVersion,
        IReadOnlyDictionary<RouteQualifierName, RouteQualifierValue> routeQualifiers
    )
    {
        ArgumentNullException.ThrowIfNull(contractVersion);
        ArgumentNullException.ThrowIfNull(routeQualifiers);

        IEnumerable<string> pairs = routeQualifiers
            .Select(pair =>
                (Key: pair.Key.Value.ToLowerInvariant(), Value: pair.Value.Value.ToLowerInvariant())
            )
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value}");

        string bindingText = string.Concat(
            (tenant ?? string.Empty).ToLowerInvariant(),
            "|",
            contractVersion,
            "|",
            string.Join(';', pairs)
        );

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(bindingText));
        return Convert.ToHexStringLower(hash)[..BindingHashLength];
    }

    private static bool TryDecodePayload(string? text, out string payload)
    {
        payload = string.Empty;

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        // The alphabet check also refuses '=', so padding is rejected wherever it appears.
        if (!IsBase64UrlAlphabet(text) || text.Length % 4 == 1)
        {
            return false;
        }

        byte[] payloadBytes = new byte[Base64Url.GetMaxDecodedLength(text.Length)];

        if (!TryDecodeBase64Url(text, payloadBytes, out int payloadLength))
        {
            return false;
        }

        try
        {
            payload = _strictUtf8.GetString(payloadBytes, 0, payloadLength);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Decodes unpadded base64url without throwing. <see cref="Base64Url.TryDecodeFromChars(ReadOnlySpan{char}, Span{byte}, out int)"/>
    /// throws <see cref="FormatException"/> when the final character carries non-zero unused bits, so
    /// a client could turn a refused cursor into an unhandled exception; the
    /// <see cref="OperationStatus"/> overload reports that case as invalid data instead.
    /// </summary>
    private static bool TryDecodeBase64Url(ReadOnlySpan<char> source, Span<byte> destination, out int written)
    {
        OperationStatus status = Base64Url.DecodeFromChars(
            source,
            destination,
            out int charsConsumed,
            out written
        );

        return status == OperationStatus.Done && charsConsumed == source.Length;
    }

    /// <summary>
    /// Also rejects <c>+</c>, <c>/</c>, whitespace and the padding character <c>=</c>.
    /// </summary>
    private static bool IsBase64UrlAlphabet(ReadOnlySpan<char> text)
    {
        foreach (char character in text)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '-' && character != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The one decimal form the encoder writes: <c>0</c>, or an optional <c>-</c> followed by a
    /// non-zero digit and further digits, within <see cref="long"/>. Refuses <c>+</c>, leading zeroes,
    /// <c>-0</c>, whitespace and grouping.
    /// </summary>
    private static bool TryParseCanonicalInt64(string field, bool allowNegative, out long value)
    {
        value = 0;

        if (field == "0")
        {
            return true;
        }

        int firstDigitIndex = allowNegative && field.StartsWith('-') ? 1 : 0;

        if (field.Length == firstDigitIndex || field[firstDigitIndex] == '0')
        {
            return false;
        }

        for (int index = firstDigitIndex; index < field.Length; index++)
        {
            if (!char.IsAsciiDigit(field[index]))
            {
                return false;
            }
        }

        // Only the Int64 range check remains once the grammar above has passed.
        return long.TryParse(field, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// A digest field is exactly the encoder's output for some 32-byte value, which also rules out
    /// alternative spellings that differ only in the unused low bits of the final character.
    /// </summary>
    private static bool IsCanonicalDigest(string field)
    {
        if (field.Length != _digestFieldLength || !IsBase64UrlAlphabet(field))
        {
            return false;
        }

        byte[] digest = new byte[ProjectionDigest.ByteLength];

        return TryDecodeBase64Url(field, digest, out int written)
            && written == ProjectionDigest.ByteLength
            && string.Equals(Base64Url.EncodeToString(digest), field, StringComparison.Ordinal);
    }

    private static bool IsBindingHash(string field) =>
        field.Length == BindingHashLength && field.All(character => char.IsAsciiHexDigitLower(character));
}
