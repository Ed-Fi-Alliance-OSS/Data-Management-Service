// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache.Cdc;

[TestFixture]
[Parallelizable]
[Category("CdcEvidenceTraceability")]
public class Given_Dms1319StoryEvidenceTraceability
{
    private static readonly string RepositoryRoot = FixturePathResolver.FindRepositoryRoot(
        AppContext.BaseDirectory
    );

    private static IEnumerable<TestCaseData> InvariantEvidence()
    {
        yield return Invariant(
            "CDC-INV-10",
            Evidence(
                "src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/DocumentCache/Cdc/CdcAdmissionEvaluatorTests.cs",
                "Given_CdcAdmissionEvaluator",
                "It_admits_when_same_operation_evidence_is_satisfied_in_order",
                "Given_CdcAdmissionOrdering"
            ),
            Evidence(
                "src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/PostgresqlCdcSourcePositionAdapterTests.cs",
                "Given_PostgresqlCdcSourcePositionAdapter",
                "It_captures_pg_current_wal_lsn_and_returns_a_reached_barrier_observation"
            ),
            Evidence(
                "src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/MssqlCdcSourcePositionAdapterTests.cs",
                "Given_MssqlCdcSourcePositionAdapter",
                "It_captures_heartbeat_after_image_and_returns_a_reached_barrier_observation"
            ),
            Evidence(
                "src/dms/tests/EdFi.DataManagementService.Tests.Integration/Tests/DocumentCache/Given_DocumentCacheStatusEndpointProductionService.cs",
                "Given_DocumentCacheStatusEndpointProductionService",
                "It_keeps_normal_api_routing_open_when_later_projection_work_makes_cdc_status_not_caught_up"
            )
        );

        yield return Invariant(
            "CDC-INV-11",
            Evidence(
                "src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/DocumentCache/Cdc/CdcSourceHistoryContinuityClassifierTests.cs",
                "Given_CdcSourceHistoryContinuityClassifier",
                "Given_CdcContinuityIncidentClassifier",
                "It_maps_terminal_evidence_to_valid_incident_candidates"
            ),
            Evidence(
                "src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/PostgresqlCdcSourcePositionAdapterTests.cs",
                "It_reads_slot_publication_and_retained_wal_metadata_for_healthy_continuity",
                "It_latches_terminal_provider_artifact_loss_when_the_binding_slot_is_missing"
            ),
            Evidence(
                "src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/MssqlCdcSourcePositionAdapterTests.cs",
                "It_reads_capture_job_and_retained_lsn_metadata_for_healthy_continuity",
                "It_latches_terminal_provider_artifact_loss_when_the_binding_heartbeat_capture_is_missing"
            )
        );

        yield return Invariant(
            "CDC-INV-12",
            Evidence(
                "src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/DocumentCache/Cdc/CdcBindingExactMatchTests.cs",
                "Given_CdcBindingExactMatch",
                "It_accepts_a_persisted_binding_only_when_every_v1_field_matches"
            ),
            Evidence(
                "src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/DocumentCache/Cdc/CdcBindingStateStoreTests.cs",
                "Given_CdcBindingStateStore",
                "It_creates_absent_bindings_and_accepts_later_exact_matches_without_rewriting"
            ),
            Evidence(
                "src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/DocumentCache/Cdc/LocalCdcStateStoreTests.cs",
                "Given_LocalCdcStateStore",
                "It_creates_bindings_with_create_new_semantics_and_owner_only_permissions",
                "It_rejects_invalid_import_and_cleanup_proofs_without_mutating_state"
            )
        );

        yield return Invariant(
            "CDC-INV-14",
            Evidence(
                "src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/DocumentCache/Cdc/CdcRetryClassifierTests.cs",
                "Given_CdcInitialEnableRetryClassifier",
                "It_rejects_resetting_or_rebuilding_lifecycle",
                "It_rejects_cache_ahead_latch"
            ),
            Evidence(
                "src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/DocumentCache/Cdc/CdcCleanupProofTests.cs",
                "Given_CdcCleanupProof",
                "It_accepts_complete_provider_applicable_governed_artifact_inventory"
            ),
            Evidence(
                "src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/DocumentCacheAdministrativePrimitivesTests.cs",
                "Given_DocumentCacheAdministrativePrimitives",
                "It_requires_internal_only_proof_and_matching_offline_admission_before_work_clearing"
            )
        );

        yield return Invariant(
            "CDC-INV-15",
            Evidence(
                "src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/DocumentCache/Cdc/CdcDiagnosticTests.cs",
                "Given_CdcDiagnostic",
                "It_orders_caps_and_appends_a_truncation_diagnostic"
            ),
            Evidence(
                "src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/DocumentCache/Cdc/CdcTelemetryTests.cs",
                "Given_CdcTelemetry",
                "It_renders_only_the_allowed_bounded_labels"
            ),
            Evidence(
                "src/dms/tests/EdFi.DataManagementService.Tests.Integration/Tests/DocumentCache/Given_DocumentCacheStatusEndpointProductionService.cs",
                "It_returns_real_provider_backed_multi_target_status_without_leaking_provider_details"
            )
        );
    }

