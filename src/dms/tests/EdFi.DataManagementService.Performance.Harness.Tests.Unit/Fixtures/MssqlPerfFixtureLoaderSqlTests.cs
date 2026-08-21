// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Fixtures;

[TestFixture]
public class Given_The_Mssql_Loader_Sql
{
    [Test]
    public void It_guards_generate_series_availability()
    {
        MssqlPerfFixtureLoaderSql.GenerateSeriesGuardSql.Should().Contain("ProductMajorVersion");
        MssqlPerfFixtureLoaderSql.GenerateSeriesGuardSql.Should().Contain("compatibility_level");
        MssqlPerfFixtureLoaderSql
            .GenerateSeriesGuardSql.Should()
            .Contain($">= {MssqlPerfFixtureLoaderSql.MinimumProductMajorVersion}");
        MssqlPerfFixtureLoaderSql
            .GenerateSeriesGuardSql.Should()
            .Contain($">= {MssqlPerfFixtureLoaderSql.MinimumCompatibilityLevel}");
    }

    [Test]
    public void It_pins_the_documented_minimums()
    {
        MssqlPerfFixtureLoaderSql.MinimumProductMajorVersion.Should().Be(16);
        MssqlPerfFixtureLoaderSql.MinimumCompatibilityLevel.Should().Be(160);
    }

    [Test]
    public void It_looks_the_resource_key_up_by_project_and_resource()
    {
        MssqlPerfFixtureLoaderSql.ResourceKeyLookupSql.Should().Contain("[ResourceKeyId]");
        MssqlPerfFixtureLoaderSql.ResourceKeyLookupSql.Should().Contain("'Ed-Fi'");
        MssqlPerfFixtureLoaderSql.ResourceKeyLookupSql.Should().Contain("'Student'");
    }

    [Test]
    public void It_brackets_the_insert_with_identity_insert()
    {
        string sql = MssqlPerfFixtureLoaderSql.DocumentInsertSql;
        int on = sql.IndexOf("SET IDENTITY_INSERT [dms].[Document] ON;", StringComparison.Ordinal);
        int insert = sql.IndexOf("INSERT INTO [dms].[Document]", StringComparison.Ordinal);
        int off = sql.IndexOf("SET IDENTITY_INSERT [dms].[Document] OFF;", StringComparison.Ordinal);
        on.Should().BeGreaterThanOrEqualTo(0);
        insert.Should().BeGreaterThan(on);
        off.Should().BeGreaterThan(insert);
    }

    [Test]
    public void It_reproduces_the_gap_rule_arithmetic()
    {
        MssqlPerfFixtureLoaderSql
            .DocumentInsertSql.Should()
            .Contain("((s.value - 1) / 9) * 10 + ((s.value - 1) % 9) + 2");
        MssqlPerfFixtureLoaderSql
            .StudentInsertSql.Should()
            .Contain("((s.value - 1) / 9) * 10 + ((s.value - 1) % 9) + 2");
    }

    [Test]
    public void It_reproduces_the_document_uuid_format()
    {
        MssqlPerfFixtureLoaderSql
            .DocumentInsertSql.Should()
            .Contain($"'{PerfFixtureDefinition.DocumentUuidPrefix}'");
        MssqlPerfFixtureLoaderSql
            .DocumentInsertSql.Should()
            .Contain("RIGHT(REPLICATE('0', 12) + LOWER(FORMAT(s.value, 'x')), 12)");
    }

    [Test]
    public void It_reproduces_the_student_unique_id_format()
    {
        MssqlPerfFixtureLoaderSql
            .StudentInsertSql.Should()
            .Contain("'perf-' + RIGHT(REPLICATE('0', 9) + CAST(s.value AS varchar(19)), 9)");
    }

    [Test]
    public void It_generates_rows_from_the_chunk_parameters()
    {
        MssqlPerfFixtureLoaderSql
            .DocumentInsertSql.Should()
            .Contain("GENERATE_SERIES(@fromOrdinal, @toOrdinal)");
        MssqlPerfFixtureLoaderSql
            .StudentInsertSql.Should()
            .Contain("GENERATE_SERIES(@fromOrdinal, @toOrdinal)");
        MssqlPerfFixtureLoaderSql.DocumentInsertSql.Should().Contain("@resourceKeyId");
    }

    [Test]
    public void It_fills_only_the_required_student_columns()
    {
        MssqlPerfFixtureLoaderSql
            .StudentInsertSql.Should()
            .Contain(
                "[edfi].[Student] ([DocumentId], [StudentUniqueId], [FirstName], [LastSurname], [BirthDate])"
            );
        MssqlPerfFixtureLoaderSql.StudentInsertSql.Should().Contain("'Perf'");
        MssqlPerfFixtureLoaderSql.StudentInsertSql.Should().Contain("'2010-01-01'");
    }

    [Test]
    public void It_reseeds_so_the_next_id_follows_the_fixture()
    {
        // RESEED takes the current seed, so the next generated identity is the value plus one,
        // matching the PostgreSQL RESTART WITH MaxDocumentId + 1 form.
        MssqlPerfFixtureLoaderSql
            .ReseedSql(new PerfFixtureDefinition(PerfFixtureKind.Primary500k))
            .Should()
            .Be("DBCC CHECKIDENT ('[dms].[Document]', RESEED, 555556);");
        MssqlPerfFixtureLoaderSql
            .ReseedSql(new PerfFixtureDefinition(PerfFixtureKind.Smoke10k))
            .Should()
            .Be("DBCC CHECKIDENT ('[dms].[Document]', RESEED, 11112);");
    }

    [Test]
    public void It_refreshes_statistics_for_both_tables()
    {
        MssqlPerfFixtureLoaderSql
            .StatisticsRefreshSqls.Should()
            .Equal("UPDATE STATISTICS [dms].[Document];", "UPDATE STATISTICS [edfi].[Student];");
    }

    [Test]
    public void It_verifies_the_definition_analytically()
    {
        PerfFixtureDefinition definition = new(PerfFixtureKind.Smoke10k);
        IReadOnlyList<PerfVerificationQuery> queries = MssqlPerfFixtureLoaderSql.VerificationQueries(
            definition
        );
        queries
            .Select(query => (query.Name, query.ExpectedValue))
            .Should()
            .Equal(
                ("student-row-count", 10_000),
                ("document-row-count", 10_000),
                ("document-student-pairing", 10_000),
                ("min-document-id", 2),
                ("max-document-id", 11_112),
                ("document-id-sum", definition.DocumentIdSum())
            );
    }

    [Test]
    public void It_matches_the_pgsql_verification_expectations()
    {
        PerfFixtureDefinition definition = new(PerfFixtureKind.Primary500k);
        MssqlPerfFixtureLoaderSql
            .VerificationQueries(definition)
            .Select(query => (query.Name, query.ExpectedValue))
            .Should()
            .Equal(
                PgsqlPerfFixtureLoaderSql
                    .VerificationQueries(definition)
                    .Select(query => (query.Name, query.ExpectedValue))
            );
    }
}
