// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_MssqlNaturalKeyLookupCommandBuilder
{
    private static readonly MappingSet _mappingSet =
        RelationalAccessTestData.CreateNaturalKeyProbeMappingSet();

    [Test]
    public void It_probes_the_target_root_with_one_openjson_input_per_group()
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

        command
            .CommandText.Should()
            .Contain("SELECT input.[Ordinal] AS [Ordinal], t.[DocumentId] AS [DocumentId]");
        command
            .CommandText.Should()
            .Contain("FROM OPENJSON(@g0) WITH ([Ordinal] int '$.o', [c0] int '$.v0') AS input");
        command.CommandText.Should().Contain("INNER JOIN [edfi].[School] t");
        command.CommandText.Should().Contain("t.[SchoolId] = input.[c0]");
        command.CommandText.Should().NotContain("(VALUES", "the set-valued input is JSON, not a VALUES list");
        command.Parameters.Select(parameter => parameter.Name).Should().Equal("@g0");
        command.Parameters[0].Value.Should().Be("""[{"o":1,"v0":255901},{"o":2,"v0":255902}]""");
        AssertJsonParameter(command.Parameters[0]);
        command.CommandText.Should().NotContain("ReferentialIdentity");
        command.CommandText.Should().NotContain("[dms].[Document]");
    }

    [Test]
    public void It_binds_exactly_one_json_parameter_per_group()
    {
        var command = MssqlNaturalKeyLookupCommandBuilder.Build(CreateThreeGroupBatch());

        command
            .Parameters.Should()
            .HaveCount(3, "each group's whole entry list travels as one nvarchar(max) JSON payload");
        command.Parameters.Select(parameter => parameter.Name).Should().Equal("@g0", "@g1", "@g2");

        foreach (var parameter in command.Parameters)
        {
            AssertJsonParameter(parameter);
        }
    }

    [Test]
    public void It_carries_the_one_based_request_ordinal_inside_the_json()
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

        command
            .CommandText.Should()
            .Contain(
                "[Ordinal] int '$.o'",
                "the reader maps a row back with Entries[ordinal - 1], so the ordinal is input, not output"
            );
        command
            .CommandText.Should()
            .NotContain(
                "ROW_NUMBER()",
                "the request ordinal comes from the caller; today's bulk ordinal is fabricated and re-sorted in C#"
            );

        var ordinals = ParseJsonArray(command.Parameters[0])
            .Select(element => element.GetProperty("o").GetInt32());

        ordinals.Should().Equal(1, 2, 3);
    }

    [Test]
    public void It_maps_each_scalar_kind_to_its_sql_server_with_clause_type()
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

        var defaults = new MssqlDialectRules().ScalarTypeDefaults;

        command
            .CommandText.Should()
            .Contain(
                "FROM OPENJSON(@g0) WITH ([Ordinal] int '$.o', "
                    + "[c0] nvarchar(50) '$.v0', "
                    + "[c1] int '$.v1', "
                    + "[c2] bigint '$.v2', "
                    + "[c3] decimal(9,2) '$.v3', "
                    + "[c4] bit '$.v4', "
                    + "[c5] date '$.v5', "
                    + "[c6] datetime2(7) '$.v6', "
                    + "[c7] time(7) '$.v7') AS input"
            );

        // The shredded input column must be the same type the DDL rules give the storage column it is
        // compared against, or SQL Server converts on every row instead of seeking the RefKey index.
        command.CommandText.Should().Contain($"[c1] {defaults.Int32Type} ");
        command.CommandText.Should().Contain($"[c2] {defaults.Int64Type} ");
        command.CommandText.Should().Contain($"[c4] {defaults.BooleanType} ");
        command.CommandText.Should().Contain($"[c5] {defaults.DateType} ");
        command.CommandText.Should().Contain($"[c6] {defaults.DateTimeType} ");
        command.CommandText.Should().Contain($"[c7] {defaults.TimeType} ");
    }

    [Test]
    public void It_serializes_each_scalar_kind_in_the_form_openjson_converts_back()
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

        command
            .Parameters[0]
            .Value.Should()
            .Be(
                """[{"o":1,"v0":"alpha","v1":2026,"v2":9000000000,"v3":1.5,"v4":true,"v5":"2026-03-05","v6":"2026-03-05T13:30:45.0000000","v7":"13:30:45.0000000"}]""",
                "strings, ISO dates and JSON numbers/booleans are what the typed WITH clause converts from; "
                    + "the datetime carries no Z because datetime2(7) stores no offset"
            );
    }

    [Test]
    public void It_joins_dms_descriptor_by_urilowered_for_descriptor_valued_identity_parts()
    {
        var command = MssqlNaturalKeyLookupCommandBuilder.Build(CreateProgramBatch());

        command
            .CommandText.Should()
            .Contain(
                "FROM OPENJSON(@g0) WITH ([Ordinal] int '$.o', [c0] bigint '$.v0', "
                    + "[c1] nvarchar(60) '$.v1', [c2] nvarchar(306) '$.v2') AS input",
                "the descriptor URI input column is sized to dms.Descriptor.UriLowered so the seek survives"
            );
        command.CommandText.Should().Contain("INNER JOIN [dms].[Descriptor] d2");
        command.CommandText.Should().Contain("d2.[UriLowered] = input.[c2]");
        command.CommandText.Should().Contain("d2.[Discriminator] = N'ProgramTypeDescriptor'");
        command.CommandText.Should().Contain("t.[ProgramTypeDescriptor_DescriptorId] = d2.[DocumentId]");
        ParseJsonArray(command.Parameters[0])[0]
            .GetProperty("v2")
            .GetString()
            .Should()
            .Be("uri://ed-fi.org/programtypedescriptor#athletics");
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
        command.CommandText.Should().Contain("[c0] bigint '$.v0'");
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

        command
            .CommandText.Should()
            .Contain("FROM OPENJSON(@g0) WITH ([Ordinal] int '$.o', [c0] nvarchar(306) '$.v0') AS input");
        command.CommandText.Should().Contain("INNER JOIN [dms].[Descriptor] descriptor");
        command.CommandText.Should().Contain("descriptor.[UriLowered] = input.[c0]");
        command.CommandText.Should().NotContain("descriptor.[Discriminator] =");
        command.CommandText.Should().Contain("descriptor.[Discriminator] AS [Discriminator]");
        command.CommandText.Should().Contain("descriptor.[ResourceKeyId] AS [ResourceKeyId]");
        command
            .Parameters[0]
            .Value.Should()
            .Be("""[{"o":1,"v0":"uri://ed-fi.org/schooltypedescriptor#alternative"}]""");
    }

    [Test]
    public void It_emits_one_statement_per_target_group_in_group_order()
    {
        var command = MssqlNaturalKeyLookupCommandBuilder.Build(CreateThreeGroupBatch());

        CountOccurrences(command.CommandText, "SELECT input.[Ordinal]")
            .Should()
            .Be(3, "each group is one statement and therefore one result set");
        CountOccurrences(command.CommandText, "FROM OPENJSON(").Should().Be(3);
        command.CommandText.Should().NotContain("UNION ALL", "a group is never split any more");
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
    }

    [Test]
    public void It_pins_the_shredded_json_as_the_driving_side_of_every_join()
    {
        var command = MssqlNaturalKeyLookupCommandBuilder.Build(CreateThreeGroupBatch());

        CountOccurrences(command.CommandText, "OPTION (FORCE ORDER)")
            .Should()
            .Be(
                3,
                "OPENJSON has no statistics, so without the hint the optimizer scans the target table and "
                    + "re-parses the whole payload once per row"
            );
        command
            .CommandText.Should()
            .EndWith("OPTION (FORCE ORDER)", "the hint closes each statement, not the batch");
    }

    [Test]
    public void It_uses_the_same_sql_shape_regardless_of_entry_count()
    {
        var single = MssqlNaturalKeyLookupCommandBuilder.Build(
            CreateBatch(
                new NaturalKeyProbeLookupGroup(
                    RelationalAccessTestData.SchoolResource,
                    RelationalAccessTestData.CreateSyntheticProbeTarget(1),
                    RelationalAccessTestData.CreateSyntheticNaturalKeyEntries(1, 1)
                )
            )
        );
        var many = MssqlNaturalKeyLookupCommandBuilder.Build(
            CreateBatch(
                new NaturalKeyProbeLookupGroup(
                    RelationalAccessTestData.SchoolResource,
                    RelationalAccessTestData.CreateSyntheticProbeTarget(1),
                    RelationalAccessTestData.CreateSyntheticNaturalKeyEntries(5000, 1)
                )
            )
        );

        many.CommandText.Should()
            .Be(
                single.CommandText,
                "N lives in the JSON payload, so 5000 entries reuse the one-entry statement and its plan"
            );
        many.Parameters.Should().HaveCount(1);
        ParseJsonArray(many.Parameters[0]).Should().HaveCount(5000);
    }

    [Test]
    public void It_keeps_the_statement_shape_for_an_empty_group()
    {
        var empty = MssqlNaturalKeyLookupCommandBuilder.Build(
            CreateBatch(
                new NaturalKeyProbeLookupGroup(
                    RelationalAccessTestData.SchoolResource,
                    RelationalAccessTestData.CreateSchoolProbeTarget(),
                    []
                )
            )
        );
        var populated = MssqlNaturalKeyLookupCommandBuilder.Build(
            CreateBatch(
                new NaturalKeyProbeLookupGroup(
                    RelationalAccessTestData.SchoolResource,
                    RelationalAccessTestData.CreateSchoolProbeTarget(),
                    RelationalAccessTestData.CreateNaturalKeyEntries([
                        [255901],
                    ])
                )
            )
        );

        empty
            .CommandText.Should()
            .Be(
                populated.CommandText,
                "an empty JSON array shreds to zero rows, so the group still owes the reader one result set"
            );
        empty.CommandText.Should().NotContain("WHERE 1 = 0");
        empty.Parameters.Should().HaveCount(1, "the group still binds its payload parameter");
        empty.Parameters[0].Value.Should().Be("[]");
    }

    [Test]
    public void It_never_lets_an_identity_value_alter_the_sql_text()
    {
        const string Hostile = "');DROP TABLE [edfi].[School];--\"\\ x] [y";

        var command = MssqlNaturalKeyLookupCommandBuilder.Build(
            CreateBatch(
                new NaturalKeyProbeLookupGroup(
                    RelationalAccessTestData.StudentSectionAssociationResource,
                    RelationalAccessTestData.CreateStudentSectionAssociationProbeTarget(),
                    RelationalAccessTestData.CreateNaturalKeyEntries([
                        [
                            new DateOnly(2026, 8, 17),
                            Hostile,
                            255901L,
                            2026,
                            Hostile,
                            "Fall Semester",
                            "10001",
                        ],
                    ])
                )
            )
        );

        var benign = MssqlNaturalKeyLookupCommandBuilder.Build(
            CreateBatch(
                new NaturalKeyProbeLookupGroup(
                    RelationalAccessTestData.StudentSectionAssociationResource,
                    RelationalAccessTestData.CreateStudentSectionAssociationProbeTarget(),
                    RelationalAccessTestData.CreateNaturalKeyEntries([
                        [
                            new DateOnly(2026, 8, 17),
                            "ALG-1",
                            255901L,
                            2026,
                            "Section-1",
                            "Fall Semester",
                            "10001",
                        ],
                    ])
                )
            )
        );

        command
            .CommandText.Should()
            .Be(benign.CommandText, "values never reach the SQL text — only the JSON payload");
        command.CommandText.Should().NotContain("DROP TABLE");
        ParseJsonArray(command.Parameters[0])[0]
            .GetProperty("v1")
            .GetString()
            .Should()
            .Be(Hostile, "the value round-trips through JSON escaping unchanged");
    }

    [Test]
    public void It_caches_command_text_per_mapping_set_and_group_shape()
    {
        var first = MssqlNaturalKeyLookupCommandBuilder.Build(CreateProgramBatch());
        var second = MssqlNaturalKeyLookupCommandBuilder.Build(CreateProgramBatch());

        ReferenceEquals(first.CommandText, second.CommandText).Should().BeTrue();

        var otherShape = MssqlNaturalKeyLookupCommandBuilder.Build(
            CreateBatch(
                new NaturalKeyProbeLookupGroup(
                    RelationalAccessTestData.SchoolResource,
                    RelationalAccessTestData.CreateSchoolProbeTarget(),
                    RelationalAccessTestData.CreateNaturalKeyEntries([
                        [255901],
                    ])
                )
            )
        );

        otherShape.CommandText.Should().NotBe(first.CommandText);
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
        command.CommandText.Should().NotContain("ROW_NUMBER()");
    }

    [Test]
    public void It_keeps_a_bulk_batch_in_one_command_of_one_parameter()
    {
        var command = MssqlNaturalKeyLookupCommandBuilder.Build(
            CreateBatch(
                new NaturalKeyProbeLookupGroup(
                    RelationalAccessTestData.StudentSectionAssociationResource,
                    RelationalAccessTestData.CreateStudentSectionAssociationProbeTarget(),
                    CreateStudentSectionAssociationEntries(2500)
                )
            )
        );

        command
            .Parameters.Should()
            .HaveCount(
                1,
                "2500 x 7 probe values used to be 17500 bound parameters; they are now one JSON payload"
            );
        command
            .Parameters.Count.Should()
            .BeLessThanOrEqualTo(MssqlNaturalKeyLookupCommandBuilder.MssqlMaxCommandParameters);
        ParseJsonArray(command.Parameters[0]).Should().HaveCount(2500);
    }

    [Test]
    public void It_rejects_a_batch_with_more_groups_than_the_command_parameter_ceiling()
    {
        var groups = Enumerable
            .Range(0, MssqlNaturalKeyLookupCommandBuilder.MssqlMaxCommandParameters + 1)
            .Select(_ =>
                (NaturalKeyLookupGroup)
                    new NaturalKeyProbeLookupGroup(
                        RelationalAccessTestData.SchoolResource,
                        RelationalAccessTestData.CreateSchoolProbeTarget(),
                        RelationalAccessTestData.CreateNaturalKeyEntries([
                            [255901],
                        ])
                    )
            )
            .ToArray();

        var act = () => MssqlNaturalKeyLookupCommandBuilder.Build(CreateBatch(groups));

        act.Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithMessage("*at most 2098 bound parameters per command*");
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

    private static NaturalKeyLookupBatch CreateThreeGroupBatch() =>
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

    private static void AssertJsonParameter(RelationalParameter parameter)
    {
        SqlParameter sqlParameter = new();
        parameter.ConfigureParameter.Should().NotBeNull();
        parameter.ConfigureParameter!(sqlParameter);

        sqlParameter.SqlDbType.Should().Be(SqlDbType.NVarChar);
        sqlParameter.Size.Should().Be(-1, "the payload grows with the batch, so it must be nvarchar(max)");
    }

    private static IReadOnlyList<JsonElement> ParseJsonArray(RelationalParameter parameter)
    {
        using var document = JsonDocument.Parse((string)parameter.Value!);

        return [.. document.RootElement.EnumerateArray().Select(element => element.Clone())];
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
