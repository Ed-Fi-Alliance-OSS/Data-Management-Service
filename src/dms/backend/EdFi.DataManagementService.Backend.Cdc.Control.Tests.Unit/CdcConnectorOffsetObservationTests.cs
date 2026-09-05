// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using FluentAssertions.Execution;
using NUnit.Framework;
using Ddl = EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit;

/// <summary>
/// Committed Connect offsets mapped onto the shared offset observation. The binding's own Connect
/// source partition selects the entry, provider positions are read only from that entry, and a
/// snapshot offset is reported as one so the provider barrier can never be satisfied by it.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CdcConnectorOffsetObservation")]
public class Given_CdcConnectorOffsetObservationMapping
{
    private const string OperationId = "operation-1";
    private const string SqlServerCatalog = "edfi_datastore";
    private const string PostgresqlStreamingOffset = """{"lsn_proc":42,"snapshot":false}""";
    private const string SqlServerStreamingOffset = """
        {"commit_lsn":"00000027:00000c78:0003","change_lsn":"00000027:00000c78:0002","event_serial_no":1}
        """;

    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);

    [Test]
    public void It_reports_the_committed_postgresql_streaming_offset()
    {
        CdcConnectorOffsetObservation observation = Map(
            Ddl.CdcProvider.Postgresql,
            PostgresqlStreamingOffset
        );

        using var _ = new AssertionScope();
        observation.SourcePartitionMatchResult.Should().Be(CdcConnectorOffsetMatchResult.Exact);
        observation.IsSnapshot.Should().BeFalse();
        observation.IsNull.Should().BeFalse();
        observation.LsnProc.Should().Be(42);
        observation.CommitLsn.Should().BeNull();
        observation.ChangeLsn.Should().BeNull();
        observation.EventSerialNo.Should().BeNull();
        observation.ConnectorName.Should().Be(ConnectorName(Ddl.CdcProvider.Postgresql));
        observation.TopicPrefix.Should().Be(ConnectorName(Ddl.CdcProvider.Postgresql));
        observation.ConnectSourcePartitionHash.Should().Be(ExpectedHash(Ddl.CdcProvider.Postgresql));
        observation.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public void It_reports_the_committed_sql_server_streaming_offset()
    {
        CdcConnectorOffsetObservation observation = Map(Ddl.CdcProvider.SqlServer, SqlServerStreamingOffset);

        using var _ = new AssertionScope();
        observation.SourcePartitionMatchResult.Should().Be(CdcConnectorOffsetMatchResult.Exact);
        observation.LsnProc.Should().BeNull();
        observation.CommitLsn.Should().Be("00000027:00000c78:0003");
        observation.ChangeLsn.Should().Be("00000027:00000c78:0002");
        observation.EventSerialNo.Should().Be(1);
        observation.ConnectSourcePartitionHash.Should().Be(ExpectedHash(Ddl.CdcProvider.SqlServer));
        observation.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public void It_reads_a_postgresql_position_reported_as_a_string()
    {
        CdcConnectorOffsetObservation observation = Map(Ddl.CdcProvider.Postgresql, """{"lsn_proc":"42"}""");

        observation.LsnProc.Should().Be(42);
    }

    [Test]
    public void It_reports_a_snapshot_offset_as_one_so_it_stays_rejected()
    {
        CdcConnectorOffsetObservation observation = Map(
            Ddl.CdcProvider.Postgresql,
            """{"lsn_proc":7,"snapshot":true}"""
        );

        using var _ = new AssertionScope();
        observation.IsSnapshot.Should().BeTrue();
        observation.LsnProc.Should().Be(7);
        observation
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Message.Contains("snapshot", StringComparison.OrdinalIgnoreCase)
            );
        Validate(observation, Ddl.CdcProvider.Postgresql).Succeeded.Should().BeFalse();
    }

    [Test]
    public void It_reports_a_last_snapshot_record_offset_as_a_snapshot()
    {
        CdcConnectorOffsetObservation observation = Map(
            Ddl.CdcProvider.Postgresql,
            """{"lsn_proc":7,"snapshot":"last"}"""
        );

        observation.IsSnapshot.Should().BeTrue();
    }

    [Test]
    public void It_reports_a_deleted_offset_as_null()
    {
        CdcConnectorOffsetObservation observation = Map(Ddl.CdcProvider.Postgresql, "null");

        using var _ = new AssertionScope();
        observation.IsNull.Should().BeTrue();
        observation.LsnProc.Should().BeNull();
        Validate(observation, Ddl.CdcProvider.Postgresql).Succeeded.Should().BeFalse();
    }

    [Test]
    public void It_reports_a_connector_that_has_committed_nothing_as_missing()
    {
        CdcConnectorOffsetObservation observation = MapEntries(Ddl.CdcProvider.Postgresql, []);

        using var _ = new AssertionScope();
        observation.SourcePartitionMatchResult.Should().Be(CdcConnectorOffsetMatchResult.Missing);
        observation.LsnProc.Should().BeNull();
        Validate(observation, Ddl.CdcProvider.Postgresql).Succeeded.Should().BeFalse();
    }

    [Test]
    public void It_reports_more_than_one_matching_offset_as_ambiguous()
    {
        CdcConnectorOffsetObservation observation = MapEntries(
            Ddl.CdcProvider.Postgresql,
            [
                Entry(Partition(Ddl.CdcProvider.Postgresql), PostgresqlStreamingOffset),
                Entry(Partition(Ddl.CdcProvider.Postgresql), PostgresqlStreamingOffset),
            ]
        );

        using var _ = new AssertionScope();
        observation.SourcePartitionMatchResult.Should().Be(CdcConnectorOffsetMatchResult.Multiple);
        observation.LsnProc.Should().BeNull();
    }

    [Test]
    public void It_reports_an_offset_committed_under_another_source_partition_as_a_mismatch()
    {
        CdcConnectorOffsetObservation observation = MapEntries(
            Ddl.CdcProvider.Postgresql,
            [Entry("""{"server":"some-other-connector"}""", PostgresqlStreamingOffset)]
        );

        using var _ = new AssertionScope();
        observation
            .SourcePartitionMatchResult.Should()
            .Be(CdcConnectorOffsetMatchResult.SourcePartitionMismatch);
        observation.ConnectSourcePartitionHash.Should().NotBe(ExpectedHash(Ddl.CdcProvider.Postgresql));
        observation.LsnProc.Should().BeNull();
        observation
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Category == CdcDiagnosticCategory.SourceMismatch);
    }

    [Test]
    public void It_reports_a_sql_server_offset_committed_under_another_catalog_as_a_mismatch()
    {
        CdcConnectorOffsetObservation observation = MapEntries(
            Ddl.CdcProvider.SqlServer,
            [
                Entry(
                    $$"""{"database":"other_datastore","server":"{{ConnectorName(Ddl.CdcProvider.SqlServer)}}"}""",
                    SqlServerStreamingOffset
                ),
            ]
        );

        using var _ = new AssertionScope();
        observation
            .SourcePartitionMatchResult.Should()
            .Be(CdcConnectorOffsetMatchResult.SourcePartitionMismatch);
        observation.ConnectSourcePartitionHash.Should().NotBe(ExpectedHash(Ddl.CdcProvider.SqlServer));
    }

    [Test]
    public void It_carries_a_malformed_sql_server_lsn_and_its_diagnostic()
    {
        CdcConnectorOffsetObservation observation = Map(
            Ddl.CdcProvider.SqlServer,
            """{"commit_lsn":"not-an-lsn","change_lsn":"00000027:00000c78:0002","event_serial_no":1}"""
        );

        using var _ = new AssertionScope();
        observation.CommitLsn.Should().Be("not-an-lsn");
        observation
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Category == CdcDiagnosticCategory.MalformedPayload);
        Validate(observation, Ddl.CdcProvider.SqlServer).Succeeded.Should().BeFalse();
    }

    [Test]
    public void It_carries_a_negative_sql_server_event_serial_and_its_diagnostic()
    {
        CdcConnectorOffsetObservation observation = Map(
            Ddl.CdcProvider.SqlServer,
            """{"commit_lsn":"00000027:00000c78:0003","change_lsn":"00000027:00000c78:0002","event_serial_no":-1}"""
        );

        using var _ = new AssertionScope();
        observation.EventSerialNo.Should().Be(-1);
        observation.Diagnostics.Should().NotBeEmpty();
        Validate(observation, Ddl.CdcProvider.SqlServer).Succeeded.Should().BeFalse();
    }

    [Test]
    public void It_reports_a_postgresql_position_that_is_not_an_integer_as_malformed()
    {
        CdcConnectorOffsetObservation observation = Map(
            Ddl.CdcProvider.Postgresql,
            """{"lsn_proc":"not-a-position"}"""
        );

        using var _ = new AssertionScope();
        observation.LsnProc.Should().BeNull();
        observation
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Category == CdcDiagnosticCategory.MalformedPayload);
    }

    /// <summary>
    /// A query the worker did not answer reports the absence of an answer, not a null offset and not
    /// an offset the worker says is missing. The two are classified differently — a missing offset can
    /// be a proved history loss, an unobtained one never is — so mapping both onto the same result let
    /// an unreachable worker latch a terminal incident.
    /// </summary>
    [Test]
    public void It_reports_unavailable_offsets_as_unavailable_rather_than_as_missing_or_null()
    {
        CdcConnectorOffsetObservation observation = Mapper()
            .MapOffset(
                Context(Ddl.CdcProvider.Postgresql),
                Binding(Ddl.CdcProvider.Postgresql),
                null,
                new(CdcConnectOutcome.Unavailable, null, new(503, "Kafka Connect answered 503.", true))
            );

        using var _ = new AssertionScope();
        observation.SourcePartitionMatchResult.Should().Be(CdcConnectorOffsetMatchResult.Unavailable);
        observation.IsNull.Should().BeFalse();
        observation
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Code == "connectorOffsetsUnavailable");
    }

    /// <summary>
    /// A worker that answers and holds no offset at all is the one shape that reports Missing, which
    /// is what the continuity classifier may treat as a proved loss for an established stream.
    /// </summary>
    [Test]
    public void It_reports_an_answered_empty_offset_set_as_missing()
    {
        CdcConnectorOffsetObservation observation = Mapper()
            .MapOffset(
                Context(Ddl.CdcProvider.Postgresql),
                Binding(Ddl.CdcProvider.Postgresql),
                null,
                new(CdcConnectOutcome.Succeeded, new([]), null)
            );

        using var _ = new AssertionScope();
        observation.SourcePartitionMatchResult.Should().Be(CdcConnectorOffsetMatchResult.Missing);
        observation
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Code == "connectorOffsetsUnavailable");
    }

    [Test]
    public void It_fails_closed_when_the_sql_server_catalog_is_not_supplied()
    {
        CdcConnectorOffsetObservation observation = Mapper()
            .MapOffset(
                Context(Ddl.CdcProvider.SqlServer),
                Binding(Ddl.CdcProvider.SqlServer),
                sqlServerCatalogName: null,
                new(
                    CdcConnectOutcome.Succeeded,
                    new([Entry(Partition(Ddl.CdcProvider.SqlServer), SqlServerStreamingOffset)]),
                    null
                )
            );

        using var _ = new AssertionScope();
        observation.SourcePartitionMatchResult.Should().NotBe(CdcConnectorOffsetMatchResult.Exact);
        observation.Diagnostics.Should().NotBeEmpty();
    }

    [Test]
    public void It_carries_the_operation_envelope_onto_the_observation()
    {
        CdcConnectorOffsetObservation observation = Map(Ddl.CdcProvider.SqlServer, SqlServerStreamingOffset);

        using var _ = new AssertionScope();
        observation.ContractVersion.Should().Be(CdcJsonContract.CurrentContractVersion);
        observation.OperationId.Should().Be(OperationId);
        observation.ObservedAt.Should().Be(ObservedAt);
        observation.TargetIdentity.Should().Be(TargetIdentity(Ddl.CdcProvider.SqlServer));
        observation.Provider.Should().Be(CdcProvider.SqlServer);
    }

    [Test]
    public void It_rejects_a_missing_offset_result()
    {
        Action mapping = () =>
            Mapper()
                .MapOffset(
                    Context(Ddl.CdcProvider.Postgresql),
                    Binding(Ddl.CdcProvider.Postgresql),
                    null,
                    null!
                );

        mapping.Should().Throw<ArgumentNullException>();
    }

    private static CdcConnectorOffsetObservation Map(Ddl.CdcProvider provider, string offsetJson) =>
        MapEntries(provider, [Entry(Partition(provider), offsetJson)]);

    private static CdcConnectorOffsetObservation MapEntries(
        Ddl.CdcProvider provider,
        IReadOnlyList<CdcConnectorOffsetEntry> entries
    ) =>
        Mapper()
            .MapOffset(
                Context(provider),
                Binding(provider),
                provider == Ddl.CdcProvider.SqlServer ? SqlServerCatalog : null,
                new(CdcConnectOutcome.Succeeded, new(entries), null)
            );

    private static ICdcConnectorObservationMapper Mapper() =>
        new CdcConnectorObservationMapper(
            A.Fake<ICdcConnectorTemplateService>(),
            new FixedTimeProvider(ObservedAt)
        );

    private static string Partition(Ddl.CdcProvider provider) =>
        provider == Ddl.CdcProvider.SqlServer
            ? $$"""{"database":"{{SqlServerCatalog}}","server":"{{ConnectorName(provider)}}"}"""
            : $$"""{"server":"{{ConnectorName(provider)}}"}""";

    private static CdcConnectorOffsetEntry Entry(string partitionJson, string offsetJson)
    {
        using JsonDocument partition = JsonDocument.Parse(partitionJson);
        using JsonDocument offset = JsonDocument.Parse(offsetJson);

        return new(partition.RootElement.Clone(), offset.RootElement.Clone());
    }

    private static string ExpectedHash(Ddl.CdcProvider provider) =>
        CdcSourcePartitionHashCalculator
            .Compute(
                provider == Ddl.CdcProvider.SqlServer ? CdcProvider.SqlServer : CdcProvider.Postgresql,
                ConnectorName(provider),
                provider == Ddl.CdcProvider.SqlServer ? SqlServerCatalog : null
            )
            .Hash!;

    private static CdcContractValidationResult Validate(
        CdcConnectorOffsetObservation observation,
        Ddl.CdcProvider provider
    ) =>
        CdcConnectorOffsetObservationValidator.ValidateForBinding(
            observation,
            Binding(provider),
            new(
                OperationId,
                TargetIdentity(provider),
                CdcControlTemplateTestData.SourceFingerprint(provider).Value,
                ObservedAt.AddMinutes(1)
            ),
            ExpectedHash(provider)
        );

    private static CdcObservationContext Context(Ddl.CdcProvider provider) =>
        new(
            OperationId,
            TargetIdentity(provider),
            CdcControlTemplateTestData.SourceFingerprint(provider).Value
        );

    private static CdcBinding Binding(Ddl.CdcProvider provider) =>
        CdcControlTemplateTestData.BuildBinding(provider);

    private static CdcTargetIdentity TargetIdentity(Ddl.CdcProvider provider) =>
        CdcControlTemplateTestData.BuildTargetIdentity(provider);

    private static string ConnectorName(Ddl.CdcProvider provider) =>
        CdcControlTemplateTestData.BuildInventory(provider).ConnectorName;

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
