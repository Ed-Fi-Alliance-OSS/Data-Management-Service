// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Core.Configuration;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Configuration;

[TestFixture]
public class Given_ConnectionStringDecryptionService
{
    private const string TestKey = "TestEncryptionKey123456789012345678901234567890";

    /// <summary>
    /// Produces the legacy AES-CBC form the CMS wrote before connection string envelopes, which DMS
    /// still reads.
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

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "*not valid Base64*Configuration Service DatabaseSettings:EncryptionKey*Ed-Fi API ConfigurationServiceSettings:EncryptionKey*"
            );
    }

    /// <summary>
    /// Too short for either form, and lengths that are a whole number of blocks for neither.
    /// </summary>
    [TestCase(4)]
    [TestCase(16)]
    [TestCase(17)]
    [TestCase(47)]
    public void It_should_throw_for_a_length_that_fits_neither_form(int byteCount)
    {
        var service = new ConnectionStringDecryptionService(TestKey);

        var act = () => service.DecryptFromBase64(Convert.ToBase64String(new byte[byteCount]));

        act.Should().Throw<InvalidOperationException>().WithMessage("*invalid length*");
    }

    [Test]
    public void It_should_throw_for_malformed_legacy_ciphertext()
    {
        var service = new ConnectionStringDecryptionService(TestKey);
        var malformedCiphertext = Convert.ToBase64String(new byte[32]);

        var act = () => service.DecryptFromBase64(malformedCiphertext);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "*Failed to decrypt*Configuration Service DatabaseSettings:EncryptionKey*Ed-Fi API ConfigurationServiceSettings:EncryptionKey*"
            )
            .WithInnerException<CryptographicException>();
    }
}

/// <summary>
/// The vectors the CMS tests own (EdFi.DmsConfigurationService.Backend.Tests.Unit/Fixtures), linked
/// into this project. They were generated once, independently of either reader, so passing here shows
/// DMS reads what the CMS writes rather than agreeing with itself.
/// </summary>
internal sealed record ConnectionStringEncryptionVectors(
    string KeyText,
    string PlainText,
    string EnvelopeV1,
    string LegacyCbc,
    string BlockAlignedPlainText,
    string BlockAlignedEnvelopeV1
)
{
    public static ConnectionStringEncryptionVectors Load() =>
        JsonSerializer.Deserialize<ConnectionStringEncryptionVectors>(
            File.ReadAllText(
                Path.Combine(
                    TestContext.CurrentContext.TestDirectory,
                    "Fixtures",
                    "connection-string-encryption-vectors.json"
                )
            ),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        )!;
}

[TestFixture]
public class Given_ConnectionStringDecryptionService_and_the_interoperability_vectors
{
    private ConnectionStringEncryptionVectors _vectors = null!;
    private ConnectionStringDecryptionService _service = null!;

    [SetUp]
    public void Setup()
    {
        _vectors = ConnectionStringEncryptionVectors.Load();
        _service = new ConnectionStringDecryptionService(_vectors.KeyText);
    }

    [Test]
    public void It_decrypts_the_version_1_envelope() =>
        _service.DecryptFromBase64(_vectors.EnvelopeV1).Should().Be(_vectors.PlainText);

    [Test]
    public void It_decrypts_the_block_aligned_version_1_envelope() =>
        _service
            .DecryptFromBase64(_vectors.BlockAlignedEnvelopeV1)
            .Should()
            .Be(_vectors.BlockAlignedPlainText);

    [Test]
    public void It_decrypts_the_legacy_cbc_value() =>
        _service.DecryptFromBase64(_vectors.LegacyCbc).Should().Be(_vectors.PlainText);

    [Test]
    public void It_decrypts_both_forms_under_a_suffixed_key()
    {
        var suffixed = new ConnectionStringDecryptionService(_vectors.KeyText + "_ignored_suffix");

        suffixed.DecryptFromBase64(_vectors.EnvelopeV1).Should().Be(_vectors.PlainText);
        suffixed.DecryptFromBase64(_vectors.LegacyCbc).Should().Be(_vectors.PlainText);
    }

