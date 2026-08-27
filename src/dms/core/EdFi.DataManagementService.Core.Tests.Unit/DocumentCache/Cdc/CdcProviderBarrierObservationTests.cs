// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache.Cdc;

[TestFixture]
[Parallelizable]
[Category("CdcProviderBarrierObservation")]
public class Given_CdcProviderBarrierObservation
{
    private static readonly DateTimeOffset ProjectionObservedAt = new(2026, 8, 17, 13, 10, 10, TimeSpan.Zero);
    private static readonly DateTimeOffset BarrierCapturedAt = ProjectionObservedAt.AddSeconds(1);
    private static readonly DateTimeOffset OffsetObservedAt = BarrierCapturedAt.AddSeconds(1);
    private static readonly DateTimeOffset ObservedAt = OffsetObservedAt.AddSeconds(1);
    private static readonly DateTimeOffset Now = ObservedAt.AddMinutes(1);

    private const string OperationId = "operation-1";
    private const string SourceFingerprint =
        "sha256:8caa6b0ad6db6f60d8d7ce6e78d1e76094e2241678c6f241670319ab60810851";

    private static CdcTargetIdentity PostgresqlTargetIdentity =>
        new("dms-local", "default", "1", "data-store-1", 1, CdcProvider.Postgresql);

    private static CdcTargetIdentity SqlServerTargetIdentity =>
        PostgresqlTargetIdentity with
        {
            Provider = CdcProvider.SqlServer,
        };

    [Test]
    public void It_accepts_postgresql_barrier_reached_observations_with_ordered_operation_evidence()
    {
        CdcProviderBarrierObservation observation = new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservedAt,
            PostgresqlTargetIdentity,
            CdcProvider.Postgresql,
            SourceFingerprint,
            ProjectionObservedAt,
            BarrierCapturedAt,
            OffsetObservedAt,
            CdcProviderBarrierState.Reached,
            "0/16B6C50",
            null,
            null,
            null,
            "0/16B6C51",
            []
        );

        string json = CdcJsonContract.Serialize(observation);
        JsonObject root = JsonNode.Parse(json)!.AsObject();

        root["barrierState"]!.GetValue<string>().Should().Be("reached");
        root["postgresqlBarrierLsn"]!.GetValue<string>().Should().Be("0/16B6C50");
        root["sqlServerCommitLsn"].Should().BeNull();
        json.Should().NotContain("SqlServer");

        CdcContractReadResult<CdcProviderBarrierObservation> readResult =
            CdcJsonContract.Deserialize<CdcProviderBarrierObservation>(json);
        CdcContractValidationResult validationResult = CdcProviderBarrierObservationValidator.Validate(
            readResult.Contract!,
            new(OperationId, PostgresqlTargetIdentity, SourceFingerprint, Now)
        );

        readResult.Succeeded.Should().BeTrue();
        validationResult.Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_accepts_sql_server_commit_change_and_event_barrier_positions()
    {
        CdcProviderBarrierObservation observation = new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservedAt,
            SqlServerTargetIdentity,
            CdcProvider.SqlServer,
            SourceFingerprint,
            ProjectionObservedAt,
            BarrierCapturedAt,
            OffsetObservedAt,
            CdcProviderBarrierState.Reached,
            null,
            "00000023:00000138:0002",
            "00000023:00000139:0001",
            2,
            "00000023:00000138:0002/00000023:00000139:0001/2",
            []
        );

        string json = CdcJsonContract.Serialize(observation);
        JsonObject root = JsonNode.Parse(json)!.AsObject();

        root["provider"]!.GetValue<string>().Should().Be("sqlServer");
        root["sqlServerCommitLsn"]!.GetValue<string>().Should().Be("00000023:00000138:0002");
        root["sqlServerEventSerialNo"]!.GetValue<long>().Should().Be(2);

        CdcContractValidationResult validationResult = CdcProviderBarrierObservationValidator.Validate(
            observation,
            new(OperationId, SqlServerTargetIdentity, SourceFingerprint, Now)
        );

        validationResult.Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_rejects_and_redacts_sensitive_committed_position_evidence()
    {
        CdcProviderBarrierObservation observation = new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservedAt,
            PostgresqlTargetIdentity,
            CdcProvider.Postgresql,
            SourceFingerprint,
            ProjectionObservedAt,
            BarrierCapturedAt,
            OffsetObservedAt,
            CdcProviderBarrierState.Reached,
            "0/16B6C50",
            null,
            null,
            null,
            "serverprod-db",
            []
        );

        string json = CdcJsonContract.Serialize(observation);
        CdcContractValidationResult result = CdcProviderBarrierObservationValidator.Validate(
            observation,
            new(OperationId, PostgresqlTargetIdentity, SourceFingerprint, Now)
        );

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain(CdcDiagnosticCategory.UnsafeEvidence);
        json.Should().NotContain("server").And.NotContain("prod-db");
    }

    [Test]
    public void It_rejects_future_out_of_order_malformed_and_provider_inapplicable_barrier_evidence()
    {
        CdcProviderBarrierObservation observation = new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            Now.AddSeconds(1),
            PostgresqlTargetIdentity,
            CdcProvider.Postgresql,
            SourceFingerprint,
            BarrierCapturedAt.AddSeconds(1),
            BarrierCapturedAt,
            BarrierCapturedAt.AddSeconds(-1),
            CdcProviderBarrierState.Reached,
            "not-lsn",
            "00000023:00000138:0002",
            null,
            2,
            null,
            []
        );

        CdcContractValidationResult result = CdcProviderBarrierObservationValidator.Validate(
            observation,
            new(OperationId, PostgresqlTargetIdentity, SourceFingerprint, Now)
        );

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain(CdcDiagnosticCategory.InvalidTimestamp)
            .And.Contain(CdcDiagnosticCategory.InvalidOrdering)
            .And.Contain(CdcDiagnosticCategory.MalformedPayload)
            .And.Contain(CdcDiagnosticCategory.InvalidObservation)
            .And.Contain(CdcDiagnosticCategory.MissingRequiredField);
    }
}
