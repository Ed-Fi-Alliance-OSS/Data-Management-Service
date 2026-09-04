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
    private const string MismatchedSourcePartitionHash =
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

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

    /// <summary>
    /// A binding is persisted before the artifacts it governs exist and long before the connector
    /// commits anything, so a durable record is not evidence that the enablement it belongs to
    /// finished. Its public topic is: a stream that has published nothing has no committed position to
    /// have lost and no consumer holding state from it, so an absent connector offset there is an
    /// enablement still in progress rather than a terminal loss. Latching one would close the
    /// documented retry, which refuses an incident-latched binding.
    /// </summary>
    [Test]
    public void It_withholds_a_terminal_offset_loss_from_a_stream_that_has_published_nothing()
    {
        CdcBinding binding = CdcContinuityFixture.CreateBinding(CdcProvider.Postgresql);

        CdcSourceHistoryClassificationResult result = CdcSourceHistoryContinuityClassifier.Evaluate(
            CdcContinuityFixture.CreateInput(binding) with
            {
                ConnectorOffset = CdcContinuityFixture.ConnectorOffset(
                    binding,
                    CdcConnectorOffsetMatchResult.Missing
                ),
                PublicTopicPublication = new(true, false),
            }
        );

        result.Observation.Continuity.Should().Be(CdcSourceHistoryContinuity.Unknown);
        result.Observation.IncidentFailureCategory.Should().BeNull();
        result.IncidentCandidate.Should().BeNull();
        ValidateObservation(result.Observation, binding).Succeeded.Should().BeTrue();
    }

    /// <summary>
    /// Evidence that could not be read is not proof that the stream is established, and the
    /// irreversible action needs proof. Unknown keeps readiness false and automates no start or
    /// resume, so nothing is admitted on the strength of an unread topic.
    /// </summary>
    [Test]
    public void It_withholds_a_terminal_offset_loss_when_the_publication_evidence_is_unreadable()
    {
        CdcBinding binding = CdcContinuityFixture.CreateBinding(CdcProvider.Postgresql);

        CdcSourceHistoryClassificationResult result = CdcSourceHistoryContinuityClassifier.Evaluate(
            CdcContinuityFixture.CreateInput(binding) with
            {
                ConnectorOffset = CdcContinuityFixture.ConnectorOffset(
                    binding,
                    CdcConnectorOffsetMatchResult.Missing
                ),
                PublicTopicPublication = CdcPublicTopicPublicationEvidence.Unreadable,
            }
        );

        result.Observation.Continuity.Should().Be(CdcSourceHistoryContinuity.Unknown);
        result.IncidentCandidate.Should().BeNull();
    }

    /// <summary>
    /// An offsets query the worker never answered proves nothing about the connector's committed
    /// position, so it is unknown however established the stream is. Only a successful query that
    /// reports no offset can be a proved loss.
    /// </summary>
    [Test]
    public void It_reports_unknown_for_an_offsets_query_the_worker_did_not_answer()
    {
        CdcBinding binding = CdcContinuityFixture.CreateBinding(CdcProvider.Postgresql);

        CdcSourceHistoryClassificationResult result = CdcSourceHistoryContinuityClassifier.Evaluate(
            CdcContinuityFixture.CreateInput(binding) with
            {
                ConnectorOffset = CdcContinuityFixture.ConnectorOffset(
                    binding,
                    CdcConnectorOffsetMatchResult.Unavailable
                ),
            }
        );

        result.Observation.Continuity.Should().Be(CdcSourceHistoryContinuity.Unknown);
        result.Observation.IncidentFailureCategory.Should().BeNull();
        result.IncidentCandidate.Should().BeNull();
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
    public void It_reports_unknown_without_incident_when_connector_offset_is_unavailable()
    {
        CdcBinding binding = CdcContinuityFixture.CreateBinding(CdcProvider.Postgresql);
        CdcSourceHistoryClassificationResult result = CdcSourceHistoryContinuityClassifier.Evaluate(
            CdcContinuityFixture.CreateInput(binding) with
            {
                ConnectorOffset = null,
            }
        );

        result.Observation.Continuity.Should().Be(CdcSourceHistoryContinuity.Unknown);
        result.Observation.IncidentFailureCategory.Should().BeNull();
        result.IncidentCandidate.Should().BeNull();
        result.Observation.ProviderArtifactState.Should().Be(CdcProviderArtifactContinuityState.ExactMatch);
        result
            .Observation.RetainedRangeState.Should()
            .Be(CdcProviderRetainedRangeState.CoversCommittedOffset);
        result
            .Observation.PositionEvidence!.UnavailableFacts.Should()
            .Contain(CdcIncidentUnavailableFact.ConnectOffset);
        result
            .Observation.Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Category == CdcDiagnosticCategory.LocalStateUnavailable
                && diagnostic.Path == "$.connectorOffset"
            );
        ValidateObservation(result.Observation, binding).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_requires_healthy_sql_server_jobs_for_healthy_continuity()
    {
        CdcBinding binding = CdcContinuityFixture.CreateBinding(CdcProvider.SqlServer);
        CdcSourceHistoryClassificationResult result = CdcSourceHistoryContinuityClassifier.Evaluate(
            CdcContinuityFixture.CreateInput(binding)
        );

        result.Observation.Continuity.Should().Be(CdcSourceHistoryContinuity.Healthy);
        result.Observation.SqlServerJobs.Should().Be(CdcSqlServerCdcJobEvidence.Healthy);
        result.IncidentCandidate.Should().BeNull();
        ValidateObservation(result.Observation, binding).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_reports_unknown_when_sql_server_expected_source_partition_hash_is_unavailable()
    {
        CdcBinding binding = CdcContinuityFixture.CreateBinding(CdcProvider.SqlServer);

        CdcSourceHistoryClassificationResult result = CdcSourceHistoryContinuityClassifier.Evaluate(
            CdcContinuityFixture.CreateInput(binding) with
            {
                ExpectedConnectSourcePartitionHash = null,
            }
        );

        result.Observation.Continuity.Should().Be(CdcSourceHistoryContinuity.Unknown);
        result.Observation.IncidentFailureCategory.Should().BeNull();
        result.IncidentCandidate.Should().BeNull();
        result
            .Observation.Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Category == CdcDiagnosticCategory.LocalStateUnavailable
                && diagnostic.Path == "$.expectedConnectSourcePartitionHash"
            );
        ValidateObservation(result.Observation, binding).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_latches_terminal_provider_artifact_loss_when_sql_server_capture_job_is_missing()
    {
        CdcBinding binding = CdcContinuityFixture.CreateBinding(CdcProvider.SqlServer);
        CdcSourceHistoryClassificationInput input = CdcContinuityFixture.CreateInput(binding);

        CdcSourceHistoryClassificationResult result = CdcSourceHistoryContinuityClassifier.Evaluate(
            input with
            {
                ProviderHistory = input.ProviderHistory! with
                {
                    SqlServerJobs = new(CdcSqlServerCdcJobState.Missing, CdcSqlServerCdcJobState.Healthy),
                },
            }
        );

        result.Observation.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
        result.Observation.ProviderArtifactState.Should().Be(CdcProviderArtifactContinuityState.Missing);
        result
            .Observation.IncidentFailureCategory.Should()
            .Be(CdcIncidentFailureCategory.ProviderArtifactMissing);
        result.Observation.SqlServerJobs!.CaptureJobState.Should().Be(CdcSqlServerCdcJobState.Missing);
        result.IncidentCandidate.Should().NotBeNull();
        ValidateObservation(result.Observation, binding).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_reports_unknown_without_incident_when_sql_server_capture_job_is_stopped()
    {
        CdcBinding binding = CdcContinuityFixture.CreateBinding(CdcProvider.SqlServer);
        CdcSourceHistoryClassificationInput input = CdcContinuityFixture.CreateInput(binding);

        CdcSourceHistoryClassificationResult result = CdcSourceHistoryContinuityClassifier.Evaluate(
            input with
            {
                ProviderHistory = input.ProviderHistory! with
                {
                    SqlServerJobs = new(CdcSqlServerCdcJobState.Stopped, CdcSqlServerCdcJobState.Healthy),
                },
            }
        );

        result.Observation.Continuity.Should().Be(CdcSourceHistoryContinuity.Unknown);
        result.Observation.ProviderArtifactState.Should().Be(CdcProviderArtifactContinuityState.ExactMatch);
        result
            .Observation.RetainedRangeState.Should()
            .Be(CdcProviderRetainedRangeState.CoversCommittedOffset);
        result.Observation.SqlServerJobs!.CaptureJobState.Should().Be(CdcSqlServerCdcJobState.Stopped);
        result
            .Observation.Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Category == CdcDiagnosticCategory.InvalidObservation
                && diagnostic.Path == "$.providerHistory.sqlServerJobs"
            );
        result.IncidentCandidate.Should().BeNull();
        ValidateObservation(result.Observation, binding).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_reports_unknown_without_incident_when_sql_server_job_health_is_unavailable()
    {
        CdcBinding binding = CdcContinuityFixture.CreateBinding(CdcProvider.SqlServer);
        CdcSourceHistoryClassificationInput input = CdcContinuityFixture.CreateInput(binding);

        CdcSourceHistoryClassificationResult result = CdcSourceHistoryContinuityClassifier.Evaluate(
            input with
            {
                ProviderHistory = input.ProviderHistory! with
                {
                    SqlServerJobs = CdcSqlServerCdcJobEvidence.Unknown,
                    Diagnostics =
                    [
                        new(
                            CdcDiagnosticCategory.LocalStateUnavailable,
                            "$.providerHistory.sqlServerJobs",
                            "CDC SQL Server capture and cleanup job health observation failed."
                        ),
                    ],
                },
            }
        );

        result.Observation.Continuity.Should().Be(CdcSourceHistoryContinuity.Unknown);
        result.Observation.SqlServerJobs.Should().Be(CdcSqlServerCdcJobEvidence.Unknown);
        result
            .Observation.Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Category == CdcDiagnosticCategory.LocalStateUnavailable
                && diagnostic.Path == "$.providerHistory.sqlServerJobs"
            );
        result.IncidentCandidate.Should().BeNull();
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
    public void It_reports_unknown_when_sql_server_schema_history_phase_is_unsupported_with_valid_state()
    {
        CdcBinding binding = CdcContinuityFixture.CreateBinding(CdcProvider.SqlServer);
        CdcSourceHistoryClassificationResult result = CdcSourceHistoryContinuityClassifier.Evaluate(
            CdcContinuityFixture.CreateInput(binding) with
            {
                SqlServerSchemaHistory = new(
                    (CdcSqlServerSchemaHistoryEnablementPhase)999,
                    CdcSqlServerSchemaHistoryState.Valid
                ),
            }
        );

        result.Observation.Continuity.Should().Be(CdcSourceHistoryContinuity.Unknown);
        result
            .Observation.SchemaHistoryEnablementPhase.Should()
            .Be(CdcSqlServerSchemaHistoryEnablementPhase.AfterInitialAdmission);
        result.Observation.SchemaHistoryState.Should().Be(CdcSqlServerSchemaHistoryState.Unknown);
        result
            .Observation.PositionEvidence!.UnavailableFacts.Should()
            .Contain(CdcIncidentUnavailableFact.SchemaHistory);
        result
            .Observation.Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Category == CdcDiagnosticCategory.InvalidEnumValue
                && diagnostic.Path == "$.sqlServerSchemaHistory.enablementPhase"
            );
        result.IncidentCandidate.Should().BeNull();
        ValidateObservation(result.Observation, binding).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_reports_unknown_when_sql_server_schema_history_state_is_unsupported()
    {
        CdcBinding binding = CdcContinuityFixture.CreateBinding(CdcProvider.SqlServer);
        CdcSourceHistoryClassificationResult result = CdcSourceHistoryContinuityClassifier.Evaluate(
            CdcContinuityFixture.CreateInput(binding) with
            {
                SqlServerSchemaHistory = new(
                    CdcSqlServerSchemaHistoryEnablementPhase.AfterInitialAdmission,
                    (CdcSqlServerSchemaHistoryState)999
                ),
            }
        );

        result.Observation.Continuity.Should().Be(CdcSourceHistoryContinuity.Unknown);
        result
            .Observation.SchemaHistoryEnablementPhase.Should()
            .Be(CdcSqlServerSchemaHistoryEnablementPhase.AfterInitialAdmission);
        result.Observation.SchemaHistoryState.Should().Be(CdcSqlServerSchemaHistoryState.Unknown);
        result
            .Observation.PositionEvidence!.UnavailableFacts.Should()
            .Contain(CdcIncidentUnavailableFact.SchemaHistory);
        result
            .Observation.Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Category == CdcDiagnosticCategory.InvalidEnumValue
                && diagnostic.Path == "$.sqlServerSchemaHistory.state"
            );
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

    [TestCase(CdcProvider.Postgresql)]
    [TestCase(CdcProvider.SqlServer)]
    public void It_latches_source_partition_hash_mismatch_from_exact_match_connector_offset(
        CdcProvider provider
    )
    {
        CdcBinding binding = CdcContinuityFixture.CreateBinding(provider);
        CdcSourceHistoryClassificationInput baselineInput = CdcContinuityFixture.CreateInput(binding);
        string expectedHash = CdcContinuityFixture.ExpectedSourcePartitionHash(binding);

        CdcSourceHistoryClassificationResult result = CdcSourceHistoryContinuityClassifier.Evaluate(
            baselineInput with
            {
                ConnectorOffset = CdcContinuityFixture.ConnectorOffset(
                    binding,
                    CdcConnectorOffsetMatchResult.Exact,
                    sourcePartitionHash: MismatchedSourcePartitionHash
                ),
            }
        );

        result.Observation.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
        result
            .Observation.IncidentFailureCategory.Should()
            .Be(CdcIncidentFailureCategory.ConnectSourcePartitionMismatch);
        result.Observation.PositionEvidence!.ConnectSourcePartitionHash.Should().Be(expectedHash);
        result.IncidentCandidate.Should().NotBeNull();
        result
            .IncidentCandidate!.FailureCategory.Should()
            .Be(CdcIncidentFailureCategory.ConnectSourcePartitionMismatch);
        result.IncidentCandidate.PositionMetadata.ConnectSourcePartitionHash.Should().Be(expectedHash);

        string incidentJson = CdcJsonContract.Serialize(result.IncidentCandidate.ToIncident());
        incidentJson.Should().Contain(expectedHash);
        incidentJson.Should().NotContain(MismatchedSourcePartitionHash);
        incidentJson.Should().NotContain("EdFi_DMS_CDC");
        ValidateObservation(result.Observation, binding).Succeeded.Should().BeTrue();
        CdcIncidentValidator
            .ValidateForBinding(result.IncidentCandidate.ToIncident(), binding, Now)
            .Succeeded.Should()
            .BeTrue();
    }

    [Test]
    public void It_reports_unknown_without_incident_when_connector_offset_envelope_fails_without_hash_mismatch()
    {
        CdcBinding binding = CdcContinuityFixture.CreateBinding(CdcProvider.Postgresql);
        CdcSourceHistoryClassificationInput baselineInput = CdcContinuityFixture.CreateInput(binding);

        CdcSourceHistoryClassificationResult result = CdcSourceHistoryContinuityClassifier.Evaluate(
            baselineInput with
            {
                ConnectorOffset = baselineInput.ConnectorOffset! with { OperationId = "other-operation" },
            }
        );

        result.Observation.Continuity.Should().Be(CdcSourceHistoryContinuity.Unknown);
        result.Observation.IncidentFailureCategory.Should().BeNull();
        result.IncidentCandidate.Should().BeNull();
        result
            .Observation.Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Category == CdcDiagnosticCategory.OperationMismatch
                && diagnostic.Path == "$.connectorOffset.operationId"
            );
        ValidateObservation(result.Observation, binding).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_preserves_a_valid_latched_incident_without_creating_a_new_candidate()
    {
        CdcBinding binding = CdcContinuityFixture.CreateBinding(CdcProvider.Postgresql);
        CdcIncident incident = CreateValidLatchedIncident(binding);

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

    private static IEnumerable<TestCaseData> ValidLatchedIncidentAuthorityCases()
    {
        yield return new TestCaseData(
            CdcProvider.Postgresql,
            new Func<CdcSourceHistoryClassificationInput, CdcSourceHistoryClassificationInput>(input =>
                input with
                {
                    Diagnostics =
                    [
                        new CdcDiagnostic(
                            CdcDiagnosticCategory.LocalStateUnavailable,
                            ObservedAt,
                            "$.providerHistory",
                            "CDC provider source-history evidence is unavailable."
                        ),
                    ],
                }
            )
        ).SetName("current_diagnostics");
        yield return new TestCaseData(
            CdcProvider.SqlServer,
            new Func<CdcSourceHistoryClassificationInput, CdcSourceHistoryClassificationInput>(input =>
                input with
                {
                    ExpectedConnectSourcePartitionHash = "sha256:not-a-valid-hash",
                }
            )
        ).SetName("invalid_expected_source_partition_hash");
        yield return new TestCaseData(
            CdcProvider.Postgresql,
            new Func<CdcSourceHistoryClassificationInput, CdcSourceHistoryClassificationInput>(input =>
                input with
                {
                    ProviderSetup = null,
                }
            )
        ).SetName("missing_provider_setup");
        yield return new TestCaseData(
            CdcProvider.SqlServer,
            new Func<CdcSourceHistoryClassificationInput, CdcSourceHistoryClassificationInput>(input =>
                input with
                {
                    ConnectorOffset = input.ConnectorOffset! with { EventSerialNo = -1 },
                }
            )
        ).SetName("malformed_connector_offset");
    }

    [TestCaseSource(nameof(ValidLatchedIncidentAuthorityCases))]
    public void It_treats_valid_latched_incident_as_authoritative_before_current_evidence(
        CdcProvider provider,
        Func<CdcSourceHistoryClassificationInput, CdcSourceHistoryClassificationInput> customize
    )
    {
        CdcBinding binding = CdcContinuityFixture.CreateBinding(provider);
        CdcIncident incident = CreateValidLatchedIncident(binding);

        CdcSourceHistoryClassificationResult result = CdcSourceHistoryContinuityClassifier.Evaluate(
            customize(CdcContinuityFixture.CreateInput(binding)) with
            {
                LatchedIncident = incident,
            }
        );

        result.Observation.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
        result.Observation.IncidentLatched.Should().BeTrue();
        result.Observation.IncidentFailureCategory.Should().Be(incident.FailureCategory);
        result.Observation.PositionEvidence.Should().Be(incident.PositionMetadata);
        result.Observation.Diagnostics.Should().BeEmpty();
        result.IncidentCandidate.Should().BeNull();
        ValidateObservation(result.Observation, binding).Succeeded.Should().BeTrue();
    }

    private static CdcIncident CreateValidLatchedIncident(CdcBinding binding)
    {
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

        return lostResult.IncidentCandidate!.ToIncident();
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

    [Test]
    public void It_reports_unknown_without_incident_when_sql_server_schema_history_phase_is_unsupported_with_terminal_state()
    {
        CdcBinding binding = CdcContinuityFixture.CreateBinding(CdcProvider.SqlServer);
        CdcSourceHistoryClassificationResult result = CdcSourceHistoryContinuityClassifier.Evaluate(
            CdcContinuityFixture.CreateInput(binding) with
            {
                SqlServerSchemaHistory = new(
                    (CdcSqlServerSchemaHistoryEnablementPhase)999,
                    CdcSqlServerSchemaHistoryState.Missing
                ),
            }
        );

        result.Observation.Continuity.Should().Be(CdcSourceHistoryContinuity.Unknown);
        result.Observation.IncidentFailureCategory.Should().BeNull();
        result
            .Observation.SchemaHistoryEnablementPhase.Should()
            .Be(CdcSqlServerSchemaHistoryEnablementPhase.AfterInitialAdmission);
        result.Observation.SchemaHistoryState.Should().Be(CdcSqlServerSchemaHistoryState.Unknown);
        result
            .Observation.PositionEvidence!.UnavailableFacts.Should()
            .Contain(CdcIncidentUnavailableFact.SchemaHistory);
        result
            .Observation.Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Category == CdcDiagnosticCategory.InvalidEnumValue
                && diagnostic.Path == "$.sqlServerSchemaHistory.enablementPhase"
            );
        result.IncidentCandidate.Should().BeNull();
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
    }

    private static IEnumerable<TestCaseData> TerminalProviderHistoryWithUnavailableConnectorOffsetCases()
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
        ).SetName("postgresql_missing_artifact_with_unavailable_offset");
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
        ).SetName("postgresql_recreated_artifact_with_unavailable_offset");
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
        ).SetName("postgresql_retained_history_gap_with_unavailable_offset");
        yield return new TestCaseData(
            CdcIncidentFailureCategory.ProviderArtifactMissing,
            CdcProvider.SqlServer,
            new Func<CdcSourceHistoryClassificationInput, CdcSourceHistoryClassificationInput>(input =>
                input with
                {
                    ProviderHistory = input.ProviderHistory! with
                    {
                        SqlServerJobs = new(CdcSqlServerCdcJobState.Missing, CdcSqlServerCdcJobState.Healthy),
                    },
                }
            )
        ).SetName("sql_server_missing_job_with_unavailable_offset");
    }

    [TestCaseSource(nameof(TerminalProviderHistoryWithUnavailableConnectorOffsetCases))]
    public void It_preserves_terminal_provider_history_when_connector_offset_is_unavailable(
        CdcIncidentFailureCategory expectedFailureCategory,
        CdcProvider provider,
        Func<CdcSourceHistoryClassificationInput, CdcSourceHistoryClassificationInput> customize
    )
    {
        CdcBinding binding = CdcContinuityFixture.CreateBinding(provider);
        CdcSourceHistoryClassificationInput input = customize(CdcContinuityFixture.CreateInput(binding)) with
        {
            ConnectorOffset = null,
        };

        CdcSourceHistoryClassificationResult result = CdcSourceHistoryContinuityClassifier.Evaluate(input);

        result.Observation.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
        result.Observation.IncidentFailureCategory.Should().Be(expectedFailureCategory);
        result.IncidentCandidate.Should().NotBeNull();
        result.IncidentCandidate!.FailureCategory.Should().Be(expectedFailureCategory);
        result
            .Observation.PositionEvidence!.UnavailableFacts.Should()
            .Contain(CdcIncidentUnavailableFact.ConnectOffset);
        result
            .Observation.Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Category == CdcDiagnosticCategory.LocalStateUnavailable
                && diagnostic.Path == "$.connectorOffset"
            );
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
    }

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
    private const string SqlServerRawCatalogName = "EdFi_DMS_CDC";

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
            // An established stream, which is the scope the terminal classifications are given: the
            // binding has published, so a position it can no longer resume from is a real loss. Cases
            // that model an enablement which has not finished override it.
            PublicTopicPublication = new(true, true),
            ExpectedConnectSourcePartitionHash =
                binding.Provider == CdcProvider.SqlServer ? ExpectedSourcePartitionHash(binding) : null,
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
            []
        );

    public static CdcConnectorOffsetObservation ConnectorOffset(
        CdcBinding binding,
        CdcConnectorOffsetMatchResult matchResult = CdcConnectorOffsetMatchResult.Exact,
        bool isSnapshot = false,
        string? sourcePartitionHash = null
    )
    {
        CdcArtifactInventory inventory = CdcArtifactNameGenerator.RecoverFromBinding(binding).Inventory!;
        string resolvedSourcePartitionHash = sourcePartitionHash ?? ExpectedSourcePartitionHash(binding);

        return new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservedAt,
            binding.ToTargetIdentity(),
            binding.Provider,
            SourceFingerprint,
            inventory.ConnectorName,
            inventory.ConnectorName,
            matchResult,
            resolvedSourcePartitionHash,
            isSnapshot,
            false,
            binding.Provider == CdcProvider.Postgresql ? 0x16B6C51 : null,
            binding.Provider == CdcProvider.SqlServer ? "00000023:00000138:0002" : null,
            binding.Provider == CdcProvider.SqlServer ? "00000023:00000139:0001" : null,
            binding.Provider == CdcProvider.SqlServer ? 2 : null,
            []
        );
    }

    public static string ExpectedSourcePartitionHash(CdcBinding binding)
    {
        CdcArtifactInventory inventory = CdcArtifactNameGenerator.RecoverFromBinding(binding).Inventory!;

        return binding.Provider == CdcProvider.Postgresql
            ? CdcSourcePartitionHashCalculator.ComputePostgresql(inventory.ConnectorName).Hash!
            : CdcSourcePartitionHashCalculator
                .ComputeSqlServer(inventory.ConnectorName, SqlServerRawCatalogName)
                .Hash!;
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
        )
        {
            SqlServerJobs =
                binding.Provider == CdcProvider.SqlServer ? CdcSqlServerCdcJobEvidence.Healthy : null,
        };
    }

    private static CdcArtifactInventory Inventory(CdcProvider provider) =>
        CdcArtifactNameGenerator.Render(new("dms-local", "edfi.dms", "data-store-1", 1, provider)).Inventory!;
}
