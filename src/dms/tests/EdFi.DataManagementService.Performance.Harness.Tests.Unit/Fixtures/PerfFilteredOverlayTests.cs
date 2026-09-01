// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Fixtures;

[TestFixture]
public class Given_The_Filtered_Overlay_Definition
{
    [Test]
    public void It_varies_every_tenth_student()
    {
        PerfFilteredOverlay
            .OverlaidStudentCount(new PerfFixtureDefinition(PerfFixtureKind.Primary500k))
            .Should()
            .Be(50_000);
        PerfFilteredOverlay
            .OverlaidStudentCount(new PerfFixtureDefinition(PerfFixtureKind.Smoke10k))
            .Should()
            .Be(1_000);
    }

    [Test]
    public void It_keeps_the_overlay_date_the_same_iso_text_length_as_the_original()
    {
        PerfFilteredOverlay
            .OverlayBirthDateIso.Should()
            .HaveLength(PerfFixtureDefinition.BirthDateIso.Length);
        PerfFilteredOverlay.OverlayBirthDateIso.Should().NotBe(PerfFixtureDefinition.BirthDateIso);
    }

    [Test]
    public void It_varies_row_ordinals_that_are_multiples_of_ten()
    {
        PerfFilteredOverlay.OverlaidStudentOrdinal(1).Should().Be(10);
        PerfFilteredOverlay.OverlaidStudentOrdinal(25).Should().Be(250);
    }

    [Test]
    public void It_computes_the_overlaid_document_id_checksum()
    {
        // Ordinals 10 and 20 map to DocumentIds 12 and 23.
        PerfFilteredOverlay
            .OverlaidDocumentIdSum(new PerfFixtureDefinition(new PerfFixtureKind("test-20", 20)))
            .Should()
            .Be(35);
    }
}

[TestFixture]
public class Given_The_Pgsql_Filtered_Overlay_Sql
{
    [Test]
    public void It_updates_exact_fixture_document_ids_only()
    {
        PgsqlPerfFilteredOverlaySql
            .UpdateSql.Should()
            .Contain("s.\"DocumentId\" = ((k * 10 - 1) / 9) * 10 + ((k * 10 - 1) % 9) + 2");
        PgsqlPerfFilteredOverlaySql.UpdateSql.Should().Contain("generate_series(@fromOrdinal, @toOrdinal)");
        PgsqlPerfFilteredOverlaySql.UpdateSql.Should().Contain("DATE '2010-06-15'");
    }

    [Test]
    public void It_verifies_the_overlay_analytically()
    {
        PerfFixtureDefinition definition = new(PerfFixtureKind.Primary500k);

        PgsqlPerfFilteredOverlaySql
            .VerificationQueries(definition)
            .Select(query => (query.Name, query.ExpectedValue))
            .Should()
            .Equal(
                ("overlaid-student-count", 50_000),
                ("unvaried-student-count", 450_000),
                ("student-row-count", 500_000),
                ("overlaid-document-id-sum", PerfFilteredOverlay.OverlaidDocumentIdSum(definition)),
                ("student-identification-document-row-count", 500_000),
                ("student-other-name-row-count", 500_000),
                ("student-personal-identification-document-row-count", 500_000),
                ("student-visa-row-count", 500_000)
            );
    }
}

[TestFixture]
public class Given_The_Mssql_Filtered_Overlay_Sql
{
    [Test]
    public void It_updates_exact_fixture_document_ids_only()
    {
        MssqlPerfFilteredOverlaySql
            .UpdateSql.Should()
            .Contain("s.[DocumentId] = ((g.value * 10 - 1) / 9) * 10 + ((g.value * 10 - 1) % 9) + 2");
        MssqlPerfFilteredOverlaySql.UpdateSql.Should().Contain("GENERATE_SERIES(@fromOrdinal, @toOrdinal)");
        MssqlPerfFilteredOverlaySql.UpdateSql.Should().Contain("'2010-06-15'");
    }

    [Test]
    public void It_verifies_the_overlay_analytically_like_the_pgsql_side()
    {
        PerfFixtureDefinition definition = new(PerfFixtureKind.Primary500k);

        MssqlPerfFilteredOverlaySql
            .VerificationQueries(definition)
            .Select(query => (query.Name, query.ExpectedValue))
            .Should()
            .Equal(
                PgsqlPerfFilteredOverlaySql
                    .VerificationQueries(definition)
                    .Select(query => (query.Name, query.ExpectedValue))
            );
    }
}
