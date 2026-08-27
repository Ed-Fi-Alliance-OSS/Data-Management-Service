// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache.Cdc;

[TestFixture]
[Parallelizable]
[Category("CdcProjectionCorrelation")]
public class Given_CdcProjectionCorrelation
{
    private static readonly DateTimeOffset ProjectionObservedAt = new(2026, 8, 17, 13, 10, 10, TimeSpan.Zero);
    private static readonly DateTimeOffset ObservedAt = ProjectionObservedAt.AddSeconds(1);
    private static readonly DateTimeOffset Now = ObservedAt.AddMinutes(1);

    private const string OperationId = "operation-1";
    private const string SourceFingerprint =
        "sha256:8caa6b0ad6db6f60d8d7ce6e78d1e76094e2241678c6f241670319ab60810851";

    private static CdcTargetIdentity TargetIdentity =>
        new("dms-local", "default", "1", "data-store-1", 1, CdcProvider.Postgresql);

    private static CdcObservationValidationContext Context =>
        new(OperationId, TargetIdentity, SourceFingerprint, Now);

    [Test]
    public void It_accepts_matched_e18_projection_status_for_the_same_target_and_source()
    {
        CdcProjectionCorrelationObservation observation = ValidObservation();

        string json = CdcJsonContract.Serialize(observation);
        JsonObject root = JsonNode.Parse(json)!.AsObject();

        root["e18TargetKey"]!["tenantKey"]!.GetValue<string>().Should().BeEmpty();
        root["e18TargetKey"]!["dataStoreId"]!.GetValue<long>().Should().Be(1);
        root["correlationState"]!.GetValue<string>().Should().Be("matched");
        root["operationalHealthStatus"]!.GetValue<string>().Should().Be("operational");
        root["caughtUpStatus"]!.GetValue<string>().Should().Be("caughtUp");
        root["queuePresence"]!.GetValue<string>().Should().Be("empty");
        root["enqueueFailureCategories"]![0]!.GetValue<string>().Should().Be("workPersistenceFailed");

        CdcContractReadResult<CdcProjectionCorrelationObservation> readResult =
            CdcJsonContract.Deserialize<CdcProjectionCorrelationObservation>(json);
        CdcContractValidationResult validationResult = CdcProjectionCorrelationObservationValidator.Validate(
            readResult.Contract!,
            Context
        );

        readResult.Succeeded.Should().BeTrue();
        validationResult.Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_rejects_e18_target_and_correlation_mismatch_evidence()
    {
        CdcProjectionCorrelationObservation observation = ValidObservation() with
        {
            E18TargetKey = new DocumentCacheStatusTargetKey("district-a", 1),
            CorrelationState = CdcProjectionCorrelationState.SourceMismatch,
        };

        CdcContractValidationResult result = CdcProjectionCorrelationObservationValidator.Validate(
            observation,
            Context
        );

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain(CdcDiagnosticCategory.TargetMismatch)
            .And.Contain(CdcDiagnosticCategory.SourceMismatch);
    }

    [Test]
    public void It_rejects_future_or_out_of_order_projection_observation_timestamps_and_invalid_e18_enums()
    {
        CdcProjectionCorrelationObservation observation = ValidObservation() with
        {
            ProjectionObservedAt = Now.AddSeconds(1),
            OperationalHealthStatus = (DocumentCacheOperationalHealthStatus)999,
            CaughtUpStatus = (DocumentCacheCaughtUpStatus)999,
            QueuePresence = (DocumentCacheStatusQueuePresence)999,
            EnqueueFailureCategories = [(DocumentCacheStatusEnqueueFailureCategory)999],
        };

        CdcContractValidationResult result = CdcProjectionCorrelationObservationValidator.Validate(
            observation,
            Context
        );

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain(CdcDiagnosticCategory.InvalidTimestamp)
            .And.Contain(CdcDiagnosticCategory.InvalidOrdering)
            .And.Contain(CdcDiagnosticCategory.InvalidEnumValue);
    }

    private static CdcProjectionCorrelationObservation ValidObservation() =>
        new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservedAt,
            TargetIdentity,
            CdcProvider.Postgresql,
            SourceFingerprint,
            ProjectionObservedAt,
            new DocumentCacheStatusTargetKey("", 1),
            CdcProjectionCorrelationState.Matched,
            DocumentCacheOperationalHealthStatus.Operational,
            DocumentCacheStatusReason.None,
            DocumentCacheCaughtUpStatus.CaughtUp,
            DocumentCacheStatusReason.None,
            DocumentCacheStatusQueuePresence.Empty,
            [DocumentCacheStatusEnqueueFailureCategory.WorkPersistenceFailed],
            []
        );
}
