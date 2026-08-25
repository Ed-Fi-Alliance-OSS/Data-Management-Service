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
[Category("CdcTopicPrefix")]
public class Given_CdcTopicPrefixRecovery
{
    private static CdcBinding SampleBinding =>
        new(
            1,
            "dms-local",
            "default",
            "1",
            "data-store-1",
            1,
            CdcProvider.Postgresql,
            "sha256:8caa6b0ad6db6f60d8d7ce6e78d1e76094e2241678c6f241670319ab60810851",
            "dms-local-data-store-1-g1",
            "edfi.dms.instance.data-store-1-g1.documents.v1",
            1,
            CdcTargetValidator.KafkaMurmur2V1PartitionerAlgorithm,
            CdcJsonContract.CurrentContractVersion
        );

    [Test]
    public void It_recovers_topic_prefix_from_persisted_topic_name_and_recomputes_inventory()
    {
        CdcArtifactNameResult result = CdcArtifactNameGenerator.RecoverFromBinding(SampleBinding);

        result.Succeeded.Should().BeTrue();
        result.Inventory!.TopicPrefix.Should().Be("edfi.dms");
        result.Inventory.ConnectorName.Should().Be(SampleBinding.ConnectorName);
        result.Inventory.TopicName.Should().Be(SampleBinding.TopicName);
        result.Inventory.ProgressTopicName.Should().Be($"{SampleBinding.TopicName}.cdc-progress");
        result.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public void It_reports_mismatch_when_the_persisted_topic_name_does_not_have_the_deterministic_suffix()
    {
        CdcArtifactNameResult result = CdcArtifactNameGenerator.RecoverFromBinding(
            SampleBinding with
            {
                TopicName = "edfi.dms.instance.data-store-1-g1.documents.v2",
            }
        );

        result.Succeeded.Should().BeFalse();
        result.Inventory.Should().BeNull();
        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Path == "$.topicName");
    }

    [Test]
    public void It_validates_the_recovered_topic_prefix()
    {
        CdcArtifactNameResult result = CdcArtifactNameGenerator.RecoverFromBinding(
            SampleBinding with
            {
                TopicName = "Edfi.instance.data-store-1-g1.documents.v1",
            }
        );

        result.Succeeded.Should().BeFalse();
        result.Inventory.Should().BeNull();
        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Path == "$.topicPrefix");
    }

    [Test]
    public void It_reports_mismatch_when_the_persisted_connector_name_differs_from_the_inventory()
    {
        CdcArtifactNameResult result = CdcArtifactNameGenerator.RecoverFromBinding(
            SampleBinding with
            {
                ConnectorName = "dms-local-data-store-2-g1",
            }
        );

        result.Succeeded.Should().BeFalse();
        result.Inventory.Should().BeNull();
        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Path == "$.connectorName");
    }
}
