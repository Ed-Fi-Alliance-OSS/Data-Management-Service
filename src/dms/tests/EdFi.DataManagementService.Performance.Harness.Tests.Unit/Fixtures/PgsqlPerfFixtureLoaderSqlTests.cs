// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Fixtures;

[TestFixture]
public class Given_The_Pgsql_Loader_Sql
{
    [Test]
    public void It_looks_the_resource_key_up_by_project_and_resource()
    {
        PgsqlPerfFixtureLoaderSql.ResourceKeyLookupSql.Should().Contain("\"ResourceKeyId\"");
        PgsqlPerfFixtureLoaderSql.ResourceKeyLookupSql.Should().Contain("'Ed-Fi'");
        PgsqlPerfFixtureLoaderSql.ResourceKeyLookupSql.Should().Contain("'Student'");
    }

    [Test]
    public void It_overrides_the_document_identity_for_explicit_ids()
    {
        PgsqlPerfFixtureLoaderSql.DocumentInsertSql.Should().Contain("OVERRIDING SYSTEM VALUE");
        PgsqlPerfFixtureLoaderSql
            .DocumentInsertSql.Should()
            .Contain("\"dms\".\"Document\" (\"DocumentId\", \"DocumentUuid\", \"ResourceKeyId\")");
    }

    [Test]
    public void It_reproduces_the_gap_rule_arithmetic()
    {
        PgsqlPerfFixtureLoaderSql
            .DocumentInsertSql.Should()
            .Contain("((n - 1) / 9) * 10 + ((n - 1) % 9) + 2");
        PgsqlPerfFixtureLoaderSql.StudentInsertSql.Should().Contain("((n - 1) / 9) * 10 + ((n - 1) % 9) + 2");
    }

    [Test]
    public void It_reproduces_the_document_uuid_format()
    {
        PgsqlPerfFixtureLoaderSql
            .DocumentInsertSql.Should()
            .Contain($"'{PerfFixtureDefinition.DocumentUuidPrefix}' || lpad(to_hex(n), 12, '0')");
    }

    [Test]
    public void It_reproduces_the_student_unique_id_format()
    {
        PgsqlPerfFixtureLoaderSql.StudentInsertSql.Should().Contain("'perf-' || lpad(n::text, 9, '0')");
    }

    [Test]
    public void It_generates_rows_from_the_chunk_parameters()
    {
        PgsqlPerfFixtureLoaderSql
            .DocumentInsertSql.Should()
            .Contain("generate_series(@fromOrdinal, @toOrdinal)");
        PgsqlPerfFixtureLoaderSql
            .StudentInsertSql.Should()
            .Contain("generate_series(@fromOrdinal, @toOrdinal)");
        PgsqlPerfFixtureLoaderSql.DocumentInsertSql.Should().Contain("@resourceKeyId");
    }

    [Test]
    public void It_fills_only_the_required_student_columns()
    {
        PgsqlPerfFixtureLoaderSql
            .StudentInsertSql.Should()
            .Contain(
                "\"edfi\".\"Student\" (\"DocumentId\", \"StudentUniqueId\", \"FirstName\", \"LastSurname\", \"BirthDate\")"
            );
        PgsqlPerfFixtureLoaderSql.StudentInsertSql.Should().Contain("'Perf'");
        PgsqlPerfFixtureLoaderSql.StudentInsertSql.Should().Contain("DATE '2010-01-01'");
    }

    [Test]
    public void It_reseeds_so_the_next_id_follows_the_fixture()
    {
        PgsqlPerfFixtureLoaderSql
            .ReseedSql(new PerfFixtureDefinition(PerfFixtureKind.Primary500k))
            .Should()
            .Contain("RESTART WITH 555557");
        PgsqlPerfFixtureLoaderSql
            .ReseedSql(new PerfFixtureDefinition(PerfFixtureKind.Smoke10k))
            .Should()
            .Contain("RESTART WITH 11113");
    }

    [Test]
    public void It_refreshes_statistics_for_both_tables()
    {
        PgsqlPerfFixtureLoaderSql
            .StatisticsRefreshSqls.Should()
            .Equal("""VACUUM (ANALYZE) "dms"."Document";""", """VACUUM (ANALYZE) "edfi"."Student";""");
    }

    [Test]
    public void It_verifies_the_definition_analytically()
    {
        PerfFixtureDefinition definition = new(PerfFixtureKind.Primary500k);
        IReadOnlyList<PerfVerificationQuery> queries = PgsqlPerfFixtureLoaderSql.VerificationQueries(
            definition
        );
        queries
            .Select(query => (query.Name, query.ExpectedValue))
            .Should()
            .Equal(
                ("student-row-count", 500_000),
                ("document-row-count", 500_000),
                ("document-student-pairing", 500_000),
                ("min-document-id", 2),
                ("max-document-id", 555_556),
                ("gap-count", 55_556),
                ("gap-id-emissions", 0),
                ("document-id-sum", definition.DocumentIdSum())
            );
    }

    [Test]
    public void It_measures_the_gaps_in_the_loaded_database()
    {
        IReadOnlyList<PerfVerificationQuery> queries = PgsqlPerfFixtureLoaderSql.VerificationQueries(
            new PerfFixtureDefinition(PerfFixtureKind.Primary500k)
        );
        queries
            .Single(query => query.Name == "gap-count")
            .Sql.Should()
            .Contain("MAX(\"DocumentId\") - COUNT(*)");
        queries
            .Single(query => query.Name == "gap-id-emissions")
            .Sql.Should()
            .Contain("\"DocumentId\" % 10 = 1");
    }
}
