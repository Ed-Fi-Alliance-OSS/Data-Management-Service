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
[Category("CdcTarget")]
public class Given_CdcTargetValidator
{
    private static CdcTargetInput ValidInput =>
        new(
            "dms-local",
            "",
            "1",
            "data-store-1",
            CdcProvider.Postgresql,
            "edfi.dms",
            1,
            3,
            CdcTargetValidator.KafkaMurmur2V1PartitionerAlgorithm
        );

    [Test]
    public void It_normalizes_valid_target_input_into_the_binding_identity_shape()
    {
        CdcTargetValidationResult result = CdcTargetValidator.Validate(ValidInput);

        result.Succeeded.Should().BeTrue();
        CdcValidatedTarget target = result.Target!;
        target.TenantKey.Should().Be("default");
        target.DataStoreId.Should().Be("1");
        target
            .ToTargetIdentity()
            .Should()
            .Be(
                new CdcTargetIdentity("dms-local", "default", "1", "data-store-1", 1, CdcProvider.Postgresql)
            );
        target
            .ToBindingIdentity()
            .Should()
            .Be(new CdcBindingIdentity("dms-local", "default", "1", "data-store-1", 1));
        result.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public void It_preserves_non_default_tenant_keys_after_validation()
    {
        CdcTargetValidationResult result = CdcTargetValidator.Validate(
            ValidInput with
            {
                TenantKey = "district-a",
            }
        );

        result.Succeeded.Should().BeTrue();
        result.Target!.TenantKey.Should().Be("district-a");
    }

    [Test]
    public void It_rejects_a_missing_tenant_key_without_default_mapping()
    {
        CdcTargetValidationResult result = CdcTargetValidator.Validate(ValidInput with { TenantKey = null });

        result.Succeeded.Should().BeFalse();
        result.Target.Should().BeNull();
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Category == CdcDiagnosticCategory.MissingRequiredField
                && diagnostic.Path == "$.tenantKey"
            );
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("0")]
    [TestCase("01")]
    [TestCase("+1")]
    [TestCase(" 1")]
    [TestCase("1 ")]
    [TestCase("1.0")]
    [TestCase("postgresql:1")]
    public void It_rejects_data_store_ids_that_are_not_positive_invariant_decimal_strings(string? dataStoreId)
    {
        CdcTargetValidationResult result = CdcTargetValidator.Validate(
            ValidInput with
            {
                DataStoreId = dataStoreId,
            }
        );

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Path == "$.dataStoreId");
    }

    [TestCase("DeploymentKey", "$.deploymentKey")]
    [TestCase("deployment..a", "$.deploymentKey")]
    [TestCase("deployment/a", "$.deploymentKey")]
    [TestCase("deployment\\a", "$.deploymentKey")]
    [TestCase(".deployment", "$.deploymentKey")]
    [TestCase("deployment-", "$.deploymentKey")]
    [TestCase("tenant A", "$.tenantKey")]
    [TestCase("../tenant", "$.tenantKey")]
    [TestCase("instance__a", "$.instanceKey")]
    [TestCase("edfi..dms", "$.topicPrefix")]
    public void It_rejects_unsafe_administrative_tokens(string token, string expectedPath)
    {
        CdcTargetInput input = expectedPath switch
        {
            "$.deploymentKey" => ValidInput with { DeploymentKey = token },
            "$.tenantKey" => ValidInput with { TenantKey = token },
            "$.instanceKey" => ValidInput with { InstanceKey = token },
            "$.topicPrefix" => ValidInput with { TopicPrefix = token },
            _ => throw new AssertionException($"Unsupported path {expectedPath}."),
        };

        CdcTargetValidationResult result = CdcTargetValidator.Validate(input);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Path == expectedPath);
        result
            .Diagnostics.Select(diagnostic => diagnostic.Message)
            .Should()
            .OnlyContain(message => !message.Contains(token, StringComparison.Ordinal));
    }

    [Test]
    public void It_rejects_unsupported_provider_partitioner_generation_and_partition_count()
    {
        CdcTargetValidationResult result = CdcTargetValidator.Validate(
            ValidInput with
            {
                Provider = (CdcProvider)999,
                PartitionerAlgorithm = "org.apache.kafka.clients.producer.internals.DefaultPartitioner",
                Generation = 0,
                PartitionCount = 0,
            }
        );

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Path == "$.provider");
        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Path == "$.partitionerAlgorithm");
        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Path == "$.generation");
        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Path == "$.partitionCount");
    }

    [Test]
    public void It_rejects_targets_that_cannot_render_required_kafka_or_connect_artifacts()
    {
        CdcTargetValidationResult result = CdcTargetValidator.Validate(
            ValidInput with
            {
                Provider = CdcProvider.SqlServer,
                TopicPrefix = new string('a', 230),
            }
        );

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Path == "$.topicPrefix"
                && diagnostic.Message.Contains("schemaHistoryTopicName", StringComparison.Ordinal)
            );
    }

    [Test]
    public void It_validates_binding_identity_with_the_same_target_rules()
    {
        CdcContractValidationResult valid = CdcTargetValidator.ValidateBindingIdentity(
            new("dms-local", "default", "1", "data-store-1", 1)
        );
        CdcContractValidationResult invalid = CdcTargetValidator.ValidateBindingIdentity(
            new("dms/local", "default", "01", "data-store-1", 0)
        );

        valid.Succeeded.Should().BeTrue();
        invalid.Succeeded.Should().BeFalse();
        invalid.Diagnostics.Should().Contain(diagnostic => diagnostic.Path == "$.deploymentKey");
        invalid.Diagnostics.Should().Contain(diagnostic => diagnostic.Path == "$.dataStoreId");
        invalid.Diagnostics.Should().Contain(diagnostic => diagnostic.Path == "$.generation");
    }
}
