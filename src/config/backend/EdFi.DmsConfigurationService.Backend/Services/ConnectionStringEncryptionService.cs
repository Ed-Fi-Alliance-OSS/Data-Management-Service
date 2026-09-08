// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Services;

public class ConnectionStringEncryptionService(IOptions<DatabaseOptions> databaseOptions)
    : IConnectionStringEncryptionService
{
    private readonly byte[] _key = Encoding.UTF8.GetBytes(
        databaseOptions.Value.EncryptionKey.PadRight(32, '0')[..32]
    );

    public byte[]? Encrypt(string? connectionString)
    {
        if (string.IsNullOrEmpty(connectionString))
        {
            return null;
        }

        using var aes = Aes.Create();
        aes.Key = _key;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plainTextBytes = Encoding.UTF8.GetBytes(connectionString);
        var cipherTextBytes = encryptor.TransformFinalBlock(plainTextBytes, 0, plainTextBytes.Length);

        var result = new byte[aes.IV.Length + cipherTextBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(cipherTextBytes, 0, result, aes.IV.Length, cipherTextBytes.Length);

        return result;
    }

    public string? Decrypt(byte[]? encryptedConnectionString)
    {
        if (encryptedConnectionString == null || encryptedConnectionString.Length == 0)
        {
            return null;
        }

        using var aes = Aes.Create();
        aes.Key = _key;

        var iv = new byte[16];
        var cipherText = new byte[encryptedConnectionString.Length - 16];

        Buffer.BlockCopy(encryptedConnectionString, 0, iv, 0, 16);
        Buffer.BlockCopy(encryptedConnectionString, 16, cipherText, 0, cipherText.Length);

        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        var plainTextBytes = decryptor.TransformFinalBlock(cipherText, 0, cipherText.Length);

        return Encoding.UTF8.GetString(plainTextBytes);
    }
}
