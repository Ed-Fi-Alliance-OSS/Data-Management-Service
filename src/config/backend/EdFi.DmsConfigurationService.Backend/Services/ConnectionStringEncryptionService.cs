// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using static EdFi.DmsConfigurationService.Backend.Services.ConnectionStringCipherText;

namespace EdFi.DmsConfigurationService.Backend.Services;

/// <summary>
/// Encrypts connection strings as AES-256-GCM envelopes and reads both envelopes and legacy AES-CBC
/// values; <see cref="ConnectionStringCipherText"/> describes both forms. A legacy value is upgraded
/// when its connection string is next written, since every write produces an envelope.
/// </summary>
public class ConnectionStringEncryptionService(IOptions<DatabaseOptions> databaseOptions)
    : IConnectionStringEncryptionService
{
    /// <summary>
    /// Binds the derived key to this use and format version, so a later version derives its own.
    /// </summary>
    private static readonly byte[] _envelopeKeyInfo = "EdFi.ConnectionString.AES-256-GCM.v1"u8.ToArray();

    private static readonly UTF8Encoding _strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    /// <summary>
    /// Only the first 32 characters of the configured key count, as they always have. Legacy values
    /// were encrypted under these bytes directly, so they stay the legacy key.
    /// </summary>
    private readonly byte[] _legacyKey = KeyMaterial(databaseOptions.Value.EncryptionKey);

    /// <summary>
    /// A separate key derived from the same bytes. HKDF adds no entropy: the strength of this key is
    /// still that of the configured value.
    /// </summary>
    private readonly byte[] _envelopeKey = HKDF.DeriveKey(
        HashAlgorithmName.SHA256,
        KeyMaterial(databaseOptions.Value.EncryptionKey),
        outputLength: 32,
        salt: [],
        info: _envelopeKeyInfo
    );

    private static byte[] KeyMaterial(string encryptionKey) =>
        Encoding.UTF8.GetBytes(encryptionKey.PadRight(32, '0')[..32]);

    public byte[]? Encrypt(string? connectionString)
    {
        if (string.IsNullOrEmpty(connectionString))
        {
            return null;
        }

        byte[] plainTextBytes = Encoding.UTF8.GetBytes(connectionString);
        int paddingLength = BlockLength - plainTextBytes.Length % BlockLength;
        byte[] padded = new byte[plainTextBytes.Length + paddingLength];
        plainTextBytes.CopyTo(padded, 0);
        padded.AsSpan(plainTextBytes.Length).Fill((byte)paddingLength);

        byte[] result = new byte[EnvelopeOverhead + padded.Length];
        Span<byte> header = result.AsSpan(0, EnvelopeHeaderLength);
        Span<byte> nonce = result.AsSpan(EnvelopeHeaderLength, EnvelopeNonceLength);
        Span<byte> tag = result.AsSpan(EnvelopeHeaderLength + EnvelopeNonceLength, EnvelopeTagLength);
        Span<byte> cipherText = result.AsSpan(EnvelopeOverhead);

        EnvelopeMarker.CopyTo(header);
        header[^1] = EnvelopeVersion;
        RandomNumberGenerator.Fill(nonce);

        using var aesGcm = new AesGcm(_envelopeKey, EnvelopeTagLength);
        aesGcm.Encrypt(nonce, padded, cipherText, tag, header);

        return result;
    }

    public string? Decrypt(byte[]? encryptedConnectionString)
    {
        if (encryptedConnectionString is null || encryptedConnectionString.Length == 0)
        {
            return null;
        }

        // Envelope first, and never a legacy attempt after it: a value with an altered marker keeps
        // the envelope length class, so it is rejected here rather than decrypted as legacy.
        if (IsEnvelopeCandidate(encryptedConnectionString))
        {
            return DecryptEnvelope(encryptedConnectionString);
        }

        if (HasLegacyLength(encryptedConnectionString.Length))
        {
            return DecryptLegacy(encryptedConnectionString);
        }

        throw new CryptographicException("The encrypted connection string has an invalid length.");
    }

    private string DecryptEnvelope(byte[] envelope)
    {
        if (!HasEnvelopeLength(envelope.Length))
        {
            throw new CryptographicException("The encrypted connection string envelope is malformed.");
        }

        ReadOnlySpan<byte> header = envelope.AsSpan(0, EnvelopeHeaderLength);

        if (!header.StartsWith(EnvelopeMarker))
        {
            throw new CryptographicException("The encrypted connection string envelope is not recognized.");
        }

        if (header[^1] != EnvelopeVersion)
        {
            throw new CryptographicException(
                "The encrypted connection string envelope has an unsupported format version."
            );
        }

        ReadOnlySpan<byte> nonce = envelope.AsSpan(EnvelopeHeaderLength, EnvelopeNonceLength);
        ReadOnlySpan<byte> tag = envelope.AsSpan(
            EnvelopeHeaderLength + EnvelopeNonceLength,
            EnvelopeTagLength
        );
        ReadOnlySpan<byte> cipherText = envelope.AsSpan(EnvelopeOverhead);
        byte[] padded = new byte[cipherText.Length];

        // Throws AuthenticationTagMismatchException for any change to the header, nonce, tag or
        // cipher text, or for the wrong key. Nothing is read from the plain text before this passes.
        using (var aesGcm = new AesGcm(_envelopeKey, EnvelopeTagLength))
        {
            aesGcm.Decrypt(nonce, cipherText, tag, padded, header);
        }

        int paddingLength = padded[^1];

        if (
            paddingLength is < 1 or > BlockLength
            || padded.AsSpan(padded.Length - paddingLength).ContainsAnyExcept((byte)paddingLength)
        )
        {
            throw new CryptographicException("The encrypted connection string has invalid padding.");
        }

        return _strictUtf8.GetString(padded, 0, padded.Length - paddingLength);
    }

    private string DecryptLegacy(byte[] encrypted)
    {
        using var aes = Aes.Create();
        aes.Key = _legacyKey;

        return Encoding.UTF8.GetString(
            aes.DecryptCbc(
                encrypted.AsSpan(LegacyIvLength),
                encrypted.AsSpan(0, LegacyIvLength),
                PaddingMode.PKCS7
            )
        );
    }
}
