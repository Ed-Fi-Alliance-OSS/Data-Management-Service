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
[Category("CdcRetryContract")]
public class Given_CdcRetryContract
{
    private static readonly DateTimeOffset SampleObservedAt = new(2026, 8, 17, 13, 10, 11, TimeSpan.Zero);

    private static readonly CdcTargetIdentity SampleTargetIdentity = new(
        "dms-local",
        "default",
        "1",
        "data-store-1",
        1,
        CdcProvider.Postgresql
    );

    [TestCase(
        CdcRetryClassification.RetryGuardedActivation,
        CdcRetryAction.Proceed,
        "retryGuardedActivation"
    )]
    [TestCase(
        CdcRetryClassification.ResumeProviderTopicConnectorSetup,
        CdcRetryAction.Proceed,
        "resumeProviderTopicConnectorSetup"
    )]
    [TestCase(
        CdcRetryClassification.RejectUnboundTracking,
        CdcRetryAction.FailClosed,
        "rejectUnboundTracking"
    )]
    [TestCase(
        CdcRetryClassification.RejectBindingMismatch,
        CdcRetryAction.FailClosed,
        "rejectBindingMismatch"
    )]
    [TestCase(
        CdcRetryClassification.RejectResettingLifecycle,
        CdcRetryAction.FailClosed,
        "rejectResettingLifecycle"
    )]
    [TestCase(
        CdcRetryClassification.RejectRebuildingLifecycle,
        CdcRetryAction.FailClosed,
        "rejectRebuildingLifecycle"
    )]
    [TestCase(
        CdcRetryClassification.RejectCacheAheadLatch,
        CdcRetryAction.FailClosed,
        "rejectCacheAheadLatch"
    )]
    [TestCase(CdcRetryClassification.RejectUnexpectedRows, CdcRetryAction.FailClosed, "rejectUnexpectedRows")]
    [TestCase(
        CdcRetryClassification.RejectNotInitialWorkflow,
        CdcRetryAction.RetireUnusedBindingAndReprovision,
        "rejectNotInitialWorkflow"
    )]
    public void It_serializes_retry_classification_and_action_values(
        CdcRetryClassification retryClassification,
        CdcRetryAction action,
        string expectedRetryClassification
    )
    {
        CdcRetry retry = new(
            CdcJsonContract.CurrentContractVersion,
            "op-20260817-001",
            SampleObservedAt,
            SampleTargetIdentity,
            retryClassification,
            action,
            CdcBlockingCategory.BindingMismatch,
            [
                new(
                    CdcDiagnosticCategory.InvalidEnumValue,
                    "$.lifecycle",
                    "unsupported lifecycle for initial CDC retry"
                ),
            ]
        );

        string json = CdcJsonContract.Serialize(retry);
        JsonObject root = JsonNode.Parse(json)!.AsObject();

        root["contractVersion"]!.GetValue<int>().Should().Be(1);
        root["operationId"]!.GetValue<string>().Should().Be("op-20260817-001");
        root["targetIdentity"]!["provider"]!.GetValue<string>().Should().Be("postgresql");
        root["retryClassification"]!.GetValue<string>().Should().Be(expectedRetryClassification);
        root["action"]!
            .GetValue<string>()
            .Should()
            .Be(
                action switch
                {
                    CdcRetryAction.Proceed => "proceed",
                    CdcRetryAction.FailClosed => "failClosed",
                    CdcRetryAction.RetireUnusedBindingAndReprovision => "retireUnusedBindingAndReprovision",
                    _ => throw new ArgumentOutOfRangeException(nameof(action)),
                }
            );
        root["primaryBlockingCategory"]!.GetValue<string>().Should().Be("bindingMismatch");
        root["diagnostics"]![0]!["category"]!.GetValue<string>().Should().Be("invalidEnumValue");
        json.Should().NotContain("CdcRetryClassification");
        json.Should().NotContain("Reject");

        CdcContractReadResult<CdcRetry> result = CdcJsonContract.Deserialize<CdcRetry>(json);
        CdcRetry expected = retry with
        {
            Diagnostics = [.. retry.Diagnostics.Select(diagnostic => diagnostic.WithPath("$"))],
        };

        result.Succeeded.Should().BeTrue();
        result.Contract.Should().BeEquivalentTo(expected);
    }
}
