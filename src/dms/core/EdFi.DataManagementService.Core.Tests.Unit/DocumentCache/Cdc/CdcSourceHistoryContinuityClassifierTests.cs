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
[Category("CdcSourceHistory")]
public class Given_CdcSourceHistoryContinuityClassifier
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 17, 13, 10, 11, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = ObservedAt.AddMinutes(1);

    [Test]
    public void It_reports_healthy_postgresql_continuity_when_artifacts_offsets_and_retained_range_match()
    {
        CdcBinding binding = CdcContinuityFixture.CreateBinding(CdcProvider.Postgresql);
        CdcSourceHistoryClassificationResult result = CdcSourceHistoryContinuityClassifier.Evaluate(
            CdcContinuityFixture.CreateInput(binding)
        );

        result.Observation.Continuity.Should().Be(CdcSourceHistoryContinuity.Healthy);
        result.Observation.IncidentLatched.Should().BeFalse();
        result.IncidentCandidate.Should().BeNull();
        result
            .Observation.PositionEvidence!.ProviderArtifactName.Should()
            .Be(PostgresqlInventory(binding).PostgresqlLogicalSlotName);
        result.Observation.PositionEvidence.LsnProc.Should().Be("0/16B6C51");
        result.Observation.PositionEvidence.RetainedRangeStart.Should().Be("0/16B6C50");
        ValidateObservation(result.Observation, binding).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_reports_unknown_without_incident_when_provider_history_is_unavailable()
    {
        CdcBinding binding = CdcContinuityFixture.CreateBinding(CdcProvider.Postgresql);
        CdcSourceHistoryClassificationResult result = CdcSourceHistoryContinuityClassifier.Evaluate(
            CdcContinuityFixture.CreateInput(binding) with
            {
                ProviderHistory = null,
            }
        );

        result.Observation.Continuity.Should().Be(CdcSourceHistoryContinuity.Unknown);
        result.Observation.IncidentFailureCategory.Should().BeNull();
        result.IncidentCandidate.Should().BeNull();
        result
            .Observation.Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain(CdcDiagnosticCategory.LocalStateUnavailable);
        ValidateObservation(result.Observation, binding).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_keeps_sql_server_pre_admission_schema_history_loss_non_latching()
    {
        CdcBinding binding = CdcContinuityFixture.CreateBinding(CdcProvider.SqlServer);
        CdcSourceHistoryClassificationResult result = CdcSourceHistoryContinuityClassifier.Evaluate(
            CdcContinuityFixture.CreateInput(binding) with
            {
                SqlServerSchemaHistory = new(
                    CdcSqlServerSchemaHistoryEnablementPhase.BeforeInitialAdmission,
                    CdcSqlServerSchemaHistoryState.Missing
                ),
            }
        );

        result.Observation.Continuity.Should().Be(CdcSourceHistoryContinuity.Unknown);
        result
            .Observation.SchemaHistoryEnablementPhase.Should()
            .Be(CdcSqlServerSchemaHistoryEnablementPhase.BeforeInitialAdmission);
        result.Observation.SchemaHistoryState.Should().Be(CdcSqlServerSchemaHistoryState.Missing);
        result.IncidentCandidate.Should().BeNull();
        ValidateObservation(result.Observation, binding).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_classifies_negative_sql_server_event_serial_as_malformed_connector_offset()
    {
        CdcBinding binding = CdcContinuityFixture.CreateBinding(CdcProvider.SqlServer);
        CdcSourceHistoryClassificationInput baselineInput = CdcContinuityFixture.CreateInput(binding);
        CdcSourceHistoryClassificationResult result = CdcSourceHistoryContinuityClassifier.Evaluate(
            baselineInput with
            {
                ConnectorOffset = baselineInput.ConnectorOffset! with { EventSerialNo = -1 },
            }
        );

        result.Observation.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
        result
            .Observation.IncidentFailureCategory.Should()
            .Be(CdcIncidentFailureCategory.ConnectOffsetMalformed);
        result.IncidentCandidate.Should().NotBeNull();
        result
            .Observation.Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Category == CdcDiagnosticCategory.MalformedPayload
                && diagnostic.Path == "$.connectorOffset.eventSerialNo"
            );
        result
            .Observation.Diagnostics.Select(diagnostic => diagnostic.Message)
            .Should()
            .NotContain(message => message.Contains("-1"));
        ValidateObservation(result.Observation, binding).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_preserves_a_valid_latched_incident_without_creating_a_new_candidate()
    {
        CdcBinding binding = CdcContinuityFixture.CreateBinding(CdcProvider.Postgresql);
        CdcSourceHistoryClassificationResult lostResult = CdcSourceHistoryContinuityClassifier.Evaluate(
            CdcContinuityFixture.CreateInput(binding) with
            {
                ProviderHistory = CdcContinuityFixture.ProviderHistory(
                    binding,
                    CdcProviderArtifactContinuityState.Missing,
                    CdcProviderRetainedRangeState.CoversCommittedOffset
                ),
            }
        );
        CdcIncident incident = lostResult.IncidentCandidate!.ToIncident();

        CdcSourceHistoryClassificationResult result = CdcSourceHistoryContinuityClassifier.Evaluate(
            CdcContinuityFixture.CreateInput(binding) with
            {
                LatchedIncident = incident,
            }
        );

        result.Observation.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
        result.Observation.IncidentLatched.Should().BeTrue();
        result
            .Observation.IncidentFailureCategory.Should()
            .Be(CdcIncidentFailureCategory.ProviderArtifactMissing);
        result.IncidentCandidate.Should().BeNull();
        ValidateObservation(result.Observation, binding).Succeeded.Should().BeTrue();
    }

    private static CdcArtifactInventory PostgresqlInventory(CdcBinding binding) =>
        CdcArtifactNameGenerator.RecoverFromBinding(binding).Inventory!;

    private static CdcContractValidationResult ValidateObservation(
        CdcSourceHistoryObservation observation,
        CdcBinding binding
    ) =>
        CdcSourceHistoryObservationValidator.ValidateForBinding(
            observation,
            binding,
            new(
                CdcContinuityFixture.OperationId,
                binding.ToTargetIdentity(),
                binding.PhysicalSourceFingerprint,
                Now
            )
        );
}

[TestFixture]
[Parallelizable]
[Category("CdcContinuity")]
public class Given_CdcContinuityIncidentClassifier
{
    private static readonly DateTimeOffset Now = CdcContinuityFixture.ObservedAt.AddMinutes(1);

    private static IEnumerable<TestCaseData> TerminalIncidentCases()
    {
        yield return new TestCaseData(
            CdcIncidentFailureCategory.ProviderArtifactMissing,
            CdcProvider.Postgresql,
            new Func<CdcSourceHistoryClassificationInput, CdcSourceHistoryClassificationInput>(input =>
                input with
                {
                    ProviderHistory = CdcContinuityFixture.ProviderHistory(
                        input.Binding,
                        CdcProviderArtifactContinuityState.Missing,
                        CdcProviderRetainedRangeState.CoversCommittedOffset
                    ),
                }
            )
        ).SetName("provider_artifact_missing");
        yield return new TestCaseData(
            CdcIncidentFailureCategory.ProviderArtifactRecreated,
            CdcProvider.Postgresql,
            new Func<CdcSourceHistoryClassificationInput, CdcSourceHistoryClassificationInput>(input =>
                input with
                {
                    ProviderHistory = CdcContinuityFixture.ProviderHistory(
                        input.Binding,
                        CdcProviderArtifactContinuityState.Recreated,
                        CdcProviderRetainedRangeState.CoversCommittedOffset
                    ),
                }
            )
        ).SetName("provider_artifact_recreated");
        yield return new TestCaseData(
            CdcIncidentFailureCategory.RetainedHistoryGap,
            CdcProvider.Postgresql,
            new Func<CdcSourceHistoryClassificationInput, CdcSourceHistoryClassificationInput>(input =>
                input with
                {
                    ProviderHistory = CdcContinuityFixture.ProviderHistory(
                        input.Binding,
                        CdcProviderArtifactContinuityState.ExactMatch,
                        CdcProviderRetainedRangeState.Gap
                    ),
                }
            )
        ).SetName("retained_history_gap");
        yield return new TestCaseData(
            CdcIncidentFailureCategory.ConnectOffsetMissing,
            CdcProvider.Postgresql,
            new Func<CdcSourceHistoryClassificationInput, CdcSourceHistoryClassificationInput>(input =>
                input with
                {
                    ConnectorOffset = CdcContinuityFixture.ConnectorOffset(
                        input.Binding,
                        CdcConnectorOffsetMatchResult.Missing
                    ),
                }
            )
        ).SetName("connect_offset_missing");
        yield return new TestCaseData(
            CdcIncidentFailureCategory.ConnectOffsetMalformed,
            CdcProvider.Postgresql,
            new Func<CdcSourceHistoryClassificationInput, CdcSourceHistoryClassificationInput>(input =>
                input with
                {
                    ConnectorOffset = CdcContinuityFixture.ConnectorOffset(
                        input.Binding,
                        CdcConnectorOffsetMatchResult.Exact,
                        isSnapshot: true
                    ),
                }
            )
        ).SetName("connect_offset_malformed");
        yield return new TestCaseData(
            CdcIncidentFailureCategory.ConnectSourcePartitionMismatch,
            CdcProvider.Postgresql,
            new Func<CdcSourceHistoryClassificationInput, CdcSourceHistoryClassificationInput>(input =>
                input with
                {
                    ConnectorOffset = CdcContinuityFixture.ConnectorOffset(
                        input.Binding,
                        CdcConnectorOffsetMatchResult.SourcePartitionMismatch
                    ),
                }
            )
        ).SetName("connect_source_partition_mismatch");
        yield return SchemaHistoryCase(
            CdcIncidentFailureCategory.SchemaHistoryMissing,
            CdcSqlServerSchemaHistoryState.Missing,
            "schema_history_missing"
        );
        yield return SchemaHistoryCase(
            CdcIncidentFailureCategory.SchemaHistoryEmptyWithRetainedOffset,
            CdcSqlServerSchemaHistoryState.EmptyWithRetainedOffset,
            "schema_history_empty"
        );
        yield return SchemaHistoryCase(
            CdcIncidentFailureCategory.SchemaHistoryRequiredRecordLost,
            CdcSqlServerSchemaHistoryState.RequiredRecordLost,
            "schema_history_required_record_lost"
        );
    }

    [TestCaseSource(nameof(TerminalIncidentCases))]
    public void It_maps_terminal_evidence_to_valid_incident_candidates(
        CdcIncidentFailureCategory expectedFailureCategory,
        CdcProvider provider,
        Func<CdcSourceHistoryClassificationInput, CdcSourceHistoryClassificationInput> customize
    )
    {
        CdcBinding binding = CdcContinuityFixture.CreateBinding(provider);
        CdcSourceHistoryClassificationInput input = customize(CdcContinuityFixture.CreateInput(binding));

        CdcSourceHistoryClassificationResult result = CdcSourceHistoryContinuityClassifier.Evaluate(input);

        result.Observation.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
        result.Observation.IncidentFailureCategory.Should().Be(expectedFailureCategory);
        result.IncidentCandidate.Should().NotBeNull();
        result.IncidentCandidate!.FailureCategory.Should().Be(expectedFailureCategory);
        CdcIncidentValidator
            .ValidateForBinding(result.IncidentCandidate.ToIncident(), binding, Now)
            .Succeeded.Should()
            .BeTrue();
        CdcSourceHistoryObservationValidator
            .ValidateForBinding(
                result.Observation,
                binding,
                new(
                    CdcContinuityFixture.OperationId,
                    binding.ToTargetIdentity(),
                    binding.PhysicalSourceFingerprint,
                    Now
                )
            )
            .Succeeded.Should()
            .BeTrue();
        CdcJsonContract.Serialize(result.Observation).Should().NotContain("EdFi_DMS_CDC");
    }

    private static TestCaseData SchemaHistoryCase(
        CdcIncidentFailureCategory expectedFailureCategory,
        CdcSqlServerSchemaHistoryState schemaHistoryState,
        string name
    )
    {
        TestCaseData data = new(
            expectedFailureCategory,
            CdcProvider.SqlServer,
            new Func<CdcSourceHistoryClassificationInput, CdcSourceHistoryClassificationInput>(input =>
                input with
                {
                    SqlServerSchemaHistory = new(
                        CdcSqlServerSchemaHistoryEnablementPhase.AfterInitialAdmission,
                        schemaHistoryState
                    ),
                }
            )
        );
        return data.SetName(name);
    }
}

internal static class CdcContinuityFixture
{
    public static readonly DateTimeOffset ObservedAt = new(2026, 8, 17, 13, 10, 11, TimeSpan.Zero);
    public const string OperationId = "operation-1";
    public const string SourceFingerprint =
        "sha256:8caa6b0ad6db6f60d8d7ce6e78d1e76094e2241678c6f241670319ab60810851";
    private const string SqlServerSourcePartitionHash =
        "sha256:678792175a93a7e810f3904d8d8e42e654289b147c3313a5c6d6a5c6593beab2";

    public static CdcSourceHistoryClassificationInput CreateInput(CdcBinding binding) =>
        new(OperationId, ObservedAt, ObservedAt.AddMinutes(1), binding)
        {
            ProviderSetup = ProviderSetup(binding),
            ConnectorOffset = ConnectorOffset(binding),
            ProviderHistory = ProviderHistory(
                binding,
                CdcProviderArtifactContinuityState.ExactMatch,
                CdcProviderRetainedRangeState.CoversCommittedOffset
            ),
            SqlServerSchemaHistory =
                binding.Provider == CdcProvider.SqlServer
                    ? new(
                        CdcSqlServerSchemaHistoryEnablementPhase.AfterInitialAdmission,
                        CdcSqlServerSchemaHistoryState.Valid
                    )
                    : null,
            ExpectedConnectSourcePartitionHash =
                binding.Provider == CdcProvider.SqlServer ? SqlServerSourcePartitionHash : null,
        };

    public static CdcBinding CreateBinding(CdcProvider provider)
    {
        CdcArtifactInventory inventory = Inventory(provider);

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

    public static CdcProviderSetupObservation ProviderSetup(CdcBinding binding) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservedAt,
            binding.ToTargetIdentity(),
            binding.Provider,
            SourceFingerprint,
            CdcProviderSetupMode.ValidateOnly,
            CdcProviderSetupOutcome.Satisfied,
            CdcProviderSetupState.Matched,
            CdcProviderSetupState.Matched,
            CdcProviderSetupState.Matched,
            CdcProviderSetupState.Matched,
            CdcProviderSetupState.Matched,
            []
        );

    public static CdcConnectorOffsetObservation ConnectorOffset(
        CdcBinding binding,
        CdcConnectorOffsetMatchResult matchResult = CdcConnectorOffsetMatchResult.Exact,
        bool isSnapshot = false
    )
    {
        CdcArtifactInventory inventory = CdcArtifactNameGenerator.RecoverFromBinding(binding).Inventory!;
        string sourcePartitionHash =
            binding.Provider == CdcProvider.Postgresql
                ? CdcSourcePartitionHashCalculator.ComputePostgresql(inventory.TopicPrefix).Hash!
                : SqlServerSourcePartitionHash;

        return new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservedAt,
            binding.ToTargetIdentity(),
            binding.Provider,
            SourceFingerprint,
            inventory.ConnectorName,
            inventory.TopicPrefix,
            matchResult,
            sourcePartitionHash,
            isSnapshot,
            false,
            binding.Provider == CdcProvider.Postgresql ? 0x16B6C51 : null,
            binding.Provider == CdcProvider.SqlServer ? "00000023:00000138:0002" : null,
            binding.Provider == CdcProvider.SqlServer ? "00000023:00000139:0001" : null,
            binding.Provider == CdcProvider.SqlServer ? 2 : null,
            []
        );
    }

    public static CdcProviderSourceHistoryEvidence ProviderHistory(
        CdcBinding binding,
        CdcProviderArtifactContinuityState artifactState,
        CdcProviderRetainedRangeState retainedRangeState
    )
    {
        CdcArtifactInventory inventory = CdcArtifactNameGenerator.RecoverFromBinding(binding).Inventory!;

        return new(
            artifactState,
            retainedRangeState,
            binding.Provider == CdcProvider.Postgresql
                ? inventory.PostgresqlLogicalSlotName
                : inventory.SqlServerCaptureInstanceCdcHeartbeatName,
            binding.Provider == CdcProvider.Postgresql ? "0/16B6C50" : "00000023:00000138:0000",
            binding.Provider == CdcProvider.Postgresql ? "0/16B6C52" : "00000023:00000140:0000",
            []
        );
    }

    private static CdcArtifactInventory Inventory(CdcProvider provider) =>
        CdcArtifactNameGenerator.Render(new("dms-local", "edfi.dms", "data-store-1", 1, provider)).Inventory!;
}
