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
[Category("CdcAdmissionContract")]
public class Given_CdcAdmissionContract
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

    [Test]
    public void It_serializes_initial_cdc_admission_with_stable_step_names()
    {
        CdcAdmission admission = new(
            CdcJsonContract.CurrentContractVersion,
            "op-20260817-001",
            SampleObservedAt,
            SampleTargetIdentity,
            CdcAdmissionState.NotAdmitted,
            CdcBlockingCategory.ProviderBarrierNotReached,
            new(
                CdcComponent.Satisfied(SampleObservedAt),
                CdcComponent.Satisfied(SampleObservedAt),
                CdcComponent.Satisfied(SampleObservedAt),
                CdcComponent.Satisfied(SampleObservedAt),
                CdcComponent.Satisfied(SampleObservedAt),
                CdcComponent.NotSatisfied(
                    CdcBlockingCategory.ProviderBarrierNotReached,
                    SampleObservedAt,
                    "provider barrier not reached"
                ),
                CdcComponent.Unknown(CdcBlockingCategory.ProviderHistoryUnknown, SampleObservedAt),
                CdcComponent.Unknown(CdcBlockingCategory.ProjectionBacklog, SampleObservedAt),
                CdcComponent.Unknown(CdcBlockingCategory.LagExceeded, SampleObservedAt)
            ),
            [
                new(
                    CdcDiagnosticCategory.MalformedPayload,
                    "$.providerBarrier",
                    "barrier evidence not yet available"
                ),
            ]
        );

        string json = CdcJsonContract.Serialize(admission);
        JsonObject root = JsonNode.Parse(json)!.AsObject();
        JsonObject steps = root["steps"]!.AsObject();

        root["operationId"]!.GetValue<string>().Should().Be("op-20260817-001");
        root["targetIdentity"]!["provider"]!.GetValue<string>().Should().Be("postgresql");
        root["admissionState"]!.GetValue<string>().Should().Be("notAdmitted");
        root["primaryBlockingCategory"]!.GetValue<string>().Should().Be("providerBarrierNotReached");
        steps
            .Select(property => property.Key)
            .Should()
            .BeEquivalentTo(
                "binding",
                "guardedTrackingActivation",
                "providerSetup",
                "connectorAndTopicValidation",
                "firstProjectionCaughtUp",
                "providerBarrier",
                "sourceHistory",
                "secondProjectionCaughtUp",
                "lag"
            );
        steps["providerBarrier"]!["state"]!.GetValue<string>().Should().Be("notSatisfied");
        steps["sourceHistory"]!["state"]!.GetValue<string>().Should().Be("unknown");
        root["diagnostics"]![0]!["category"]!.GetValue<string>().Should().Be("malformedPayload");
        json.Should().NotContain("canonicalWriteAdmission");
        json.Should().NotContain("DocumentProjectionWork");

        CdcContractReadResult<CdcAdmission> result = CdcJsonContract.Deserialize<CdcAdmission>(json);

        result.Succeeded.Should().BeTrue();
        result.Contract.Should().BeEquivalentTo(admission);
    }
}