    [Test]
    public void It_rejects_the_envelope_under_a_different_key()
    {
        var act = () =>
            new ConnectionStringDecryptionService("Rk3pQ8sT2vW9xZ4bC6dE1gH5jL7mN0rY").DecryptFromBase64(
                _vectors.EnvelopeV1
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*failed authentication*")
            .WithInnerExceptionExactly<AuthenticationTagMismatchException>();
    }

    [Test]
    public void It_rejects_the_legacy_value_under_a_different_key()
    {
        var act = () =>
            new ConnectionStringDecryptionService("Rk3pQ8sT2vW9xZ4bC6dE1gH5jL7mN0rY").DecryptFromBase64(
                _vectors.LegacyCbc
            );

        act.Should().Throw<InvalidOperationException>().WithMessage("*Failed to decrypt*");
    }
}

/// <summary>
/// Header, body and length corruptions of a valid envelope: each has to be rejected, never
/// decrypted under the legacy path and never returned as different text. Substitution with a
/// legacy-shaped value is outside what the format can detect.
/// </summary>
[TestFixture]
public class Given_ConnectionStringDecryptionService_and_a_corrupted_envelope
{
    private const int VersionOffset = 16;
    private const int NonceOffset = 17;
    private const int TagOffset = 29;
    private const int CipherTextOffset = 45;

    private ConnectionStringDecryptionService _service = null!;
    private byte[] _envelope = null!;

    [SetUp]
    public void Setup()
    {
        var vectors = ConnectionStringEncryptionVectors.Load();
        _service = new ConnectionStringDecryptionService(vectors.KeyText);
        _envelope = Convert.FromBase64String(vectors.EnvelopeV1);
    }

    private string WithByteFlipped(int index)
    {
        byte[] corrupted = (byte[])_envelope.Clone();
        corrupted[index] ^= 0x01;
        return Convert.ToBase64String(corrupted);
    }

    /// <summary>
    /// The altered marker no longer identifies the envelope, but the length class still does, so the
    /// value stays on the envelope path instead of reaching CBC.
    /// </summary>
    [Test]
    public void It_rejects_a_change_to_any_marker_byte([Range(0, 15)] int index)
    {
        var act = () => _service.DecryptFromBase64(WithByteFlipped(index));

        act.Should().Throw<InvalidOperationException>().WithMessage("*envelope is not recognized*");
    }

    [TestCase((byte)0x00)]
    [TestCase((byte)0x02)]
    public void It_rejects_an_unsupported_version(byte version)
    {
        byte[] corrupted = (byte[])_envelope.Clone();
        corrupted[VersionOffset] = version;

        var act = () => _service.DecryptFromBase64(Convert.ToBase64String(corrupted));

        act.Should().Throw<InvalidOperationException>().WithMessage("*unsupported format version*");
    }

    [TestCase(NonceOffset, TestName = "It_fails_authentication_for_a_changed_nonce")]
    [TestCase(TagOffset, TestName = "It_fails_authentication_for_a_changed_tag")]
    [TestCase(CipherTextOffset, TestName = "It_fails_authentication_for_changed_cipher_text")]
    public void It_fails_authentication(int index)
    {
        var act = () => _service.DecryptFromBase64(WithByteFlipped(index));

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*failed authentication*")
            .WithInnerExceptionExactly<AuthenticationTagMismatchException>();
    }

    [Test]
    public void It_rejects_a_truncated_envelope()
    {
        var act = () => _service.DecryptFromBase64(Convert.ToBase64String(_envelope[..^1]));

        act.Should().Throw<InvalidOperationException>().WithMessage("*envelope is malformed*");
    }

    [Test]
    public void It_rejects_an_envelope_without_cipher_text()
    {
        var act = () => _service.DecryptFromBase64(Convert.ToBase64String(_envelope[..CipherTextOffset]));

        act.Should().Throw<InvalidOperationException>().WithMessage("*envelope is malformed*");
    }
}