    private static IEnumerable<TestCaseData> CoverageReview()
    {
        yield return Coverage(
            "immutable JSON serialization",
            Evidence(
                "src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/DocumentCache/Cdc/CdcBindingContractTests.cs",
                "It_serializes_the_persisted_binding_as_the_design_approved_immutable_record"
            )
        );
        yield return Coverage(
            "binding exact-match behavior",
            Evidence(
                "src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/DocumentCache/Cdc/CdcBindingExactMatchTests.cs",
                "It_reports_missing_and_extra_persisted_fields"
            )
        );
        yield return Coverage(
            "target validation",
            Evidence(
                "src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/DocumentCache/Cdc/CdcTargetValidationTests.cs",
                "It_rejects_unsafe_administrative_tokens"
            )
        );
        yield return Coverage(
            "state-store CAS",
            Evidence(
                "src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/DocumentCache/Cdc/CdcBindingStateStoreTests.cs",
                "It_creates_absent_bindings_and_accepts_later_exact_matches_without_rewriting"
            )
        );
        yield return Coverage(
            "proof validation",
            Evidence(
                "src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/DocumentCache/Cdc/CdcAdoptionProofTests.cs",
                "It_rejects_duplicate_missing_non_exact_and_unsafe_verification_results"
            ),
            Evidence(
                "src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/DocumentCache/Cdc/CdcCleanupProofTests.cs",
                "It_rejects_incomplete_duplicate_unexpected_mismatched_and_unremoved_artifacts"
            )
        );
        yield return Coverage(
            "artifact-name conformance",
            Evidence(
                "src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/DocumentCache/Cdc/CdcArtifactNameTests.cs",
                "It_renders_complete_postgresql_inventory_from_design_formulas",
                "It_truncates_provider_artifacts_with_literal_artifact_kind_hashes"
            )
        );
        yield return Coverage(
            "source fingerprint vectors",
            Evidence(
                "src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/DocumentCache/Cdc/PhysicalSourceFingerprintTests.cs",
                "It_computes_the_design_physical_source_fingerprint_vectors"
            )
        );
        yield return Coverage(
            "source-partition hash vectors",
            Evidence(
                "src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/DocumentCache/Cdc/CdcSourcePartitionHashTests.cs",
                "It_computes_the_design_postgresql_source_partition_hash_vector",
                "It_computes_the_design_sql_server_source_partition_hash_vectors"
            )
        );
        yield return Coverage(
            "observation validation",
            Evidence(
                "src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/DocumentCache/Cdc/CdcObservationEnvelopeTests.cs",
                "It_rejects_envelope_operation_target_provider_source_and_future_timestamp_mismatches"
            )
        );
        yield return Coverage(
            "status aggregation",
            Evidence(
                "src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/DocumentCache/Cdc/CdcAggregateStatusEvaluatorTests.cs",
                "It_keeps_every_target_result_and_per_target_diagnostic_unchanged"
            )
        );
        yield return Coverage(
            "admission ordering",
            Evidence(
                "src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/DocumentCache/Cdc/CdcAdmissionEvaluatorTests.cs",
                "It_requires_the_second_projection_caught_up_observation_after_provider_barrier_success"
            )
        );
        yield return Coverage(
            "retry classification",
            Evidence(
                "src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/DocumentCache/Cdc/CdcRetryClassifierTests.cs",
                "It_classifies_exact_tracking_binding_as_provider_topic_connector_resume"
            )
        );
        yield return Coverage(
            "continuity matrices",
            Evidence(
                "src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/DocumentCache/Cdc/CdcSourceHistoryContinuityClassifierTests.cs",
                "It_maps_terminal_evidence_to_valid_incident_candidates"
            )
        );
        yield return Coverage(
            "provider adapters",
            Evidence(
                "src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/PostgresqlCdcSourcePositionAdapterTests.cs",
                "Given_PostgresqlCdcSourcePositionAdapter"
            ),
            Evidence(
                "src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/MssqlCdcSourcePositionAdapterTests.cs",
                "Given_MssqlCdcSourcePositionAdapter"
            )
        );
        yield return Coverage(
            "diagnostic privacy",
            Evidence(
                "src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/DocumentCache/Cdc/CdcDiagnosticTests.cs",
                "It_bounds_sanitized_text_fields"
            ),
            Evidence(
                "src/dms/tests/EdFi.DataManagementService.Tests.Integration/Tests/DocumentCache/Given_DocumentCacheStatusEndpointProductionService.cs",
                "It_returns_real_provider_backed_multi_target_status_without_leaking_provider_details"
            )
        );
    }

