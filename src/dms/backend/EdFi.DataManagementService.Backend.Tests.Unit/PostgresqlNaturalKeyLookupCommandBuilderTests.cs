// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Postgresql;
using FluentAssertions;
using Npgsql;
using NpgsqlTypes;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Tests.Unit.NaturalKeyLookupTestMappingSets;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_Postgresql_Natural_Key_Lookup_Command_Builder
{
    private const string ProgramTypeUri =
        "uri://ed-fi.org/ProgramTypeDescriptor#Career and Technical Education";

    private static readonly string StatementSeparator = ";" + Environment.NewLine;
    private static readonly string UnionSeparator = Environment.NewLine + "UNION ALL" + Environment.NewLine;

    private readonly PostgresqlNaturalKeyLookupCommandBuilder _builder = new();

    private MappingSet _mappingSet = null!;
    private NaturalKeyLookupRequest _mixedRequest = null!;
    private RelationalCommand _mixedUnionCommand = null!;
    private RelationalCommand _mixedMultiStatementCommand = null!;
    private IReadOnlyList<string> _statements = null!;

    [SetUp]
    public void Setup()
    {
        _mappingSet = Ds52(SqlDialect.Pgsql);

        _mixedRequest = new NaturalKeyLookupRequest(
            _mappingSet,
            [
                Group(1, ProbeTarget(_mappingSet, School), [255901L]),
                Group(2, ProbeTarget(_mappingSet, EducationOrganization), [255901L]),
                Group(3, ProbeTarget(_mappingSet, Program), [255901L, "Career Prep", ProgramTypeUri]),
                Group(4, ProbeTarget(_mappingSet, CourseOffering), ["ALG-1", 255901L, 255901L, 2026, "Fall"]),
                Group(
                    5,
                    ProbeTarget(_mappingSet, GeneralStudentProgramAssociation),
                    [new DateOnly(2026, 8, 20), 255901L, 255901L, "Career Prep", ProgramTypeUri, "S-1"]
                ),
                Group(6, DescriptorTarget(_mappingSet, ProgramTypeDescriptor), [ProgramTypeUri]),
            ]
        );

        _mixedUnionCommand = _builder.BuildEmbeddableCommand(_mixedRequest);
        _mixedMultiStatementCommand = _builder.BuildCommand(_mixedRequest);
        _statements = SplitStatements(_mixedMultiStatementCommand.CommandText);
    }

    [Test]
    public void It_emits_the_same_text_for_one_and_five_hundred_entries()
    {
        NaturalKeyLookupRequest Request(int entryCount) =>
            new(
                _mappingSet,
                [
                    new NaturalKeyLookupGroup(
                        1,
                        ProbeTarget(_mappingSet, Program),
                        [
                            .. Enumerable
                                .Range(1, entryCount)
                                .Select(index => new NaturalKeyLookupEntry([
                                    (long)index,
                                    $"Program {index}",
                                    ProgramTypeUri,
                                ])),
                        ]
                    ),
                ]
            );

        var single = _builder.BuildEmbeddableCommand(Request(1));
        var large = _builder.BuildEmbeddableCommand(Request(500));

        large.CommandText.Should().Be(single.CommandText);
        string[] expectedParameterNames = ["@g1_o", "@g1_k1", "@g1_k2", "@g1_k3"];
        large.Parameters.Select(parameter => parameter.Name).Should().Equal(expectedParameterNames);
        ((int[])large.Parameters[0].Value!).Should().Equal(Enumerable.Range(1, 500));
        _builder
            .BuildCommand(Request(500))
            .CommandText.Should()
            .Be(_builder.BuildCommand(Request(1)).CommandText);
    }

    [Test]
    public void It_emits_the_exact_program_statement()
    {
        var programResourceKeyId = _mappingSet.ResourceKeyIdByResource[Program];
        var programTypeResourceKeyId = _mappingSet.ResourceKeyIdByResource[ProgramTypeDescriptor];

        _statements[2]
            .Should()
            .Be(
                string.Join(
                    Environment.NewLine,
                    $"""SELECT CAST(3 AS integer) AS "GroupOrdinal", keys."o" AS "Ordinal", target."DocumentId" AS "DocumentId", CAST(NULL AS integer) AS "DescriptorId", CAST({programResourceKeyId} AS smallint) AS "ResourceKeyId" """.TrimEnd(),
                    """FROM unnest(@g3_o::integer[], @g3_k1::bigint[], @g3_k2::text[], @g3_k3::text[]) AS keys("o", "k1", "k2", "k3")""",
                    """INNER JOIN "dms"."Descriptor" d1""",
                    """    ON lower((d1."Namespace" || '#' || d1."CodeValue") COLLATE "pg_c_utf8") = lower(keys."k3" COLLATE "pg_c_utf8")""",
                    $"""   AND d1."ResourceKeyId" = CAST({programTypeResourceKeyId} AS smallint)""",
                    """INNER JOIN "edfi"."Program" target""",
                    """    ON target."EducationOrganization_EducationOrganizationId" = keys."k1" """.TrimEnd(),
                    """   AND target."ProgramName" = keys."k2" """.TrimEnd(),
                    """   AND target."ProgramTypeDescriptor_DescriptorId" = d1."DescriptorId";"""
                )
            );
    }

    [Test]
    public void It_collates_every_descriptor_fold_on_the_column_and_the_parameter_side()
    {
        var text = _mixedUnionCommand.CommandText;

        var lowerCalls = Regex.Matches(text, @"lower\(").Count;
        var columnFolds = Regex
            .Matches(
                text,
                """lower\(\((d\d*)\."Namespace" \|\| '#' \|\| \1\."CodeValue"\) COLLATE "pg_c_utf8"\)"""
            )
            .Count;
        var parameterFolds = Regex.Matches(text, """lower\(keys\."k\d+" COLLATE "pg_c_utf8"\)""").Count;

        // Program, GeneralStudentProgramAssociation and the descriptor target each fold once per side.
        columnFolds.Should().Be(3);
        parameterFolds.Should().Be(3);
        lowerCalls.Should().Be(columnFolds + parameterFolds);
    }

    [Test]
    public void It_never_numbers_limits_or_dedupes_rows_in_sql()
    {
        var text = _mixedUnionCommand.CommandText + _mixedMultiStatementCommand.CommandText;

        text.Should().NotContain("WITH ORDINALITY");
        text.Should().NotContain("LIMIT");
        text.Should().NotContain("DISTINCT");
        text.Should().NotContain("ORDER BY");
    }

    [Test]
    public void It_reads_the_unnested_keys_before_any_joined_table_in_every_group()
    {
        foreach (var statement in _statements)
        {
            var fromIndex = statement.IndexOf("FROM unnest(", StringComparison.Ordinal);

            fromIndex.Should().BeGreaterThan(0);
            statement.IndexOf("INNER JOIN", StringComparison.Ordinal).Should().BeGreaterThan(fromIndex);
            statement.Should().NotContain(" JOIN unnest(");
        }
    }

    [Test]
    public void It_projects_the_concrete_resource_key_as_a_literal_and_a_null_descriptor_id()
    {
        var schoolResourceKeyId = _mappingSet.ResourceKeyIdByResource[School];

        _statements[0]
            .Should()
            .Contain($"""CAST({schoolResourceKeyId} AS smallint) AS "ResourceKeyId" """.TrimEnd());
        _statements[0].Should().Contain("""CAST(NULL AS integer) AS "DescriptorId" """.TrimEnd());
        _statements[0].Should().Contain("""target."DocumentId" AS "DocumentId" """.TrimEnd());
        _statements[0].Should().Contain("""INNER JOIN "edfi"."School" target""");
        _statements[0].Should().Contain("""ON target."SchoolId" = keys."k1" """.TrimEnd());
    }

    [Test]
    public void It_projects_the_concrete_member_key_from_the_abstract_identity_table()
    {
        _statements[1].Should().Contain("""INNER JOIN "edfi"."EducationOrganizationIdentity" target""");
        _statements[1].Should().Contain("""target."ResourceKeyId" AS "ResourceKeyId" """.TrimEnd());
        _statements[1].Should().Contain("""CAST(NULL AS integer) AS "DescriptorId" """.TrimEnd());
        _statements[1].Should().NotContain("AS smallint");
        _mixedUnionCommand.CommandText.Should().NotContain("Discriminator");
    }

    [Test]
    public void It_projects_both_descriptor_keys_from_the_descriptor_row_for_a_descriptor_target()
    {
        var programTypeResourceKeyId = _mappingSet.ResourceKeyIdByResource[ProgramTypeDescriptor];

        _statements[5].Should().Contain("""d."DocumentId" AS "DocumentId" """.TrimEnd());
        _statements[5].Should().Contain("""d."DescriptorId" AS "DescriptorId" """.TrimEnd());
        _statements[5].Should().Contain("""d."ResourceKeyId" AS "ResourceKeyId" """.TrimEnd());
        _statements[5].Should().Contain("""INNER JOIN "dms"."Descriptor" d""");
        _statements[5]
            .Should()
            .Contain($"""AND d."ResourceKeyId" = CAST({programTypeResourceKeyId} AS smallint)""");
    }

    [Test]
    public void It_joins_a_concrete_descriptor_part_on_the_compact_descriptor_id()
    {
        _statements[2]
            .Should()
            .Contain("""target."ProgramTypeDescriptor_DescriptorId" = d1."DescriptorId" """.TrimEnd());
        _statements[2].Should().NotContain("""= d1."DocumentId" """.TrimEnd());
    }

    [Test]
    public void It_joins_a_copied_descriptor_identity_on_an_abstract_target_by_the_compact_descriptor_id()
    {
        _statements[4]
            .Should()
            .Contain("""INNER JOIN "edfi"."GeneralStudentProgramAssociationIdentity" target""");
        _statements[4]
            .Should()
            .Contain(
                """target."Program_ProgramTypeDescriptor_DescriptorId" = d1."DescriptorId" """.TrimEnd()
            );
        _statements[4].Should().NotContain("""= d1."DocumentId" """.TrimEnd());
        _statements[4].Should().Contain("""target."ResourceKeyId" AS "ResourceKeyId" """.TrimEnd());
        _statements[4].Should().Contain("""CAST(NULL AS integer) AS "DescriptorId" """.TrimEnd());
    }

    [Test]
    public void It_emits_one_predicate_per_key_unified_path()
    {
        _statements[3].Should().Contain("""target."SchoolId_Unified" = keys."k2" """.TrimEnd());
        _statements[3].Should().Contain("""target."SchoolId_Unified" = keys."k3" """.TrimEnd());
        Regex.Matches(_statements[3], "\"SchoolId_Unified\"").Count.Should().Be(2);
    }

    [Test]
    public void It_joins_the_groups_into_one_union_statement_for_the_embeddable_form()
    {
        var text = _mixedUnionCommand.CommandText;

        text.Should().NotContain(";");
        text.Split(UnionSeparator).Should().HaveCount(_mixedRequest.Groups.Count);
        text.Split(UnionSeparator).Select(statement => statement + ";").Should().Equal(_statements);
        _mixedUnionCommand
            .Parameters.Select(parameter => parameter.Name)
            .Should()
            .Equal(_mixedMultiStatementCommand.Parameters.Select(parameter => parameter.Name));
        _mixedUnionCommand
            .Parameters.Select(parameter => parameter.Value)
            .Should()
            .BeEquivalentTo(
                _mixedMultiStatementCommand.Parameters.Select(parameter => parameter.Value),
                options => options.WithStrictOrdering()
            );
    }

    [Test]
    public void It_terminates_one_statement_per_group_for_the_standalone_form()
    {
        _mixedMultiStatementCommand.CommandText.Should().NotContain("UNION ALL");
        _statements.Should().HaveCount(_mixedRequest.Groups.Count);
        _statements.Should().OnlyContain(statement => statement.EndsWith(';'));

        for (var index = 0; index < _statements.Count; index++)
        {
            _statements[index].Should().StartWith($"SELECT CAST({index + 1} AS integer) AS \"GroupOrdinal\"");
        }
    }

    [Test]
    public void It_binds_descriptor_uris_unchanged()
    {
        var descriptorValues = (string[])
            _mixedUnionCommand.Parameters.Single(parameter => parameter.Name == "@g6_k1").Value!;

        descriptorValues.Should().ContainSingle().Which.Should().Be(ProgramTypeUri);
    }

    [Test]
    public void It_binds_a_typed_array_for_every_scalar_kind()
    {
        (ScalarKind Kind, object Value, string SqlType, NpgsqlDbType DbType)[] cases =
        [
            (ScalarKind.String, "Fall", "text", NpgsqlDbType.Text),
            (ScalarKind.Int16, (short)7, "smallint", NpgsqlDbType.Smallint),
            (ScalarKind.Int32, 2026, "integer", NpgsqlDbType.Integer),
            (ScalarKind.Int64, 255901L, "bigint", NpgsqlDbType.Bigint),
            (ScalarKind.Decimal, 12.50m, "numeric", NpgsqlDbType.Numeric),
            (ScalarKind.Boolean, true, "boolean", NpgsqlDbType.Boolean),
            (ScalarKind.Date, new DateOnly(2026, 8, 20), "date", NpgsqlDbType.Date),
            (
                ScalarKind.DateTime,
                new DateTime(2026, 8, 20, 13, 45, 0, DateTimeKind.Utc),
                "timestamp with time zone",
                NpgsqlDbType.TimestampTz
            ),
            (ScalarKind.Time, new TimeOnly(13, 45, 0), "time", NpgsqlDbType.Time),
        ];

        var probe = new NaturalKeyProbeTarget(
            new QualifiedResourceName("Ed-Fi", "Synthetic"),
            NaturalKeyProbeTargetKind.Concrete,
            new DbTableName(new DbSchemaName("edfi"), "Synthetic"),
            [
                .. cases.Select(
                    (testCase, index) =>
                        new NaturalKeyProbeKeyColumn(
                            new JsonPathExpression($"$.key{index + 1}", []),
                            new DbColumnName($"Key{index + 1}"),
                            new NaturalKeyProbeKeyBinding.Scalar(new RelationalScalarType(testCase.Kind))
                        )
                ),
            ],
            new DbColumnName("DocumentId")
        );

        var command = _builder.BuildCommand(
            new NaturalKeyLookupRequest(
                _mappingSet,
                [
                    new NaturalKeyLookupGroup(
                        1,
                        NaturalKeyLookupTarget.ForProbe(probe, 1),
                        [new NaturalKeyLookupEntry([.. cases.Select(testCase => testCase.Value)])]
                    ),
                ]
            )
        );

        ParameterDbType(command.Parameters[0]).Should().Be(ArrayOf(NpgsqlDbType.Integer));

        for (var index = 0; index < cases.Length; index++)
        {
            var parameter = command.Parameters[index + 1];
            var testCase = cases[index];

            parameter.Name.Should().Be($"@g1_k{index + 1}");
            ParameterDbType(parameter).Should().Be(ArrayOf(testCase.DbType), testCase.Kind.ToString());
            ((Array)parameter.Value!).GetValue(0).Should().Be(testCase.Value);
            parameter.Value!.GetType().GetElementType().Should().Be(testCase.Value.GetType());
            command.CommandText.Should().Contain($"@g1_k{index + 1}::{testCase.SqlType}[]");
        }
    }

    [Test]
    public void It_rejects_a_request_without_groups()
    {
        var act = () => _builder.BuildEmbeddableCommand(new NaturalKeyLookupRequest(_mappingSet, []));

        act.Should().Throw<ArgumentException>();
    }

    private static NaturalKeyLookupGroup Group(
        int groupOrdinal,
        NaturalKeyLookupTarget target,
        IReadOnlyList<object> keyValues
    ) => new(groupOrdinal, target, [new NaturalKeyLookupEntry(keyValues)]);

    private static IReadOnlyList<string> SplitStatements(string commandText) =>
        [
            .. commandText
                .Split(StatementSeparator)
                .Select(statement => statement.EndsWith(';') ? statement : statement + ";"),
        ];

    private static NpgsqlDbType ArrayOf(NpgsqlDbType elementType) =>
        (NpgsqlDbType)((int)NpgsqlDbType.Array | (int)elementType);

    private static NpgsqlDbType ParameterDbType(RelationalParameter parameter)
    {
        var npgsqlParameter = new NpgsqlParameter();
        parameter.ConfigureParameter!(npgsqlParameter);

        return npgsqlParameter.NpgsqlDbType;
    }
}
