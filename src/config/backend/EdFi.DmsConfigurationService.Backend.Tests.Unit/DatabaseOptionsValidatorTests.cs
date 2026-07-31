// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

public class DatabaseOptionsValidatorTests
{
    private const string ValidDatabaseConnection =
        "host=localhost;port=5432;username=postgres;database=edfi_configurationservice;";

    /// <summary>
    /// Exactly 32 characters: the minimum accepted length, and the shape a generated deployment key
    /// takes.
    /// </summary>
    private const string ThirtyTwoCharacterKey = "Fk3pQ8sT2vW9xZ4bC6dE1gH5jL7mN0rY";

    /// <summary>
    /// One character short of the minimum.
    /// </summary>
    private const string ThirtyOneCharacterKey = "Fk3pQ8sT2vW9xZ4bC6dE1gH5jL7mN0r";

    /// <summary>
    /// The value formerly shipped in the Configuration Service appsettings.json.
    /// </summary>
    private const string ShippedDefaultKey = "YourSecureEncryptionKey32Characters";

    /// <summary>
    /// U+00E9, a non-ASCII code point. It occupies two bytes in UTF-8, so including it in the
    /// significant prefix pushes the derived key past the 32 bytes AES-256 accepts. Written as a code
    /// point so the source file stays ASCII.
    /// </summary>
    private const char NonAsciiCharacter = (char)0xE9;

    private static ValidateOptionsResult Validate(DatabaseOptions options) =>
        new DatabaseOptionsValidator().Validate(null, options);

    /// <summary>
    /// Every fixture supplies a valid DatabaseConnection so that a failure can only come from the
    /// EncryptionKey rules.
    /// </summary>
    private static DatabaseOptions OptionsWithKey(string encryptionKey) =>
        new() { DatabaseConnection = ValidDatabaseConnection, EncryptionKey = encryptionKey };

    [TestFixture]
    public class Given_the_known_shipped_default_encryption_key
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() => _result = Validate(OptionsWithKey(ShippedDefaultKey));

        [Test]
        public void It_fails_validation() => _result.Failed.Should().BeTrue();

        [Test]
        public void It_reports_the_known_default_rule() =>
            _result.FailureMessage.Should().Contain("DatabaseSettings:EncryptionKey").And.Contain("default");

