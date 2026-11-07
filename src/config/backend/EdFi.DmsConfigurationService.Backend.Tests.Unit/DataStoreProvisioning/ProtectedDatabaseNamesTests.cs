// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

public class ProtectedDatabaseNamesTests
{
    [TestFixture]
    public class Given_a_catalog_name_and_operator_listed_names
    {
        private ProtectedDatabaseNames _names = null!;

        [SetUp]
        public void Setup() =>
            _names = ProtectedDatabaseNames.Create(
                " edfi_configurationservice ",
                ["EdFi_Admin", "edfi_admin", "Keycloak"]
            );

        [Test]
        public void It_protects_the_catalog_database() =>
            _names.IsProtected("edfi_configurationservice").Should().BeTrue();

        [Test]
        public void It_protects_the_catalog_database_case_insensitively() =>
            _names.IsProtected("EDFI_CONFIGURATIONSERVICE").Should().BeTrue();

        [Test]
        public void It_protects_each_listed_name_case_insensitively() =>
            _names.IsProtected("KEYCLOAK").Should().BeTrue();

        [Test]
        public void It_does_not_protect_other_names() => _names.IsProtected("EdFi_Ods").Should().BeFalse();

        [Test]
        public void It_exposes_the_trimmed_sorted_distinct_set() =>
            _names.Names.Should().Equal("EdFi_Admin", "edfi_configurationservice", "Keycloak");
    }

    [TestFixture]
    public class Given_the_reserved_system_databases
    {
        [Test]
        public void It_lists_both_engines_system_databases() =>
            ProtectedDatabaseNames
                .Reserved.Should()
                .BeEquivalentTo("postgres", "template0", "template1", "master", "model", "msdb", "tempdb");

        [Test]
        public void It_matches_reserved_names_case_insensitively() =>
            ProtectedDatabaseNames.IsReserved("MASTER").Should().BeTrue();

        [Test]
        public void It_does_not_treat_the_catalog_name_as_reserved() =>
            ProtectedDatabaseNames.IsReserved("edfi_configurationservice").Should().BeFalse();

        [Test]
        public void It_matches_the_staging_prefix_case_insensitively() =>
            ProtectedDatabaseNames.HasReservedPrefix("EDFI_DMS_PROVISION_stage_abc").Should().BeTrue();

        [Test]
        public void It_matches_the_restore_session_prefix() =>
            ProtectedDatabaseNames.HasReservedPrefix("dms-provision:0123456789ab").Should().BeTrue();

        [Test]
        public void It_does_not_match_a_shorter_name() =>
            ProtectedDatabaseNames.HasReservedPrefix("edfi_dms_provisio").Should().BeFalse();
    }

    [TestFixture]
    public class Given_blank_configuration_entries
    {
        private Action _blankCatalog = () => { };
        private Action _blankEntry = () => { };

        [SetUp]
        public void Setup()
        {
            _blankCatalog = () => ProtectedDatabaseNames.Create(" ", []);
            _blankEntry = () =>
                ProtectedDatabaseNames.Create("edfi_configurationservice", ["EdFi_Admin", ""]);
        }

        [Test]
        public void It_rejects_a_blank_catalog_name() => _blankCatalog.Should().Throw<ArgumentException>();

        [Test]
        public void It_rejects_a_blank_listed_name() => _blankEntry.Should().Throw<ArgumentException>();
    }
}
