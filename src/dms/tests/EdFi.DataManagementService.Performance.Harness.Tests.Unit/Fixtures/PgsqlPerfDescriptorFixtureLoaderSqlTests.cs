// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Fixtures;

[TestFixture]
public class Given_The_Pgsql_Descriptor_Loader_Sql
{
    [Test]
    public void It_looks_the_resource_key_up_by_project_and_resource()
    {
        PgsqlPerfDescriptorFixtureLoaderSql.ResourceKeyLookupSql.Should().Contain("'Ed-Fi'");
        PgsqlPerfDescriptorFixtureLoaderSql
            .ResourceKeyLookupSql.Should()
            .Contain("'AcademicSubjectDescriptor'");
    }

    [Test]
    public void It_loads_dense_document_ids_with_the_fixture_uuid_prefix()
    {
        PgsqlPerfDescriptorFixtureLoaderSql.DocumentInsertSql.Should().Contain("OVERRIDING SYSTEM VALUE");
        PgsqlPerfDescriptorFixtureLoaderSql
            .DocumentInsertSql.Should()
            .Contain("'8f7a3000-0000-4000-8000-' || lpad(to_hex(n), 12, '0')");
        PgsqlPerfDescriptorFixtureLoaderSql
            .DocumentInsertSql.Should()
            .Contain("generate_series(@fromOrdinal, @toOrdinal)");
    }

    [Test]
    public void It_interleaves_the_namespaces_on_ordinal_parity()
    {
        PgsqlPerfDescriptorFixtureLoaderSql
            .DescriptorInsertSql.Should()
            .Contain("CASE WHEN n % 2 = 1 THEN 'uri://perf-accessible.ed-fi.org/AcademicSubjectDescriptor'");
        PgsqlPerfDescriptorFixtureLoaderSql
            .DescriptorInsertSql.Should()
            .Contain("'uri://perf-denied.example/AcademicSubjectDescriptor'");
        PgsqlPerfDescriptorFixtureLoaderSql
            .DescriptorInsertSql.Should()
            .Contain("'perf-' || lpad(n::text, 9, '0')");
    }

    [Test]
    public void It_derives_referential_identities_through_the_database_uuidv5_function()
    {
        string sql = PgsqlPerfDescriptorFixtureLoaderSql.ReferentialIdentityInsertSql;

        sql.Should().Contain("\"dms\".\"uuidv5\"");
        sql.Should().Contain("'edf1edf1-3df1-3df1-3df1-3df1edf1edf1'::uuid");
        sql.Should().Contain("'Ed-FiAcademicSubjectDescriptor'");
        sql.Should().Contain("'$.descriptor=' || LOWER(");
    }

    [Test]
    public void It_reseeds_so_the_next_id_follows_the_fixture()
    {
        PgsqlPerfDescriptorFixtureLoaderSql
            .ReseedSql(new PerfDescriptorFixtureDefinition(PerfDescriptorFixtureKind.Descriptors25k))
            .Should()
            .Contain("RESTART WITH 25001");
    }

    [Test]
    public void It_verifies_the_definition_analytically()
    {
        PerfDescriptorFixtureDefinition definition = new(PerfDescriptorFixtureKind.Descriptors25k);

        PgsqlPerfDescriptorFixtureLoaderSql
            .VerificationQueries(definition)
            .Select(query => (query.Name, query.ExpectedValue))
            .Should()
            .Equal(
                ("descriptor-row-count", 25_000),
                ("document-count", 25_000),
                ("descriptor-document-pairing", 25_000),
                ("accessible-count", 12_500),
                ("inaccessible-count", 12_500),
                ("accessible-even-ordinal-emissions", 0),
                ("min-document-id", 1),
                ("max-document-id", 25_000),
                ("document-id-sum", 312_512_500),
                ("referential-identity-count", 25_000),
                ("referential-identity-pairing", 25_000),
                ("uri-shape-count", 25_000)
            );
    }
}
