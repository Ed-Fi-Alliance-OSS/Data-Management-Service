// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.TestHelpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Services;

[TestFixture]
public class ConnectionStringEncryptionServiceTests
{
    /// <summary>
    /// ASCII "EdFi.CS.Envelope" followed by version 1, written out rather than taken from the
    /// production constants so a change to the format fails here.
    /// </summary>
    private static readonly byte[] ExpectedEnvelopeHeader =
    [
        0x45,
        0x64,
        0x46,
        0x69,
        0x2E,
        0x43,
        0x53,
        0x2E,
        0x45,
        0x6E,
        0x76,
        0x65,
        0x6C,
        0x6F,
        0x70,
        0x65,
        0x01,
    ];

    private const int NonceOffset = 17;
    private const int NonceLength = 12;
    private const int TagOffset = 29;
    private const int CipherTextOffset = 45;

    private ConnectionStringEncryptionService _service = null!;
    private readonly IOptions<DatabaseOptions> _databaseOptions = Options.Create(
        new DatabaseOptions
        {
            DatabaseConnection = "Server=test;",
            EncryptionKey = "TestEncryptionKey123456789012345678901234567890",
        }
    );

    [SetUp]
    public void Setup()
    {
        _service = new ConnectionStringEncryptionService(_databaseOptions);
    }

    private static ConnectionStringEncryptionService ServiceWithKey(
        string encryptionKey,
        bool allowLegacyConnectionStrings = true
    ) =>
        new(
            Options.Create(
                new DatabaseOptions
                {
                    DatabaseConnection = "Server=test;",
                    EncryptionKey = encryptionKey,
                    AllowLegacyConnectionStrings = allowLegacyConnectionStrings,
                }
            )
        );

    private const string LegacyKey = "TestEncryptionKey123456789012345678901234567890";
    private const string LegacyPlainText = "host=dbhost;port=5432;database=edfi;";
    private const string RedirectedPlainText = "host=evil.exampl=5432;database=edfi;";

    /// <summary>
    /// A legacy value with a fixed IV, so a test can change its first plain text block through the IV
    /// alone, as someone who can write the data store tables could.
    /// </summary>
    private static byte[] EncryptLegacy(string plainText)
    {
        using var aes = Aes.Create();
        aes.Key = System.Text.Encoding.UTF8.GetBytes(LegacyKey[..32]);
        byte[] iv = new byte[16];
        return
        [
            .. iv,
            .. aes.EncryptCbc(System.Text.Encoding.UTF8.GetBytes(plainText), iv, PaddingMode.PKCS7),
        ];
    }

    /// <summary>
    /// <see cref="LegacyPlainText"/> with its IV changed so that it decrypts, with valid padding, to
    /// <see cref="RedirectedPlainText"/>: "dbhost;port" becomes "evil.exampl".
    /// </summary>
    private static byte[] LegacyWithRedirectedHost()
    {
        byte[] value = EncryptLegacy(LegacyPlainText);
        byte[] original = System.Text.Encoding.UTF8.GetBytes(LegacyPlainText[..16]);
        byte[] redirected = System.Text.Encoding.UTF8.GetBytes(RedirectedPlainText[..16]);

        for (int index = 0; index < 16; index++)
        {
            value[index] ^= (byte)(original[index] ^ redirected[index]);
        }

        return value;
    }

    [TestFixture]
    public class Given_valid_connection_string : ConnectionStringEncryptionServiceTests
    {
        private const string TestConnectionString =
            "Server=localhost;Database=TestDb;User Id=user;Password=pass;";

        [Test]
        public void It_should_encrypt_and_decrypt_successfully()
        {
            var encrypted = _service.Encrypt(TestConnectionString);
            encrypted.Should().NotBeNull();
            encrypted!.Length.Should().BeGreaterThan(0);

            var decrypted = _service.Decrypt(encrypted);
            decrypted.Should().Be(TestConnectionString);
        }

        [Test]
        public void It_writes_the_version_1_envelope_header()
        {
            byte[] encrypted = _service.Encrypt(TestConnectionString)!;

            encrypted[..ExpectedEnvelopeHeader.Length].Should().Equal(ExpectedEnvelopeHeader);
        }

        /// <summary>
        /// 60 bytes of UTF-8 pad to 64, behind 45 bytes of header, nonce and tag.
        /// </summary>
        [Test]
        public void It_pads_the_cipher_text_to_whole_blocks_after_the_overhead() =>
            _service.Encrypt(TestConnectionString)!.Length.Should().Be(CipherTextOffset + 64);

