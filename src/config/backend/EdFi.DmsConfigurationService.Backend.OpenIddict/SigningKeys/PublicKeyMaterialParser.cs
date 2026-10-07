// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;

/// <summary>
/// The encodings a stored public key may use.
/// </summary>
public enum PublicKeyFormat
{
    SubjectPublicKeyInfo,
    Pkcs1,
    Base64Encoded,
    Unknown,
}

/// <summary>
/// Detects and imports the public key material of an <c>OpenIddictKey</c> row (moved from
/// <c>OpenIddictTokenManager</c>, spec §4.3.3). Formats are detected on every call; there is no per-key-id format cache
/// (Q7), so a key whose stored bytes change under the same key id is detected afresh.
/// </summary>
public static class PublicKeyMaterialParser
{
    /// <summary>
    /// Detects the format of <paramref name="keyData"/>, trying SubjectPublicKeyInfo, then PKCS#1, then a Base64 string
    /// holding SubjectPublicKeyInfo.
    /// </summary>
    public static PublicKeyFormat DetectFormat(byte[] keyData, ILogger logger)
    {
        try
        {
            // Try importing as SubjectPublicKeyInfo (X.509) format
            using (var rsa = RSA.Create())
            {
                try
                {
                    rsa.ImportSubjectPublicKeyInfo(keyData, out _);
                    return PublicKeyFormat.SubjectPublicKeyInfo;
                }
                catch
                {
                    // Not in SPKI format, continue to next check
                }

                // Try importing as PKCS#1 format
                try
                {
                    rsa.ImportRSAPublicKey(keyData, out _);
                    return PublicKeyFormat.Pkcs1;
                }
                catch
                {
                    // Not in PKCS#1 format, continue to next check
                }

                // Try as Base64 encoded string
                try
                {
                    var publicKeyString = System.Text.Encoding.UTF8.GetString(keyData);
                    var decodedKey = Convert.FromBase64String(publicKeyString);
                    rsa.ImportSubjectPublicKeyInfo(decodedKey, out _);
                    return PublicKeyFormat.Base64Encoded;
                }
                catch
                {
                    // Not a Base64 encoded string
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error while detecting key format");
        }

        return PublicKeyFormat.Unknown;
    }

    /// <summary>
    /// Imports <paramref name="keyData"/> in the given format and returns its public parameters.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is <see cref="PublicKeyFormat.Unknown"/>.</exception>
    /// <exception cref="CryptographicException">The data is not a valid key in that format.</exception>
    public static RSAParameters ImportPublicParameters(byte[] keyData, PublicKeyFormat format)
    {
        using var rsa = RSA.Create();

        switch (format)
        {
            case PublicKeyFormat.SubjectPublicKeyInfo:
                rsa.ImportSubjectPublicKeyInfo(keyData, out _);
                break;

            case PublicKeyFormat.Pkcs1:
                rsa.ImportRSAPublicKey(keyData, out _);
                break;

            case PublicKeyFormat.Base64Encoded:
                var publicKeyString = System.Text.Encoding.UTF8.GetString(keyData);
                var decodedKey = Convert.FromBase64String(publicKeyString);
                rsa.ImportSubjectPublicKeyInfo(decodedKey, out _);
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(format),
                    format,
                    "The key format is not importable."
                );
        }

        return rsa.ExportParameters(false);
    }
}
