// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Postgresql;
using FluentAssertions;
using Npgsql;
using NpgsqlTypes;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_PostgresqlNaturalKeyLookupCommandBuilder
{
    private static readonly MappingSet _mappingSet =
        RelationalAccessTestData.CreateNaturalKeyProbeMappingSet();

    [Test]
    public void It_probes_the_target_root_with_typed_unnest_parallel_arrays()
    {
        var command = PostgresqlNaturalKeyLookupCommandBuilder.Build(
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
            .Contain("FROM unnest(", "the batch input is a set of parallel arrays, not per-entry parameters");
        command
            .CommandText.Should()
            .Contain("WITH ORDINALITY", "the request ordinal comes from the array position");
        command.CommandText.Should().Contain("JOIN \"edfi\".\"School\" t");
        command
            .CommandText.Should()
            .Contain(
                "t.\"SchoolId\" = input.\"c0\"",
                "the probe predicate must name the RefKey storage columns so the index can seek"
            );
        command
            .CommandText.Should()
            .Contain("SELECT input.\"Ordinal\" AS \"Ordinal\", t.\"DocumentId\" AS \"DocumentId\"");
        command
            .CommandText.Should()
            .NotContain(
                "ReferentialIdentity",
                "the natural-key probe replaces the referential-identity join"
            );
        command.CommandText.Should().NotContain("dms.\"Document\"");
    }

    [Test]
    public void It_uses_the_same_sql_shape_regardless_of_entry_count()
    {
        var probe = RelationalAccessTestData.CreateStudentSectionAssociationProbeTarget();

        var shapeCommand = PostgresqlNaturalKeyLookupCommandBuilder.Build(
            CreateBatch(
                new NaturalKeyProbeLookupGroup(
                    RelationalAccessTestData.StudentSectionAssociationResource,
                    probe,
                    CreateStudentSectionAssociationEntries(1)
                )
            )
        );
        var largeCommand = PostgresqlNaturalKeyLookupCommandBuilder.Build(
            CreateBatch(
                new NaturalKeyProbeLookupGroup(
                    RelationalAccessTestData.StudentSectionAssociationResource,
                    probe,
                    CreateStudentSectionAssociationEntries(5000)
                )
            )
        );

        largeCommand.CommandText.Should().Be(shapeCommand.CommandText);
        largeCommand
            .Parameters.Should()
            .HaveCount(7, "parallel arrays bind one parameter per probe column, never per entry");
    }

    [Test]
    public void It_binds_one_array_parameter_per_probe_column_in_refkey_order()
    {
        var command = PostgresqlNaturalKeyLookupCommandBuilder.Build(
            CreateBatch(
                new NaturalKeyProbeLookupGroup(
                    RelationalAccessTestData.StudentSectionAssociationResource,
                    RelationalAccessTestData.CreateStudentSectionAssociationProbeTarget(),
                    CreateStudentSectionAssociationEntries(3)
                )
            )
        );

        command
            .Parameters.Select(parameter => parameter.Name)
            .Should()
            .Equal("@g0c0", "@g0c1", "@g0c2", "@g0c3", "@g0c4", "@g0c5", "@g0c6");
        ((DateOnly[])command.Parameters[0].Value!).Should().HaveCount(3);
        ((string[])command.Parameters[1].Value!).Should().Equal("ALG-1", "ALG-1", "ALG-1");
        ((long[])command.Parameters[2].Value!).Should().Equal(255901L, 255901L, 255901L);
    }

    [Test]
    public void It_maps_each_scalar_kind_to_its_postgresql_array_element_type()
    {
        var command = PostgresqlNaturalKeyLookupCommandBuilder.Build(
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

        command.CommandText.Should().Contain("@g0c0::varchar[]");
        command.CommandText.Should().Contain("@g0c1::integer[]");
        command.CommandText.Should().Contain("@g0c2::bigint[]");
        command.CommandText.Should().Contain("@g0c3::numeric[]");
        command.CommandText.Should().Contain("@g0c4::boolean[]");
        command.CommandText.Should().Contain("@g0c5::date[]");
        command.CommandText.Should().Contain("@g0c6::timestamptz[]");
        command.CommandText.Should().Contain("@g0c7::time[]");

        AssertArrayDbType(command.Parameters[0], NpgsqlDbType.Varchar);
        AssertArrayDbType(command.Parameters[1], NpgsqlDbType.Integer);
        AssertArrayDbType(command.Parameters[2], NpgsqlDbType.Bigint);
        AssertArrayDbType(command.Parameters[3], NpgsqlDbType.Numeric);
        AssertArrayDbType(command.Parameters[4], NpgsqlDbType.Boolean);
        AssertArrayDbType(command.Parameters[5], NpgsqlDbType.Date);
        AssertArrayDbType(command.Parameters[6], NpgsqlDbType.TimestampTz);
        AssertArrayDbType(command.Parameters[7], NpgsqlDbType.Time);

        command.Parameters[0].Value.Should().BeOfType<string[]>();
        command.Parameters[1].Value.Should().BeOfType<int[]>();
        command.Parameters[2].Value.Should().BeOfType<long[]>();
        command.Parameters[3].Value.Should().BeOfType<decimal[]>();
        command.Parameters[4].Value.Should().BeOfType<bool[]>();
        command.Parameters[5].Value.Should().BeOfType<DateOnly[]>();
        command.Parameters[6].Value.Should().BeOfType<DateTime[]>();
        command.Parameters[7].Value.Should().BeOfType<TimeOnly[]>();
    }

    [Test]
    public void It_joins_dms_descriptor_by_urilowered_for_descriptor_valued_identity_parts()
    {
        var command = PostgresqlNaturalKeyLookupCommandBuilder.Build(CreateProgramBatch());

        command.CommandText.Should().Contain("INNER JOIN \"dms\".\"Descriptor\" d2");
        command.CommandText.Should().Contain("d2.\"UriLowered\" = input.\"c2\"");
        command
            .CommandText.Should()
            .Contain(
                "d2.\"Discriminator\" = 'ProgramTypeDescriptor'",
                "the compiled per-resource discriminator literal pins the descriptor type"
            );
        command
            .CommandText.Should()
            .Contain(
                "t.\"ProgramTypeDescriptor_DescriptorId\" = d2.\"DocumentId\"",
                "the resolved descriptor document id completes the target's RefKey predicate"
            );
        command
            .CommandText.Should()
            .Contain(
                "@g0c2::varchar[]",
                "a descriptor part binds the lower-cased URI, not the descriptor document id"
            );
        AssertArrayDbType(command.Parameters[2], NpgsqlDbType.Varchar);
        ((string[])command.Parameters[2].Value!)
            .Should()
            .Equal("uri://ed-fi.org/programtypedescriptor#athletics");
    }

    [Test]
    public void It_probes_the_abstract_identity_table_and_projects_the_discriminator()
    {
        var command = PostgresqlNaturalKeyLookupCommandBuilder.Build(
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

        command
            .CommandText.Should()
            .Contain(
                "INNER JOIN \"edfi\".\"EducationOrganizationIdentity\" t",
                "abstract references seek the identity table's RefKey index, never the union view"
            );
        command.CommandText.Should().NotContain("_View");
        command.CommandText.Should().Contain("t.\"EducationOrganizationId\" = input.\"c0\"");
        command
            .CommandText.Should()
            .Contain(
                "t.\"Discriminator\" AS \"Discriminator\"",
                "the caller needs the concrete subtype to validate the reference"
            );
    }

    [Test]
    public void It_probes_the_descriptor_table_by_urilowered_only_and_projects_the_type_columns()
    {
        var command = PostgresqlNaturalKeyLookupCommandBuilder.Build(
            CreateBatch(
                new DescriptorLookupGroup(
                    RelationalAccessTestData.SchoolTypeDescriptorResource,
                    RelationalAccessTestData.CreateNaturalKeyEntries([
                        ["uri://ed-fi.org/schooltypedescriptor#alternative"],
                    ])
                )
            )
        );

        command.CommandText.Should().Contain("INNER JOIN \"dms\".\"Descriptor\" descriptor");
        command.CommandText.Should().Contain("descriptor.\"UriLowered\" = input.\"c0\"");
        command
            .CommandText.Should()
            .NotContain(
                "descriptor.\"Discriminator\" =",
                "a descriptor target seeks UriLowered alone so a wrong-type URI still returns a row to classify"
            );
        command.CommandText.Should().Contain("descriptor.\"Discriminator\" AS \"Discriminator\"");
        command.CommandText.Should().Contain("descriptor.\"ResourceKeyId\" AS \"ResourceKeyId\"");
        command.CommandText.Should().Contain("@g0c0::varchar[]");
    }

    [Test]
    public void It_emits_one_statement_per_target_group_in_group_order()
    {
        var command = PostgresqlNaturalKeyLookupCommandBuilder.Build(
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

        CountOccurrences(command.CommandText, "SELECT input.\"Ordinal\"")
            .Should()
            .Be(3, "each group is one statement and therefore one result set");
        command
            .CommandText.IndexOf("\"edfi\".\"School\" t", StringComparison.Ordinal)
            .Should()
            .BeLessThan(
                command.CommandText.IndexOf(
                    "\"edfi\".\"EducationOrganizationIdentity\" t",
                    StringComparison.Ordinal
                ),
                "result sets arrive in group order"
            );
        command
            .CommandText.IndexOf("\"edfi\".\"EducationOrganizationIdentity\" t", StringComparison.Ordinal)
            .Should()
            .BeLessThan(
                command.CommandText.IndexOf("\"dms\".\"Descriptor\" descriptor", StringComparison.Ordinal)
            );
        command
            .Parameters.Select(parameter => parameter.Name)
            .Should()
            .Equal(
                ["@g0c0", "@g1c0", "@g2c0"],
                "parameter names are namespaced by group index so groups cannot collide"
            );
    }

    [Test]
    public void It_keeps_the_statement_shape_for_an_empty_group()
    {
        var emptyCommand = PostgresqlNaturalKeyLookupCommandBuilder.Build(
            CreateBatch(
                new NaturalKeyProbeLookupGroup(
                    RelationalAccessTestData.SchoolResource,
                    RelationalAccessTestData.CreateSchoolProbeTarget(),
                    []
                )
            )
        );
        var populatedCommand = PostgresqlNaturalKeyLookupCommandBuilder.Build(
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

        emptyCommand.CommandText.Should().Be(populatedCommand.CommandText);
        ((int[])emptyCommand.Parameters[0].Value!).Should().BeEmpty();
    }

    [Test]
    public void It_caches_command_text_per_mapping_set_and_group_shape()
    {
        var first = PostgresqlNaturalKeyLookupCommandBuilder.Build(CreateProgramBatch());
        var second = PostgresqlNaturalKeyLookupCommandBuilder.Build(CreateProgramBatch());

        ReferenceEquals(first.CommandText, second.CommandText)
            .Should()
            .BeTrue("command text is compiled once per mapping set and group shape");
    }

    [Test]
    public void It_rejects_entries_whose_ordinals_are_not_the_one_based_position()
    {
        var act = () =>
            PostgresqlNaturalKeyLookupCommandBuilder.Build(
                CreateBatch(
                    new NaturalKeyProbeLookupGroup(
                        RelationalAccessTestData.SchoolResource,
                        RelationalAccessTestData.CreateSchoolProbeTarget(),
                        [new NaturalKeyLookupEntry(0, [255901]), new NaturalKeyLookupEntry(1, [255902])]
                    )
                )
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*ordinals must be the one-based entry position*");
    }

    [Test]
    public void It_rejects_a_descriptor_part_whose_discriminator_literal_was_never_compiled()
    {
        var mappingSetWithoutDescriptorLiterals =
            RelationalAccessTestData.CreateNaturalKeyProbeMappingSet() with
            {
                DescriptorProbeTarget = new DescriptorProbeTarget(
                    new DbTableName(new DbSchemaName("dms"), "Descriptor"),
                    DescriptorProbeColumns.UriLowered,
                    new DbColumnName("Discriminator"),
                    new Dictionary<QualifiedResourceName, string>()
                ),
            };

        var act = () =>
            PostgresqlNaturalKeyLookupCommandBuilder.Build(
                new NaturalKeyLookupBatch(
                    mappingSetWithoutDescriptorLiterals,
                    [
                        new NaturalKeyProbeLookupGroup(
                            RelationalAccessTestData.ProgramResource,
                            RelationalAccessTestData.CreateProgramProbeTarget(),
                            RelationalAccessTestData.CreateNaturalKeyEntries([
                                [255901L, "Athletics", "uri://ed-fi.org/programtypedescriptor#athletics"],
                            ])
                        ),
                    ]
                )
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*missing a compiled descriptor discriminator literal*Ed-Fi.ProgramTypeDescriptor*");
    }

    [Test]
    public void It_never_reads_the_document_or_referential_identity_tables()
    {
        var command = PostgresqlNaturalKeyLookupCommandBuilder.Build(
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
        command.CommandText.Should().NotContain("dms.\"Document\"");
        command.CommandText.Should().NotContain("\"dms\".\"Document\"");
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

    private static void AssertArrayDbType(RelationalParameter parameter, NpgsqlDbType elementDbType)
    {
        NpgsqlParameter npgsqlParameter = new();
        parameter.ConfigureParameter.Should().NotBeNull();
        parameter.ConfigureParameter!(npgsqlParameter);

        npgsqlParameter
            .NpgsqlDbType.Should()
            .Be((NpgsqlDbType)((int)NpgsqlDbType.Array | (int)elementDbType));
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