        /// <summary>
        /// The default is 35 characters of ASCII, so it would pass every other rule. Reaching the
        /// blank-value message here would mean the branches had been reordered.
        /// </summary>
        [Test]
        public void It_does_not_report_the_missing_value_rule() =>
            _result.FailureMessage.Should().NotContain("Missing required");
    }

    [TestFixture]
    public class Given_a_one_character_encryption_key
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() => _result = Validate(OptionsWithKey("a"));

        [Test]
        public void It_fails_validation() => _result.Failed.Should().BeTrue();

        [Test]
        public void It_reports_the_minimum_length_rule() =>
            _result
                .FailureMessage.Should()
                .Contain("DatabaseSettings:EncryptionKey")
                .And.Contain("at least 32");
    }

    [TestFixture]
    public class Given_an_encryption_key_one_character_short_of_32
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() => _result = Validate(OptionsWithKey(ThirtyOneCharacterKey));

        [Test]
        public void It_is_a_31_character_key() => ThirtyOneCharacterKey.Length.Should().Be(31);

        [Test]
        public void It_fails_validation() => _result.Failed.Should().BeTrue();

        [Test]
        public void It_reports_the_minimum_length_rule() =>
            _result
                .FailureMessage.Should()
                .Contain("DatabaseSettings:EncryptionKey")
                .And.Contain("at least 32");
    }

    [TestFixture]
    public class Given_an_encryption_key_of_exactly_32_characters
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() => _result = Validate(OptionsWithKey(ThirtyTwoCharacterKey));

        [Test]
        public void It_is_a_32_character_key() => ThirtyTwoCharacterKey.Length.Should().Be(32);

        [Test]
        public void It_succeeds_validation() => _result.Succeeded.Should().BeTrue();
    }

    [TestFixture]
    public class Given_an_encryption_key_longer_than_32_characters
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() => _result = Validate(OptionsWithKey(ThirtyTwoCharacterKey + "_extra_padding5"));

        [Test]
        public void It_succeeds_validation() => _result.Succeeded.Should().BeTrue();
    }

    [TestFixture]
    public class Given_a_null_encryption_key
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() => _result = Validate(OptionsWithKey(null!));

        [Test]
        public void It_fails_validation() => _result.Failed.Should().BeTrue();

        [Test]
        public void It_reports_the_pre_existing_missing_value_message() =>
            _result.FailureMessage.Should().Be("Missing required DatabaseSettings value: EncryptionKey");
    }

    [TestFixture]
    public class Given_an_empty_encryption_key
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() => _result = Validate(OptionsWithKey(string.Empty));

        [Test]
        public void It_fails_validation() => _result.Failed.Should().BeTrue();

        [Test]
        public void It_reports_the_pre_existing_missing_value_message() =>
            _result.FailureMessage.Should().Be("Missing required DatabaseSettings value: EncryptionKey");
    }

    [TestFixture]
    public class Given_a_whitespace_only_encryption_key
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() => _result = Validate(OptionsWithKey("   "));

        [Test]
        public void It_fails_validation() => _result.Failed.Should().BeTrue();

        [Test]
        public void It_reports_the_pre_existing_missing_value_message() =>
            _result.FailureMessage.Should().Be("Missing required DatabaseSettings value: EncryptionKey");
    }

    /// <summary>
    /// A non-ASCII character in the first 32 characters is rejected because only that prefix reaches
    /// the AES key, and a multi-byte character pushes its UTF-8 length past 32 bytes. The character
    /// sits at index 31 here, the last position the rule covers.
    /// </summary>
    [TestFixture]
    public class Given_a_non_ascii_character_within_the_first_32_characters
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() => _result = Validate(OptionsWithKey(ThirtyOneCharacterKey + NonAsciiCharacter));

        [Test]
        public void It_fails_validation() => _result.Failed.Should().BeTrue();

        [Test]
        public void It_reports_the_ascii_rule() =>
            _result.FailureMessage.Should().Contain("DatabaseSettings:EncryptionKey").And.Contain("ASCII");
    }

    /// <summary>
    /// The ASCII rule is scoped to the significant prefix, so a key that works today keeps working
    /// even when a later character is non-ASCII: those characters never reach the AES key.
    /// </summary>
    [TestFixture]
    public class Given_a_non_ascii_character_only_beyond_the_first_32_characters
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() => _result = Validate(OptionsWithKey(ThirtyTwoCharacterKey + NonAsciiCharacter));

        [Test]
        public void It_succeeds_validation() => _result.Succeeded.Should().BeTrue();
    }

    [TestFixture]
    public class Given_a_missing_database_connection
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() =>
            _result = Validate(
                new DatabaseOptions
                {
                    DatabaseConnection = string.Empty,
                    EncryptionKey = ThirtyTwoCharacterKey,
                }
            );

        [Test]
        public void It_fails_validation() => _result.Failed.Should().BeTrue();

        [Test]
        public void It_reports_the_database_connection_rule() =>
            _result.FailureMessage.Should().Be("Missing required DatabaseSettings value: DatabaseConnection");
    }

    /// <summary>
    /// The known-default comparison is ordinal, so a key that only differs from the default in case
    /// is a different key.
    /// </summary>
    [TestFixture]
    public class Given_an_encryption_key_that_differs_from_the_default_only_in_case
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() => _result = Validate(OptionsWithKey("yourSecureEncryptionKey32Characters"));

        [Test]
        public void It_succeeds_validation() => _result.Succeeded.Should().BeTrue();
    }
}
