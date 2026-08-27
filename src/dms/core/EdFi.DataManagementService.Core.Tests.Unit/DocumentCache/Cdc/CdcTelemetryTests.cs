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
[Category("CdcTelemetry")]
public class Given_CdcTelemetry
{
    [Test]
    public void It_renders_only_the_allowed_bounded_labels()
    {
        CdcTargetIdentity targetIdentity = new(
            "deployment-a",
            "default",
            "7",
            "instance-a",
            3,
            CdcProvider.SqlServer
        );

        IReadOnlyDictionary<string, string> labels = CdcTelemetryLabels
            .FromTarget(
                targetIdentity,
                CdcReadiness.NotReady,
                CdcDiagnosticComponent.ConnectorRuntime,
                "not-ready"
            )
            .ToDictionary();

        labels
            .Keys.Should()
            .BeEquivalentTo(
                "provider",
                "readiness",
                "component",
                "deploymentKey",
                "instanceKey",
                "generation",
                "outcome"
            );
        labels["provider"].Should().Be("sqlServer");
        labels["readiness"].Should().Be("notReady");
        labels["component"].Should().Be("connectorRuntime");
        labels["deploymentKey"].Should().Be("deployment-a");
        labels["instanceKey"].Should().Be("instance-a");
        labels["generation"].Should().Be("3");
        labels["outcome"].Should().Be("not-ready");
    }

    [Test]
    public void It_rejects_unbounded_or_unsafe_label_values()
    {
        CdcTelemetryLabels labels = new(
            CdcProvider.Postgresql,
            CdcReadiness.Unknown,
            CdcDiagnosticComponent.StateStore,
            "../deployment-a",
            "instance/a",
            0,
            "consumer/group"
        );

        labels.DeploymentKey.Should().Be("invalid");
        labels.InstanceKey.Should().Be("invalid");
        labels.Generation.Should().Be("unknown");
        labels.Outcome.Should().Be("invalid");
    }
}