        [Test]
        public void It_uses_a_fresh_nonce_for_each_encryption()
        {
            byte[] encrypted1 = _service.Encrypt(TestConnectionString)!;
            byte[] encrypted2 = _service.Encrypt(TestConnectionString)!;

            // The header pins the nonce to bytes 17-28 of an envelope. Without it, a random legacy
            // IV would also vary those bytes and this test would pass against AES-CBC.
            encrypted1[..ExpectedEnvelopeHeader.Length].Should().Equal(ExpectedEnvelopeHeader);
            encrypted2[..ExpectedEnvelopeHeader.Length].Should().Equal(ExpectedEnvelopeHeader);

            encrypted1
                .AsSpan(NonceOffset, NonceLength)
                .ToArray()
                .Should()
                .NotEqual(encrypted2.AsSpan(NonceOffset, NonceLength).ToArray());

            _service.Decrypt(encrypted1).Should().Be(TestConnectionString);
            _service.Decrypt(encrypted2).Should().Be(TestConnectionString);
        }
    }

    [TestFixture]
    public class Given_null_connection_string : ConnectionStringEncryptionServiceTests
    {
        [Test]
        public void It_should_return_null_for_encryption()
        {
            var result = _service.Encrypt(null);
            result.Should().BeNull();
        }

        [Test]
        public void It_should_return_null_for_decryption()
        {
            var result = _service.Decrypt(null);
            result.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_empty_connection_string : ConnectionStringEncryptionServiceTests
    {
        [Test]
        public void It_should_return_null_for_encryption()
        {
            var result = _service.Encrypt("");
            result.Should().BeNull();
        }

        [Test]
        public void It_should_return_null_for_decryption()
        {
            var result = _service.Decrypt([]);
            result.Should().BeNull();
        }
    }

    /// <summary>
    /// Only the first 32 characters of the configured text reach either key, so two values sharing a
    /// 32-character prefix are the same key spelled differently. DatabaseOptionsValidator rejects the
    /// former shipped default by comparing that prefix rather than the whole string, which is only sound
    /// while this holds, so it is pinned here next to the derivation that owns it.
    /// </summary>
    [TestFixture]
    public class Given_two_encryption_keys_sharing_a_32_character_prefix
    {
        private const string SignificantPrefix = "Fk3pQ8sT2vW9xZ4bC6dE1gH5jL7mN0rY";

        private const string TestConnectionString = "Server=localhost;Database=TestDb;";

        [Test]
        public void It_is_a_32_character_prefix() => SignificantPrefix.Length.Should().Be(32);

        [Test]
        public void It_decrypts_cipher_text_written_under_the_bare_prefix()
        {
            var encrypted = ServiceWithKey(SignificantPrefix).Encrypt(TestConnectionString);

            ServiceWithKey(SignificantPrefix + "_ignored_suffix")
                .Decrypt(encrypted)
                .Should()
                .Be(TestConnectionString);
        }

        [Test]
        public void It_decrypts_cipher_text_written_under_the_suffixed_key()
        {
            var encrypted = ServiceWithKey(SignificantPrefix + "_a_different_suffix")
                .Encrypt(TestConnectionString);

            ServiceWithKey(SignificantPrefix).Decrypt(encrypted).Should().Be(TestConnectionString);
        }
    }

    [TestFixture]
    public class Given_round_trip_encryption : ConnectionStringEncryptionServiceTests
    {
        [TestCase("Simple")]
        [TestCase("Server=localhost;Database=TestDb;")]
        [TestCase("Server=localhost;Database=TestDb;User Id=user;Password=complex$Pass@word123!;")]
        [TestCase(
            "Data Source=server;Initial Catalog=database;Integrated Security=True;MultipleActiveResultSets=True;"
        )]
        [TestCase("host=localhost;password=Pässwörd€😀;database=edfi")]
        public void It_should_correctly_round_trip_various_connection_strings(string connectionString)
        {
            var encrypted = _service.Encrypt(connectionString);
            var decrypted = _service.Decrypt(encrypted);

            decrypted.Should().Be(connectionString);
        }

        /// <summary>
        /// Either side of each block boundary: a whole block of plain text takes a whole block of
        /// padding, so 16 bytes encrypt to two blocks and 15 to one.
        /// </summary>
        [TestCase(1, 1)]
        [TestCase(15, 1)]
        [TestCase(16, 2)]
        [TestCase(17, 2)]
        [TestCase(31, 2)]
        [TestCase(32, 3)]
        [TestCase(33, 3)]
        public void It_round_trips_at_block_boundaries(int plainTextLength, int expectedBlocks)
        {
            string connectionString = new('a', plainTextLength);

            byte[] encrypted = _service.Encrypt(connectionString)!;

            encrypted.Length.Should().Be(CipherTextOffset + (16 * expectedBlocks));
            _service.Decrypt(encrypted).Should().Be(connectionString);
        }

        /// <summary>
        /// "€" is three UTF-8 bytes, so 5 of them are 15 bytes and 6 are 18: the padding has to be
        /// measured in bytes, not characters.
        /// </summary>
        [TestCase(5, 1)]
        [TestCase(6, 2)]
        public void It_pads_by_utf8_byte_count(int euroSigns, int expectedBlocks)
        {
            string connectionString = new('€', euroSigns);

            byte[] encrypted = _service.Encrypt(connectionString)!;

            encrypted.Length.Should().Be(CipherTextOffset + (16 * expectedBlocks));
            _service.Decrypt(encrypted).Should().Be(connectionString);
        }
    }

    /// <summary>
    /// The shared vectors: generated once outside this service, so passing shows interoperability with
    /// the format rather than agreement with itself.
    /// </summary>
    [TestFixture]
    public class Given_the_interoperability_vectors
    {
        private ConnectionStringEncryptionVectors _vectors = null!;
        private ConnectionStringEncryptionService _service = null!;

        [SetUp]
        public void Setup()
        {
            _vectors = ConnectionStringEncryptionVectors.Load();
            _service = ServiceWithKey(_vectors.KeyText);
        }

        [Test]
        public void It_decrypts_the_version_1_envelope() =>
            _service.Decrypt(Convert.FromBase64String(_vectors.EnvelopeV1)).Should().Be(_vectors.PlainText);

        [Test]
        public void It_decrypts_the_block_aligned_version_1_envelope() =>
            _service
                .Decrypt(Convert.FromBase64String(_vectors.BlockAlignedEnvelopeV1))
                .Should()
                .Be(_vectors.BlockAlignedPlainText);

        [Test]
        public void It_decrypts_the_legacy_cbc_value() =>
            _service.Decrypt(Convert.FromBase64String(_vectors.LegacyCbc)).Should().Be(_vectors.PlainText);

        [Test]
        public void It_decrypts_both_forms_under_a_suffixed_key()
        {
            var suffixed = ServiceWithKey(_vectors.KeyText + "_ignored_suffix");

            suffixed.Decrypt(Convert.FromBase64String(_vectors.EnvelopeV1)).Should().Be(_vectors.PlainText);
            suffixed.Decrypt(Convert.FromBase64String(_vectors.LegacyCbc)).Should().Be(_vectors.PlainText);
        }

        /// <summary>
        /// The upgrade path: whatever a legacy value decrypts to is written back as an envelope.
        /// </summary>
        [Test]
        public void It_rewrites_a_legacy_value_as_an_envelope()
        {
            string plainText = _service.Decrypt(Convert.FromBase64String(_vectors.LegacyCbc))!;

            byte[] rewritten = _service.Encrypt(plainText)!;

            rewritten[..ExpectedEnvelopeHeader.Length].Should().Equal(ExpectedEnvelopeHeader);
            _service.Decrypt(rewritten).Should().Be(_vectors.PlainText);
        }
    }

    /// <summary>
    /// Header, body and length corruptions of a valid envelope: each has to be rejected, never
    /// decrypted under the legacy path and never returned as different text. Substitution with a
    /// legacy-shaped value is outside what the format can detect.
    /// </summary>
    [TestFixture]
    public class Given_a_corrupted_envelope
    {
        private ConnectionStringEncryptionVectors _vectors = null!;
        private ConnectionStringEncryptionService _service = null!;
        private byte[] _envelope = null!;

        [SetUp]
        public void Setup()
        {
            _vectors = ConnectionStringEncryptionVectors.Load();
            _service = ServiceWithKey(_vectors.KeyText);
            _envelope = Convert.FromBase64String(_vectors.EnvelopeV1);
        }

        private byte[] WithByteFlipped(int index)
        {
            byte[] corrupted = (byte[])_envelope.Clone();
            corrupted[index] ^= 0x01;
            return corrupted;
        }

        /// <summary>
        /// The altered marker no longer identifies the envelope, but the length class still does, so
        /// the value stays on the envelope path instead of reaching CBC.
        /// </summary>
        [Test]
        public void It_rejects_a_change_to_any_marker_byte([Range(0, 15)] int index)
        {
            var act = () => _service.Decrypt(WithByteFlipped(index));

            act.Should().Throw<CryptographicException>().WithMessage("*envelope is not recognized*");
        }

        [TestCase((byte)0x00)]
        [TestCase((byte)0x02)]
        public void It_rejects_an_unsupported_version(byte version)
        {
            byte[] corrupted = (byte[])_envelope.Clone();
            corrupted[16] = version;

            var act = () => _service.Decrypt(corrupted);

            act.Should().Throw<CryptographicException>().WithMessage("*unsupported format version*");
        }

        [TestCase(NonceOffset, TestName = "It_fails_authentication_for_a_changed_nonce")]
        [TestCase(TagOffset, TestName = "It_fails_authentication_for_a_changed_tag")]
        [TestCase(CipherTextOffset, TestName = "It_fails_authentication_for_changed_cipher_text")]
        public void It_fails_authentication(int index)
        {
            var act = () => _service.Decrypt(WithByteFlipped(index));

            act.Should().ThrowExactly<AuthenticationTagMismatchException>();
        }

        [Test]
        public void It_fails_authentication_for_an_appended_block()
        {
            var act = () => _service.Decrypt([.. _envelope, .. new byte[16]]);

            act.Should().ThrowExactly<AuthenticationTagMismatchException>();
        }

        [Test]
        public void It_rejects_a_truncated_envelope()
        {
            var act = () => _service.Decrypt(_envelope[..^1]);

            act.Should().Throw<CryptographicException>().WithMessage("*envelope is malformed*");
        }

        [Test]
        public void It_rejects_an_envelope_without_cipher_text()
        {
            var act = () => _service.Decrypt(_envelope[..CipherTextOffset]);

            act.Should().Throw<CryptographicException>().WithMessage("*envelope is malformed*");
        }

        [Test]
        public void It_fails_authentication_under_a_different_key()
        {
            var act = () => ServiceWithKey("Rk3pQ8sT2vW9xZ4bC6dE1gH5jL7mN0rY").Decrypt(_envelope);

            act.Should().ThrowExactly<AuthenticationTagMismatchException>();
        }
    }

    [TestFixture]
    public class Given_a_value_in_neither_form : ConnectionStringEncryptionServiceTests
    {
        [TestCase(16)]
        [TestCase(33)]
        [TestCase(47)]
        public void It_rejects_the_length(int byteCount)
        {
            var act = () => _service.Decrypt(new byte[byteCount]);

            act.Should().Throw<CryptographicException>().WithMessage("*invalid length*");
        }
    }

    [TestFixture]
    public class Given_a_legacy_value_under_a_different_key
    {
        /// <summary>
        /// CBC has no integrity check, so a wrong key is caught only when the padding comes out wrong.
        /// For this fixed vector and key it does, every time.
        /// </summary>
        [Test]
        public void It_is_rejected()
        {
            var vectors = ConnectionStringEncryptionVectors.Load();

            var act = () =>
                ServiceWithKey("Rk3pQ8sT2vW9xZ4bC6dE1gH5jL7mN0rY")
                    .Decrypt(Convert.FromBase64String(vectors.LegacyCbc));

            act.Should().Throw<CryptographicException>();
        }
    }

    [TestFixture]
    public class Given_legacy_connection_strings_allowed
    {
        /// <summary>
        /// The exposure that disallowing legacy values closes: the IV change is not detected.
        /// </summary>
        [Test]
        public void It_returns_the_redirected_host_of_a_legacy_value_changed_through_its_IV() =>
            ServiceWithKey(LegacyKey).Decrypt(LegacyWithRedirectedHost()).Should().Be(RedirectedPlainText);
    }

    [TestFixture]
    public class Given_legacy_connection_strings_disallowed
    {
        private ConnectionStringEncryptionVectors _vectors = null!;
        private ConnectionStringEncryptionService _service = null!;

        [SetUp]
        public void Setup()
        {
            _vectors = ConnectionStringEncryptionVectors.Load();
            _service = ServiceWithKey(_vectors.KeyText, allowLegacyConnectionStrings: false);
        }

        private static void ShouldBeRejectedAsLegacy(Func<string?> act) =>
            act.Should()
                .ThrowExactly<LegacyConnectionStringRejectedException>()
                .WithMessage("*legacy AES-CBC format*DatabaseSettings:AllowLegacyConnectionStrings is false*")
                .Which.InnerException.Should()
                .BeNull();

        [Test]
        public void It_decrypts_the_version_1_envelope() =>
            _service.Decrypt(Convert.FromBase64String(_vectors.EnvelopeV1)).Should().Be(_vectors.PlainText);

        [Test]
        public void It_still_writes_an_envelope()
        {
            byte[] written = _service.Encrypt(_vectors.PlainText)!;

            written[..ExpectedEnvelopeHeader.Length].Should().Equal(ExpectedEnvelopeHeader);
            _service.Decrypt(written).Should().Be(_vectors.PlainText);
        }

        [Test]
        public void It_rejects_the_legacy_vector() =>
            ShouldBeRejectedAsLegacy(() => _service.Decrypt(Convert.FromBase64String(_vectors.LegacyCbc)));

        [Test]
        public void It_rejects_a_legacy_value_changed_through_its_IV() =>
            ShouldBeRejectedAsLegacy(() =>
                ServiceWithKey(LegacyKey, allowLegacyConnectionStrings: false)
                    .Decrypt(LegacyWithRedirectedHost())
            );

        /// <summary>
        /// Rejected the same way as a valid legacy value, so padding validity cannot be probed.
        /// </summary>
        [Test]
        public void It_rejects_a_legacy_length_value_with_invalid_padding_the_same_way() =>
            ShouldBeRejectedAsLegacy(() => _service.Decrypt(new byte[32]));

        [Test]
        public void It_still_rejects_a_tampered_envelope_by_authentication()
        {
            byte[] tampered = Convert.FromBase64String(_vectors.EnvelopeV1);
            tampered[^1] ^= 0x01;

            var act = () => _service.Decrypt(tampered);

            act.Should().ThrowExactly<AuthenticationTagMismatchException>();
        }
    }

    /// <summary>
    /// Binds DatabaseSettings the way the Configuration Service registers it.
    /// </summary>
    [TestFixture]
    public class Given_AllowLegacyConnectionStrings_bound_from_DatabaseSettings
    {
        private static IOptions<DatabaseOptions> Bind(string? allowLegacyConnectionStrings)
        {
            Dictionary<string, string?> settings = new()
            {
                ["DatabaseSettings:DatabaseConnection"] = "Server=test;",
                ["DatabaseSettings:EncryptionKey"] = LegacyKey,
            };

            if (allowLegacyConnectionStrings is not null)
            {
                settings["DatabaseSettings:AllowLegacyConnectionStrings"] = allowLegacyConnectionStrings;
            }

            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var services = new ServiceCollection();
            services.Configure<DatabaseOptions>(configuration.GetSection("DatabaseSettings"));

            return services.BuildServiceProvider().GetRequiredService<IOptions<DatabaseOptions>>();
        }

        [TestCase(null)]
        [TestCase("true")]
        [TestCase("True")]
        public void It_reads_legacy_values_when_the_setting_is_absent_or_true(string? value) =>
            new ConnectionStringEncryptionService(Bind(value))
                .Decrypt(EncryptLegacy(LegacyPlainText))
                .Should()
                .Be(LegacyPlainText);

        [TestCase("false")]
        [TestCase("False")]
        public void It_rejects_legacy_values_when_the_setting_is_false(string value)
        {
            var service = new ConnectionStringEncryptionService(Bind(value));

            var act = () => service.Decrypt(EncryptLegacy(LegacyPlainText));

            act.Should()
                .ThrowExactly<LegacyConnectionStringRejectedException>()
                .WithMessage("*AllowLegacyConnectionStrings is false*");
        }

        /// <summary>
        /// Fails closed: the options never resolve, so a typo cannot leave legacy reads enabled. The
        /// Configuration Service resolves these options at startup. Compose never passes an empty value:
        /// it substitutes true for an empty CONFIGURATION_SERVICE_ALLOW_LEGACY_CONNECTION_STRINGS. It
        /// does pass a whitespace-only one through.
        /// </summary>
        [TestCase("")]
        [TestCase(" ")]
        [TestCase("flase")]
        [TestCase("0")]
        public void It_fails_binding_for_a_blank_or_malformed_value(string value)
        {
            IOptions<DatabaseOptions> options = Bind(value);

            var act = () => options.Value;

            act.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*DatabaseSettings:AllowLegacyConnectionStrings*System.Boolean*");
        }
    }
}
