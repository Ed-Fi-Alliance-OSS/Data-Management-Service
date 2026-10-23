// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Services;

/// <summary>
/// Describes the stored forms of a connection string that <see cref="ConnectionStringEncryptionService"/>
/// encrypted, and recognizes their Base64 text. A get returns cipher text as Base64 (the stored bytes,
/// or for a value carrying a secret reference, its resolved value encrypted again), so a client that
/// reads a data store, edits an unrelated field and writes the object back resubmits this shape;
/// encrypting it again would leave a value no reader can decrypt to a connection string.
///
/// Two forms exist:
/// <list type="bullet">
/// <item>Envelope (written today): marker (16) || version (1) || nonce (12) || tag (16) || cipher text.
/// AES-256-GCM over the PKCS7-padded UTF-8 text, with marker and version as associated data. Padding
/// keeps the cipher text a whole number of blocks, so every envelope is 13 bytes past a block
/// multiple.</item>
/// <item>Legacy (read only): a 16-byte IV followed by AES-CBC/PKCS7 cipher text, always a whole number
/// of blocks.</item>
/// </list>
/// The two length classes never overlap, which is what lets a reader route a value whose marker was
/// altered to the envelope path and reject it, rather than decrypt it as legacy.
///
/// It lives beside the encryption service because it describes that service's output format: the
/// two must change together, and so must the DMS and PowerShell readers of the same bytes.
/// </summary>
public static class ConnectionStringCipherText
{
    /// <summary>
    /// ASCII "EdFi.CS.Envelope". It carries no version; that is the byte after it.
    /// </summary>
    internal static ReadOnlySpan<byte> EnvelopeMarker => "EdFi.CS.Envelope"u8;

    internal const byte EnvelopeVersion = 1;

    /// <summary>
    /// Marker plus version: the associated data the GCM tag covers.
    /// </summary>
    internal const int EnvelopeHeaderLength = 17;

    internal const int EnvelopeNonceLength = 12;

    internal const int EnvelopeTagLength = 16;

    /// <summary>
    /// Everything ahead of the cipher text.
    /// </summary>
    internal const int EnvelopeOverhead = EnvelopeHeaderLength + EnvelopeNonceLength + EnvelopeTagLength;

    /// <summary>
    /// AES block length in bytes. Envelope cipher text and legacy output are both whole blocks.
    /// </summary>
    internal const int BlockLength = 16;

    /// <summary>
    /// The shortest envelope: one block of cipher text, which even one character of plain text fills.
    /// </summary>
    private const int MinimumEnvelopeLength = EnvelopeOverhead + BlockLength;

    /// <summary>
    /// Length of the legacy AES-CBC initialization vector.
    /// </summary>
    internal const int LegacyIvLength = 16;

    /// <summary>
    /// The shortest legacy value: the initialization vector plus one block.
    /// </summary>
    private const int MinimumLegacyLength = LegacyIvLength + BlockLength;

    /// <summary>
    /// True when the bytes belong on the envelope path: they carry the marker, or they have the
    /// envelope length class. Either alone is enough, so a value with an altered marker is still
    /// held to the envelope rules and never read as legacy.
    /// </summary>
    internal static bool IsEnvelopeCandidate(ReadOnlySpan<byte> value) =>
        value.Length % BlockLength == EnvelopeOverhead % BlockLength || value.StartsWith(EnvelopeMarker);

    /// <summary>
    /// True when the length is one an envelope can have.
    /// </summary>
    internal static bool HasEnvelopeLength(int length) =>
        length >= MinimumEnvelopeLength && length % BlockLength == EnvelopeOverhead % BlockLength;

    /// <summary>
    /// True when the length is one legacy output can have.
    /// </summary>
    internal static bool HasLegacyLength(int length) =>
        length >= MinimumLegacyLength && length % BlockLength == 0;

    /// <summary>
    /// True when the value could be output of the encryption service, in either form. A valid
    /// connection string can never match: it has to assign at least one setting, so it contains an
    /// '=' that is not Base64 padding, or it is a lone "keyword=" that assigns nothing and is rejected
    /// for that instead.
    /// </summary>
    public static bool LooksLikeCipherText(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        // Base64 text is longer than the bytes it encodes, so a buffer the length of the input
        // always holds the decoded value.
        byte[] decoded = new byte[value.Length];

        if (!Convert.TryFromBase64String(value, decoded, out int decodedLength))
        {
            return false;
        }

        return HasEnvelopeLength(decodedLength) || HasLegacyLength(decodedLength);
    }
}
