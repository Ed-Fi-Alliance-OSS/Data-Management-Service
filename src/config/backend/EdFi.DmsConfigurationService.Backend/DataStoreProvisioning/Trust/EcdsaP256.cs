// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Trust;

/// <summary>
/// Proof that an ECDSA key is on NIST P-256, mirroring the delivered <c>Assert-EcdsaP256Key</c>: the
/// <c>ECDSA_P256_SHA256</c> label is a claim, and without this check a key on any other curve would sign and
/// verify under it. The named-curve OID <c>1.2.840.10045.3.1.7</c> is the primary evidence; the platform friendly
/// names are accepted only when the provider reports no OID value; a 256-bit key size is required in every case.
/// </summary>
internal static class EcdsaP256
{
    public const string CurveOid = "1.2.840.10045.3.1.7";

    private static readonly string[] _friendlyNames = ["nistP256", "ECDSA_P256", "prime256v1", "secp256r1"];

    /// <summary>
    /// Imports a DER SubjectPublicKeyInfo and proves the key is on P-256; the caller disposes the result. Throws
    /// <see cref="TemplateTrustPolicyException"/> naming <paramref name="keyDescription"/> (position text only, never
    /// a value) when the bytes do not import as an EC public key or the curve is not P-256.
    /// </summary>
    public static ECDsa ImportPublicKey(byte[] subjectPublicKeyInfo, string keyDescription)
    {
        ArgumentNullException.ThrowIfNull(subjectPublicKeyInfo);
        ECDsa ecdsa = ECDsa.Create();
        bool proven = false;
        try
        {
            try
            {
                ecdsa.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out _);
            }
            catch (CryptographicException)
            {
                throw new TemplateTrustPolicyException(
                    $"{keyDescription} is not an importable SubjectPublicKeyInfo."
                );
            }

            if (!IsP256(ecdsa))
            {
                throw new TemplateTrustPolicyException(
                    $"{keyDescription} is not a NIST P-256 ECDSA key; the {TemplateTrustPolicy.EcdsaP256Sha256Algorithm} contract requires curve {CurveOid}."
                );
            }

            proven = true;
            return ecdsa;
        }
        finally
        {
            if (!proven)
            {
                ecdsa.Dispose();
            }
        }
    }

    public static bool IsP256(ECDsa ecdsa)
    {
        ArgumentNullException.ThrowIfNull(ecdsa);
        ECCurve curve = ecdsa.ExportParameters(false).Curve;
        string oidValue = curve.Oid?.Value ?? string.Empty;
        string friendlyName = curve.Oid?.FriendlyName ?? string.Empty;
        bool hasP256Oid = oidValue == CurveOid;
        bool hasP256FriendlyName =
            oidValue.Length == 0 && Array.Exists(_friendlyNames, name => name == friendlyName);
        return ecdsa.KeySize == 256 && (hasP256Oid || hasP256FriendlyName);
    }
}
