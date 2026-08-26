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
[Category("CdcStatusContract")]
public class Given_CdcStatusContract
{
    private static readonly DateTimeOffset SampleObservedAt = new(2026, 8, 17, 13, 10, 11, TimeSpan.Zero);

    private static readonly CdcTargetIdentity SampleTargetIdentity = new(
        "dms-local",
        "default",
        "1",
        "data-store-1",
        1,
        CdcProvider.SqlServer
    );

    [Test]
    public void It_serializes_aggregate_and_per_target_status_with_required_component_names()
    {
        CdcStatus status = new(
            CdcJsonContract.CurrentContractVersion,
            SampleObservedAt,
            CdcReadiness.NotReady,
            CdcBlockingCategory.SourceHistoryLost,
            [
                new(
                    SampleTargetIdentity,
                    CdcReadiness.NotReady,
                    CdcBlockingCategory.SourceHistoryLost,
                    CdcComponent.Satisfied(SampleObservedAt),
                    CdcComponent.Satisfied(SampleObservedAt),
                    CdcComponent.Satisfied(SampleObservedAt),
                    CdcComponent.Satisfied(SampleObservedAt),
                    CdcSourceHistoryComponent.FromComponent(
                        CdcComponent.NotSatisfied(
                            CdcBlockingCategory.SourceHistoryLost,
                            SampleObservedAt,
                            "history continuity lost"
                        ),
                        CdcSourceHistoryContinuity.Lost,
                        incidentLatched: true
                    ),
                    CdcComponent.Satisfied(SampleObservedAt),
                    CdcComponent.Satisfied(SampleObservedAt),
                    CdcComponent.Satisfied(SampleObservedAt),
                    CdcComponent.Satisfied(SampleObservedAt),
                    CdcComponent.Satisfied(SampleObservedAt),
                    [
                        new(
                            CdcDiagnosticCategory.MalformedPayload,
                            "$.sourceHistory",
                            "continuity evidence unavailable"
                        ),
                    ]
                ),
            ]
        );

        string json = CdcJsonContract.Serialize(status);
        JsonObject root = JsonNode.Parse(json)!.AsObject();
        JsonObject target = root["targets"]![0]!.AsObject();

        root["contractVersion"]!.GetValue<int>().Should().Be(1);
        root["observedAt"]!.GetValue<DateTimeOffset>().Should().Be(SampleObservedAt);
        root["readiness"]!.GetValue<string>().Should().Be("notReady");
        root["primaryBlockingCategory"]!.GetValue<string>().Should().Be("sourceHistoryLost");
        target["targetIdentity"]!["provider"]!.GetValue<string>().Should().Be("sqlServer");
        target["readiness"]!.GetValue<string>().Should().Be("notReady");
        target
            .Select(property => property.Key)
            .Should()
            .Contain(
                "binding",
                "projection",
                "providerSetup",
                "providerBarrier",
                "sourceHistory",
                "kafkaPolicy",
                "connectOffsetStore",
                "connectorConfig",
                "connectorRuntime",
                "lag",
                "diagnostics"
            );
        target["sourceHistory"]!["state"]!.GetValue<string>().Should().Be("notSatisfied");
        target["sourceHistory"]!["category"]!.GetValue<string>().Should().Be("sourceHistoryLost");
        target["sourceHistory"]!["continuity"]!.GetValue<string>().Should().Be("lost");
        target["sourceHistory"]!["incidentLatched"]!.GetValue<bool>().Should().BeTrue();
        target["diagnostics"]![0]!["category"]!.GetValue<string>().Should().Be("malformedPayload");
        json.Should().NotContain("CdcReadiness");
        json.Should().NotContain("SourceHistoryLost");

        CdcContractReadResult<CdcStatus> result = CdcJsonContract.Deserialize<CdcStatus>(json);
        CdcStatus expected = status with
        {
            Targets =
            [
                status.Targets[0] with
                {
                    Diagnostics =
                    [
                        .. status.Targets[0].Diagnostics.Select(diagnostic => diagnostic.WithPath("$")),
                    ],
                },
            ],
        };

        result.Succeeded.Should().BeTrue();
        result.Contract.Should().BeEquivalentTo(expected);
    }
}
