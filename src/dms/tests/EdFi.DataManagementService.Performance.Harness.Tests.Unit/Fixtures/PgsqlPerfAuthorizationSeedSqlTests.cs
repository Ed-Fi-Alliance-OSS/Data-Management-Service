// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Fixtures;

[TestFixture]
public class Given_The_Pgsql_Authorization_Seed_Sql
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
                    PgsqlPerfAuthorizationSeedSql.SchoolDocumentInsertSql(_seed),
                    PgsqlPerfAuthorizationSeedSql.SchoolInsertSql(_seed),
                    PgsqlPerfAuthorizationSeedSql.SsaDocumentInsertSql(_seed),
                    PgsqlPerfAuthorizationSeedSql.SsaInsertSql(_seed),
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
        string sql = PgsqlPerfAuthorizationSeedSql.SchoolInsertSql(_seed);

        sql.Should().Contain("\"edfi\".\"School\" (\"DocumentId\", \"NameOfInstitution\", \"SchoolId\")");
        sql.Should().Contain("555562");
        sql.Should().Contain("8990001");
    }

    [Test]
    public void It_enrolls_student_ordinal_two_k_via_the_gap_rule_arithmetic()
    {
        string sql = PgsqlPerfAuthorizationSeedSql.SsaInsertSql(_seed);

        sql.Should().Contain("((k * 2 - 1) / 9) * 10 + ((k * 2 - 1) % 9) + 2");
        sql.Should().Contain("'perf-' || lpad((k * 2)::text, 9, '0')");
        sql.Should().Contain("generate_series(@fromOrdinal, @toOrdinal)");
        sql.Should().Contain("DATE '2025-08-11'");
        sql.Should().Contain("555563 + k");
    }

    [Test]
    public void It_overrides_the_document_identity_for_explicit_ids()
    {
        PgsqlPerfAuthorizationSeedSql
            .SchoolDocumentInsertSql(_seed)
            .Should()
            .Contain("OVERRIDING SYSTEM VALUE");
        string ssaDocumentSql = PgsqlPerfAuthorizationSeedSql.SsaDocumentInsertSql(_seed);
        ssaDocumentSql.Should().Contain("OVERRIDING SYSTEM VALUE");
        ssaDocumentSql.Should().Contain("'8f7a1000-0000-4000-8000-' || lpad(to_hex(k), 12, '0')");
    }

    [Test]
    public void It_reseeds_so_the_next_id_follows_the_association_block()
    {
        PgsqlPerfAuthorizationSeedSql.ReseedSql(_seed).Should().Contain("RESTART WITH 805564");
    }

    [Test]
    public void It_verifies_the_seed_analytically()
    {
        PgsqlPerfAuthorizationSeedSql
            .VerificationQueries(_seed)
            .Select(query => (query.Name, query.ExpectedValue))
            .Should()
            .Equal(
                ("ssa-row-count", 250_000),
                ("ssa-document-count", 250_000),
                ("ssa-document-pairing", 250_000),
                ("ssa-distinct-students", 250_000),
                ("ssa-student-document-id-sum", _seed.EnrolledStudentDocumentIdSum()),
                ("ssa-odd-ordinal-enrollments", 0),
                ("ssa-referential-identity-count", 250_000),
                ("school-row-count", 1),
                ("school-self-auth-edge", 1),
                ("authorized-view-membership", 250_000),
                ("grade-level-descriptor-count", 1),
                ("max-document-id", 805_563)
            );
    }

    [Test]
    public void It_reads_the_view_only_to_verify_membership()
    {
        PgsqlPerfAuthorizationSeedSql
            .VerificationQueries(_seed)
            .Single(query => query.Name == "authorized-view-membership")
            .Sql.Should()
            .Contain("\"auth\".\"EducationOrganizationIdToStudentDocumentId\"");
    }
}
