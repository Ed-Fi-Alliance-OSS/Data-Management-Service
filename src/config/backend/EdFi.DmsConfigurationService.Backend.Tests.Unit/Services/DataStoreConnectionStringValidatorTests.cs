// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Mssql;
using EdFi.DmsConfigurationService.Backend.Postgresql;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Services;

[TestFixture]
public class DataStoreConnectionStringValidatorTests
{
    private static readonly IDataStoreConnectionStringValidator Postgresql =
        new PostgresqlDataStoreConnectionStringValidator();

    private static readonly IDataStoreConnectionStringValidator SqlServer =
        new MssqlDataStoreConnectionStringValidator();

    private static readonly ConnectionStringEncryptionService EncryptionService = new(
        Options.Create(
            new DatabaseOptions
            {
                DatabaseConnection = "Server=test;",
                EncryptionKey = "TestEncryptionKey123456789012345678901234567890",
            }
        )
    );

    /// <summary>
    /// What a get returns for a stored connection string, which is what a client resubmits when it
    /// writes back an object it read.
    /// </summary>
    private static string StoredValueFor(string plainText) =>
        Convert.ToBase64String(EncryptionService.Encrypt(plainText)!);

    private static void ShouldBeValid(IDataStoreConnectionStringValidator validator, string? value) =>
        validator.Validate(value).Should().BeOfType<ConnectionStringValidationResult.Valid>();

    private static void ShouldBeInvalidWith(
        IDataStoreConnectionStringValidator validator,
        string? value,
        string expectedMessage
    ) =>
        validator
            .Validate(value)
            .Should()
            .BeOfType<ConnectionStringValidationResult.Invalid>()
            .Which.ErrorMessage.Should()
            .Be(expectedMessage);

    [TestFixture]
    public class Given_no_value_was_provided : DataStoreConnectionStringValidatorTests
    {
        [Test]
        public void It_is_valid_for_postgresql() => ShouldBeValid(Postgresql, null);

        [Test]
        public void It_is_valid_for_sql_server() => ShouldBeValid(SqlServer, null);
    }

    [TestFixture]
    public class Given_an_empty_or_whitespace_value : DataStoreConnectionStringValidatorTests
    {
        [TestCase("", TestName = "It_is_rejected_for_postgresql(empty)")]
        [TestCase("   ", TestName = "It_is_rejected_for_postgresql(whitespace)")]
        public void It_is_rejected_for_postgresql(string value) =>
            ShouldBeInvalidWith(Postgresql, value, DataStoreConnectionStringValidator.EmptyMessage);

        [TestCase("", TestName = "It_is_rejected_for_sql_server(empty)")]
        [TestCase("   ", TestName = "It_is_rejected_for_sql_server(whitespace)")]
        public void It_is_rejected_for_sql_server(string value) =>
            ShouldBeInvalidWith(SqlServer, value, DataStoreConnectionStringValidator.EmptyMessage);
    }

    /// <summary>
    /// The defect this validation exists for: the value a get returns, submitted back unchanged. The
    /// plain text lengths below cover all three Base64 padding shapes of the stored value, including
    /// the single '=' shape that SQL Server's parser accepts as a keyword with an empty value.
    /// </summary>
    [TestFixture]
    public class Given_cipher_text_returned_by_a_previous_request : DataStoreConnectionStringValidatorTests
    {
        [TestCase("Server=a;Database=b")]
        [TestCase("Server=newtest;Database=NewTestDb;")]
        [TestCase("host=localhost;database=edfi_datamanagementservice")]
        [TestCase("host=dms-postgresql;port=5432;username=postgres;password=p;database=edfi_dms;")]
        [TestCase("Server=dms-mssql,1433;Database=edfi;User Id=sa;Password=p;TrustServerCertificate=true;")]
        public void It_is_rejected_for_postgresql(string plainText) =>
            ShouldBeInvalidWith(
                Postgresql,
                StoredValueFor(plainText),
                DataStoreConnectionStringValidator.CipherTextMessage
            );

