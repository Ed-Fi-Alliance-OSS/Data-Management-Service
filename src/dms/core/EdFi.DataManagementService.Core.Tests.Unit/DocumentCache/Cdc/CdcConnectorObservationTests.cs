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
[Category("CdcConnectorObservation")]
public class Given_CdcConnectorObservation
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 17, 13, 10, 11, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = ObservedAt.AddMinutes(1);

    private const string OperationId = "operation-1";
    private const string SourceFingerprint =
        "sha256:8caa6b0ad6db6f60d8d7ce6e78d1e76094e2241678c6f241670319ab60810851";

    [Test]
    public void It_accepts_shared_connect_offset_store_policy_observations()
    {
        CdcTargetIdentity targetIdentity = CreateBinding(CdcProvider.Postgresql).ToTargetIdentity();
        CdcConnectOffsetStorePolicyObservation observation = new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservedAt,
            targetIdentity,
            CdcProvider.Postgresql,
            SourceFingerprint,
            "worker-1",
            "connect-offsets",
            CdcConnectOffsetStorePolicyState.Satisfied,
            "compact",
            1,
            1,
            CdcConnectOffsetStoreItemState.Satisfied,
            []
        );

        string json = CdcJsonContract.Serialize(observation);
        JsonObject root = JsonNode.Parse(json)!.AsObject();

        root["workerKey"]!.GetValue<string>().Should().Be("worker-1");
        root["offsetStorageTopic"]!.GetValue<string>().Should().Be("connect-offsets");
        root["policyState"]!.GetValue<string>().Should().Be("satisfied");
        root["aclState"]!.GetValue<string>().Should().Be("satisfied");
        json.Should().NotContain("principal");
        json.Should().NotContain("credential");

        CdcContractReadResult<CdcConnectOffsetStorePolicyObservation> readResult =
            CdcJsonContract.Deserialize<CdcConnectOffsetStorePolicyObservation>(json);
        CdcContractValidationResult validationResult =
            CdcConnectOffsetStorePolicyObservationValidator.Validate(
                readResult.Contract!,
                new(OperationId, targetIdentity, SourceFingerprint, Now)
            );

        readResult.Succeeded.Should().BeTrue();
        validationResult.Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_accepts_connector_configuration_and_runtime_observations_for_binding_derived_names()
    {
        CdcBinding binding = CreateBinding(CdcProvider.SqlServer);
        CdcArtifactInventory inventory = CdcArtifactNameGenerator.RecoverFromBinding(binding).Inventory!;
        CdcConnectorConfigurationObservation configuration = new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservedAt,
            binding.ToTargetIdentity(),
            CdcProvider.SqlServer,
            SourceFingerprint,
            inventory.ConnectorName,
            CdcConnectorConfigurationState.Matched,
            inventory.TopicPrefix,
            1,
            CdcConnectorConfigurationItemState.Matched,
            CdcConnectorConfigurationItemState.Matched,
            CdcConnectorConfigurationItemState.Matched,
            CdcConnectorConfigurationItemState.Matched,
            CdcConnectorConfigurationItemState.Matched,
            CdcConnectorConfigurationItemState.Matched,
            CdcConnectorConfigurationItemState.Matched,
            []
        );
        CdcConnectorRuntimeObservation runtime = new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservedAt.AddSeconds(1),
            binding.ToTargetIdentity(),
            CdcProvider.SqlServer,
            SourceFingerprint,
            inventory.ConnectorName,
            CdcConnectorRuntimeState.Running,
            1,
            1,
            CdcConnectorRuntimeState.Running,
            CdcConnectorSnapshotState.Completed,
            null,
            null,
            []
        );

        string configurationJson = CdcJsonContract.Serialize(configuration);
        string runtimeJson = CdcJsonContract.Serialize(runtime);
        JsonObject configurationRoot = JsonNode.Parse(configurationJson)!.AsObject();
        JsonObject runtimeRoot = JsonNode.Parse(runtimeJson)!.AsObject();

        configurationRoot["configurationState"]!.GetValue<string>().Should().Be("matched");
        configurationRoot["schemaHistoryState"]!.GetValue<string>().Should().Be("matched");
        runtimeRoot["connectorState"]!.GetValue<string>().Should().Be("running");
        runtimeRoot["snapshotState"]!.GetValue<string>().Should().Be("completed");
        configurationJson.Should().NotContain("databaseName");
        runtimeJson.Should().NotContain("stackTrace");

        CdcContractValidationResult configurationResult =
            CdcConnectorConfigurationObservationValidator.ValidateForBinding(
                configuration,
                binding,
                new(OperationId, binding.ToTargetIdentity(), SourceFingerprint, Now)
            );
        CdcContractValidationResult runtimeResult =
            CdcConnectorRuntimeObservationValidator.ValidateForBinding(
                runtime,
                binding,
                new(OperationId, binding.ToTargetIdentity(), SourceFingerprint, Now)
            );

        configurationResult.Succeeded.Should().BeTrue();
        runtimeResult.Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_rejects_mismatched_connector_names_invalid_counts_and_unsafe_error_categories()
    {
        CdcBinding binding = CreateBinding(CdcProvider.Postgresql);
        CdcArtifactInventory inventory = CdcArtifactNameGenerator.RecoverFromBinding(binding).Inventory!;
        CdcConnectorConfigurationObservation configuration = new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservedAt,
            binding.ToTargetIdentity(),
            CdcProvider.Postgresql,
            SourceFingerprint,
            $"{inventory.ConnectorName}-wrong",
            CdcConnectorConfigurationState.Matched,
            "wrong.topic-prefix",
            2,
            CdcConnectorConfigurationItemState.Matched,
            CdcConnectorConfigurationItemState.Matched,
            CdcConnectorConfigurationItemState.Matched,
            CdcConnectorConfigurationItemState.Matched,
            CdcConnectorConfigurationItemState.Matched,
            CdcConnectorConfigurationItemState.Matched,
            CdcConnectorConfigurationItemState.Matched,
            []
        );
        CdcConnectorRuntimeObservation runtime = new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservedAt,
            binding.ToTargetIdentity(),
            CdcProvider.Postgresql,
            SourceFingerprint,
            $"{inventory.ConnectorName}-wrong",
            CdcConnectorRuntimeState.Running,
            1,
            0,
            CdcConnectorRuntimeState.Failed,
            CdcConnectorSnapshotState.Running,
            "password=not-allowed",
            Now.AddSeconds(1),
            []
        );

        CdcContractValidationResult configurationResult =
            CdcConnectorConfigurationObservationValidator.ValidateForBinding(
                configuration,
                binding,
                new(OperationId, binding.ToTargetIdentity(), SourceFingerprint, Now)
            );
        CdcContractValidationResult runtimeResult =
            CdcConnectorRuntimeObservationValidator.ValidateForBinding(
                runtime,
                binding,
                new(OperationId, binding.ToTargetIdentity(), SourceFingerprint, Now)
            );

        configurationResult.Succeeded.Should().BeFalse();
        runtimeResult.Succeeded.Should().BeFalse();
        configurationResult
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain(CdcDiagnosticCategory.InvalidObservation)
            .And.Contain(CdcDiagnosticCategory.ArtifactNameMismatch);
        runtimeResult
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain(CdcDiagnosticCategory.InvalidObservation)
            .And.Contain(CdcDiagnosticCategory.InvalidTimestamp)
            .And.Contain(CdcDiagnosticCategory.InvalidOrdering)
            .And.Contain(CdcDiagnosticCategory.ArtifactNameMismatch);
    }

    [TestFixture("aclState")]
    [TestFixture("topicState")]
    public class Given_an_unsupported_offset_store_item_state(string fieldName)
    {
        private CdcContractValidationResult _result = null!;

        [SetUp]
        public void Setup()
        {
            CdcBinding binding = CreateBinding(CdcProvider.Postgresql);
            CdcConnectOffsetStorePolicyObservation observation = CdcTargetStatusFixture.ConnectOffsetStore(
                binding
            ) with
            {
                OperationId = OperationId,
                ObservedAt = ObservedAt,
                AclState =
                    fieldName == "aclState"
                        ? (CdcConnectOffsetStoreItemState)999
                        : CdcConnectOffsetStoreItemState.Satisfied,
                TopicState =
                    fieldName == "topicState"
                        ? (CdcConnectOffsetStoreItemState)999
                        : CdcConnectOffsetStoreItemState.Satisfied,
            };

            _result = CdcConnectOffsetStorePolicyObservationValidator.Validate(
                observation,
                new(OperationId, binding.ToTargetIdentity(), SourceFingerprint, Now)
            );
        }

        [Test]
        public void It_rejects_the_unsupported_state() => _result.Succeeded.Should().BeFalse();

        [Test]
        public void It_identifies_the_unsupported_field() =>
            _result
                .Diagnostics.Should()
                .BeEquivalentTo(
                    new[]
                    {
                        new CdcDiagnostic(
                            CdcDiagnosticCategory.InvalidEnumValue,
                            $"$.{fieldName}",
                            $"CDC Connect offset-store observation {fieldName} is unsupported."
                        ),
                    }
                );
    }

    [TestCase(CdcConnectOffsetStoreItemState.Satisfied, true)]
    [TestCase(CdcConnectOffsetStoreItemState.Invalid, false)]
    [TestCase(CdcConnectOffsetStoreItemState.Unknown, false)]
    public void It_preserves_offset_store_policy_consistency_for_supported_topic_states(
        CdcConnectOffsetStoreItemState topicState,
        bool expectedSuccess
    )
    {
        CdcBinding binding = CreateBinding(CdcProvider.Postgresql);
        CdcConnectOffsetStorePolicyObservation observation = CdcTargetStatusFixture.ConnectOffsetStore(
            binding
        ) with
        {
            OperationId = OperationId,
            ObservedAt = ObservedAt,
            TopicState = topicState,
        };

        CdcContractValidationResult result = CdcConnectOffsetStorePolicyObservationValidator.Validate(
            observation,
            new(OperationId, binding.ToTargetIdentity(), SourceFingerprint, Now)
        );

        result
            .Diagnostics.Should()
            .BeEquivalentTo(
                expectedSuccess
                    ? Array.Empty<CdcDiagnostic>()
                    : new[]
                    {
                        new CdcDiagnostic(
                            CdcDiagnosticCategory.InvalidObservation,
                            "$.policyState",
                            "CDC Connect offset-store satisfied state requires compact cleanup, positive durability values, and satisfied ACLs."
                        ),
                    }
            );
    }

    private static CdcBinding CreateBinding(CdcProvider provider)
    {
        CdcArtifactInventory inventory = CdcArtifactNameGenerator
            .Render(new("dms-local", "edfi.dms", "data-store-1", 1, provider))
            .Inventory!;

        return new(
            1,
            "dms-local",
            "default",
            "1",
            "data-store-1",
            1,
            provider,
            SourceFingerprint,
            inventory.ConnectorName,
            inventory.TopicName,
            1,
            "kafka-murmur2-v1",
            CdcJsonContract.CurrentContractVersion
        );
    }
}
