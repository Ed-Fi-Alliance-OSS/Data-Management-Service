// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

/// <summary>
/// DMS-1529: the constraint-name recognition behind <see cref="MssqlExceptionExtensions"/> reads
/// the name from the position SQL Server quotes it at, so a duplicate value that spells another
/// constraint's name cannot be mistaken for that constraint. These fixtures exercise the message
/// formats of errors 2627, 2601 and 547 without a database connection; they carry the lane category
/// so the SQL Server lane filter selects them.
/// </summary>
public class MssqlExceptionExtensionsTests
{
    private const string NameConstraint = "UX_ApiClient_ApplicationId_Name";
    private const string KeyConstraint = "UX_ApiClient_ClientId";

    [TestFixture]
    [Category("MssqlIntegration")]
    public class Given_a_unique_constraint_violation_whose_duplicate_value_spells_another_constraint
        : MssqlExceptionExtensionsTests
    {
        private const string Message =
            $"Violation of UNIQUE KEY constraint '{KeyConstraint}'. Cannot insert duplicate key in object 'dmscs.ApiClient'. The duplicate key value is ({NameConstraint}).";

        private string? _violatedName;

        [SetUp]
        public void Setup() =>
            _violatedName = MssqlExceptionExtensions.UniqueViolationConstraintName(2627, Message);

        [Test]
        public void It_names_the_violated_constraint() => _violatedName.Should().Be(KeyConstraint);

        [Test]
        public void It_does_not_name_the_constraint_the_duplicate_value_spells() =>
            _violatedName.Should().NotBe(NameConstraint);
    }

    [TestFixture]
    [Category("MssqlIntegration")]
    public class Given_a_primary_key_violation : MssqlExceptionExtensionsTests
    {
        private const string Message =
            "Violation of PRIMARY KEY constraint 'PK_ApiClient'. Cannot insert duplicate key in object 'dmscs.ApiClient'. The duplicate key value is (42).";

        [Test]
        public void It_names_the_primary_key() =>
            MssqlExceptionExtensions.UniqueViolationConstraintName(2627, Message).Should().Be("PK_ApiClient");
    }

    [TestFixture]
    [Category("MssqlIntegration")]
    public class Given_a_unique_index_violation_whose_duplicate_value_spells_another_constraint
        : MssqlExceptionExtensionsTests
    {
        private const string Message =
            $"Cannot insert duplicate key row in object 'dmscs.ApiClient' with unique index '{KeyConstraint}'. The duplicate key value is ({NameConstraint}).";

        private string? _violatedName;

        [SetUp]
        public void Setup() =>
            _violatedName = MssqlExceptionExtensions.UniqueViolationConstraintName(2601, Message);

        [Test]
        public void It_names_the_violated_index_rather_than_the_object() =>
            _violatedName.Should().Be(KeyConstraint);

        [Test]
        public void It_does_not_name_the_constraint_the_duplicate_value_spells() =>
            _violatedName.Should().NotBe(NameConstraint);
    }

    [TestFixture]
    [Category("MssqlIntegration")]
    public class Given_a_unique_violation_whose_duplicate_value_contains_quotes
        : MssqlExceptionExtensionsTests
    {
        private const string Value = "it's 'quoted' (\"twice\")";

        [Test]
        public void It_names_the_constraint_of_error_2627() =>
            MssqlExceptionExtensions
                .UniqueViolationConstraintName(
                    2627,
                    $"Violation of UNIQUE KEY constraint '{KeyConstraint}'. Cannot insert duplicate key in object 'dmscs.ApiClient'. The duplicate key value is ({Value})."
                )
                .Should()
                .Be(KeyConstraint);

        [Test]
        public void It_names_the_index_of_error_2601() =>
            MssqlExceptionExtensions
                .UniqueViolationConstraintName(
                    2601,
                    $"Cannot insert duplicate key row in object 'dmscs.ApiClient' with unique index '{KeyConstraint}'. The duplicate key value is ({Value})."
                )
                .Should()
                .Be(KeyConstraint);
    }

    [TestFixture]
    [Category("MssqlIntegration")]
    public class Given_errors_that_are_not_unique_violations : MssqlExceptionExtensionsTests
    {
        [Test]
        public void It_names_nothing_for_a_foreign_key_error_number() =>
            MssqlExceptionExtensions
                .UniqueViolationConstraintName(
                    547,
                    $"The INSERT statement conflicted with the FOREIGN KEY constraint \"FK_ApiClient_Application\". The duplicate key value is ({KeyConstraint})."
                )
                .Should()
                .BeNull();

        [Test]
        public void It_names_nothing_for_a_unique_error_number_whose_message_carries_no_quoted_name() =>
            MssqlExceptionExtensions
                .UniqueViolationConstraintName(2627, $"Unique violation on {KeyConstraint}")
                .Should()
                .BeNull();

        [Test]
        public void It_names_nothing_for_a_unique_index_error_number_with_a_single_quoted_token() =>
            MssqlExceptionExtensions
                .UniqueViolationConstraintName(
                    2601,
                    $"Cannot insert duplicate key row in object '{KeyConstraint}'."
                )
                .Should()
                .BeNull();
    }

    [TestFixture]
    [Category("MssqlIntegration")]
    public class Given_foreign_key_violations : MssqlExceptionExtensionsTests
    {
        [Test]
        public void It_names_the_foreign_key_of_an_insert_conflict() =>
            MssqlExceptionExtensions
                .ForeignKeyViolationConstraintName(
                    547,
                    "The INSERT statement conflicted with the FOREIGN KEY constraint \"FK_ApiClient_Application\". The conflict occurred in database \"edfi\", table \"dmscs.Application\", column 'Id'."
                )
                .Should()
                .Be("FK_ApiClient_Application");

        [Test]
        public void It_names_the_foreign_key_of_a_delete_conflict() =>
            MssqlExceptionExtensions
                .ForeignKeyViolationConstraintName(
                    547,
                    "The DELETE statement conflicted with the REFERENCE constraint \"FK_ApplicationProfile_Profile\". The conflict occurred in database \"edfi\", table \"dmscs.ApplicationProfile\", column 'ProfileId'."
                )
                .Should()
                .Be("FK_ApplicationProfile_Profile");

        [Test]
        public void It_names_nothing_for_a_unique_error_number() =>
            MssqlExceptionExtensions
                .ForeignKeyViolationConstraintName(
                    2627,
                    "Violation of UNIQUE KEY constraint 'UX_ApiClient_ClientId'. Cannot insert duplicate key in object 'dmscs.ApiClient'. The duplicate key value is (\"FK_ApiClient_Application\")."
                )
                .Should()
                .BeNull();
    }
}
