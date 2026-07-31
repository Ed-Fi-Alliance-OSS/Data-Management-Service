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
    /// The 32-character prefix of <see cref="ShippedDefaultKey" />: the part that actually reaches the
    /// AES key. Any value sharing this prefix derives the published key, so it must be rejected too.
    /// </summary>
    private const string ShippedDefaultKeySignificantPrefix = "YourSecureEncryptionKey32Charact";

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

    /// <summary>
    /// The known default truncated to its significant 32 characters derives exactly the same AES key as
    /// the full 35-character value, so it is the same published key by another spelling.
    /// </summary>
    [TestFixture]
    public class Given_the_significant_prefix_of_the_shipped_default_encryption_key
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() => _result = Validate(OptionsWithKey(ShippedDefaultKeySignificantPrefix));

        /// <summary>
        /// Pins the two constants together: an edit that leaves the prefix no longer prefixing the
        /// default would otherwise make the fixtures below assert against a key the code never sees.
        /// </summary>
        [Test]
        public void It_is_the_first_32_characters_of_the_shipped_default_key()
        {
            ShippedDefaultKeySignificantPrefix.Length.Should().Be(32);
            ShippedDefaultKey.Should().StartWith(ShippedDefaultKeySignificantPrefix);
        }

        [Test]
        public void It_fails_validation() => _result.Failed.Should().BeTrue();

        [Test]
        public void It_reports_the_known_default_rule() =>
            _result.FailureMessage.Should().Contain("DatabaseSettings:EncryptionKey").And.Contain("default");

        /// <summary>
        /// It is exactly 32 characters, so reaching the length message here would mean the rule was
        /// comparing whole strings again.
        /// </summary>
        [Test]
        public void It_does_not_report_the_minimum_length_rule() =>
            _result.FailureMessage.Should().NotContain("at least 32");
    }

    /// <summary>
    /// Characters beyond the first 32 are discarded before the key is derived, so a suffix cannot make
    /// the published key acceptable.
    /// </summary>
    [TestFixture]
    public class Given_the_shipped_default_key_prefix_followed_by_an_arbitrary_suffix
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() =>
            _result = Validate(OptionsWithKey(ShippedDefaultKeySignificantPrefix + "_arbitrary_suffix"));

        [Test]
        public void It_fails_validation() => _result.Failed.Should().BeTrue();

        [Test]
        public void It_reports_the_known_default_rule() =>
            _result.FailureMessage.Should().Contain("DatabaseSettings:EncryptionKey").And.Contain("default");
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

    /// <summary>
    /// Reaching 32 characters by padding a short value with spaces derives a key with the entropy of
    /// the short value, which is what the minimum-length rule exists to prevent. It is not caught by
    /// the blank-value rule, because the value is not whitespace throughout.
    /// </summary>
    [TestFixture]
    public class Given_a_short_encryption_key_padded_with_spaces_to_32_characters
    {
        private const string SpacePaddedKey = "a                               ";

        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() => _result = Validate(OptionsWithKey(SpacePaddedKey));

        /// <summary>
        /// Pins the shape the fixture depends on: exactly 32 characters, so it clears the length rule
        /// and can only fail on the spaces rule.
        /// </summary>
        [Test]
        public void It_is_a_32_character_key() => SpacePaddedKey.Length.Should().Be(32);

        [Test]
        public void It_fails_validation() => _result.Failed.Should().BeTrue();

        [Test]
        public void It_reports_the_spaces_and_control_characters_rule() =>
            _result
                .FailureMessage.Should()
                .Contain("DatabaseSettings:EncryptionKey")
                .And.Contain("spaces or control characters");

        [Test]
        public void It_does_not_report_the_minimum_length_rule() =>
            _result.FailureMessage.Should().NotContain("at least 32");
    }

    /// <summary>
    /// A control character is valid ASCII and occupies one UTF-8 byte, so it clears both the length
    /// and ASCII rules while contributing no key material. Tab sits at index 31 here, the last
    /// position the rules cover.
    /// </summary>
    [TestFixture]
    public class Given_a_control_character_within_the_first_32_characters
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() => _result = Validate(OptionsWithKey(ThirtyOneCharacterKey + '\t'));

        [Test]
        public void It_fails_validation() => _result.Failed.Should().BeTrue();

        [Test]
        public void It_reports_the_spaces_and_control_characters_rule() =>
            _result
                .FailureMessage.Should()
                .Contain("DatabaseSettings:EncryptionKey")
                .And.Contain("spaces or control characters");
    }

    /// <summary>
    /// The rule is scoped to the significant prefix, so trailing whitespace — which configuration
    /// files and environment variables pick up easily — leaves a working key working. Those
    /// characters never reach the AES key.
    /// </summary>
    [TestFixture]
    public class Given_whitespace_only_beyond_the_first_32_characters
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Act() => _result = Validate(OptionsWithKey(ThirtyTwoCharacterKey + "   "));

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
