// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DataManagementService.Backend.Composite;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Backend.Tests.Unit.Composite;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Tests.Unit.NaturalKeyLookupTestMappingSets;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// Proves the union lookup statement joins a composite command through the same rewriter and builder the
/// composite write path uses, and that its read callback decodes only its own result set, so a statement
/// appended after it still decodes. Production composition is not touched.
/// </summary>
[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_A_Natural_Key_Lookup_Embedded_In_A_Composite_Command(SqlDialect dialect)
{
    private const string ProgramTypeUri =
        "uri://ed-fi.org/ProgramTypeDescriptor#Career and Technical Education";
    private const int MarkerValue = 7;

    private NaturalKeyLookupRequest _request = null!;
    private RelationalCommand _lookupCommand = null!;
    private bool _fits;
    private RelationalCompositeRewrittenStatement _rewritten = null!;
    private int _lookupOrdinal;
    private int _markerOrdinal;
    private RelationalCompositeCommand _compositeCommand = null!;
    private ScriptedWriteSession _session = null!;
    private IReadOnlyList<RelationalCompositeStatementOutcome> _outcomes = null!;
    private short _schoolResourceKeyId;
    private short _programTypeResourceKeyId;

    [SetUp]
    public async Task Setup()
    {
        var mappingSet = Ds52(dialect);
        INaturalKeyLookupCommandBuilder lookupBuilder =
            dialect == SqlDialect.Pgsql
                ? new PostgresqlNaturalKeyLookupCommandBuilder()
                : new MssqlNaturalKeyLookupCommandBuilder();
        IRelationalCompositeCommandDialect compositeDialect =
            dialect == SqlDialect.Pgsql
                ? PgsqlCompositeCommandDialect.Instance
                : MssqlCompositeCommandDialect.Instance;

        _schoolResourceKeyId = mappingSet.ResourceKeyIdByResource[School];
        _programTypeResourceKeyId = mappingSet.ResourceKeyIdByResource[ProgramTypeDescriptor];

        _request = new NaturalKeyLookupRequest(
            mappingSet,
            [
                new NaturalKeyLookupGroup(
                    1,
                    ProbeTarget(mappingSet, School),
                    [new NaturalKeyLookupEntry([255901L]), new NaturalKeyLookupEntry([255902L])]
                ),
                new NaturalKeyLookupGroup(
                    2,
                    DescriptorTarget(mappingSet, ProgramTypeDescriptor),
                    [new NaturalKeyLookupEntry([ProgramTypeUri])]
                ),
            ]
        );

        _lookupCommand = lookupBuilder.BuildEmbeddableCommand(_request);

        var compositeBuilder = new RelationalCompositeCommandBuilder(compositeDialect);
        _fits = compositeBuilder.Fits(_lookupCommand.Parameters.Count);
        _rewritten = RelationalCompositeStatementRewriter.Rewrite(
            _lookupCommand,
            compositeBuilder.Allocator,
            compositeBuilder.NextOrdinal
        );

        var request = _request;
        _lookupOrdinal = compositeBuilder.Append(
            "natural-key-lookup",
            _rewritten.Sql,
            _rewritten.Parameters,
            RelationalCompositeResultShape.Rows,
            (reader, cancellationToken) =>
                NaturalKeyLookupResultReader.ReadCompositeResultSetAsync(reader, request, cancellationToken)
        );
        _markerOrdinal = compositeBuilder.Append(
            "marker",
            dialect == SqlDialect.Pgsql
                ? $"SELECT {MarkerValue} AS \"Marker\""
                : $"SELECT {MarkerValue} AS [Marker]",
            [],
            RelationalCompositeResultShape.Rows,
            static async (reader, cancellationToken) =>
                await reader.ReadAsync(cancellationToken) ? reader.GetInt32(0) : null
        );
        _compositeCommand = compositeBuilder.Seal();

        // The lookup rows arrive out of ordinal order; School ordinal 1 has no match.
        _session = new ScriptedWriteSession(
            new ScriptedDbDataReader(
                [
                    [
                        [2, 1, 900001L, 7, _programTypeResourceKeyId],
                        [1, 2, 900002L, null, _schoolResourceKeyId],
                    ],
                    [
                        [MarkerValue],
                    ],
                ],
                [
                    [
                        NaturalKeyLookupColumns.GroupOrdinal,
                        NaturalKeyLookupColumns.Ordinal,
                        NaturalKeyLookupColumns.DocumentId,
                        NaturalKeyLookupColumns.DescriptorId,
                        NaturalKeyLookupColumns.ResourceKeyId,
                    ],
                    ["Marker"],
                ]
            )
        )
        {
            Dialect = dialect,
        };

        _outcomes = await new RelationalCompositeCommandExecution().ExecuteAsync(_session, _compositeCommand);
    }

    [Test]
    public void It_fits_the_composite_parameter_budget()
    {
        _fits.Should().BeTrue();
    }

    [Test]
    public void It_embeds_a_single_unterminated_statement()
    {
        _rewritten.Sql.Should().NotContain(";");
        _lookupOrdinal.Should().Be(0);
        _markerOrdinal.Should().Be(1);
    }

    [Test]
    public void It_renames_every_parameter_to_a_statement_scoped_name()
    {
        _rewritten
            .Parameters.Select(parameter => parameter.Name)
            .Should()
            .Equal(_lookupCommand.Parameters.Select(parameter => $"{parameter.Name}_s{_lookupOrdinal}"));

        foreach (var original in _lookupCommand.Parameters)
        {
            Regex
                .IsMatch(_compositeCommand.Command.CommandText, $@"{Regex.Escape(original.Name)}(?!_s\d)")
                .Should()
                .BeFalse(original.Name);
            _compositeCommand.Command.CommandText.Should().Contain($"{original.Name}_s{_lookupOrdinal}");
        }
    }

    [Test]
    public void It_preserves_parameter_values_and_provider_configuration()
    {
        for (var index = 0; index < _lookupCommand.Parameters.Count; index++)
        {
            _rewritten.Parameters[index].Value.Should().BeSameAs(_lookupCommand.Parameters[index].Value);
            _rewritten
                .Parameters[index]
                .ConfigureParameter.Should()
                .NotBeNull()
                .And.BeSameAs(_lookupCommand.Parameters[index].ConfigureParameter);
        }

        _compositeCommand.Command.Parameters.Should().Equal(_rewritten.Parameters);
    }

    [Test]
    public void It_places_the_lookup_statement_in_the_sealed_command_exactly_once()
    {
        var text = _compositeCommand.Command.CommandText;
        var statement = _rewritten.Sql + ";";
        var first = text.IndexOf(statement, StringComparison.Ordinal);

        first.Should().BeGreaterThanOrEqualTo(0);
        text.IndexOf(statement, first + 1, StringComparison.Ordinal).Should().Be(-1);
        text.IndexOf("Marker", StringComparison.Ordinal).Should().BeGreaterThan(first);
    }

    [Test]
    public void It_sends_the_sealed_command_as_one_round_trip()
    {
        _session.Commands.Should().ContainSingle().Which.Should().BeSameAs(_compositeCommand.Command);
    }

    [Test]
    public void It_decodes_the_lookup_matches_by_group_and_ordinal()
    {
        var matches = _outcomes
            .Single(outcome => outcome.Ordinal == _lookupOrdinal)
            .Value.Should()
            .BeOfType<NaturalKeyLookupMatch?[][]>()
            .Subject;

        matches[0][0].Should().BeNull();
        matches[0][1].Should().Be(new NaturalKeyLookupMatch(900002L, null, _schoolResourceKeyId));
        matches[1][0].Should().Be(new NaturalKeyLookupMatch(900001L, 7, _programTypeResourceKeyId));
    }

    [Test]
    public void It_leaves_the_next_result_set_for_the_statement_after_the_lookup()
    {
        _outcomes.Single(outcome => outcome.Ordinal == _markerOrdinal).Value.Should().Be(MarkerValue);
    }
}
