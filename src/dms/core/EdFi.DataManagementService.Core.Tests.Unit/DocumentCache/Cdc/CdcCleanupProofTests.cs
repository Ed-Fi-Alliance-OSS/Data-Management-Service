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
[Category("CdcCleanupProof")]
public class Given_CdcCleanupProof
{
    private static readonly DateTimeOffset SampleObservedAt = new(2026, 8, 17, 13, 10, 11, TimeSpan.Zero);
    private static readonly DateTimeOffset SampleNow = SampleObservedAt.AddMinutes(1);

    [Test]
    public void It_accepts_complete_provider_applicable_governed_artifact_inventory()
    {
        CdcBinding postgresqlBinding = CreateBinding(CdcProvider.Postgresql);
        CdcBinding sqlServerBinding = CreateBinding(CdcProvider.SqlServer);

        CdcContractValidationResult postgresqlResult = CdcCleanupProofValidator.Validate(
            CreateCompleteProof(postgresqlBinding),
            postgresqlBinding,
            SampleNow
        );
        CdcContractValidationResult sqlServerResult = CdcCleanupProofValidator.Validate(
            CreateCompleteProof(sqlServerBinding),
            sqlServerBinding,
            SampleNow
        );

        postgresqlResult.Succeeded.Should().BeTrue();
        sqlServerResult.Succeeded.Should().BeTrue();
        CdcArtifactNameGenerator
            .RecoverFromBinding(postgresqlBinding)
            .Inventory!.GovernedArtifacts.Should()
            .HaveCount(8);
        CdcArtifactNameGenerator
            .RecoverFromBinding(sqlServerBinding)
            .Inventory!.GovernedArtifacts.Should()
            .HaveCount(12);
    }

    [Test]
    public void It_rejects_incomplete_duplicate_unexpected_mismatched_and_unremoved_artifacts()
    {
        CdcBinding binding = CreateBinding(CdcProvider.Postgresql);
        CdcArtifactInventory inventory = CdcArtifactNameGenerator.RecoverFromBinding(binding).Inventory!;
        CdcCleanupProof proof = new(
            CdcJsonContract.CurrentContractVersion,
            "operation-1",
            SampleObservedAt,
            binding.ToCompleteBindingIdentity(),
            CdcCleanupMode.RetireBindingGeneration,
            [
                new(
                    CdcGovernedArtifactKind.KafkaConnectConnector,
                    $"{inventory.ConnectorName}-wrong",
                    CdcCleanupState.Deleted,
                    "deleted"
                ),
                new(
                    CdcGovernedArtifactKind.ConnectSourceOffsets,
                    inventory.ConnectorName,
                    CdcCleanupState.Deleted,
                    "deleted"
                ),
                new(
                    CdcGovernedArtifactKind.ConnectSourceOffsets,
                    inventory.ConnectorName,
                    CdcCleanupState.Deleted,
                    "deleted twice"
                ),
                new(
                    CdcGovernedArtifactKind.SchemaHistoryTopic,
                    $"{inventory.TopicName}.schema-history",
                    CdcCleanupState.NotFound,
                    "not found"
                ),
                new(
                    CdcGovernedArtifactKind.PublicTopic,
                    inventory.TopicName,
                    (CdcCleanupState)999,
                    "retained"
                ),
            ]
        );

        CdcContractValidationResult result = CdcCleanupProofValidator.Validate(proof, binding, SampleNow);

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain(CdcDiagnosticCategory.InventoryIncomplete)
            .And.Contain(CdcDiagnosticCategory.DuplicateArtifact)
            .And.Contain(CdcDiagnosticCategory.UnexpectedArtifact)
            .And.Contain(CdcDiagnosticCategory.ArtifactNameMismatch)
            .And.Contain(CdcDiagnosticCategory.ArtifactNotRemoved);
    }

    [Test]
    public void It_rejects_wrong_operation_timestamp_and_binding_identity_mismatch()
    {
        CdcBinding binding = CreateBinding(CdcProvider.SqlServer);
        CdcCleanupProof proof = CreateCompleteProof(binding) with
        {
            OperationId = "bad/operation",
            VerifiedAt = SampleNow.AddSeconds(1),
            BindingIdentity = binding.ToCompleteBindingIdentity() with
            {
                TopicName = "edfi.dms.instance.data-store-1-g1.documents.v1-mismatch",
            },
        };

        CdcContractValidationResult result = CdcCleanupProofValidator.Validate(proof, binding, SampleNow);

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain(CdcDiagnosticCategory.InvalidOperationId)
            .And.Contain(CdcDiagnosticCategory.InvalidTimestamp)
            .And.Contain(CdcDiagnosticCategory.BindingIdentityMismatch)
            .And.Contain(CdcDiagnosticCategory.ArtifactNameMismatch);
    }

    private static CdcCleanupProof CreateCompleteProof(CdcBinding binding)
    {
        CdcArtifactInventory inventory = CdcArtifactNameGenerator.RecoverFromBinding(binding).Inventory!;

        return new(
            CdcJsonContract.CurrentContractVersion,
            "operation-1",
            SampleObservedAt,
            binding.ToCompleteBindingIdentity(),
            CdcCleanupMode.RetireBindingGeneration,
            inventory
                .GovernedArtifacts.Select(artifact => new CdcGovernedArtifact(
                    artifact.Kind,
                    artifact.Name,
                    CdcCleanupState.Deleted,
                    "deleted"
                ))
                .ToArray()
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
            "sha256:8caa6b0ad6db6f60d8d7ce6e78d1e76094e2241678c6f241670319ab60810851",
            inventory.ConnectorName,
            inventory.TopicName,
            1,
            "kafka-murmur2-v1",
            CdcJsonContract.CurrentContractVersion
        );
    }
}
