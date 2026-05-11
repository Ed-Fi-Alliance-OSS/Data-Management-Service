// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;
using EdFi.DataManagementService.Core.Configuration;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Configuration;

[TestFixture]
public class Given_ConnectionStringDecryptionService
{
    private const string TestKey = "TestEncryptionKey123456789012345678901234567890";

    /// <summary>
    /// Mirrors the CMS ConnectionStringEncryptionService.Encrypt() method exactly.
    /// </summary>
    private static string EncryptToBase64(string plainText, string encryptionKey)
    {
        byte[] keyBytes = Encoding.UTF8.GetBytes(encryptionKey.PadRight(32, '0')[..32]);
        using var aes = Aes.Create();
        aes.Key = keyBytes;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
        byte[] cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        byte[] result = new byte[aes.IV.Length + cipherBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(cipherBytes, 0, result, aes.IV.Length, cipherBytes.Length);

        return Convert.ToBase64String(result);
    }

    [Test]
    public void It_should_decrypt_a_Base64_encrypted_connection_string()
    {
        var service = new ConnectionStringDecryptionService(TestKey);
        var plainText = "host=localhost;port=5432;database=edfi;";
        var encrypted = EncryptToBase64(plainText, TestKey);

        service.DecryptFromBase64(encrypted).Should().Be(plainText);
    }

    [Test]
    public void It_should_return_null_for_null_input()
    {
        var service = new ConnectionStringDecryptionService(TestKey);
        service.DecryptFromBase64(null).Should().BeNull();
    }

    [Test]
    public void It_should_return_null_for_empty_string_input()
    {
        var service = new ConnectionStringDecryptionService(TestKey);
        service.DecryptFromBase64(string.Empty).Should().BeNull();
    }

    [Test]
    public void It_should_pad_short_key_to_32_bytes()
    {
        var service = new ConnectionStringDecryptionService("ShortKey");
        var plainText = "Server=test;";
        var encrypted = EncryptToBase64(plainText, "ShortKey");

        service.DecryptFromBase64(encrypted).Should().Be(plainText);
    }

    [Test]
    public void It_should_truncate_long_key_to_32_bytes()
    {
        var service = new ConnectionStringDecryptionService(
            "ThisIsAVeryLongKeyThatExceedsThirtyTwoCharactersInLength"
        );
        var plainText = "Server=test;";
        var encrypted = EncryptToBase64(
            plainText,
            "ThisIsAVeryLongKeyThatExceedsThirtyTwoCharactersInLength"
        );

        service.DecryptFromBase64(encrypted).Should().Be(plainText);
    }

    [Test]
    public void It_should_throw_for_invalid_Base64_input()
    {
        var service = new ConnectionStringDecryptionService(TestKey);

        var act = () => service.DecryptFromBase64("not-valid-base64!!!");

        act.Should().Throw<InvalidOperationException>().WithMessage("*not valid Base64*");
    }

    [Test]
    public void It_should_throw_for_input_too_short_to_contain_IV()
    {
        var service = new ConnectionStringDecryptionService(TestKey);
        // Valid Base64 but only 4 bytes after decode — shorter than 16-byte IV
        var tooShort = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });

        var act = () => service.DecryptFromBase64(tooShort);

        act.Should().Throw<InvalidOperationException>().WithMessage("*too short*");
    }

    [Test]
    public void It_should_throw_for_ciphertext_encrypted_with_a_different_key()
    {
        var service = new ConnectionStringDecryptionService(TestKey);
        var encryptedWithWrongKey = EncryptToBase64("Server=test;", "WrongKey________________________");

        var act = () => service.DecryptFromBase64(encryptedWithWrongKey);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Failed to decrypt*");
    }
}