        [TestCase("Server=a;Database=b")]
        [TestCase("Server=newtest;Database=NewTestDb;")]
        [TestCase("host=localhost;database=edfi_datamanagementservice")]
        [TestCase("host=dms-postgresql;port=5432;username=postgres;password=p;database=edfi_dms;")]
        [TestCase("Server=dms-mssql,1433;Database=edfi;User Id=sa;Password=p;TrustServerCertificate=true;")]
        public void It_is_rejected_for_sql_server(string plainText) =>
            ShouldBeInvalidWith(
                SqlServer,
                StoredValueFor(plainText),
                DataStoreConnectionStringValidator.CipherTextMessage
            );
    }

    [TestFixture]
    public class Given_a_value_no_engine_can_parse : DataStoreConnectionStringValidatorTests
    {
        [TestCase("not-a-connection-string")]
        [TestCase("test-connection-string")]
        [TestCase("AAAA==")]
        [TestCase("=x")]
        public void It_is_rejected_for_postgresql(string value) =>
            ShouldBeInvalidWith(Postgresql, value, DataStoreConnectionStringValidator.MalformedMessage);

        [TestCase("not-a-connection-string")]
        [TestCase("test-connection-string")]
        [TestCase("AAAA==")]
        [TestCase("=x")]
        public void It_is_rejected_for_sql_server(string value) =>
            ShouldBeInvalidWith(SqlServer, value, DataStoreConnectionStringValidator.MalformedMessage);

        [Test]
        public void It_rejects_a_long_run_of_digits_for_postgresql() =>
            ShouldBeInvalidWith(
                Postgresql,
                new string('0', 1001),
                DataStoreConnectionStringValidator.MalformedMessage
            );

        [Test]
        public void It_rejects_a_long_run_of_digits_for_sql_server() =>
            ShouldBeInvalidWith(
                SqlServer,
                new string('0', 1001),
                DataStoreConnectionStringValidator.MalformedMessage
            );
    }

    [TestFixture]
    public class Given_a_value_that_parses_but_assigns_nothing : DataStoreConnectionStringValidatorTests
    {
        [TestCase(";;;")]
        [TestCase("host=")]
        [TestCase("Server=")]
        [TestCase("Server=;Database=")]
        public void It_is_rejected_for_postgresql(string value) =>
            ShouldBeInvalidWith(Postgresql, value, DataStoreConnectionStringValidator.NoSettingsMessage);

        [TestCase(";;;")]
        [TestCase("host=")]
        [TestCase("Server=")]
        [TestCase("Server=;Database=")]
        public void It_is_rejected_for_sql_server(string value) =>
            ShouldBeInvalidWith(SqlServer, value, DataStoreConnectionStringValidator.NoSettingsMessage);
    }

    [TestFixture]
    public class Given_a_value_with_an_unbalanced_quote : DataStoreConnectionStringValidatorTests
    {
        private const string Value = "host=localhost;\"weird";

        [Test]
        public void It_is_rejected_for_postgresql() =>
            ShouldBeInvalidWith(Postgresql, Value, DataStoreConnectionStringValidator.MalformedMessage);

        [Test]
        public void It_is_rejected_for_sql_server() =>
            ShouldBeInvalidWith(SqlServer, Value, DataStoreConnectionStringValidator.MalformedMessage);
    }

    /// <summary>
    /// An unrecognized keyword carrying an empty value is where the two parsers disagree, and where
    /// Npgsql throws KeyNotFoundException rather than ArgumentException. It is pinned because
    /// catching only the documented type would answer caller input with a 500.
    /// </summary>
    [TestFixture]
    public class Given_an_unknown_keyword_with_an_empty_value : DataStoreConnectionStringValidatorTests
    {
        [Test]
        public void It_is_malformed_for_postgresql() =>
            ShouldBeInvalidWith(Postgresql, "bogus=", DataStoreConnectionStringValidator.MalformedMessage);

        [Test]
        public void It_assigns_nothing_for_sql_server() =>
            ShouldBeInvalidWith(SqlServer, "bogus=", DataStoreConnectionStringValidator.NoSettingsMessage);
    }

