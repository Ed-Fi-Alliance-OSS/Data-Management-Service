// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Fixtures;

[TestFixture]
public class Given_The_Mssql_Authorization_Seed_Sql
{
    private PerfAuthorizationSeedDefinition _seed = null!;

    [SetUp]
    public void Setup()
    {
        _seed = new PerfAuthorizationSeedDefinition(new PerfFixtureDefinition(PerfFixtureKind.Primary500k));
    }

    [Test]
    public void It_writes_no_generated_authorization_view()
    {
        foreach (
            string sql in (string[])
                [
                    MssqlPerfAuthorizationSeedSql.SchoolDocumentInsertSql(_seed),
                    MssqlPerfAuthorizationSeedSql.SchoolInsertSql(_seed),
                    MssqlPerfAuthorizationSeedSql.SsaDocumentInsertSql(_seed),
                    MssqlPerfAuthorizationSeedSql.SsaInsertSql(_seed),
                ]
        )
        {
            sql.Should()
                .NotContain(
                    "EducationOrganizationIdToStudentDocumentId",
                    "the seed must feed the view through durable source tables only"
                );
        }
    }

    [Test]
    public void It_inserts_the_school_with_only_payload_backed_columns()
    {
        string sql = MssqlPerfAuthorizationSeedSql.SchoolInsertSql(_seed);

        sql.Should().Contain("[edfi].[School] ([DocumentId], [NameOfInstitution], [SchoolId])");
        sql.Should().Contain("555562");
        sql.Should().Contain("8990001");
    }

    [Test]
    public void It_enrolls_student_ordinal_two_k_via_the_gap_rule_arithmetic()
    {
        string sql = MssqlPerfAuthorizationSeedSql.SsaInsertSql(_seed);

        sql.Should().Contain("((s.value * 2 - 1) / 9) * 10 + ((s.value * 2 - 1) % 9) + 2");
        sql.Should().Contain("'perf-' + RIGHT(REPLICATE('0', 9) + CAST(s.value * 2 AS varchar(19)), 9)");
        sql.Should().Contain("GENERATE_SERIES(@fromOrdinal, @toOrdinal)");
        sql.Should().Contain("'2025-08-11'");
        sql.Should().Contain("555563 + s.value");
    }

    [Test]
    public void It_brackets_document_inserts_with_identity_insert()
    {
        foreach (
            string sql in (string[])
                [
                    MssqlPerfAuthorizationSeedSql.SchoolDocumentInsertSql(_seed),
                    MssqlPerfAuthorizationSeedSql.SsaDocumentInsertSql(_seed),
                ]
        )
        {
            sql.Should().Contain("SET IDENTITY_INSERT [dms].[Document] ON;");
            sql.Should().Contain("SET IDENTITY_INSERT [dms].[Document] OFF;");
        }
    }

    [Test]
    public void It_reseeds_so_the_next_id_follows_the_association_block()
    {
        MssqlPerfAuthorizationSeedSql.ReseedSql(_seed).Should().Contain("RESEED, 805563");
    }

    [Test]
    public void It_verifies_the_seed_analytically_like_the_pgsql_side()
    {
        MssqlPerfAuthorizationSeedSql
            .VerificationQueries(_seed)
            .Select(query => (query.Name, query.ExpectedValue))
            .Should()
            .Equal(
                PgsqlPerfAuthorizationSeedSql
                    .VerificationQueries(_seed)
                    .Select(query => (query.Name, query.ExpectedValue))
            );
    }
}
