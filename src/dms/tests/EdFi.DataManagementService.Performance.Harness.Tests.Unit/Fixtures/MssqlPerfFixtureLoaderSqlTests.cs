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
                "[edfi].[Student] ([DocumentId], [StudentUniqueId], [FirstName], [LastSurname], [BirthDate], [BirthSexDescriptor_DescriptorId])"
            );
        MssqlPerfFixtureLoaderSql.StudentInsertSql.Should().Contain("'Perf'");
        MssqlPerfFixtureLoaderSql.StudentInsertSql.Should().Contain("'2010-01-01'");
        MssqlPerfFixtureLoaderSql.StudentInsertSql.Should().Contain("@birthSexDescriptorId");
    }

    [Test]
    public void It_inserts_one_row_per_student_into_each_child_collection()
    {
        MssqlPerfFixtureLoaderSql.ChildCollectionInsertSqls.Should().HaveCount(4);
        MssqlPerfFixtureLoaderSql
            .ChildCollectionInsertSqls.Should()
            .AllSatisfy(sql =>
            {
                sql.Should().Contain("GENERATE_SERIES(@fromOrdinal, @toOrdinal)");
                sql.Should().Contain("((s.value - 1) / 9) * 10 + ((s.value - 1) % 9) + 2");
                sql.Should().Contain("[Ordinal]", "the production write path assigns explicit ordinals");
                sql.Should()
                    .NotContain("CollectionItemId", "the shared sequence default must assign item ids");
            });
        MssqlPerfFixtureLoaderSql
            .ChildCollectionInsertSqls[0]
            .Should()
            .Contain("[edfi].[StudentIdentificationDocument]");
        MssqlPerfFixtureLoaderSql.ChildCollectionInsertSqls[1].Should().Contain("[edfi].[StudentOtherName]");
        MssqlPerfFixtureLoaderSql
            .ChildCollectionInsertSqls[2]
            .Should()
            .Contain("[edfi].[StudentPersonalIdentificationDocument]");
        MssqlPerfFixtureLoaderSql.ChildCollectionInsertSqls[3].Should().Contain("[edfi].[StudentVisa]");
        MssqlPerfFixtureLoaderSql.ChildCollectionInsertSqls[3].Should().Contain("@visaDescriptorId");
    }

    [Test]
    public void It_writes_the_descriptor_catalog_production_faithfully()
    {
        MssqlPerfFixtureLoaderSql
            .DescriptorDocumentInsertSql.Should()
            .Contain("SET IDENTITY_INSERT [dms].[Document] ON;");
        MssqlPerfFixtureLoaderSql.DescriptorDocumentInsertSql.Should().Contain("@descriptorDocumentId");
        string sql = MssqlPerfFixtureLoaderSql.DescriptorInsertSql("VisaDescriptor");
        sql.Should().Contain("'uri://ed-fi.org/VisaDescriptor'");
        sql.Should().Contain("'uri://ed-fi.org/VisaDescriptor#Perf'");
        sql.Should().Contain("'VisaDescriptor'");
        MssqlPerfFixtureLoaderSql
            .DescriptorResourceKeyLookupSql("SexDescriptor")
            .Should()
            .Contain("'SexDescriptor'");
    }

    [Test]
    public void It_reseeds_so_the_next_id_follows_the_fixture()
    {
        // RESEED takes the current seed, so the next generated identity is the value plus one,
        // matching the PostgreSQL RESTART WITH reseed-target + 1 form.
        MssqlPerfFixtureLoaderSql
            .ReseedSql(new PerfFixtureDefinition(PerfFixtureKind.Primary500k))
            .Should()
            .Be("DBCC CHECKIDENT ('[dms].[Document]', RESEED, 555561);");
        MssqlPerfFixtureLoaderSql
            .ReseedSql(new PerfFixtureDefinition(PerfFixtureKind.Smoke10k))
            .Should()
            .Be("DBCC CHECKIDENT ('[dms].[Document]', RESEED, 11117);");
    }

    [Test]
    public void It_refreshes_statistics_for_every_loaded_table()
    {
        MssqlPerfFixtureLoaderSql
            .StatisticsRefreshSqls.Should()
            .Equal(
                "UPDATE STATISTICS [dms].[Document];",
                "UPDATE STATISTICS [edfi].[Student];",
                "UPDATE STATISTICS [edfi].[StudentIdentificationDocument];",
                "UPDATE STATISTICS [edfi].[StudentOtherName];",
                "UPDATE STATISTICS [edfi].[StudentPersonalIdentificationDocument];",
                "UPDATE STATISTICS [edfi].[StudentVisa];",
                "UPDATE STATISTICS [dms].[Descriptor];"
            );
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
                ("student-document-count", 10_000),
                ("document-student-pairing", 10_000),
                ("min-document-id", 2),
                ("max-student-document-id", 11_112),
                ("max-document-id", 11_117),
                ("gap-count", 1_112),
                ("gap-id-emissions", 0),
                ("document-id-sum", definition.DocumentIdSum()),
                ("descriptor-row-count", 5),
                ("descriptor-document-pairing", 5),
                ("students-with-birth-sex-descriptor", 10_000),
                ("student-identification-document-row-count", 10_000),
                ("student-identification-document-descriptor-bindings", 10_000),
                ("student-other-name-row-count", 10_000),
                ("student-personal-identification-document-row-count", 10_000),
                ("student-visa-row-count", 10_000)
            );
    }

    [Test]
    public void It_measures_the_gaps_in_the_loaded_database()
    {
        IReadOnlyList<PerfVerificationQuery> queries = MssqlPerfFixtureLoaderSql.VerificationQueries(
            new PerfFixtureDefinition(PerfFixtureKind.Smoke10k)
        );
        queries
            .Single(query => query.Name == "gap-count")
            .Sql.Should()
            .Contain("MAX([DocumentId]) - COUNT(*)");
        queries
            .Single(query => query.Name == "gap-id-emissions")
            .Sql.Should()
            .Contain("[DocumentId] % 10 = 1");
    }

    [Test]
    public void It_scopes_the_id_scheme_checks_to_student_documents()
    {
        IReadOnlyList<PerfVerificationQuery> queries = MssqlPerfFixtureLoaderSql.VerificationQueries(
            new PerfFixtureDefinition(PerfFixtureKind.Primary500k)
        );
        foreach (
            string name in (string[])
                [
                    "student-document-count",
                    "max-student-document-id",
                    "gap-count",
                    "gap-id-emissions",
                    "document-id-sum",
                ]
        )
        {
            queries.Single(query => query.Name == name).Sql.Should().Contain("'Student'");
        }

        queries.Single(query => query.Name == "max-document-id").Sql.Should().NotContain("'Student'");
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
