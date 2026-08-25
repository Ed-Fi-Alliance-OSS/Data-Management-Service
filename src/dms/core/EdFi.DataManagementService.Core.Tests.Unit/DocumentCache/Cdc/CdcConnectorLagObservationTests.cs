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
[Category("CdcConnectorLagObservation")]
public class Given_CdcConnectorLagObservation
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 17, 13, 10, 11, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = ObservedAt.AddMinutes(1);

    private const string OperationId = "operation-1";
    private const string SourceFingerprint =
        "sha256:8caa6b0ad6db6f60d8d7ce6e78d1e76094e2241678c6f241670319ab60810851";

    private static CdcTargetIdentity TargetIdentity =>
        new("dms-local", "default", "1", "data-store-1", 1, CdcProvider.Postgresql);

    [Test]
    public void It_accepts_within_threshold_lag_observations_with_debezium_quantiles()
    {
        CdcConnectorLagObservation observation = new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservedAt,
            TargetIdentity,
            CdcProvider.Postgresql,
            SourceFingerprint,
            CdcConnectorLagState.WithinThreshold,
            250,
            1_000,
            100,
            200,
            400,
            []
        );

        string json = CdcJsonContract.Serialize(observation);
        JsonObject root = JsonNode.Parse(json)!.AsObject();

        root["lagState"]!.GetValue<string>().Should().Be("withinThreshold");
        root["currentLagMilliseconds"]!.GetValue<long>().Should().Be(250);
        root["p95LagMilliseconds"]!.GetValue<long>().Should().Be(200);

        CdcContractReadResult<CdcConnectorLagObservation> readResult =
            CdcJsonContract.Deserialize<CdcConnectorLagObservation>(json);
        CdcContractValidationResult validationResult = CdcConnectorLagObservationValidator.Validate(
            readResult.Contract!,
            new(OperationId, TargetIdentity, SourceFingerprint, Now)
        );

        readResult.Succeeded.Should().BeTrue();
        validationResult.Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_accepts_unknown_lag_observations_without_reusing_stale_telemetry()
    {
        CdcConnectorLagObservation observation = new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservedAt,
            TargetIdentity,
            CdcProvider.Postgresql,
            SourceFingerprint,
            CdcConnectorLagState.Unknown,
            null,
            null,
            null,
            null,
            null,
            []
        );

        CdcContractValidationResult validationResult = CdcConnectorLagObservationValidator.Validate(
            observation,
            new(OperationId, TargetIdentity, SourceFingerprint, Now)
        );

        validationResult.Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_rejects_future_incomplete_negative_and_out_of_order_lag_evidence()
    {
        CdcConnectorLagObservation observation = new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            Now.AddSeconds(1),
            TargetIdentity,
            CdcProvider.Postgresql,
            SourceFingerprint,
            CdcConnectorLagState.WithinThreshold,
            1_500,
            null,
            100,
            50,
            -1,
            []
        );

        CdcContractValidationResult result = CdcConnectorLagObservationValidator.Validate(
            observation,
            new(OperationId, TargetIdentity, SourceFingerprint, Now)
        );

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain(CdcDiagnosticCategory.InvalidTimestamp)
            .And.Contain(CdcDiagnosticCategory.MissingRequiredField)
            .And.Contain(CdcDiagnosticCategory.InvalidObservation)
            .And.Contain(CdcDiagnosticCategory.InvalidOrdering);
    }
}