    /// <summary>
    /// The shapes this platform actually registers: the Configuration Service end to end tests, both
    /// engine branches of the eng/Dms-Management.psm1 connection string factory, and the documented
    /// examples.
    /// </summary>
    [TestFixture]
    public class Given_a_connection_string_the_platform_registers : DataStoreConnectionStringValidatorTests
    {
        [TestCase("Server=newtest;Database=NewTestDb;")]
        [TestCase("Server=localhost;Database=TestDb;")]
        [TestCase("host=dms-postgresql;port=5432;username=postgres;password=p;database=edfi_dms;")]
        [TestCase("host=dms-postgresql;port=5432;username=postgres;password=;database=edfi_dms")]
        [TestCase(
            "host=dms-postgresql;port=5432;username=postgres;password=p;database=edfi_dms;NoResetOnClose=true"
        )]
        [TestCase("Server=dms-mssql,1433;Database=edfi;User Id=sa;Password=p;TrustServerCertificate=true;")]
        public void It_is_accepted_for_postgresql(string connectionString) =>
            ShouldBeValid(Postgresql, connectionString);

        [TestCase("Server=newtest;Database=NewTestDb;")]
        [TestCase("Server=localhost;Database=TestDb;")]
        [TestCase("Server=dms-mssql,1433;Database=edfi;User Id=sa;Password=p;TrustServerCertificate=true;")]
        [TestCase("Data Source=localhost;Initial Catalog=edfi;Integrated Security=True;")]
        [TestCase("Server=localhost,1433;Database=edfi;User Id=sa;Password=p;Encrypt=True;")]
        public void It_is_accepted_for_sql_server(string connectionString) =>
            ShouldBeValid(SqlServer, connectionString);
    }

    /// <summary>
    /// Each engine accepts what its own provider accepts, which is what validating against the
    /// configured engine means.
    /// </summary>
    [TestFixture]
    public class Given_a_connection_string_for_the_other_engine : DataStoreConnectionStringValidatorTests
    {
        [TestCase("host=localhost;port=5432;username=postgres;password=p;database=edfi_dms")]
        [TestCase("host=localhost;database=edfi_dms")]
        public void It_is_rejected_by_the_sql_server_validator(string connectionString) =>
            ShouldBeInvalidWith(
                SqlServer,
                connectionString,
                DataStoreConnectionStringValidator.MalformedMessage
            );

        [TestCase("Data Source=localhost;Initial Catalog=edfi;Integrated Security=True;")]
        [TestCase("Server=localhost,1433;Database=edfi;User Id=sa;Password=p;Encrypt=True;")]
        public void It_is_rejected_by_the_postgresql_validator(string connectionString) =>
            ShouldBeInvalidWith(
                Postgresql,
                connectionString,
                DataStoreConnectionStringValidator.MalformedMessage
            );
    }

    /// <summary>
    /// A provider's own message repeats the text it could not read, so a rejection must never carry
    /// the submitted value out of the service.
    /// </summary>
    [TestFixture]
    public class Given_any_rejected_value : DataStoreConnectionStringValidatorTests
    {
        private static IEnumerable<string> RejectedValues()
        {
            yield return StoredValueFor("Server=localhost;Database=TestDb;");
            yield return StoredValueFor("host=localhost;database=edfi_datamanagementservice");
            yield return "not-a-connection-string";
            yield return "host=localhost;port=5432;username=postgres;password=p;database=edfi_dms";
            yield return "Data Source=localhost;Initial Catalog=edfi;";
            yield return "Server=;Database=";
            yield return "bogus=";
        }

        [TestCaseSource(nameof(RejectedValues))]
        public void It_does_not_repeat_the_value_in_the_postgresql_message(string value) =>
            MessageFor(Postgresql, value).Should().NotContain(value);

        [TestCaseSource(nameof(RejectedValues))]
        public void It_does_not_repeat_the_value_in_the_sql_server_message(string value) =>
            MessageFor(SqlServer, value).Should().NotContain(value);

        /// <summary>
        /// Empty when the engine accepts the value, which some of these are for one engine and not the
        /// other. An accepted value carries no message to leak.
        /// </summary>
        private static string MessageFor(IDataStoreConnectionStringValidator validator, string value) =>
            validator.Validate(value) is ConnectionStringValidationResult.Invalid invalid
                ? invalid.ErrorMessage
                : string.Empty;
    }
}
