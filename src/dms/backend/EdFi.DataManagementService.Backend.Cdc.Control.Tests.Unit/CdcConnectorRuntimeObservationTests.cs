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
/// Kafka Connect status mapped onto the shared runtime observation. The connector contract admits
/// exactly one task, so any other count is reported as observed and rejected by validation rather
/// than rounded off, and snapshot progress is read from the connector's committed offset because a
/// status document does not carry it.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CdcConnectorRuntimeObservation")]
public class Given_CdcConnectorRuntimeObservationMapping
{
    private const string OperationId = "operation-1";
    private const string StreamingOffset = """{"lsn_proc":42,"snapshot":false}""";
    private const string SnapshotOffset = """{"lsn_proc":7,"snapshot":true}""";

    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);

    [Test]
    public void It_reports_a_running_connector_with_its_sole_running_task()
    {
        CdcConnectorRuntimeObservation observation = Map(Status("RUNNING", ConnectorTask("RUNNING")));

        using var _ = new AssertionScope();
        observation.ConnectorState.Should().Be(CdcConnectorRuntimeState.Running);
        observation.TaskCount.Should().Be(1);
        observation.RunningTaskCount.Should().Be(1);
        observation.SoleTaskState.Should().Be(CdcConnectorRuntimeState.Running);
        observation.SnapshotState.Should().Be(CdcConnectorSnapshotState.Completed);
        observation.LastErrorCategory.Should().BeNull();
        observation.LastErrorObservedAt.Should().BeNull();
        observation.ConnectorName.Should().Be(ConnectorName());
        observation.Diagnostics.Should().BeEmpty();
        Validate(observation).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_rejects_a_connector_that_runs_more_than_one_task()
    {
        CdcConnectorRuntimeObservation observation = Map(
            Status("RUNNING", ConnectorTask("RUNNING"), ConnectorTask("RUNNING", id: 1))
        );

        using var _ = new AssertionScope();
        observation.TaskCount.Should().Be(2);
        observation.RunningTaskCount.Should().Be(2);
        observation.SoleTaskState.Should().Be(CdcConnectorRuntimeState.Unknown);
        observation
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Category == CdcDiagnosticCategory.InvalidObservation);
        Validate(observation).Succeeded.Should().BeFalse();
    }

    [Test]
    public void It_reports_a_failed_task_under_a_running_connector()
    {
        CdcConnectorRuntimeObservation observation = Map(
            Status(
                "RUNNING",
                ConnectorTask("FAILED", errorCategory: "org.apache.kafka.connect.errors.ConnectException")
            )
        );

        using var _ = new AssertionScope();
        observation.ConnectorState.Should().Be(CdcConnectorRuntimeState.Running);
        observation.SoleTaskState.Should().Be(CdcConnectorRuntimeState.Failed);
        observation.RunningTaskCount.Should().Be(0);
        observation.LastErrorCategory.Should().Be("org.apache.kafka.connect.errors.connectexception");

        // A running connector whose sole task is not running is not a runnable observation.
        Validate(observation).Succeeded.Should().BeFalse();
        observation.Diagnostics.Should().NotBeEmpty();
    }

    [TestCase("PAUSED", CdcConnectorRuntimeState.Paused)]
    [TestCase("STOPPED", CdcConnectorRuntimeState.Stopped)]
    [TestCase("UNASSIGNED", CdcConnectorRuntimeState.Unassigned)]
    [TestCase("FAILED", CdcConnectorRuntimeState.Failed)]
    [TestCase("RESTARTING", CdcConnectorRuntimeState.Unknown)]
    public void It_maps_each_reported_connector_state(string reportedState, CdcConnectorRuntimeState expected)
    {
        CdcConnectorRuntimeObservation observation = Map(Status(reportedState, ConnectorTask(reportedState)));

        using var _ = new AssertionScope();
        observation.ConnectorState.Should().Be(expected);
        observation.SoleTaskState.Should().Be(expected);
        observation.RunningTaskCount.Should().Be(0);
    }

    [Test]
    public void It_reports_a_failed_connector_without_a_trace_as_unclassified()
    {
        CdcConnectorRuntimeObservation observation = Map(Status("FAILED", ConnectorTask("FAILED")));

        observation.LastErrorCategory.Should().Be(CdcConnectRestAdapter.UnclassifiedErrorCategory);
    }

    [Test]
    public void It_reports_an_error_category_that_is_not_a_safe_token_as_unclassified()
    {
        CdcConnectorRuntimeObservation observation = Map(
            Status("FAILED", ConnectorTask("FAILED", errorCategory: "io.debezium.Outer$Inner"))
        );

        observation.LastErrorCategory.Should().Be(CdcConnectRestAdapter.UnclassifiedErrorCategory);
    }

    [Test]
    public void It_reports_an_unavailable_status_as_unknown_rather_than_stopped()
    {
        CdcConnectorRuntimeObservation observation = Map(
            new(CdcConnectOutcome.Unavailable, null, new(503, "Kafka Connect answered 503.", true))
        );

        using var _ = new AssertionScope();
        observation.ConnectorState.Should().Be(CdcConnectorRuntimeState.Unknown);
        observation.SoleTaskState.Should().Be(CdcConnectorRuntimeState.Unknown);
        observation.TaskCount.Should().BeNull();
        observation.RunningTaskCount.Should().BeNull();
        observation
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Code == "connectorRuntimeUnavailable");
    }

    [Test]
    public void It_reports_an_in_progress_snapshot_from_the_committed_offset()
    {
        CdcConnectorRuntimeObservation observation = Map(
            Status("RUNNING", ConnectorTask("RUNNING")),
            Offsets(SnapshotOffset)
        );

        observation.SnapshotState.Should().Be(CdcConnectorSnapshotState.Running);
    }

    [Test]
    public void It_reports_a_connector_that_has_committed_nothing_as_snapshot_not_started()
    {
        CdcConnectorRuntimeObservation observation = Map(
            Status("RUNNING", ConnectorTask("RUNNING")),
            new(CdcConnectOutcome.Succeeded, new([]), null)
        );

        observation.SnapshotState.Should().Be(CdcConnectorSnapshotState.NotStarted);
    }

    [Test]
    public void It_reports_a_deleted_committed_offset_as_snapshot_not_started()
    {
        CdcConnectorRuntimeObservation observation = Map(
            Status("RUNNING", ConnectorTask("RUNNING")),
            Offsets("null")
        );

        observation.SnapshotState.Should().Be(CdcConnectorSnapshotState.NotStarted);
    }

    [Test]
    public void It_reports_unavailable_offsets_as_an_unknown_snapshot_state()
    {
        CdcConnectorRuntimeObservation observation = Map(
            Status("RUNNING", ConnectorTask("RUNNING")),
            new(CdcConnectOutcome.NotFound, null, new(404, "Kafka Connect answered 404.", false))
        );

        observation.SnapshotState.Should().Be(CdcConnectorSnapshotState.Unknown);
    }

    [Test]
    public void It_reports_an_ambiguous_offset_set_as_an_unknown_snapshot_state()
    {
        CdcConnectorRuntimeObservation observation = Map(
            Status("RUNNING", ConnectorTask("RUNNING")),
            new(CdcConnectOutcome.Succeeded, new([Entry(StreamingOffset), Entry(SnapshotOffset)]), null)
        );

        observation.SnapshotState.Should().Be(CdcConnectorSnapshotState.Unknown);
    }

    [Test]
    public void It_carries_the_operation_envelope_onto_the_observation()
    {
        CdcConnectorRuntimeObservation observation = Map(Status("RUNNING", ConnectorTask("RUNNING")));

        using var _ = new AssertionScope();
        observation.ContractVersion.Should().Be(CdcJsonContract.CurrentContractVersion);
        observation.OperationId.Should().Be(OperationId);
        observation.ObservedAt.Should().Be(ObservedAt);
        observation.TargetIdentity.Should().Be(TargetIdentity());
        observation.Provider.Should().Be(CdcProvider.Postgresql);
        observation
            .PhysicalSourceFingerprint.Should()
            .Be(CdcControlTemplateTestData.SourceFingerprint(Ddl.CdcProvider.Postgresql).Value);
    }

    [Test]
    public void It_rejects_a_missing_status_result()
    {
        Action mapping = () => Mapper().MapRuntime(Context(), Binding(), null!, Offsets(StreamingOffset));

        mapping.Should().Throw<ArgumentNullException>();
    }

    private static CdcConnectorRuntimeObservation Map(
        CdcConnectResult<CdcConnectorStatus> status,
        CdcConnectResult<CdcConnectorOffsets>? committedOffsets = null
    ) => Mapper().MapRuntime(Context(), Binding(), status, committedOffsets ?? Offsets(StreamingOffset));

    private static ICdcConnectorObservationMapper Mapper() =>
        new CdcConnectorObservationMapper(
            A.Fake<ICdcConnectorTemplateService>(),
            new FixedTimeProvider(ObservedAt)
        );

    private static CdcConnectResult<CdcConnectorStatus> Status(
        string connectorState,
        params CdcConnectorTaskStatus[] tasks
    ) => new(CdcConnectOutcome.Succeeded, new(connectorState, tasks), null);

    private static CdcConnectorTaskStatus ConnectorTask(
        string state,
        int id = 0,
        string? errorCategory = null
    ) => new(id, state, errorCategory);

    private static CdcConnectResult<CdcConnectorOffsets> Offsets(string offsetJson) =>
        new(CdcConnectOutcome.Succeeded, new([Entry(offsetJson)]), null);

    private static CdcConnectorOffsetEntry Entry(string offsetJson)
    {
        using JsonDocument partition = JsonDocument.Parse($$"""{"server":"{{ConnectorName()}}"}""");
        using JsonDocument offset = JsonDocument.Parse(offsetJson);

        return new(partition.RootElement.Clone(), offset.RootElement.Clone());
    }

    private static CdcContractValidationResult Validate(CdcConnectorRuntimeObservation observation) =>
        CdcConnectorRuntimeObservationValidator.ValidateForBinding(
            observation,
            Binding(),
            new(
                OperationId,
                TargetIdentity(),
                CdcControlTemplateTestData.SourceFingerprint(Ddl.CdcProvider.Postgresql).Value,
                ObservedAt.AddMinutes(1)
            )
        );

    private static CdcObservationContext Context() =>
        new(
            OperationId,
            TargetIdentity(),
            CdcControlTemplateTestData.SourceFingerprint(Ddl.CdcProvider.Postgresql).Value
        );

    private static CdcBinding Binding() =>
        CdcControlTemplateTestData.BuildBinding(Ddl.CdcProvider.Postgresql);

    private static CdcTargetIdentity TargetIdentity() =>
        CdcControlTemplateTestData.BuildTargetIdentity(Ddl.CdcProvider.Postgresql);

    private static string ConnectorName() =>
        CdcControlTemplateTestData.BuildInventory(Ddl.CdcProvider.Postgresql).ConnectorName;

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
