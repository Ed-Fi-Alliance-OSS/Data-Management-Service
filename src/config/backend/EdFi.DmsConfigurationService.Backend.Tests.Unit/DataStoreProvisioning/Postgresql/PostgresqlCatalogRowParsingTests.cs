// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Postgresql;

/// <summary>
/// The pipe-row splits mirror the delivered PowerShell: inventory rows need exactly three non-blank fields;
/// ownership and privilege rows are split at most three and five times so a quoted identity may carry the separator.
/// </summary>
public class PostgresqlCatalogRowParsingTests
{
    [TestFixture]
    public class Given_inventory_object_rows
    {
        private DmsInventoryObject? _function;
        private DmsInventoryObject? _trimmed;
        private bool _fourFields;
        private bool _blankField;
        private bool _empty;
        private DmsDatabaseLevelObject? _databaseLevel;
        private DmsDatabaseLevelObject? _databaseLevelWithSeparator;
        private bool _databaseLevelOneField;

        [SetUp]
        public void Setup()
        {
            PostgresqlInventoryQueries.TryParseObjectRow(
                "dms|TF_Document_EnqueueProjectionInsert()|function",
                out _function
            );
            PostgresqlInventoryQueries.TryParseObjectRow(" edfi | School | table ", out _trimmed);
            _fourFields = PostgresqlInventoryQueries.TryParseObjectRow("public|a|b|operator", out _);
            _blankField = PostgresqlInventoryQueries.TryParseObjectRow("edfi||table", out _);
            _empty = PostgresqlInventoryQueries.TryParseObjectRow("", out _);
            PostgresqlInventoryQueries.TryParseDatabaseLevelObjectRow(
                "extension|postgis",
                out _databaseLevel
            );
            PostgresqlInventoryQueries.TryParseDatabaseLevelObjectRow(
                "cast|text->integer|x",
                out _databaseLevelWithSeparator
            );
            _databaseLevelOneField = PostgresqlInventoryQueries.TryParseDatabaseLevelObjectRow(
                "extension",
                out _
            );
        }

        [Test]
        public void It_splits_a_function_row_with_its_signature() =>
            _function
                .Should()
                .Be(new DmsInventoryObject("dms", "TF_Document_EnqueueProjectionInsert()", "function"));

        [Test]
        public void It_trims_each_field() =>
            _trimmed.Should().Be(new DmsInventoryObject("edfi", "School", "table"));

        [Test]
        public void It_refuses_a_name_carrying_the_separator() => _fourFields.Should().BeFalse();

        [Test]
        public void It_refuses_a_blank_field() => _blankField.Should().BeFalse();

        [Test]
        public void It_refuses_an_empty_row() => _empty.Should().BeFalse();

        [Test]
        public void It_splits_a_database_level_row() =>
            _databaseLevel.Should().Be(new DmsDatabaseLevelObject("extension", "postgis"));

        [Test]
        public void It_keeps_a_separator_inside_a_database_level_name() =>
            _databaseLevelWithSeparator.Should().Be(new DmsDatabaseLevelObject("cast", "text->integer|x"));

        [Test]
        public void It_refuses_a_database_level_row_without_a_name() =>
            _databaseLevelOneField.Should().BeFalse();
    }

    [TestFixture]
    public class Given_security_rows
    {
        private DmsPostgresqlOwnershipRow? _ownership;
        private DmsPostgresqlOwnershipRow? _ownershipWithSeparator;
        private bool _ownershipBlankIdentity;
        private DmsPostgresqlPrivilegeRow? _privilege;
        private DmsPostgresqlPrivilegeRow? _privilegeWithSeparator;
        private bool _privilegeShort;

        [SetUp]
        public void Setup()
        {
            PostgresqlSecurityQueries.TryParseOwnershipRow(
                "function|edfi_dms_enqueue_owner|dms.\"TF_Document_EnqueueProjectionInsert\"()",
                out _ownership
            );
            PostgresqlSecurityQueries.TryParseOwnershipRow(
                "relation|other|edfi.\"a|b\"",
                out _ownershipWithSeparator
            );
            _ownershipBlankIdentity = PostgresqlSecurityQueries.TryParseOwnershipRow(
                "relation|other| ",
                out _
            );
            PostgresqlSecurityQueries.TryParsePrivilegeRow(
                "relation|SELECT|postgres|edfi_dms_enqueue_owner|dms.\"DocumentProjectionWork\"",
                out _privilege
            );
            PostgresqlSecurityQueries.TryParsePrivilegeRow(
                "column|SELECT|postgres|-|edfi.\"School\".\"a|b\"",
                out _privilegeWithSeparator
            );
            _privilegeShort = PostgresqlSecurityQueries.TryParsePrivilegeRow(
                "relation|SELECT|postgres|-",
                out _
            );
        }

        [Test]
        public void It_splits_an_ownership_row() =>
            _ownership
                .Should()
                .Be(
                    new DmsPostgresqlOwnershipRow(
                        "function",
                        "edfi_dms_enqueue_owner",
                        "dms.\"TF_Document_EnqueueProjectionInsert\"()"
                    )
                );

        [Test]
        public void It_keeps_a_separator_inside_an_ownership_identity() =>
            _ownershipWithSeparator
                .Should()
                .Be(new DmsPostgresqlOwnershipRow("relation", "other", "edfi.\"a|b\""));

        [Test]
        public void It_refuses_an_ownership_row_with_a_blank_identity() =>
            _ownershipBlankIdentity.Should().BeFalse();

        [Test]
        public void It_splits_a_privilege_row() =>
            _privilege
                .Should()
                .Be(
                    new DmsPostgresqlPrivilegeRow(
                        "relation",
                        "SELECT",
                        "postgres",
                        "edfi_dms_enqueue_owner",
                        "dms.\"DocumentProjectionWork\""
                    )
                );

        [Test]
        public void It_keeps_a_separator_inside_a_privilege_identity() =>
            _privilegeWithSeparator
                .Should()
                .Be(
                    new DmsPostgresqlPrivilegeRow(
                        "column",
                        "SELECT",
                        "postgres",
                        "-",
                        "edfi.\"School\".\"a|b\""
                    )
                );

        [Test]
        public void It_refuses_a_privilege_row_without_an_identity() => _privilegeShort.Should().BeFalse();
    }
}
