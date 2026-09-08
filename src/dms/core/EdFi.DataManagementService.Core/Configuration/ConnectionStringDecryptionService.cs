// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Decrypts AES-encrypted connection strings produced by the CMS
/// <c>ConnectionStringEncryptionService</c>. The stored format is IV (16 bytes)
/// prepended to the AES-CBC ciphertext, Base64-encoded for transport.
/// </summary>
public class ConnectionStringDecryptionService(string encryptionKey) : IConnectionStringDecryptionService
{
    private readonly byte[] _key = Encoding.UTF8.GetBytes(encryptionKey.PadRight(32, '0')[..32]);

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
                "ConnectionString value from CMS is not valid Base64. "
                    + "Verify that CMS and DMS share the same EncryptionKey and that the CMS version returns encrypted connection strings.",
                ex
            );
        }

        const int ivLength = 16;
        // Must have at least ivLength + 1 bytes: full IV plus some ciphertext.
        if (encryptedBytes.Length <= ivLength)
        {
            throw new InvalidOperationException(
                $"Encrypted connection string is too short ({encryptedBytes.Length} bytes); "
                    + $"expected at least {ivLength + 1} bytes (IV + ciphertext)."
            );
        }

        using var aes = Aes.Create();
        aes.Key = _key;

        var iv = new byte[ivLength];
        var cipherText = new byte[encryptedBytes.Length - ivLength];

        Buffer.BlockCopy(encryptedBytes, 0, iv, 0, ivLength);
        Buffer.BlockCopy(encryptedBytes, ivLength, cipherText, 0, cipherText.Length);

        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        byte[] plainTextBytes;
        try
        {
            plainTextBytes = decryptor.TransformFinalBlock(cipherText, 0, cipherText.Length);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                "Failed to decrypt the connection string. "
                    + "Verify that CMS and DMS are configured with the same EncryptionKey.",
                ex
            );
        }

        return Encoding.UTF8.GetString(plainTextBytes);
    }
}
