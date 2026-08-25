// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Services;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Services;

[TestFixture]
public class ConnectionStringCipherTextTests
{
    private static readonly ConnectionStringEncryptionService EncryptionService = new(
        Options.Create(
            new DatabaseOptions
            {
                DatabaseConnection = "Server=test;",
                EncryptionKey = "TestEncryptionKey123456789012345678901234567890",
            }
        )
    );

    private static string StoredValueFor(string plainText) =>
        Convert.ToBase64String(EncryptionService.Encrypt(plainText)!);

    /// <summary>
    /// The sweep is the guarantee behind rejecting a resubmitted connection string: every length the
    /// encryption service can emit has to be recognized, not most of them.
    /// </summary>
    [TestFixture]
    public class Given_cipher_text_the_encryption_service_produces : ConnectionStringCipherTextTests
    {
        private const int LongestPlainTextLength = 200;

        private readonly List<int> _lengthsNotRecognized = [];
        private readonly List<int> _paddingCharacterCounts = [];

        [SetUp]
        public void SetUpSweep()
        {
            for (int length = 1; length <= LongestPlainTextLength; length++)
            {
                string storedValue = StoredValueFor(new string('a', length));

                if (!ConnectionStringCipherText.LooksLikeCipherText(storedValue))
                {
                    _lengthsNotRecognized.Add(length);
                }

                _paddingCharacterCounts.Add(storedValue.Count(character => character == '='));
            }
        }

        [Test]
        public void It_recognizes_every_plain_text_length() => _lengthsNotRecognized.Should().BeEmpty();

        /// <summary>
        /// The single '=' shape is the one a provider parse accepts on its own, so the sweep has to
        /// keep covering all three shapes for the assertion above to mean anything.
        /// </summary>
        [Test]
        public void It_covers_all_three_base64_padding_shapes() =>
            _paddingCharacterCounts.Distinct().Should().BeEquivalentTo(new[] { 0, 1, 2 });
    }

    [TestFixture]
    public class Given_cipher_text_of_a_real_connection_string : ConnectionStringCipherTextTests
    {
        [TestCase("Server=newtest;Database=NewTestDb;")]
        [TestCase("Server=localhost;Database=TestDb;User Id=user;Password=pass;")]
        [TestCase("host=dms-postgresql;port=5432;username=postgres;password=p;database=edfi_dms;")]
        [TestCase("host=localhost;database=edfi_datamanagementservice")]
        [TestCase("Server=dms-mssql,1433;Database=edfi;User Id=sa;Password=p;TrustServerCertificate=true;")]
        public void It_is_recognized(string plainText) =>
            ConnectionStringCipherText.LooksLikeCipherText(StoredValueFor(plainText)).Should().BeTrue();
    }

    /// <summary>
    /// The values the platform itself registers, including the shapes
    /// eng/Dms-Management.psm1 builds for each engine. None of them may be mistaken for cipher text.
    /// </summary>
    [TestFixture]
    public class Given_a_connection_string_in_plain_text : ConnectionStringCipherTextTests
    {
        [TestCase("Server=newtest;Database=NewTestDb;")]
        [TestCase("Server=localhost;Database=TestDb;User Id=user;Password=pass;")]
        [TestCase("Data Source=localhost;Initial Catalog=edfi;Integrated Security=True;")]
        [TestCase("Server=dms-mssql,1433;Database=edfi;User Id=sa;Password=p;TrustServerCertificate=true;")]
        [TestCase("host=dms-postgresql;port=5432;username=postgres;password=p;database=edfi_dms;")]
        [TestCase("host=dms-postgresql;port=5432;username=postgres;password=;database=edfi_dms")]
        [TestCase(
            "host=dms-postgresql;port=5432;username=postgres;password=p;database=edfi_dms;NoResetOnClose=true"
        )]
        public void It_is_not_recognized_as_cipher_text(string connectionString) =>
            ConnectionStringCipherText.LooksLikeCipherText(connectionString).Should().BeFalse();
    }

    [TestFixture]
    public class Given_a_value_that_is_not_cipher_text : ConnectionStringCipherTextTests
    {
        [TestCase("", TestName = "It_is_not_recognized_as_cipher_text(empty)")]
        [TestCase("   ", TestName = "It_is_not_recognized_as_cipher_text(whitespace)")]
        [TestCase("Timeout=")]
        [TestCase("bogus=")]
        [TestCase("AAAA==")]
        [TestCase("not-base64-at-all")]
        public void It_is_not_recognized_as_cipher_text(string value) =>
            ConnectionStringCipherText.LooksLikeCipherText(value).Should().BeFalse();

        [TestCase(15, TestName = "It_is_not_recognized_at_15_bytes")]
        [TestCase(31, TestName = "It_is_not_recognized_at_31_bytes")]
        public void It_is_not_recognized_below_the_shortest_stored_value(int byteCount) =>
            ConnectionStringCipherText
                .LooksLikeCipherText(Convert.ToBase64String(new byte[byteCount]))
                .Should()
                .BeFalse();

        /// <summary>
        /// Long enough, but not a whole number of cipher blocks, so the encryption service cannot
        /// have written it.
        /// </summary>
        [TestCase(33)]
        [TestCase(47)]
        public void It_is_not_recognized_when_the_length_is_not_a_block_multiple(int byteCount) =>
            ConnectionStringCipherText
                .LooksLikeCipherText(Convert.ToBase64String(new byte[byteCount]))
                .Should()
                .BeFalse();
    }
}
