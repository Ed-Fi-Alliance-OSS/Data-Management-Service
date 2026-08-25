// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache.Cdc;

[TestFixture]
[Parallelizable]
[Category("CdcComponentContract")]
public class Given_CdcComponentContract
{
    private static readonly DateTimeOffset SampleObservedAt = new(2026, 8, 17, 13, 10, 11, TimeSpan.Zero);

    private static readonly CdcTargetIdentity SampleTargetIdentity = new(
        "deployment-a",
        "default",
        "7",
        "instance-a",
        3,
        CdcProvider.SqlServer
    );

    [Test]
    public void It_serializes_common_identity_component_and_lifecycle_primitives()
    {
        string noisyMessage = "{projection:\"raw\"}<unsafe>\r\n" + new string('x', 540);
        CdcPrimitiveContract contract = new(
            CdcJsonContract.CurrentContractVersion,
            SampleTargetIdentity,
            CdcBindingIdentity.FromTargetIdentity(SampleTargetIdentity),
            CdcReadiness.NotReady,
            CdcLifecycleState.Tracking,
            CdcComponent.NotSatisfied(CdcBlockingCategory.ProjectionBacklog, SampleObservedAt, noisyMessage),
            []
        );

        string json = CdcJsonContract.Serialize(contract);
        JsonObject root = JsonNode.Parse(json)!.AsObject();

        root["targetIdentity"]!["deploymentKey"]!.GetValue<string>().Should().Be("deployment-a");
        root["targetIdentity"]!["provider"]!.GetValue<string>().Should().Be("sqlServer");
        root["bindingIdentity"]!["instanceKey"]!.GetValue<string>().Should().Be("instance-a");
        root["bindingIdentity"]!.AsObject().Should().NotContainKey("provider");
        root["readiness"]!.GetValue<string>().Should().Be("notReady");
        root["lifecycle"]!.GetValue<string>().Should().Be("tracking");
        root["component"]!["state"]!.GetValue<string>().Should().Be("notSatisfied");
        root["component"]!["category"]!.GetValue<string>().Should().Be("projectionBacklog");
        root["component"]!["observedAt"]!.GetValue<DateTimeOffset>().Should().Be(SampleObservedAt);

        string sanitizedMessage = root["component"]!["message"]!.GetValue<string>();
        sanitizedMessage.Should().HaveLength(512);
        sanitizedMessage.Should().NotContain("\r");
        sanitizedMessage.Should().NotContain("\n");
        sanitizedMessage.Should().NotContain("{");
        sanitizedMessage.Should().NotContain("}");
        sanitizedMessage.Should().NotContain("<");
        sanitizedMessage.Should().NotContain(">");
        json.Should().NotContain("CdcComponentState");
        json.Should().NotContain("ProjectionBacklog");
    }

    [Test]
    public void It_serializes_typed_sanitized_diagnostics()
    {
        CdcPrimitiveContract contract = new(
            CdcJsonContract.CurrentContractVersion,
            SampleTargetIdentity,
            CdcBindingIdentity.FromTargetIdentity(SampleTargetIdentity),
            CdcReadiness.Unknown,
            CdcLifecycleState.Unknown,
            CdcComponent.Unknown(
                CdcBlockingCategory.StatusObservationUnavailable,
                message: "status unavailable"
            ),
            [
                new CdcDiagnostic(
                    CdcDiagnosticCategory.InvalidEnumValue,
                    "$.provider",
                    "{rawProvider:\"secret\"}\r\nnot-valid"
                ),
            ]
        );

        JsonObject root = JsonNode.Parse(CdcJsonContract.Serialize(contract))!.AsObject();

        root["diagnostics"]![0]!["category"]!.GetValue<string>().Should().Be("invalidEnumValue");
        root["diagnostics"]![0]!["path"]!.GetValue<string>().Should().Be("$.provider");
        string message = root["diagnostics"]![0]!["message"]!.GetValue<string>();
        message.Should().NotContain("{");
        message.Should().NotContain("}");
        message.Should().NotContain("\r");
        message.Should().NotContain("\n");
    }

    [Test]
    public void It_uses_none_for_satisfied_and_not_applicable_components()
    {
        CdcComponent satisfied = CdcComponent.Satisfied(SampleObservedAt);
        CdcComponent notApplicable = CdcComponent.NotApplicable();

        satisfied.Category.Should().Be(CdcBlockingCategory.None);
        satisfied.State.Should().Be(CdcComponentState.Satisfied);
        satisfied.Message.Should().BeNull();
        notApplicable.Category.Should().Be(CdcBlockingCategory.None);
        notApplicable.State.Should().Be(CdcComponentState.NotApplicable);
    }

    private sealed record CdcPrimitiveContract(
        [property: JsonRequired] int ContractVersion,
        CdcTargetIdentity TargetIdentity,
        CdcBindingIdentity BindingIdentity,
        CdcReadiness Readiness,
        CdcLifecycleState Lifecycle,
        CdcComponent Component,
        IReadOnlyList<CdcDiagnostic> Diagnostics
    ) : ICdcJsonContract;
}
