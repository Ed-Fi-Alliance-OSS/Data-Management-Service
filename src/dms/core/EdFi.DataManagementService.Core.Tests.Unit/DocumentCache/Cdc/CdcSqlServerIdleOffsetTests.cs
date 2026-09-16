// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache.Cdc;

[TestFixture]
[Parallelizable]
[Category("CdcProviderPosition")]
public sealed class Given_SqlServer_Idle_Commit_Boundary
{
    private const string Commit = "00000023:00000138:0002";
    private static CdcSqlServerConnectorOffset Offset =>
        new(CdcConnectorOffsetMatchResult.Exact, false, false, Commit, "NULL", 0);

    [TestCase("00000023:00000138:0001", true)]
    [TestCase(Commit, false)]
    [TestCase("00000023:00000138:0003", false)]
    public void It_crosses_only_a_barrier_in_an_earlier_commit(string barrierCommit, bool crossed)
    {
        var barrier = CdcSqlServerProviderPosition.HeartbeatAfterImage(
            CdcSqlServerProviderPositionParser.ParseLsn(barrierCommit, "$.barrier").Lsn!.Value,
            new(0x23, 0x139, 1)
        );
        var result = CdcSqlServerProviderPositionParser.CompareCommittedOffsetToBarrier(barrier, Offset);
        result.Succeeded.Should().Be(crossed);
        result.AtOrBeyondBarrier.Should().Be(crossed);
        if (crossed)
        {
            result.CommittedPosition.Should().Be(Commit + "/NULL/0");
        }
        else
        {
            result.Diagnostics.Should().Contain(d => d.Category == CdcDiagnosticCategory.InvalidOrdering);
        }
    }

    private static IEnumerable<CdcSqlServerConnectorOffset> RejectedOffsets =>
        [
            Offset with
            {
                CommitLsn = null,
            },
            Offset with
            {
                CommitLsn = "NULL",
            },
            Offset with
            {
                CommitLsn = "private-invalid",
            },
            Offset with
            {
                CommitLsn = "00000000:00000000:0000",
            },
            Offset with
            {
                ChangeLsn = null,
            },
            Offset with
            {
                ChangeLsn = "null",
            },
            Offset with
            {
                ChangeLsn = "private-invalid",
            },
            Offset with
            {
                EventSerialNo = null,
            },
            Offset with
            {
                EventSerialNo = -1,
            },
            Offset with
            {
                EventSerialNo = 1,
            },
            Offset with
            {
                EventSerialNo = 2,
            },
            Offset with
            {
                EventSerialNo = long.MaxValue,
            },
            Offset with
            {
                IsSnapshot = true,
            },
            Offset with
            {
                IsNull = true,
            },
            Offset with
            {
                SourcePartitionMatchResult = CdcConnectorOffsetMatchResult.Multiple,
            },
        ];

    [TestCaseSource(nameof(RejectedOffsets))]
    public void It_rejects_unsupported_markers_and_non_streaming_evidence(CdcSqlServerConnectorOffset offset)
    {
        var result = CdcSqlServerProviderPositionParser.CompareCommittedOffsetToBarrier(
            new(new(0, 0, 0), new(0, 0, 0), 0),
            offset
        );
        result.Succeeded.Should().BeFalse();
        result.CommittedPosition.Should().BeNull();
    }

    private static CdcSourceHistoryClassificationInput Input()
    {
        var binding = CdcContinuityFixture.CreateBinding(CdcProvider.SqlServer);
        var input = CdcContinuityFixture.CreateInput(binding);
        return input with
        {
            ConnectorOffset = input.ConnectorOffset! with { ChangeLsn = "NULL", EventSerialNo = 0 },
        };
    }

    [TestCase("00000023:00000137:ffff", CdcSourceHistoryContinuity.Lost)]
    [TestCase("00000023:00000138:0000", CdcSourceHistoryContinuity.Healthy)]
    [TestCase(Commit, CdcSourceHistoryContinuity.Healthy)]
    [TestCase("00000023:00000140:0000", CdcSourceHistoryContinuity.Healthy)]
    [TestCase("00000023:00000140:0001", CdcSourceHistoryContinuity.Lost)]
    public void It_requires_the_idle_resume_commit_to_remain_in_retained_history(
        string commit,
        CdcSourceHistoryContinuity expected
    )
    {
        var input = Input();
        input = input with { ConnectorOffset = input.ConnectorOffset! with { CommitLsn = commit } };
        var result = CdcSourceHistoryContinuityClassifier.Evaluate(input);
        result.Observation.Continuity.Should().Be(expected);
        result.Observation.PositionEvidence!.ChangeLsn.Should().Be("NULL");
        result.Observation.PositionEvidence.EventSerialNo.Should().Be(0);
        var read = CdcJsonContract.Deserialize<CdcSourceHistoryObservation>(
            CdcJsonContract.Serialize(result.Observation)
        );
        read.Succeeded.Should().BeTrue("{0}", string.Join(", ", read.Diagnostics));
        CdcSourceHistoryObservationValidator
            .ValidateForBinding(
                read.Contract!,
                input.Binding,
                new(
                    input.OperationId,
                    input.Binding.ToTargetIdentity(),
                    input.Binding.PhysicalSourceFingerprint,
                    input.NowUtc
                )
            )
            .Succeeded.Should()
            .BeTrue();
        if (expected == CdcSourceHistoryContinuity.Lost)
        {
            result
                .IncidentCandidate!.FailureCategory.Should()
                .Be(CdcIncidentFailureCategory.RetainedHistoryGap);
            var incident = Incident(result.IncidentCandidate);
            CdcIncidentValidator
                .ValidateForBinding(incident, input.Binding, input.NowUtc)
                .Succeeded.Should()
                .BeTrue();
            CdcJsonContract
                .Deserialize<CdcIncident>(CdcJsonContract.Serialize(incident))
                .Succeeded.Should()
                .BeTrue();
        }
    }

