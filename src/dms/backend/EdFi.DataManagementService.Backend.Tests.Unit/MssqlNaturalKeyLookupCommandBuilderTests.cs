// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_MssqlNaturalKeyLookupCommandBuilder
{
    private const int ParameterBudget = MssqlNaturalKeyLookupCommandBuilder.MssqlParameterBudget;

    private static readonly MappingSet _mappingSet =
        RelationalAccessTestData.CreateNaturalKeyProbeMappingSet();

    [Test]
    public void It_probes_the_target_root_with_typed_values_rows()
    {
        var command = MssqlNaturalKeyLookupCommandBuilder.Build(
            CreateBatch(
                new NaturalKeyProbeLookupGroup(
                    RelationalAccessTestData.SchoolResource,
                    RelationalAccessTestData.CreateSchoolProbeTarget(),
                    RelationalAccessTestData.CreateNaturalKeyEntries([
                        [255901],
                        [255902],
                    ])
                )
            )
        );

        command.CommandText.Should().Contain("FROM (VALUES");
        command.CommandText.Should().Contain(") AS input([Ordinal], [c0])");
        command.CommandText.Should().Contain("INNER JOIN [edfi].[School] t");
        command.CommandText.Should().Contain("t.[SchoolId] = input.[c0]");
        command
            .CommandText.Should()
            .Contain("SELECT input.[Ordinal] AS [Ordinal], t.[DocumentId] AS [DocumentId]");
        command.Parameters.Select(parameter => parameter.Name).Should().Equal("@g0p0_0", "@g0p1_0");
        command.Parameters[0].Value.Should().Be(255901);
        command.CommandText.Should().NotContain("ReferentialIdentity");
        command.CommandText.Should().NotContain("[dms].[Document]");
    }

    [Test]
    public void It_returns_rows_in_request_order_from_inline_ordinal_literals()
    {
        var command = MssqlNaturalKeyLookupCommandBuilder.Build(
            CreateBatch(
                new NaturalKeyProbeLookupGroup(
                    RelationalAccessTestData.SchoolResource,
                    RelationalAccessTestData.CreateSchoolProbeTarget(),
                    RelationalAccessTestData.CreateNaturalKeyEntries([
                        [255901],
                        [255902],
                        [255903],
                    ])
                )
            )
        );

        command.CommandText.Should().Contain("(1, @g0p0_0)");
        command.CommandText.Should().Contain("(2, @g0p1_0)");
        command.CommandText.Should().Contain("(3, @g0p2_0)");
        command
            .CommandText.Should()
            .NotContain(
                "ROW_NUMBER()",
                "the request ordinal is an inline literal; today's bulk ordinal is fabricated and re-sorted in C#"
            );
        command.Parameters.Should().HaveCount(3, "the ordinal is a literal, never a parameter");
    }

    [Test]
    public void It_maps_each_scalar_kind_to_its_sql_server_parameter_type()
    {
        var command = MssqlNaturalKeyLookupCommandBuilder.Build(
            CreateBatch(
                new NaturalKeyProbeLookupGroup(
                    RelationalAccessTestData.AllScalarKindsResource,
                    RelationalAccessTestData.CreateAllScalarKindsProbeTarget(),
                    RelationalAccessTestData.CreateNaturalKeyEntries([
                        RelationalAccessTestData.CreateAllScalarKindsValues(),
                    ])
                )
            )
        );

        AssertSqlDbType(command.Parameters[0], SqlDbType.NVarChar, expectedSize: 50);
        AssertSqlDbType(command.Parameters[1], SqlDbType.Int);
        AssertSqlDbType(command.Parameters[2], SqlDbType.BigInt);
        AssertSqlDbType(command.Parameters[3], SqlDbType.Decimal);
        AssertSqlDbType(command.Parameters[4], SqlDbType.Bit);
        AssertSqlDbType(command.Parameters[5], SqlDbType.Date);
        AssertSqlDbType(command.Parameters[6], SqlDbType.DateTime2);
        AssertSqlDbType(command.Parameters[7], SqlDbType.Time);

        SqlParameter decimalParameter = new();
        command.Parameters[3].ConfigureParameter!(decimalParameter);
        decimalParameter.Precision.Should().Be(9);
        decimalParameter.Scale.Should().Be(2);
    }

    [Test]
    public void It_joins_dms_descriptor_by_urilowered_for_descriptor_valued_identity_parts()
    {
        var command = MssqlNaturalKeyLookupCommandBuilder.Build(CreateProgramBatch());

        command.CommandText.Should().Contain("INNER JOIN [dms].[Descriptor] d2");
        command.CommandText.Should().Contain("d2.[UriLowered] = input.[c2]");
        command.CommandText.Should().Contain("d2.[Discriminator] = N'ProgramTypeDescriptor'");
        command.CommandText.Should().Contain("t.[ProgramTypeDescriptor_DescriptorId] = d2.[DocumentId]");
        command.Parameters[2].Value.Should().Be("uri://ed-fi.org/programtypedescriptor#athletics");
        AssertSqlDbType(command.Parameters[2], SqlDbType.NVarChar, expectedSize: 306);
    }

    [Test]
    public void It_probes_the_abstract_identity_table_and_projects_the_discriminator()
    {
        var command = MssqlNaturalKeyLookupCommandBuilder.Build(
            CreateBatch(
                new NaturalKeyProbeLookupGroup(
                    RelationalAccessTestData.EducationOrganizationResource,
                    RelationalAccessTestData.CreateEducationOrganizationProbeTarget(),
                    RelationalAccessTestData.CreateNaturalKeyEntries([
                        [255901L],
                    ])
                )
            )
        );

        command.CommandText.Should().Contain("INNER JOIN [edfi].[EducationOrganizationIdentity] t");
        command.CommandText.Should().NotContain("_View");
        command.CommandText.Should().Contain("t.[EducationOrganizationId] = input.[c0]");
        command.CommandText.Should().Contain("t.[Discriminator] AS [Discriminator]");
    }

    [Test]
    public void It_probes_the_descriptor_table_by_urilowered_only_and_projects_the_type_columns()
    {
        var command = MssqlNaturalKeyLookupCommandBuilder.Build(
            CreateBatch(
                new DescriptorLookupGroup(
                    RelationalAccessTestData.SchoolTypeDescriptorResource,
                    RelationalAccessTestData.CreateNaturalKeyEntries([
                        ["uri://ed-fi.org/schooltypedescriptor#alternative"],
                    ])
                )
            )
        );

        command.CommandText.Should().Contain("INNER JOIN [dms].[Descriptor] descriptor");
        command.CommandText.Should().Contain("descriptor.[UriLowered] = input.[c0]");
        command.CommandText.Should().NotContain("descriptor.[Discriminator] =");
        command.CommandText.Should().Contain("descriptor.[Discriminator] AS [Discriminator]");
        command.CommandText.Should().Contain("descriptor.[ResourceKeyId] AS [ResourceKeyId]");
        AssertSqlDbType(command.Parameters[0], SqlDbType.NVarChar, expectedSize: 306);
    }

    [Test]
    public void It_emits_one_statement_per_target_group_in_group_order()
    {
        var command = MssqlNaturalKeyLookupCommandBuilder.Build(
            CreateBatch(
                new NaturalKeyProbeLookupGroup(
                    RelationalAccessTestData.SchoolResource,
                    RelationalAccessTestData.CreateSchoolProbeTarget(),
                    RelationalAccessTestData.CreateNaturalKeyEntries([
                        [255901],
                    ])
                ),
                new NaturalKeyProbeLookupGroup(
                    RelationalAccessTestData.EducationOrganizationResource,
                    RelationalAccessTestData.CreateEducationOrganizationProbeTarget(),
                    RelationalAccessTestData.CreateNaturalKeyEntries([
                        [255901L],
                    ])
                ),
                new DescriptorLookupGroup(
                    RelationalAccessTestData.SchoolTypeDescriptorResource,
                    RelationalAccessTestData.CreateNaturalKeyEntries([
                        ["uri://ed-fi.org/schooltypedescriptor#alternative"],
                    ])
                )
            )
        );

        CountOccurrences(command.CommandText, "SELECT input.[Ordinal]")
            .Should()
            .Be(3, "each group is one statement and therefore one result set");
        command
            .CommandText.IndexOf("[edfi].[School] t", StringComparison.Ordinal)
            .Should()
            .BeLessThan(
                command.CommandText.IndexOf(
                    "[edfi].[EducationOrganizationIdentity] t",
                    StringComparison.Ordinal
                )
            );
        command
            .CommandText.IndexOf("[edfi].[EducationOrganizationIdentity] t", StringComparison.Ordinal)
            .Should()
            .BeLessThan(
                command.CommandText.IndexOf("[dms].[Descriptor] descriptor", StringComparison.Ordinal)
            );
        command
            .Parameters.Select(parameter => parameter.Name)
            .Should()
            .Equal("@g0p0_0", "@g1p0_0", "@g2p0_0");
    }

    [Test]
    public void It_chunks_a_wide_probe_into_additional_values_clauses_with_continuous_ordinals()
    {
        const int EntryCount = 300;
        const int ColumnCount = 7;
        var chunkEntryCount = ParameterBudget / ColumnCount;

        var command = MssqlNaturalKeyLookupCommandBuilder.Build(
            CreateBatch(
                new NaturalKeyProbeLookupGroup(
                    RelationalAccessTestData.StudentSectionAssociationResource,
                    RelationalAccessTestData.CreateStudentSectionAssociationProbeTarget(),
                    CreateStudentSectionAssociationEntries(EntryCount)
                )
            )
        );

        chunkEntryCount.Should().Be(285);
        CountOccurrences(command.CommandText, ") AS input(")
            .Should()
            .Be(2, "the group splits into two VALUES clauses inside the same command");
        CountOccurrences(command.CommandText, "UNION ALL")
            .Should()
            .Be(1, "the chunks stay one statement, so the group is still exactly one result set");
        command
            .CommandText.Should()
            .Contain(
                $"({chunkEntryCount}, @g0p{chunkEntryCount - 1}_0",
                "the first chunk fills up to the budget boundary"
            );
        command
            .CommandText.Should()
            .Contain(
                $"({chunkEntryCount + 1}, @g0p{chunkEntryCount}_0",
                "ordinals continue across chunks instead of restarting"
            );
        command
            .CommandText.Should()
            .Contain($"({EntryCount}, @g0p{EntryCount - 1}_0", "the last entry keeps its request ordinal");
        command.Parameters.Should().HaveCount(EntryCount * ColumnCount);
        (chunkEntryCount * ColumnCount)
            .Should()
            .BeLessThanOrEqualTo(ParameterBudget, "each VALUES chunk stays inside the parameter budget");
    }

    [Test]
    public void It_keeps_a_narrow_probe_in_a_single_values_clause()
    {
        var command = MssqlNaturalKeyLookupCommandBuilder.Build(
            CreateBatch(
                new NaturalKeyProbeLookupGroup(
                    RelationalAccessTestData.SchoolResource,
                    RelationalAccessTestData.CreateSyntheticProbeTarget(1),
                    RelationalAccessTestData.CreateSyntheticNaturalKeyEntries(400, 1)
                )
            )
        );

        CountOccurrences(command.CommandText, ") AS input(").Should().Be(1);
        command.CommandText.Should().NotContain("UNION ALL");
        command.Parameters.Should().HaveCount(400);
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(7)]
    [TestCase(20)]
    public void It_keeps_a_realistic_batch_in_a_single_values_clause(int columnCount)
    {
        var command = MssqlNaturalKeyLookupCommandBuilder.Build(
            CreateBatch(
                new NaturalKeyProbeLookupGroup(
                    RelationalAccessTestData.SchoolResource,
                    RelationalAccessTestData.CreateSyntheticProbeTarget(columnCount),
                    RelationalAccessTestData.CreateSyntheticNaturalKeyEntries(100, columnCount)
                )
            )
        );

        CountOccurrences(command.CommandText, ") AS input(")
            .Should()
            .Be(1, "the realistic case — at most ~100 references per target — never chunks");
        command.Parameters.Should().HaveCount(100 * columnCount);
        command.Parameters.Count.Should().BeLessThanOrEqualTo(ParameterBudget);
    }

    [Test]
    public void It_emits_an_empty_result_set_for_an_empty_group()
    {
        var command = MssqlNaturalKeyLookupCommandBuilder.Build(
            CreateBatch(
                new NaturalKeyProbeLookupGroup(
                    RelationalAccessTestData.SchoolResource,
                    RelationalAccessTestData.CreateSchoolProbeTarget(),
                    []
                )
            )
        );

        command
            .CommandText.Should()
            .Contain(
                "SELECT CAST(NULL AS int) AS [Ordinal], CAST(NULL AS bigint) AS [DocumentId]",
                "SQL Server has no empty VALUES clause, but the group still owes the reader a result set"
            );
        command.CommandText.Should().Contain("WHERE 1 = 0");
        command.Parameters.Should().BeEmpty();
    }

    [Test]
    public void It_caches_command_text_per_mapping_set_group_shape_and_entry_count()
    {
        var first = MssqlNaturalKeyLookupCommandBuilder.Build(CreateProgramBatch());
        var second = MssqlNaturalKeyLookupCommandBuilder.Build(CreateProgramBatch());

        ReferenceEquals(first.CommandText, second.CommandText).Should().BeTrue();

        var wider = MssqlNaturalKeyLookupCommandBuilder.Build(
            CreateBatch(
                new NaturalKeyProbeLookupGroup(
                    RelationalAccessTestData.ProgramResource,
                    RelationalAccessTestData.CreateProgramProbeTarget(),
                    RelationalAccessTestData.CreateNaturalKeyEntries([
                        [255901L, "Athletics", "uri://ed-fi.org/programtypedescriptor#athletics"],
                        [255902L, "Bilingual", "uri://ed-fi.org/programtypedescriptor#bilingual"],
                    ])
                )
            )
        );

        wider
            .CommandText.Should()
            .NotBe(
                first.CommandText,
                "the VALUES text varies with the entry count, so the count is part of the key"
            );
    }

    [Test]
    public void It_never_reads_the_document_or_referential_identity_tables()
    {
        var command = MssqlNaturalKeyLookupCommandBuilder.Build(
            CreateBatch(
                new NaturalKeyProbeLookupGroup(
                    RelationalAccessTestData.ProgramResource,
                    RelationalAccessTestData.CreateProgramProbeTarget(),
                    RelationalAccessTestData.CreateNaturalKeyEntries([
                        [255901L, "Athletics", "uri://ed-fi.org/programtypedescriptor#athletics"],
                    ])
                ),
                new DescriptorLookupGroup(
                    RelationalAccessTestData.SchoolTypeDescriptorResource,
                    RelationalAccessTestData.CreateNaturalKeyEntries([
                        ["uri://ed-fi.org/schooltypedescriptor#alternative"],
                    ])
                ),
                new NaturalKeyProbeLookupGroup(
                    RelationalAccessTestData.EducationOrganizationResource,
                    RelationalAccessTestData.CreateEducationOrganizationProbeTarget(),
                    RelationalAccessTestData.CreateNaturalKeyEntries([
                        [255901L],
                    ])
                )
            )
        );

        command.CommandText.Should().NotContain("ReferentialIdentity");
        command.CommandText.Should().NotContain("[dms].[Document]");
    }

    [Test]
    public void It_rejects_entries_whose_ordinals_are_not_the_one_based_position()
    {
        var act = () =>
            MssqlNaturalKeyLookupCommandBuilder.Build(
                CreateBatch(
                    new NaturalKeyProbeLookupGroup(
                        RelationalAccessTestData.SchoolResource,
                        RelationalAccessTestData.CreateSchoolProbeTarget(),
                        [new NaturalKeyLookupEntry(1, [255901]), new NaturalKeyLookupEntry(3, [255902])]
                    )
                )
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*ordinals must be the one-based entry position*");
    }

    private static NaturalKeyLookupBatch CreateBatch(params NaturalKeyLookupGroup[] groups) =>
        new(_mappingSet, groups);

    private static NaturalKeyLookupBatch CreateProgramBatch() =>
        CreateBatch(
            new NaturalKeyProbeLookupGroup(
                RelationalAccessTestData.ProgramResource,
                RelationalAccessTestData.CreateProgramProbeTarget(),
                RelationalAccessTestData.CreateNaturalKeyEntries([
                    [255901L, "Athletics", "uri://ed-fi.org/programtypedescriptor#athletics"],
                ])
            )
        );

    private static IReadOnlyList<NaturalKeyLookupEntry> CreateStudentSectionAssociationEntries(
        int entryCount
    ) =>
        RelationalAccessTestData.CreateNaturalKeyEntries(
            Enumerable
                .Range(0, entryCount)
                .Select(index =>
                    (IReadOnlyList<object>)
                        [
                            new DateOnly(2026, 8, 17),
                            "ALG-1",
                            255901L,
                            2026,
                            $"Section-{index}",
                            "Fall Semester",
                            "10001",
                        ]
                )
        );

    private static void AssertSqlDbType(
        RelationalParameter parameter,
        SqlDbType expectedSqlDbType,
        int? expectedSize = null
    )
    {
        SqlParameter sqlParameter = new();
        parameter.ConfigureParameter.Should().NotBeNull();
        parameter.ConfigureParameter!(sqlParameter);

        sqlParameter.SqlDbType.Should().Be(expectedSqlDbType);

        if (expectedSize is { } size)
        {
            sqlParameter.Size.Should().Be(size);
        }
    }

    private static int CountOccurrences(string text, string fragment)
    {
        var count = 0;
        var index = text.IndexOf(fragment, StringComparison.Ordinal);

        while (index >= 0)
        {
            count++;
            index = text.IndexOf(fragment, index + fragment.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
