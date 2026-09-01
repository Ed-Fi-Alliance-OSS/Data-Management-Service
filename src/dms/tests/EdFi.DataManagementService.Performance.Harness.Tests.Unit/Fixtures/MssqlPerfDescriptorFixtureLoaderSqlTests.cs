// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Fixtures;

[TestFixture]
public class Given_The_Mssql_Descriptor_Loader_Sql
{
    [Test]
    public void It_loads_dense_document_ids_under_identity_insert()
    {
        string sql = MssqlPerfDescriptorFixtureLoaderSql.DocumentInsertSql;

        sql.Should().Contain("SET IDENTITY_INSERT [dms].[Document] ON;");
        sql.Should().Contain("SET IDENTITY_INSERT [dms].[Document] OFF;");
        sql.Should().Contain("'8f7a3000-0000-4000-8000-'");
        sql.Should().Contain("GENERATE_SERIES(@fromOrdinal, @toOrdinal)");
    }

    [Test]
    public void It_interleaves_the_namespaces_on_ordinal_parity()
    {
        MssqlPerfDescriptorFixtureLoaderSql
            .DescriptorInsertSql.Should()
            .Contain(
                "CASE WHEN s.value % 2 = 1 THEN 'uri://perf-accessible.ed-fi.org/AcademicSubjectDescriptor'"
            );
        MssqlPerfDescriptorFixtureLoaderSql
            .DescriptorInsertSql.Should()
            .Contain("'perf-' + RIGHT(REPLICATE('0', 9) + CAST(s.value AS varchar(19)), 9)");
    }

    [Test]
    public void It_derives_referential_identities_through_the_database_uuidv5_function()
    {
        string sql = MssqlPerfDescriptorFixtureLoaderSql.ReferentialIdentityInsertSql;

        sql.Should().Contain("[dms].[uuidv5]");
        sql.Should().Contain("'edf1edf1-3df1-3df1-3df1-3df1edf1edf1'");
        sql.Should().Contain("N'Ed-FiAcademicSubjectDescriptor'");
        sql.Should().Contain("N'$.descriptor=' + LOWER(");
    }

    [Test]
    public void It_reseeds_so_the_next_id_follows_the_fixture()
    {
        MssqlPerfDescriptorFixtureLoaderSql
            .ReseedSql(new PerfDescriptorFixtureDefinition(PerfDescriptorFixtureKind.Descriptors25k))
            .Should()
            .Contain("RESEED, 25000");
    }

    [Test]
    public void It_verifies_the_definition_analytically_like_the_pgsql_side()
    {
        PerfDescriptorFixtureDefinition definition = new(PerfDescriptorFixtureKind.Descriptors25k);

        MssqlPerfDescriptorFixtureLoaderSql
            .VerificationQueries(definition)
            .Select(query => (query.Name, query.ExpectedValue))
            .Should()
            .Equal(
                PgsqlPerfDescriptorFixtureLoaderSql
                    .VerificationQueries(definition)
                    .Select(query => (query.Name, query.ExpectedValue))
            );
    }
}
