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
[Category("CdcSourceHistoryObservation")]
public class Given_CdcSourceHistoryObservation
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 17, 13, 10, 11, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = ObservedAt.AddMinutes(1);

    private const string OperationId = "operation-1";
    private const string SourceFingerprint =
        "sha256:8caa6b0ad6db6f60d8d7ce6e78d1e76094e2241678c6f241670319ab60810851";

    [Test]
    public void It_accepts_healthy_postgresql_continuity_with_retained_range_and_exact_artifacts()
    {
        CdcBinding binding = CreateBinding(CdcProvider.Postgresql);
        CdcArtifactInventory inventory = CdcArtifactNameGenerator.RecoverFromBinding(binding).Inventory!;
        CdcSourceHistoryObservation observation = new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservedAt,
            binding.ToTargetIdentity(),
            CdcProvider.Postgresql,
            SourceFingerprint,
            CdcSourceHistoryContinuity.Healthy,
            false,
            CdcProviderArtifactContinuityState.ExactMatch,
            CdcProviderRetainedRangeState.CoversCommittedOffset,
            new(
                inventory.ConnectorName,
                inventory.TopicName,
                inventory.ProgressTopicName,
                null,
                inventory.PostgresqlLogicalSlotName,
                CdcSourcePartitionHashCalculator.ComputePostgresql(inventory.TopicPrefix).Hash,
                "0/16B6C51",
                null,
                null,
                null,
                "0/16B6C50",
                "0/16B6C52",
                []
            ),
            null,
            null,
            CdcSqlServerSchemaHistoryState.NotApplicable,
            []
        );

        string json = CdcJsonContract.Serialize(observation);
        JsonObject root = JsonNode.Parse(json)!.AsObject();

        root["continuity"]!.GetValue<string>().Should().Be("healthy");
        root["providerArtifactState"]!.GetValue<string>().Should().Be("exactMatch");
        root["retainedRangeState"]!.GetValue<string>().Should().Be("coversCommittedOffset");
        root["schemaHistoryState"]!.GetValue<string>().Should().Be("notApplicable");
        root["positionEvidence"]!["providerArtifactName"]!
            .GetValue<string>()
            .Should()
            .Be(inventory.PostgresqlLogicalSlotName);

        CdcContractReadResult<CdcSourceHistoryObservation> readResult =
            CdcJsonContract.Deserialize<CdcSourceHistoryObservation>(json);
        CdcContractValidationResult validationResult =
            CdcSourceHistoryObservationValidator.ValidateForBinding(
                readResult.Contract!,
                binding,
                new(OperationId, binding.ToTargetIdentity(), SourceFingerprint, Now)
            );

        readResult.Succeeded.Should().BeTrue();
        validationResult.Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_accepts_sql_server_schema_history_loss_after_initial_admission_as_lost_continuity()
    {
        CdcBinding binding = CreateBinding(CdcProvider.SqlServer);
        CdcArtifactInventory inventory = CdcArtifactNameGenerator.RecoverFromBinding(binding).Inventory!;
        CdcSourceHistoryObservation observation = new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservedAt,
            binding.ToTargetIdentity(),
            CdcProvider.SqlServer,
            SourceFingerprint,
            CdcSourceHistoryContinuity.Lost,
            false,
            CdcProviderArtifactContinuityState.ExactMatch,
            CdcProviderRetainedRangeState.CoversCommittedOffset,
            new(
                inventory.ConnectorName,
                inventory.TopicName,
                inventory.ProgressTopicName,
                inventory.SchemaHistoryTopicName,
                inventory.SqlServerCaptureInstanceCdcHeartbeatName,
                "sha256:678792175a93a7e810f3904d8d8e42e654289b147c3313a5c6d6a5c6593beab2",
                null,
                "00000023:00000138:0002",
                "00000023:00000139:0001",
                2,
                "00000023:00000138:0002",
                "00000023:00000140:0000",
                []
            ),
            CdcIncidentFailureCategory.SchemaHistoryMissing,
            CdcSqlServerSchemaHistoryEnablementPhase.AfterInitialAdmission,
            CdcSqlServerSchemaHistoryState.Missing,
            []
        )
        {
            SqlServerJobs = CdcSqlServerCdcJobEvidence.Healthy,
        };

        string json = CdcJsonContract.Serialize(observation);

        json.Should().Contain("schemaHistoryMissing");
        json.Should().Contain("afterInitialAdmission");
        json.Should().NotContain("database");
        json.Should().NotContain("EdFi_DMS_CDC");

        CdcContractValidationResult validationResult =
            CdcSourceHistoryObservationValidator.ValidateForBinding(
                observation,
                binding,
                new(OperationId, binding.ToTargetIdentity(), SourceFingerprint, Now)
            );

        validationResult.Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_rejects_healthy_sql_server_continuity_without_healthy_jobs()
    {
        CdcBinding binding = CreateBinding(CdcProvider.SqlServer);
        CdcSourceHistoryObservation observation = SqlServerSchemaHistoryLossObservation(binding) with
        {
            Continuity = CdcSourceHistoryContinuity.Healthy,
            IncidentFailureCategory = null,
            SchemaHistoryState = CdcSqlServerSchemaHistoryState.Valid,
            SqlServerJobs = new(CdcSqlServerCdcJobState.Failed, CdcSqlServerCdcJobState.Healthy),
        };

        CdcContractValidationResult result = CdcSourceHistoryObservationValidator.ValidateForBinding(
            observation,
            binding,
            new(OperationId, binding.ToTargetIdentity(), SourceFingerprint, Now)
        );

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Category == CdcDiagnosticCategory.InvalidObservation
                && diagnostic.Path == "$.sqlServerJobs"
            );
    }

    [Test]
    public void It_rejects_negative_sql_server_event_serial_position_evidence()
    {
        CdcBinding binding = CreateBinding(CdcProvider.SqlServer);
        CdcSourceHistoryObservation baselineObservation = SqlServerSchemaHistoryLossObservation(binding);
        CdcSourceHistoryObservation observation = baselineObservation with
        {
            PositionEvidence = baselineObservation.PositionEvidence! with { EventSerialNo = -1 },
        };

        CdcContractValidationResult result = CdcSourceHistoryObservationValidator.ValidateForBinding(
            observation,
            binding,
            new(OperationId, binding.ToTargetIdentity(), SourceFingerprint, Now)
        );

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Category == CdcDiagnosticCategory.MalformedPayload
                && diagnostic.Path == "$.positionEvidence.eventSerialNo"
            );
        result
            .Diagnostics.Select(diagnostic => diagnostic.Message)
            .Should()
            .NotContain(message => message.Contains("-1"));
    }

    [Test]
    public void It_rejects_inconsistent_continuity_provider_artifacts_retained_ranges_and_schema_history()
    {
        CdcBinding binding = CreateBinding(CdcProvider.Postgresql);
        CdcArtifactInventory inventory = CdcArtifactNameGenerator.RecoverFromBinding(binding).Inventory!;
        CdcSourceHistoryObservation observation = new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservedAt,
            binding.ToTargetIdentity(),
            CdcProvider.Postgresql,
            SourceFingerprint,
            CdcSourceHistoryContinuity.Healthy,
            true,
            CdcProviderArtifactContinuityState.Missing,
            CdcProviderRetainedRangeState.Gap,
            new(
                $"{inventory.ConnectorName}-wrong",
                inventory.TopicName,
                inventory.ProgressTopicName,
                $"{inventory.TopicName}.schema-history",
                "not-the-slot",
                "sha256:9caa6b0ad6db6f60d8d7ce6e78d1e76094e2241678c6f241670319ab60810851",
                "0/16B6C51",
                "00000023:00000138:0002",
                null,
                2,
                "0/16B6C52",
                "0/16B6C50",
                [CdcIncidentUnavailableFact.ConnectOffset, CdcIncidentUnavailableFact.ConnectOffset]
            ),
            CdcIncidentFailureCategory.ProviderArtifactMissing,
            CdcSqlServerSchemaHistoryEnablementPhase.AfterInitialAdmission,
            CdcSqlServerSchemaHistoryState.Valid,
            []
        )
        {
            SqlServerJobs = CdcSqlServerCdcJobEvidence.Healthy,
        };

        CdcContractValidationResult result = CdcSourceHistoryObservationValidator.ValidateForBinding(
            observation,
            binding,
            new(OperationId, binding.ToTargetIdentity(), SourceFingerprint, Now)
        );

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain(CdcDiagnosticCategory.InvalidObservation)
            .And.Contain(CdcDiagnosticCategory.InvalidOrdering)
            .And.Contain(CdcDiagnosticCategory.ArtifactNameMismatch);
    }

    [Test]
    public void It_rejects_sql_server_terminal_schema_history_loss_before_initial_admission()
    {
        CdcBinding binding = CreateBinding(CdcProvider.SqlServer);
        CdcSourceHistoryObservation observation = SqlServerSchemaHistoryLossObservation(binding) with
        {
            SchemaHistoryEnablementPhase = CdcSqlServerSchemaHistoryEnablementPhase.BeforeInitialAdmission,
        };

        CdcContractValidationResult result = CdcSourceHistoryObservationValidator.ValidateForBinding(
            observation,
            binding,
            new(OperationId, binding.ToTargetIdentity(), SourceFingerprint, Now)
        );

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain(CdcDiagnosticCategory.InvalidObservation);
    }

    private static CdcSourceHistoryObservation SqlServerSchemaHistoryLossObservation(CdcBinding binding)
    {
        CdcArtifactInventory inventory = CdcArtifactNameGenerator.RecoverFromBinding(binding).Inventory!;

        return new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservedAt,
            binding.ToTargetIdentity(),
            CdcProvider.SqlServer,
            SourceFingerprint,
            CdcSourceHistoryContinuity.Lost,
            false,
            CdcProviderArtifactContinuityState.ExactMatch,
            CdcProviderRetainedRangeState.CoversCommittedOffset,
            new(
                inventory.ConnectorName,
                inventory.TopicName,
                inventory.ProgressTopicName,
                inventory.SchemaHistoryTopicName,
                inventory.SqlServerCaptureInstanceCdcHeartbeatName,
                "sha256:678792175a93a7e810f3904d8d8e42e654289b147c3313a5c6d6a5c6593beab2",
                null,
                "00000023:00000138:0002",
                "00000023:00000139:0001",
                2,
                "00000023:00000138:0002",
                "00000023:00000140:0000",
                []
            ),
            CdcIncidentFailureCategory.SchemaHistoryMissing,
            CdcSqlServerSchemaHistoryEnablementPhase.AfterInitialAdmission,
            CdcSqlServerSchemaHistoryState.Missing,
            []
        )
        {
            SqlServerJobs = CdcSqlServerCdcJobEvidence.Healthy,
        };
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
