// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Tests.Unit.NaturalKeyLookupTestMappingSets;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_Mssql_Natural_Key_Lookup_Command_Builder
{
    private const string Collation = "SQL_Latin1_General_CP1_CI_AS";
    private const string ForceOrder = "OPTION (FORCE ORDER)";
    private const string ProgramTypeUri =
        "uri://ed-fi.org/ProgramTypeDescriptor#Career and Technical Education";

    private static readonly string StatementSeparator = ";" + Environment.NewLine;
    private static readonly string UnionSeparator = Environment.NewLine + "UNION ALL" + Environment.NewLine;

    private readonly MssqlNaturalKeyLookupCommandBuilder _builder = new();

    private MappingSet _mappingSet = null!;
    private NaturalKeyLookupRequest _mixedRequest = null!;
    private RelationalCommand _mixedUnionCommand = null!;
    private RelationalCommand _mixedMultiStatementCommand = null!;
    private IReadOnlyList<string> _statements = null!;

    [SetUp]
    public void Setup()
    {
        _mappingSet = Ds52(SqlDialect.Mssql);

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

        var large = _builder.BuildEmbeddableCommand(Request(500));

        large.CommandText.Should().Be(_builder.BuildEmbeddableCommand(Request(1)).CommandText);
        large.Parameters.Should().ContainSingle().Which.Name.Should().Be("@g1_keys");
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
                    $"SELECT CAST(3 AS int) AS [GroupOrdinal], keys.o AS [Ordinal], target.[DocumentId] AS [DocumentId], CAST(NULL AS int) AS [DescriptorId], CAST({programResourceKeyId} AS smallint) AS [ResourceKeyId]",
                    "FROM OPENJSON(@g3_keys) WITH (o int '$.o', k1 bigint '$.k1', k2 nvarchar(60) '$.k2', k3 nvarchar(4000) '$.k3') AS keys",
                    "INNER JOIN [dms].[Descriptor] AS d1",
                    $"    ON d1.[UriLowered] = LOWER(keys.k3 COLLATE {Collation})",
                    $"   AND d1.[ResourceKeyId] = CAST({programTypeResourceKeyId} AS smallint)",
                    "INNER JOIN [edfi].[Program] AS target",
                    "    ON target.[EducationOrganization_EducationOrganizationId] = keys.k1",
                    $"   AND target.[ProgramName] = keys.k2 COLLATE {Collation}",
                    "   AND target.[ProgramTypeDescriptor_DescriptorId] = d1.[DescriptorId]",
                    $"{ForceOrder};"
                )
            );
    }

    [Test]
    public void It_reads_openjson_before_any_joined_table_in_every_branch()
    {
        foreach (var branch in _mixedUnionCommand.CommandText.Split(UnionSeparator))
        {
            var fromIndex = branch.IndexOf("FROM OPENJSON(", StringComparison.Ordinal);

            fromIndex.Should().BeGreaterThan(0);
            Regex.Matches(branch, @"\bFROM\b").Count.Should().Be(1);
            branch.IndexOf("INNER JOIN", StringComparison.Ordinal).Should().BeGreaterThan(fromIndex);
        }
    }

    [Test]
    public void It_forces_join_order_once_at_the_end_of_the_union_statement()
    {
        var text = _mixedUnionCommand.CommandText;

        Regex.Matches(text, Regex.Escape(ForceOrder)).Count.Should().Be(1);
        text.Should().EndWith(Environment.NewLine + ForceOrder);
        text.Should().NotContain(";");
        text.Split(UnionSeparator).Should().HaveCount(_mixedRequest.Groups.Count);
    }

    [Test]
    public void It_forces_join_order_once_per_statement_in_the_standalone_form()
    {
        _mixedMultiStatementCommand.CommandText.Should().NotContain("UNION ALL");
        _statements.Should().HaveCount(_mixedRequest.Groups.Count);
        Regex
            .Matches(_mixedMultiStatementCommand.CommandText, Regex.Escape(ForceOrder))
            .Count.Should()
            .Be(_mixedRequest.Groups.Count);
        _statements.Should().OnlyContain(statement => statement.EndsWith(ForceOrder + ";"));

        for (var index = 0; index < _statements.Count; index++)
        {
            _statements[index].Should().StartWith($"SELECT CAST({index + 1} AS int) AS [GroupOrdinal]");
        }
    }

    [Test]
    public void It_collates_every_textual_key_operand_and_every_lower_argument()
    {
        var textualOperands = 0;

        foreach (var branch in _mixedUnionCommand.CommandText.Split(UnionSeparator))
        {
            var textualKeys = Regex
                .Matches(branch, @"(?<key>k\d+) nvarchar\(\d+\) '\$\.k\d+'")
                .Select(match => match.Groups["key"].Value)
                .ToHashSet();
            var body = branch[(branch.IndexOf(") AS keys", StringComparison.Ordinal) + ") AS keys".Length)..];

            foreach (Match operand in Regex.Matches(body, @"keys\.(?<key>k\d+)(?<collation> COLLATE \w+)?"))
            {
                var isTextual = textualKeys.Contains(operand.Groups["key"].Value);

                operand.Groups["collation"].Success.Should().Be(isTextual, operand.Value);

                if (isTextual)
                {
                    textualOperands++;
                    operand.Groups["collation"].Value.Should().Be($" COLLATE {Collation}");
                }
            }
        }

        var lowerCalls = Regex.Matches(_mixedUnionCommand.CommandText, @"LOWER\(").Count;
        var collatedLowerCalls = Regex
            .Matches(_mixedUnionCommand.CommandText, $@"LOWER\(keys\.k\d+ COLLATE {Collation}\)")
            .Count;

        // Program, GeneralStudentProgramAssociation and the descriptor target each fold one URI.
        lowerCalls.Should().Be(3);
        collatedLowerCalls.Should().Be(lowerCalls);

        // Descriptor URIs (3) plus ProgramName, Program_ProgramName, Student_StudentUniqueId, LocalCourseCode
        // and Session_SessionName.
        textualOperands.Should().Be(8);
    }

    [Test]
    public void It_never_limits_rows_or_declares_locals()
    {
        var text = _mixedUnionCommand.CommandText + _mixedMultiStatementCommand.CommandText;

        text.Should().NotContain("TOP");
        text.Should().NotContain("DECLARE");
        text.Should().NotContain("DISTINCT");
        text.Should().NotContain("ORDER BY");
    }

    [Test]
    public void It_projects_the_concrete_member_key_from_the_abstract_identity_table()
    {
        _statements[1].Should().Contain("INNER JOIN [edfi].[EducationOrganizationIdentity] AS target");
        _statements[1].Should().Contain("target.[ResourceKeyId] AS [ResourceKeyId]");
        _statements[1].Should().Contain("CAST(NULL AS int) AS [DescriptorId]");
        _mixedUnionCommand.CommandText.Should().NotContain("Discriminator");
    }

    [Test]
    public void It_projects_both_descriptor_keys_from_the_descriptor_row_for_a_descriptor_target()
    {
        _statements[5].Should().Contain("d.[DocumentId] AS [DocumentId]");
        _statements[5].Should().Contain("d.[DescriptorId] AS [DescriptorId]");
        _statements[5].Should().Contain("d.[ResourceKeyId] AS [ResourceKeyId]");
        _statements[5].Should().Contain($"ON d.[UriLowered] = LOWER(keys.k1 COLLATE {Collation})");
    }

    [Test]
    public void It_joins_a_copied_descriptor_identity_on_an_abstract_target_by_the_compact_descriptor_id()
    {
        _statements[4]
            .Should()
            .Contain("INNER JOIN [edfi].[GeneralStudentProgramAssociationIdentity] AS target");
        _statements[4]
            .Should()
            .Contain("target.[Program_ProgramTypeDescriptor_DescriptorId] = d1.[DescriptorId]");
        _statements[4].Should().NotContain("= d1.[DocumentId]");
        _statements[4].Should().Contain("target.[ResourceKeyId] AS [ResourceKeyId]");
        _statements[2].Should().NotContain("= d1.[DocumentId]");
    }

    [Test]
    public void It_emits_one_predicate_per_key_unified_path()
    {
        _statements[3].Should().Contain("target.[SchoolId_Unified] = keys.k2");
        _statements[3].Should().Contain("target.[SchoolId_Unified] = keys.k3");
    }

    [Test]
    public void It_binds_one_unbounded_nvarchar_json_parameter_per_group()
    {
        _mixedUnionCommand
            .Parameters.Select(parameter => parameter.Name)
            .Should()
            .Equal(Enumerable.Range(1, 6).Select(groupOrdinal => $"@g{groupOrdinal}_keys"));

        foreach (var parameter in _mixedUnionCommand.Parameters)
        {
            SqlParameter sqlParameter = new();
            parameter.ConfigureParameter!(sqlParameter);

            sqlParameter.SqlDbType.Should().Be(SqlDbType.NVarChar);
            sqlParameter.Size.Should().Be(-1);
        }
    }

    [Test]
    public void It_serializes_every_scalar_kind_into_exact_json_and_column_types()
    {
        (ScalarKind Kind, int? MaxLength, object Value, string WithType, string Json)[] cases =
        [
            (
                ScalarKind.String,
                60,
                "O'Brien \"Q\" Zoë",
                "nvarchar(60)",
                "\"O\\u0027Brien \\u0022Q\\u0022 Zo\\u00EB\""
            ),
            (ScalarKind.Int16, null, (short)7, "smallint", "7"),
            (ScalarKind.Int32, null, 2026, "int", "2026"),
            (ScalarKind.Int64, null, 255901L, "bigint", "255901"),
            (ScalarKind.Decimal, null, 12.50m, "decimal(9, 2)", "12.50"),
            (ScalarKind.Boolean, null, true, "bit", "true"),
            (ScalarKind.Date, null, new DateOnly(2026, 8, 20), "date", "\"2026-08-20\""),
            (
                ScalarKind.DateTime,
                null,
                new DateTime(2026, 8, 20, 13, 45, 7, DateTimeKind.Utc),
                "datetime2(7)",
                "\"2026-08-20T13:45:07.0000000\""
            ),
            (ScalarKind.Time, null, new TimeOnly(13, 45, 7), "time(7)", "\"13:45:07\""),
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
                            new NaturalKeyProbeKeyBinding.Scalar(
                                new RelationalScalarType(
                                    testCase.Kind,
                                    testCase.MaxLength,
                                    testCase.Kind == ScalarKind.Decimal ? (9, 2) : null
                                )
                            )
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

        var expectedJson =
            "[{\"o\":1,"
            + string.Join(",", cases.Select((testCase, index) => $"\"k{index + 1}\":{testCase.Json}"))
            + "}]";

        command.Parameters.Should().ContainSingle().Which.Value.Should().Be(expectedJson);
        JsonDocument
            .Parse(expectedJson)
            .RootElement[0]
            .GetProperty("k1")
            .GetString()
            .Should()
            .Be("O'Brien \"Q\" Zoë");

        for (var index = 0; index < cases.Length; index++)
        {
            command
                .CommandText.Should()
                .Contain(
                    $"k{index + 1} {cases[index].WithType} '$.k{index + 1}'",
                    cases[index].Kind.ToString()
                );
        }
    }

    [Test]
    public void It_builds_at_the_parameter_limit()
    {
        var command = _builder.BuildEmbeddableCommand(
            SchoolGroups(MssqlCommandLimits.MaxUserParametersPerCommand)
        );

        command.Parameters.Should().HaveCount(MssqlCommandLimits.MaxUserParametersPerCommand);
    }

    [Test]
    public void It_rejects_one_group_past_the_parameter_limit_before_building()
    {
        var act = () =>
            _builder.BuildCommand(SchoolGroups(MssqlCommandLimits.MaxUserParametersPerCommand + 1));

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "SQL Server natural-key lookup requires 2099 parameters*2098-parameter command limit*"
            );
    }

    private NaturalKeyLookupRequest SchoolGroups(int groupCount) =>
        new(
            _mappingSet,
            [
                .. Enumerable
                    .Range(1, groupCount)
                    .Select(groupOrdinal =>
                        Group(groupOrdinal, ProbeTarget(_mappingSet, School), [(long)groupOrdinal])
                    ),
            ]
        );

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
}
