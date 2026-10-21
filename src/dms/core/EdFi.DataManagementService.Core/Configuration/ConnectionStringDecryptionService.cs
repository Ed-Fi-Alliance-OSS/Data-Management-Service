// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Decrypts connection strings produced by the Configuration Service <c>ConnectionStringEncryptionService</c>,
/// Base64-encoded for transport. Two stored forms exist, and this must stay in step with the Configuration
/// Service <c>ConnectionStringCipherText</c> that defines them:
/// <list type="bullet">
/// <item>Envelope (written by the current Configuration Service): marker "EdFi.CS.Envelope" (16) ||
/// version 1 (1) || nonce (12) || tag (16) || AES-256-GCM cipher text of the PKCS7-padded UTF-8 text, with
/// marker and version as associated data. The key is HKDF-SHA256 over the key material below. Every
/// envelope is 13 bytes past a 16-byte block multiple.</item>
/// <item>Legacy: IV (16) || AES-CBC/PKCS7 cipher text, keyed by the key material directly; always a
/// whole number of blocks.</item>
/// </list>
/// The key material is the first 32 characters of the configured key, padded with '0'.
/// </summary>
public class ConnectionStringDecryptionService(string encryptionKey) : IConnectionStringDecryptionService
{
    private const int BlockLength = 16;
    private const int EnvelopeHeaderLength = 17;
    private const int EnvelopeNonceLength = 12;
    private const int EnvelopeTagLength = 16;
    private const int EnvelopeOverhead = EnvelopeHeaderLength + EnvelopeNonceLength + EnvelopeTagLength;
    private const int MinimumEnvelopeLength = EnvelopeOverhead + BlockLength;
    private const byte EnvelopeVersion = 1;
    private const int LegacyIvLength = 16;
    private const int MinimumLegacyLength = LegacyIvLength + BlockLength;

    private static ReadOnlySpan<byte> EnvelopeMarker => "EdFi.CS.Envelope"u8;

    private static readonly byte[] _envelopeKeyInfo = "EdFi.ConnectionString.AES-256-GCM.v1"u8.ToArray();

    private static readonly UTF8Encoding _strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    private readonly byte[] _legacyKey = KeyMaterial(encryptionKey);

    private readonly byte[] _envelopeKey = HKDF.DeriveKey(
        HashAlgorithmName.SHA256,
        KeyMaterial(encryptionKey),
        outputLength: 32,
        salt: [],
        info: _envelopeKeyInfo
    );

    private static byte[] KeyMaterial(string encryptionKey) =>
        Encoding.UTF8.GetBytes(encryptionKey.PadRight(32, '0')[..32]);

    public string? DecryptFromBase64(string? base64EncodedCipherText)
    {
        if (string.IsNullOrEmpty(base64EncodedCipherText))
        {
            return null;
        }

        byte[] encryptedBytes;
        try
        {
            encryptedBytes = Convert.FromBase64String(base64EncodedCipherText);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "ConnectionString value from the Configuration Service is not valid Base64. "
                    + "Verify that the Configuration Service DatabaseSettings:EncryptionKey and the Ed-Fi API "
                    + "ConfigurationServiceSettings:EncryptionKey have the same value (the provided Docker Compose "
                    + "deployments set both from CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY), and that the "
                    + "Configuration Service version returns encrypted connection strings.",
                ex
            );
        }

        // Envelope first, and never a legacy attempt after it: a value with an altered marker keeps
        // the envelope length class, so it is rejected here rather than decrypted as legacy.
        if (
            encryptedBytes.Length % BlockLength == EnvelopeOverhead % BlockLength
            || encryptedBytes.AsSpan().StartsWith(EnvelopeMarker)
        )
        {
            return DecryptEnvelope(encryptedBytes);
        }

        if (encryptedBytes.Length >= MinimumLegacyLength && encryptedBytes.Length % BlockLength == 0)
        {
            return DecryptLegacy(encryptedBytes);
        }

        throw new InvalidOperationException(
            $"Encrypted connection string has an invalid length ({encryptedBytes.Length} bytes); "
                + "it is neither a Configuration Service connection string envelope nor legacy AES-CBC cipher text."
        );
    }

    private string DecryptEnvelope(byte[] envelope)
    {
        if (
            envelope.Length < MinimumEnvelopeLength
            || envelope.Length % BlockLength != EnvelopeOverhead % BlockLength
        )
        {
            throw new InvalidOperationException(
                $"Encrypted connection string envelope is malformed ({envelope.Length} bytes)."
            );
        }

        ReadOnlySpan<byte> header = envelope.AsSpan(0, EnvelopeHeaderLength);

        if (!header.StartsWith(EnvelopeMarker))
        {
            throw new InvalidOperationException("Encrypted connection string envelope is not recognized.");
        }

        if (header[^1] != EnvelopeVersion)
        {
            throw new InvalidOperationException(
                $"Encrypted connection string envelope has unsupported format version {header[^1]}. "
                    + "Verify that this Ed-Fi API version supports the connection strings the Configuration Service writes."
            );
        }

        ReadOnlySpan<byte> nonce = envelope.AsSpan(EnvelopeHeaderLength, EnvelopeNonceLength);
        ReadOnlySpan<byte> tag = envelope.AsSpan(
            EnvelopeHeaderLength + EnvelopeNonceLength,
            EnvelopeTagLength
        );
        ReadOnlySpan<byte> cipherText = envelope.AsSpan(EnvelopeOverhead);
        byte[] padded = new byte[cipherText.Length];

        try
        {
            // Rejects any change to the header, nonce, tag or cipher text, or the wrong key. Nothing
            // is read from the plain text before this passes.
            using var aesGcm = new AesGcm(_envelopeKey, EnvelopeTagLength);
            aesGcm.Decrypt(nonce, cipherText, tag, padded, header);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new InvalidOperationException(
                "Failed to decrypt the connection string: it failed authentication, so either the stored "
                    + "value was altered or it was encrypted with a different key. "
                    + "Verify that the Configuration Service DatabaseSettings:EncryptionKey and the Ed-Fi API "
                    + "ConfigurationServiceSettings:EncryptionKey have the same value (the provided Docker Compose "
                    + "deployments set both from CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY).",
                ex
            );
        }

        int paddingLength = padded[^1];

        if (
            paddingLength is < 1 or > BlockLength
            || padded.AsSpan(padded.Length - paddingLength).ContainsAnyExcept((byte)paddingLength)
        )
        {
            throw new InvalidOperationException("Decrypted connection string has invalid padding.");
        }

        try
        {
            return _strictUtf8.GetString(padded, 0, padded.Length - paddingLength);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidOperationException("Decrypted connection string is not valid UTF-8.", ex);
        }
    }

    private string DecryptLegacy(byte[] encrypted)
    {
        using var aes = Aes.Create();
        aes.Key = _legacyKey;

        byte[] plainTextBytes;
        try
        {
            plainTextBytes = aes.DecryptCbc(
                encrypted.AsSpan(LegacyIvLength),
                encrypted.AsSpan(0, LegacyIvLength),
                PaddingMode.PKCS7
            );
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                "Failed to decrypt the connection string. "
                    + "Verify that the Configuration Service DatabaseSettings:EncryptionKey and the Ed-Fi API "
                    + "ConfigurationServiceSettings:EncryptionKey have the same value (the provided Docker Compose "
                    + "deployments set both from CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY).",
                ex
            );
        }

        return Encoding.UTF8.GetString(plainTextBytes);
    }
}