    [Test]
    public void It_round_trips_the_actual_offset_marker_in_the_connector_observation()
    {
        var input = Input();
        var read = CdcJsonContract.Deserialize<CdcConnectorOffsetObservation>(
            CdcJsonContract.Serialize(input.ConnectorOffset!)
        );
        read.Succeeded.Should().BeTrue();
        read.Contract!.ChangeLsn.Should().Be("NULL");
        CdcConnectorOffsetObservationValidator
            .ValidateForBinding(
                read.Contract,
                input.Binding,
                new(
                    input.OperationId,
                    input.Binding.ToTargetIdentity(),
                    input.Binding.PhysicalSourceFingerprint,
                    input.NowUtc
                ),
                input.ExpectedConnectSourcePartitionHash
            )
            .Succeeded.Should()
            .BeTrue();
    }

    [TestCase("source")]
    [TestCase("snapshot")]
    [TestCase("null")]
    [TestCase("schema")]
    [TestCase("jobs")]
    public void It_keeps_other_continuity_guards_mandatory(string condition)
    {
        var input = Input();
        input = condition switch
        {
            "source" => input with
            {
                ConnectorOffset = input.ConnectorOffset! with
                {
                    ConnectSourcePartitionHash = "sha256:" + new string('a', 64),
                },
            },
            "snapshot" => input with { ConnectorOffset = input.ConnectorOffset! with { IsSnapshot = true } },
            "null" => input with { ConnectorOffset = input.ConnectorOffset! with { IsNull = true } },
            "schema" => input with
            {
                SqlServerSchemaHistory = new(
                    CdcSqlServerSchemaHistoryEnablementPhase.AfterInitialAdmission,
                    CdcSqlServerSchemaHistoryState.Missing
                ),
            },
            "jobs" => input with
            {
                ProviderHistory = input.ProviderHistory! with
                {
                    SqlServerJobs = new(CdcSqlServerCdcJobState.Stopped, CdcSqlServerCdcJobState.Healthy),
                },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(condition)),
        };
        CdcSourceHistoryContinuityClassifier
            .Evaluate(input)
            .Observation.Continuity.Should()
            .NotBe(CdcSourceHistoryContinuity.Healthy);
    }

    [Test]
    public void It_never_clears_a_latched_incident_after_the_idle_commit_becomes_retained()
    {
        var input = Input();
        var lost = CdcSourceHistoryContinuityClassifier.Evaluate(
            input with
            {
                ProviderHistory = input.ProviderHistory! with
                {
                    RetainedRangeState = CdcProviderRetainedRangeState.Gap,
                },
            }
        );
        var result = CdcSourceHistoryContinuityClassifier.Evaluate(
            input with
            {
                LatchedIncident = Incident(lost.IncidentCandidate!),
            }
        );
        result.Observation.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
        result.Observation.IncidentLatched.Should().BeTrue();
    }

    [TestCase("provider")]
    [TestCase("missingCommit")]
    [TestCase("zeroCommit")]
    [TestCase("initialSerial")]
    [TestCase("otherSerial")]
    public void It_rejects_the_idle_marker_in_incidents_without_its_complete_sql_server_shape(
        string condition
    )
    {
        var input = Input();
        var observation = CdcSourceHistoryContinuityClassifier.Evaluate(input).Observation;
        var incident = new CdcIncident(
            CdcJsonContract.CurrentContractVersion,
            CdcIncidentType.SourceHistoryContinuityLost,
            input.ObservedAt,
            input.Binding.ToCompleteBindingIdentity(),
            CdcIncidentFailureCategory.RetainedHistoryGap,
            observation.PositionEvidence!
        );
        var position = incident.PositionMetadata;
        incident = condition switch
        {
            "provider" => incident with
            {
                BindingIdentity = incident.BindingIdentity with { Provider = CdcProvider.Postgresql },
            },
            "missingCommit" => incident with { PositionMetadata = position with { CommitLsn = null } },
            "zeroCommit" => incident with
            {
                PositionMetadata = position with { CommitLsn = "00000000:00000000:0000" },
            },
            "initialSerial" => incident with { PositionMetadata = position with { EventSerialNo = 1 } },
            "otherSerial" => incident with { PositionMetadata = position with { EventSerialNo = 2 } },
            _ => throw new ArgumentOutOfRangeException(nameof(condition)),
        };
        var result = CdcIncidentValidator.Validate(incident, input.NowUtc);
        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Path == "$.positionMetadata.changeLsn");
    }

    private static CdcIncident Incident(CdcSourceHistoryIncidentCandidate candidate) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            CdcIncidentType.SourceHistoryContinuityLost,
            candidate.ObservedAt,
            candidate.BindingIdentity,
            candidate.FailureCategory,
            candidate.PositionMetadata
        );
}