    [TestCaseSource(nameof(InvariantEvidence))]
    public void It_keeps_story_owned_evidence_traceable_to_cdc_invariants(
        string invariantId,
        EvidenceReference[] evidence
    )
    {
        invariantId.Should().MatchRegex("^CDC-INV-1(0|1|2|4|5)$");
        evidence.Should().NotBeEmpty();

        foreach (EvidenceReference evidenceReference in evidence)
        {
            AssertEvidencePresent(evidenceReference, invariantId);
        }
    }

    [TestCaseSource(nameof(CoverageReview))]
    public void It_keeps_the_final_coverage_review_anchored_to_focused_tests(
        string coverageArea,
        EvidenceReference[] evidence
    )
    {
        coverageArea.Should().NotBeNullOrWhiteSpace();
        evidence.Should().NotBeEmpty();

        foreach (EvidenceReference evidenceReference in evidence)
        {
            AssertEvidencePresent(evidenceReference, coverageArea);
        }
    }

    private static TestCaseData Invariant(string invariantId, params EvidenceReference[] evidence) =>
        new TestCaseData(invariantId, evidence).SetName(invariantId.ToLowerInvariant().Replace("-", "_"));

    private static TestCaseData Coverage(string coverageArea, params EvidenceReference[] evidence) =>
        new TestCaseData(coverageArea, evidence).SetName(
            coverageArea.Replace(" ", "_", StringComparison.Ordinal)
        );

    private static EvidenceReference Evidence(
        string repositoryRelativePath,
        params string[] requiredTokens
    ) => new(repositoryRelativePath, requiredTokens);

    private static void AssertEvidencePresent(EvidenceReference evidence, string reason)
    {
        string path = Path.Combine(RepositoryRoot, evidence.RepositoryRelativePath);
        File.Exists(path).Should().BeTrue($"{reason} evidence file should exist: {path}");

        string text = File.ReadAllText(path);
        foreach (string token in evidence.RequiredTokens)
        {
            text.Should().Contain(token, $"{reason} evidence should remain anchored in {path}");
        }
    }

    public sealed record EvidenceReference(
        string RepositoryRelativePath,
        IReadOnlyList<string> RequiredTokens
    );
}
