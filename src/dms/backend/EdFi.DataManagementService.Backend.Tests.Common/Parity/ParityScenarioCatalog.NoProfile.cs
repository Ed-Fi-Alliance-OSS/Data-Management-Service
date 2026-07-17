// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;

namespace EdFi.DataManagementService.Backend.Tests.Common.Parity;

public static partial class ParityScenarioCatalog
{
    private const string DmsGapOwner = "DMS-1285";

    private const string ContactSmoke = "PostgresqlRelationalWriteAuthoritativeDs52ContactSmokeTests.cs";
    private const string SchoolSmoke = "PostgresqlRelationalWriteAuthoritativeDs52SchoolSmokeTests.cs";
    private const string SeoaSmoke = "PostgresqlRelationalWriteAuthoritativeSampleSmokeTests.cs";
    private const string SsaSmoke =
        "PostgresqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationSmokeTests.cs";
    private const string SsecaSmoke =
        "PostgresqlRelationalWriteAuthoritativeSampleStudentSectionAssociationSmokeTests.cs";
    private const string SurveyQuestionSmoke =
        "PostgresqlRelationalWriteAuthoritativeSampleSurveyQuestionSmokeTests.cs";

    /// <summary>
    /// DMS-984 no-profile relational-write scenarios. Every canonical family is PostgreSQL-only
    /// today and owed a SQL Server twin by DMS-1285 (KnownGap). Authoritative real-world smokes are
    /// split into one row per behavioral mechanic: family mechanics (create / changed-PUT / no-op)
    /// defer to a canonical family at the same boundary (SupportingSmoke, other engine Mapped), while
    /// reference-identity and read-back mechanics — which are not among the eight no-profile write
    /// families — are first-class Both rows on their own boundaries.
    /// </summary>
    internal static readonly ImmutableArray<ParityScenario> NoProfileScenarios =
    [
        // --- NoProfileFullSurfaceCreate + variants ------------------------------------------
        Gap(
            "NoProfileFullSurfaceCreate",
            "Creates the full no-profile surface (root, nested collections, root extension, collection extension, extension-child collections) with stable ids and contiguous 0-based ordinals.",
            ProductionBoundary.NoProfilePersister,
            "PostgresqlRelationalWriteCreateBaselineTests.cs",
            "Given_A_Postgresql_Relational_Write_Create_Baseline_With_A_Focused_Stable_Key_Fixture",
            [
                "It_returns_insert_success_for_the_repository_create_flow",
                "It_persists_root_and_nested_collection_rows_with_stable_collection_ids",
                "It_persists_root_extensions_collection_extensions_and_extension_child_collections",
            ],
            sharedEntryPoint: "NoProfileCreateBaselineScenarios"
        ),
        CreateVariant(
            "InsertSuccess",
            "Create returns InsertSuccess with a persisted Document row.",
            "It_returns_insert_success_for_the_repository_create_flow"
        ),
        CreateVariant(
            "RootAndNestedCollectionStableIds",
            "Root and nested collection rows persist with unique, positive, stable CollectionItemIds and 0-based ordinals.",
            "It_persists_root_and_nested_collection_rows_with_stable_collection_ids"
        ),
        CreateVariant(
            "RootAndCollectionExtensionAndExtensionChild",
            "Root extension, collection-aligned extension, and extension-child collection rows all persist on create.",
            "It_persists_root_extensions_collection_extensions_and_extension_child_collections"
        ),
        // --- NoProfileChangedPutOmissionSemantics + variants --------------------------------
        Gap(
            "NoProfileChangedPutOmissionSemantics",
            "Changed PUT bumps ContentVersion, clears omitted inlined columns, and deletes omitted collection-aligned extension scope rows without deleting base rows.",
            ProductionBoundary.NoProfileMerge,
            "PostgresqlRelationalWriteUpdateSemanticsTests.cs",
            "Given_A_Postgresql_Relational_Write_Update_Baseline_With_A_Focused_Stable_Key_Fixture",
            [
                "It_returns_update_success_and_bumps_content_version_for_the_put_flow",
                "It_clears_omitted_inlined_root_columns_instead_of_preserving_the_old_value",
                "It_deletes_omitted_collection_aligned_extension_scope_rows_without_deleting_base_rows",
            ],
            sharedEntryPoint: "NoProfileUpdateSemanticsScenarios",
            notes: "Core omission semantics are backed by the shared contract; the variants below decompose them and add the standalone extension-child deletion proof."
        ),
        UpdateVariant(
            "ClearedInlinedColumn",
            "A changed PUT clears an omitted inlined root column instead of preserving the old value.",
            "PostgresqlRelationalWriteUpdateSemanticsTests.cs",
            "Given_A_Postgresql_Relational_Write_Update_Baseline_With_A_Focused_Stable_Key_Fixture",
            "It_clears_omitted_inlined_root_columns_instead_of_preserving_the_old_value"
        ),
        UpdateVariant(
            "DeletedAlignedExtensionScope",
            "A changed PUT deletes omitted collection-aligned extension scope rows without deleting the base rows.",
            "PostgresqlRelationalWriteUpdateSemanticsTests.cs",
            "Given_A_Postgresql_Relational_Write_Update_Baseline_With_A_Focused_Stable_Key_Fixture",
            "It_deletes_omitted_collection_aligned_extension_scope_rows_without_deleting_base_rows"
        ),
        UpdateVariant(
            "ContentVersionBump",
            "A changed PUT returns UpdateSuccess and bumps ContentVersion.",
            "PostgresqlRelationalWriteUpdateSemanticsTests.cs",
            "Given_A_Postgresql_Relational_Write_Update_Baseline_With_A_Focused_Stable_Key_Fixture",
            "It_returns_update_success_and_bumps_content_version_for_the_put_flow"
        ),
        UpdateVariant(
            "DeletedBaseCollectionRows",
            "A changed PUT reduces a large base collection to the retained rows, deleting omitted rows in batches.",
            "PostgresqlRelationalWriteMultiBatchCollectionTests.cs",
            "Given_A_Postgresql_Relational_Write_Multi_Batch_Collection_Delete_Update_With_A_Focused_Stable_Key_Fixture",
            "It_returns_update_success_and_persists_only_the_retained_rows_after_delete_batches"
        ),
        UpdateVariant(
            "DeletedAndReplacedChildCollectionRows",
            "A changed PUT reuses retained child-collection ids and replaces omitted rows across multiple child tables.",
            "PostgresqlRelationalWritePostAsUpdateSmokeTests.cs",
            "Given_A_Postgresql_Relational_Post_As_Update_With_The_Authoritative_Sample_StudentAcademicRecord_Fixture",
            "It_reuses_stable_collection_item_ids_for_retained_child_rows_and_replaces_omitted_rows"
        ),
        Gap(
            "NoProfileChangedPutOmissionSemantics/DeletedStandaloneExtensionChildCollection",
            "Changed PUT that omits a standalone extension-child collection deletes those rows without disturbing base rows.",
            ProductionBoundary.NoProfileMerge,
            "PostgresqlRelationalWriteStandaloneExtensionChildDeleteTests.cs",
            "Given_A_Postgresql_Changed_Put_Omitting_A_Standalone_Extension_Child_Collection",
            ["It_deletes_the_omitted_standalone_extension_child_collection_without_deleting_base_rows"],
            sharedEntryPoint: "NoProfileUpdateSemanticsScenarios",
            notes: "DMS-1023 adds the PostgreSQL proof (G1 ruling) that omitting a standalone extension-child collection on a changed PUT deletes those rows; the SQL Server twin is owed to DMS-1285."
        ),
        // --- NoProfileWriteBehavior (umbrella) ----------------------------------------------
        Gap(
            "NoProfileWriteBehavior",
            "Umbrella no-profile changed-write control path at the persister boundary: the changed-write flow is orchestrated to update success and bumps ContentVersion.",
            ProductionBoundary.NoProfilePersister,
            "PostgresqlRelationalWriteUpdateSemanticsTests.cs",
            "Given_A_Postgresql_Relational_Write_Update_Baseline_With_A_Focused_Stable_Key_Fixture",
            ["It_returns_update_success_and_bumps_content_version_for_the_put_flow"],
            sharedEntryPoint: "NoProfileUpdateSemanticsScenarios",
            notes: "The no-profile _ext create mechanic is recorded on NoProfileWriteBehavior/NoProfileExt; merge-level omission and deletion semantics are owned by NoProfileChangedPutOmissionSemantics at the NoProfileMerge boundary."
        ),
        Gap(
            "NoProfileWriteBehavior/OmittedNonCollectionScope",
            "A changed PUT clears an omitted non-collection (inlined) scope rather than preserving hidden data.",
            ProductionBoundary.NoProfileMerge,
            "PostgresqlRelationalWriteUpdateSemanticsTests.cs",
            "Given_A_Postgresql_Relational_Write_Update_Baseline_With_A_Focused_Stable_Key_Fixture",
            ["It_clears_omitted_inlined_root_columns_instead_of_preserving_the_old_value"]
        ),
        new ParityScenario
        {
            Id = "NoProfileWriteBehavior/NoProfileExt",
            Layer = ParityLayer.NoProfile,
            BehavioralContract =
                "The control full-surface write persists the no-profile _ext surface (root extension, collection-aligned extension, and extension-child collection rows) on create.",
            SharedEntryPoint = "NoProfileCreateBaselineScenarios",
            Boundary = ProductionBoundary.NoProfilePersister,
            BoundaryDetail =
                "No-profile _ext create surface persisted via NoProfileFullSurfaceCreate/RootAndCollectionExtensionAndExtensionChild; the omitted-aligned-extension deletion on a changed PUT is a merge mechanic cataloged under NoProfileChangedPutOmissionSemantics/DeletedAlignedExtensionScope.",
            PgsqlLocations =
            [
                Loc(
                    "PostgresqlRelationalWriteCreateBaselineTests.cs",
                    "Given_A_Postgresql_Relational_Write_Create_Baseline_With_A_Focused_Stable_Key_Fixture",
                    ["It_persists_root_extensions_collection_extensions_and_extension_child_collections"]
                ),
            ],
            PgsqlCoverage = EngineCoverage.Covered,
            MssqlCoverage = EngineCoverage.Gap,
            Classification = ParityClassification.KnownGap,
            MssqlGapOwner = DmsGapOwner,
        },
        // --- FullSurfaceCollectionReorder + variants ----------------------------------------
        Gap(
            "FullSurfaceCollectionReorder",
            "No-profile full-surface reorder matches stored rows by semantic identity, reuses CollectionItemIds, and recomputes contiguous 0-based ordinals.",
            ProductionBoundary.NoProfileMerge,
            "PostgresqlRelationalWriteCollectionReorderTests.cs",
            "Given_A_Postgresql_Relational_Write_Full_Surface_Collection_Reorder_With_A_Focused_Stable_Key_Fixture",
            [
                "It_returns_update_success_and_bumps_content_version_for_a_full_surface_reorder",
                "It_reuses_collection_item_ids_while_recomputing_ordinals_for_a_full_surface_reorder",
                "It_succeeds_for_a_two_row_swap_under_the_db_sibling_ordinal_uniqueness_constraint",
            ],
            sharedEntryPoint: "NoProfileCollectionReorderScenarios"
        ),
        ReorderVariant(
            "OrdinalReuseStableIds",
            "Reorder reuses CollectionItemIds while recomputing ordinals.",
            "It_reuses_collection_item_ids_while_recomputing_ordinals_for_a_full_surface_reorder"
        ),
        ReorderVariant(
            "TwoRowSwapUnderSiblingUniqueness",
            "A two-row ordinal swap commits under the sibling-ordinal uniqueness constraint.",
            "It_succeeds_for_a_two_row_swap_under_the_db_sibling_ordinal_uniqueness_constraint"
        ),
        ReorderVariant(
            "ContentVersionBump",
            "A full-surface reorder returns UpdateSuccess and bumps ContentVersion.",
            "It_returns_update_success_and_bumps_content_version_for_a_full_surface_reorder"
        ),
        // --- NoProfileGuardedNoOp (10 variants) ---------------------------------------------
        Gap(
            "NoProfileGuardedNoOp",
            "Unchanged PUT / POST-as-update compares the post-merge rowset to current state and skips DML, revalidating freshness before returning no-op.",
            ProductionBoundary.GuardedNoOp,
            "PostgresqlRelationalWriteGuardedNoOpTests.cs",
            "Given_A_Postgresql_Relational_Guarded_No_Op_Put_With_A_Focused_Stable_Key_Fixture",
            [
                "It_returns_update_success_for_an_unchanged_put",
                "It_keeps_rowsets_and_content_version_unchanged_for_a_guarded_no_op_put",
            ],
            sharedEntryPoint: "NoProfileGuardedNoOpScenarios",
            boundaryDetail: "RelationalWriteGuardedNoOp + IRelationalWriteFreshnessChecker/IRelationalWriteCurrentStateLoader"
        ),
        GuardedNoOp(
            "Put",
            "Given_A_Postgresql_Relational_Guarded_No_Op_Put_With_A_Focused_Stable_Key_Fixture",
            [
                "It_returns_update_success_for_an_unchanged_put",
                "It_keeps_rowsets_and_content_version_unchanged_for_a_guarded_no_op_put",
            ]
        ),
        GuardedNoOp(
            "PostAsUpdate",
            "Given_A_Postgresql_Relational_Guarded_No_Op_Post_As_Update_With_A_Focused_Stable_Key_Fixture",
            [
                "It_returns_update_success_and_preserves_the_existing_document_for_an_unchanged_post_as_update",
                "It_keeps_rowsets_and_content_version_unchanged_for_a_guarded_no_op_post_as_update",
            ]
        ),
        GuardedNoOp(
            "PutCurrentStateRefresh",
            "Given_A_Postgresql_Relational_Guarded_No_Op_Put_When_Current_State_Refreshes_Content_Version",
            [
                "It_returns_update_success_without_a_repository_retry_when_current_state_refreshes_the_content_version",
                "It_preserves_rowsets_and_avoids_an_extra_content_version_bump_during_the_guarded_no_op_put",
            ]
        ),
        GuardedNoOp(
            "PostAsUpdateCurrentStateRefresh",
            "Given_A_Postgresql_Relational_Guarded_No_Op_Post_As_Update_When_Current_State_Refreshes_Content_Version",
            [
                "It_returns_update_success_without_a_repository_retry_when_post_as_update_refreshes_current_state_freshness",
                "It_preserves_rowsets_and_avoids_an_extra_content_version_bump_during_the_guarded_no_op_post_as_update",
            ]
        ),
        GuardedNoOp(
            "PutAfterReorder",
            "Given_A_Postgresql_Relational_Guarded_No_Op_Put_After_A_Full_Surface_Collection_Reorder_With_A_Focused_Stable_Key_Fixture",
            [
                "It_returns_update_success_for_an_unchanged_put_after_reorder",
                "It_keeps_rowsets_and_content_version_unchanged_for_a_guarded_no_op_put_after_reorder",
            ]
        ),
        GuardedNoOp(
            "PostAsUpdateAfterReorder",
            "Given_A_Postgresql_Relational_Guarded_No_Op_Post_As_Update_After_A_Full_Surface_Collection_Reorder_With_A_Focused_Stable_Key_Fixture",
            [
                "It_returns_update_success_and_preserves_the_existing_document_for_an_unchanged_post_as_update_after_reorder",
                "It_keeps_rowsets_and_content_version_unchanged_for_a_guarded_no_op_post_as_update_after_reorder",
            ]
        ),
        GuardedNoOp(
            "StalePut",
            "Given_A_Postgresql_Relational_Stale_Guarded_No_Op_Put_With_A_Focused_Stable_Key_Fixture",
            [
                "It_retries_and_returns_update_success_after_the_no_op_compare_goes_stale",
                "It_preserves_the_rowsets_but_keeps_the_concurrent_content_version_bump",
            ]
        ),
        GuardedNoOp(
            "StalePostAsUpdate",
            "Given_A_Postgresql_Relational_Stale_Guarded_No_Op_Post_As_Update_With_A_Focused_Stable_Key_Fixture",
            [
                "It_retries_and_returns_update_success_for_a_stale_post_as_update_no_op_compare",
                "It_preserves_the_existing_rowsets_but_keeps_the_concurrent_content_version_bump",
            ]
        ),
        GuardedNoOp(
            "PutCommitWindowRace",
            "Given_A_Postgresql_Relational_Guarded_No_Op_Put_With_A_Commit_Window_Race",
            [
                "It_retries_the_no_op_after_the_commit_window_race_and_returns_update_success",
                "It_preserves_rowsets_but_keeps_the_concurrent_content_version_bump",
            ]
        ),
        GuardedNoOp(
            "PostAsUpdateCommitWindowRace",
            "Given_A_Postgresql_Relational_Guarded_No_Op_Post_As_Update_With_A_Commit_Window_Race",
            [
                "It_retries_the_no_op_after_the_commit_window_race_and_preserves_the_existing_document",
                "It_preserves_existing_rowsets_but_keeps_the_concurrent_content_version_bump",
            ]
        ),
        // --- NoProfileMultiBatchCollection + variants ---------------------------------------
        Gap(
            "NoProfileMultiBatchCollection",
            "Collection create/update/delete are partitioned into batches at the compiled MaxRowsPerBatch / ParametersPerRow.",
            ProductionBoundary.BatchSqlEmitter,
            "PostgresqlRelationalWriteMultiBatchCollectionTests.cs",
            "Given_A_Postgresql_Relational_Write_Multi_Batch_Collection_Create_With_A_Focused_Stable_Key_Fixture",
            [
                "It_returns_insert_success_and_persists_the_full_large_collection",
                "It_partitions_collection_id_reservation_and_insert_commands_using_the_compiled_batch_limit",
            ],
            sharedEntryPoint: "NoProfileMultiBatchCollectionScenarios",
            boundaryDetail: "WritePlanBatchSqlEmitter / PlanWriteBatchingConventions",
            diff: new DialectDifference(
                "PostgreSQL reserves collection ids via generate_series and caps at 65535 parameters / 1000 rows; SQL Server has no generate_series equivalent and caps at 2100 parameters / 1000 rows.",
                "Dialect parameter limits and id-reservation strategy differ; behavioral parity is the persisted rowset, contiguous 0-based ordinals, and batch partition counts, not the SQL text."
            )
        ),
        Gap(
            "NoProfileMultiBatchCollection/Create",
            "Multi-batch create persists the full large collection and partitions id-reservation and insert commands at the compiled batch limit.",
            ProductionBoundary.BatchSqlEmitter,
            "PostgresqlRelationalWriteMultiBatchCollectionTests.cs",
            "Given_A_Postgresql_Relational_Write_Multi_Batch_Collection_Create_With_A_Focused_Stable_Key_Fixture",
            [
                "It_returns_insert_success_and_persists_the_full_large_collection",
                "It_partitions_collection_id_reservation_and_insert_commands_using_the_compiled_batch_limit",
            ]
        ),
        Gap(
            "NoProfileMultiBatchCollection/DeleteUpdate",
            "Multi-batch delete/update reduces a large collection, partitioning delete commands at the compiled batch limit.",
            ProductionBoundary.BatchSqlEmitter,
            "PostgresqlRelationalWriteMultiBatchCollectionTests.cs",
            "Given_A_Postgresql_Relational_Write_Multi_Batch_Collection_Delete_Update_With_A_Focused_Stable_Key_Fixture",
            [
                "It_returns_update_success_and_persists_only_the_retained_rows_after_delete_batches",
                "It_partitions_collection_delete_commands_using_the_compiled_batch_limit",
            ]
        ),
        Gap(
            "NoProfileMultiBatchCollection/AlignedExtensionCreate",
            "Multi-batch create of a large collection-aligned extension scope, aligned to base row ids and partitioned at the compiled batch limit.",
            ProductionBoundary.BatchSqlEmitter,
            "PostgresqlRelationalWriteMultiBatchCollectionTests.cs",
            "Given_A_Postgresql_Relational_Write_Multi_Batch_Collection_Aligned_Extension_Create_With_A_Focused_Stable_Key_Fixture",
            [
                "It_returns_insert_success_and_persists_the_full_large_collection_aligned_extension_scope",
                "It_partitions_collection_aligned_extension_insert_commands_using_the_compiled_batch_limit",
            ]
        ),
        Gap(
            "NoProfileMultiBatchCollection/AuthoritativeParameterPressure",
            "Authoritative StudentAcademicRecord large-collection create exercises real parameter pressure (28 rows, >300 insert parameters).",
            ProductionBoundary.BatchSqlEmitter,
            "PostgresqlRelationalWritePostAsUpdateSmokeTests.cs",
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentAcademicRecord_Fixture",
            [
                "It_persists_authoritative_student_academic_record_root_extension_and_large_collection_rows_on_create",
                "It_uses_a_payload_large_enough_to_exercise_real_parameter_pressure",
            ]
        ),
        Gap(
            "NoProfileMultiBatchCollection/AuthoritativeChangedPutIdentity",
            "Authoritative StudentAcademicRecord changed PUT reuses stable collection item ids across the large-collection tables.",
            ProductionBoundary.NoProfileMerge,
            "PostgresqlRelationalWritePostAsUpdateSmokeTests.cs",
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentAcademicRecord_Fixture",
            ["It_reuses_stable_collection_item_ids_across_large_collection_tables_for_a_changed_put"]
        ),
        // --- NoProfilePostAsUpdate + variants -----------------------------------------------
        Gap(
            "NoProfilePostAsUpdate",
            "POST that resolves to an existing document (FocusedStableKey) updates in place, preserving the document row and inserting no duplicate rows.",
            ProductionBoundary.NoProfilePersister,
            "PostgresqlRelationalWritePostAsUpdateSmokeTests.cs",
            "Given_A_Postgresql_Relational_Post_As_Update_With_A_Focused_Stable_Key_Fixture",
            [
                "It_returns_update_success_and_preserves_the_existing_document_row_for_post_as_update",
                "It_applies_changed_full_surface_state_without_inserting_new_rows_for_post_as_update",
            ],
            sharedEntryPoint: "NoProfilePostAsUpdateScenarios"
        ),
        Gap(
            "NoProfilePostAsUpdate/FocusedStableKey",
            "POST-as-update on the focused stable-key fixture preserves the existing document row and applies changed full-surface state without inserting new rows.",
            ProductionBoundary.NoProfilePersister,
            "PostgresqlRelationalWritePostAsUpdateSmokeTests.cs",
            "Given_A_Postgresql_Relational_Post_As_Update_With_A_Focused_Stable_Key_Fixture",
            [
                "It_returns_update_success_and_preserves_the_existing_document_row_for_post_as_update",
                "It_applies_changed_full_surface_state_without_inserting_new_rows_for_post_as_update",
            ]
        ),
        Gap(
            "NoProfilePostAsUpdate/ImmutableIdentityRejected",
            "POST-as-update that changes an immutable identity is rejected with UpsertFailureImmutableIdentity and commits no row changes.",
            ProductionBoundary.IdentityStability,
            "PostgresqlRelationalWritePostAsUpdateSmokeTests.cs",
            "Given_A_Postgresql_Relational_Post_As_Update_Immutable_Identity_Change_With_A_Focused_Stable_Key_Fixture",
            [
                "It_returns_explicit_immutable_identity_failure_for_post_as_update",
                "It_does_not_commit_row_changes_for_rejected_post_as_update",
            ],
            boundaryDetail: "RelationalWriteIdentityStability.TryBuildFailureResult"
        ),
        Gap(
            "NoProfilePostAsUpdate/CreateRaceConvertedToUpdate",
            "A stale create candidate converts to POST-as-update after a competing create commits, applying last-writer state without duplicate rows.",
            ProductionBoundary.NoProfilePersister,
            "PostgresqlRelationalWritePostAsUpdateSmokeTests.cs",
            "Given_A_Postgresql_Relational_Post_Create_Race_With_The_Focused_Stable_Key_Fixture",
            [
                "It_converts_the_stale_create_candidate_into_post_as_update_after_the_competing_create_commits",
                "It_applies_last_writer_state_to_the_existing_document_instead_of_creating_duplicate_rows",
            ]
        ),
        Gap(
            "NoProfilePostAsUpdate/AuthoritativeDs52SchoolYearType",
            "Authoritative DS-5.2 SchoolYearType POST-as-update preserves the existing document uuid and updates the row in place.",
            ProductionBoundary.NoProfilePersister,
            "PostgresqlRelationalWritePostAsUpdateSmokeTests.cs",
            "Given_A_Postgresql_Relational_Post_As_Update_With_The_Authoritative_Ds52_SchoolYearType_Fixture",
            [
                "It_returns_update_success_for_authoritative_post_as_update_and_preserves_the_existing_document_uuid",
                "It_updates_the_authoritative_ds52_row_in_place_for_post_as_update",
            ]
        ),
        Gap(
            "NoProfilePostAsUpdate/AuthoritativeStudentAcademicRecord",
            "Authoritative StudentAcademicRecord POST-as-update preserves the existing document uuid and updates root/extension rows in place.",
            ProductionBoundary.NoProfilePersister,
            "PostgresqlRelationalWritePostAsUpdateSmokeTests.cs",
            "Given_A_Postgresql_Relational_Post_As_Update_With_The_Authoritative_Sample_StudentAcademicRecord_Fixture",
            [
                "It_returns_update_success_for_authoritative_post_as_update_and_preserves_the_existing_document_uuid",
                "It_updates_root_and_extension_rows_in_place_for_authoritative_student_academic_record_post_as_update",
            ]
        ),
        // This fixture also exercises retained-and-omitted child-collection replacement, which is a merge
        // behavior owned by the DeletedAndReplacedChildCollectionRows omission-semantics variant, and a
        // repeat POST-as-update guarded no-op recorded below. The no-op runs the guarded-no-op path yet
        // asserts through NoProfilePostAsUpdateScenarios, so it records that shared contract directly while
        // deferring its SQL Server twin to the NoProfileGuardedNoOp family.
        new ParityScenario
        {
            Id = "NoProfile/AuthoritativeSmoke/SampleStudentAcademicRecord/RepeatPostAsUpdateNoOp",
            Layer = ParityLayer.NoProfile,
            BehavioralContract =
                "Authoritative StudentAcademicRecord repeat POST-as-update is a guarded no-op.",
            SharedEntryPoint = "NoProfilePostAsUpdateScenarios",
            Boundary = ProductionBoundary.GuardedNoOp,
            PgsqlLocations =
            [
                Loc(
                    "PostgresqlRelationalWritePostAsUpdateSmokeTests.cs",
                    "Given_A_Postgresql_Relational_Post_As_Update_With_The_Authoritative_Sample_StudentAcademicRecord_Fixture",
                    [
                        "It_keeps_rowsets_and_content_version_unchanged_for_a_repeat_authoritative_post_as_update",
                    ]
                ),
            ],
            PgsqlCoverage = EngineCoverage.Covered,
            MssqlCoverage = EngineCoverage.Mapped,
            Classification = ParityClassification.SupportingSmoke,
            CoveredByScenarioId = "NoProfileGuardedNoOp",
        },
        // --- NoProfileRollbackSafety + variants ---------------------------------------------
        Gap(
            "NoProfileRollbackSafety",
            "A failure after early relational writes rolls the whole request back, leaving no partial state.",
            ProductionBoundary.NoProfilePersister,
            "PostgresqlRelationalWriteRollbackSafetyTests.cs",
            "Given_A_Postgresql_Relational_Write_Create_Failure_After_Early_Writes_With_A_Focused_Stable_Key_Fixture",
            [
                "It_surfaces_the_injected_failure_only_after_the_early_write_commands_are_attempted",
                "It_leaves_no_partial_relational_state_after_the_transaction_rolls_back",
            ],
            sharedEntryPoint: "NoProfileAtomicRollbackAssertions"
        ),
        Gap(
            "NoProfileRollbackSafety/CreateFailureAfterEarlyWrites",
            "An injected failure after early writes rolls back fully, leaving Document/School/SchoolAddress counts at zero.",
            ProductionBoundary.NoProfilePersister,
            "PostgresqlRelationalWriteRollbackSafetyTests.cs",
            "Given_A_Postgresql_Relational_Write_Create_Failure_After_Early_Writes_With_A_Focused_Stable_Key_Fixture",
            [
                "It_surfaces_the_injected_failure_only_after_the_early_write_commands_are_attempted",
                "It_leaves_no_partial_relational_state_after_the_transaction_rolls_back",
            ]
        ),
        Gap(
            "NoProfileRollbackSafety/KeyUnificationConflictRejectedAtomically",
            "A key-unification conflict is rejected as a validation failure and leaves the document and authoritative tables unchanged (atomic rollback).",
            ProductionBoundary.KeyUnificationValidation,
            SsaSmoke,
            "Given_A_Postgresql_Relational_Write_Key_Unification_Conflict_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture",
            ["It_returns_a_validation_failure_and_leaves_document_and_authoritative_tables_unchanged"],
            sharedEntryPoint: "NoProfileAtomicRollbackAssertions"
        ),
        // --- Authoritative PostgreSQL breadth smokes (one row per mechanic/boundary) --------
        PgSmokeCreate(
            "Ds52Contact",
            "DS-5.2 Contact create with two-level nested grandchild collections.",
            ContactSmoke,
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Ds52_Contact_Fixture",
            ["It_persists_authoritative_ds52_contact_addresses_and_nested_address_periods_on_create"]
        ),
        PgSmokeChangedPut(
            "Ds52Contact",
            "DS-5.2 Contact changed-PUT reuses stable ids for retained addresses and nested periods.",
            ContactSmoke,
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Ds52_Contact_Fixture",
            ["It_reuses_stable_collection_item_ids_for_retained_addresses_and_nested_periods_on_changed_put"]
        ),
        PgSmokeNoOp(
            "Ds52Contact",
            "DS-5.2 Contact repeat-PUT is a guarded no-op.",
            ContactSmoke,
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Ds52_Contact_Fixture",
            ["It_keeps_rowsets_and_content_version_unchanged_for_a_repeat_put"]
        ),
        PgSmokeCreate(
            "Ds52School",
            "DS-5.2 School create across parallel root collections.",
            SchoolSmoke,
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Ds52_School_Fixture",
            ["It_persists_authoritative_ds52_school_root_and_collection_rows_on_create"]
        ),
        PgSmokeChangedPut(
            "Ds52School",
            "DS-5.2 School changed-PUT reuses stable ids and updates ordinals (reorder).",
            SchoolSmoke,
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Ds52_School_Fixture",
            ["It_reuses_stable_collection_item_ids_and_updates_ordinals_for_a_changed_put"]
        ),
        PgSmokeNoOp(
            "Ds52School",
            "DS-5.2 School repeat-PUT is a guarded no-op.",
            SchoolSmoke,
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Ds52_School_Fixture",
            ["It_keeps_rowsets_and_content_version_unchanged_for_a_repeat_put"]
        ),
        PgSmokeCreate(
            "SampleStudentEducationOrganizationAssociation",
            "Sample SEOA create with collection-aligned extension children.",
            SeoaSmoke,
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentEducationOrganizationAssociation_Fixture",
            ["It_persists_authoritative_sample_base_root_and_extension_rows_on_create"]
        ),
        PgSmokeChangedPut(
            "SampleStudentEducationOrganizationAssociation",
            "Sample SEOA changed-PUT reuses stable ids and updates root extension data (omission/update semantics).",
            SeoaSmoke,
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentEducationOrganizationAssociation_Fixture",
            ["It_reuses_stable_collection_item_ids_and_updates_root_extension_data_for_a_changed_put"],
            coveredBy: "NoProfileChangedPutOmissionSemantics"
        ),
        PgSmokeNoOp(
            "SampleStudentEducationOrganizationAssociation",
            "Sample SEOA repeat-PUT is a guarded no-op.",
            SeoaSmoke,
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentEducationOrganizationAssociation_Fixture",
            ["It_keeps_rowsets_and_content_version_unchanged_for_a_repeat_put"]
        ),
        PgSmokeCreate(
            "SampleStudentSchoolAssociation",
            "Sample StudentSchoolAssociation create with root extension and child rows.",
            SsaSmoke,
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture",
            ["It_persists_authoritative_student_school_association_root_extension_and_child_rows_on_create"]
        ),
        PgSmokeChangedPut(
            "SampleStudentSchoolAssociation",
            "Sample StudentSchoolAssociation changed-PUT reuses stable ids and updates authoritative state.",
            SsaSmoke,
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture",
            ["It_reuses_stable_collection_item_ids_and_updates_authoritative_state_on_changed_put"]
        ),
        PgSmokeNoOp(
            "SampleStudentSchoolAssociation",
            "Sample StudentSchoolAssociation repeat-PUT is a guarded no-op.",
            SsaSmoke,
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture",
            ["It_keeps_rowsets_and_content_version_unchanged_for_a_repeat_put"]
        ),
        PgSmokeCreate(
            "SampleStudentSectionAssociation",
            "Sample StudentSectionAssociation create with a reference-backed extension child collection.",
            SsecaSmoke,
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentSectionAssociation_Fixture",
            [
                "It_persists_the_authoritative_sample_root_extension_and_extension_child_collection_rows_on_create",
            ]
        ),
        PgSmokeChangedPut(
            "SampleStudentSectionAssociation",
            "Sample StudentSectionAssociation changed-PUT reorders/removes/replaces reference-backed extension children with stable ids.",
            SsecaSmoke,
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentSectionAssociation_Fixture",
            [
                "It_reuses_stable_collection_item_ids_when_extension_children_are_reordered_removed_and_replaced",
            ]
        ),
        PgSmokeNoOp(
            "SampleStudentSectionAssociation",
            "Sample StudentSectionAssociation repeat-PUT is a guarded no-op.",
            SsecaSmoke,
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentSectionAssociation_Fixture",
            ["It_keeps_rowsets_and_content_version_unchanged_for_a_repeat_put"]
        ),
        PgSmokeCreate(
            "SampleSurveyQuestion",
            "Sample SurveyQuestion create with two sibling root child collections and a composite reference.",
            SurveyQuestionSmoke,
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_SurveyQuestion_Fixture",
            ["It_persists_authoritative_sample_survey_question_root_and_child_rows_on_create"]
        ),
        PgSmokeChangedPut(
            "SampleSurveyQuestion",
            "Sample SurveyQuestion changed-PUT reuses stable ids for retained matrices and response choices.",
            SurveyQuestionSmoke,
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_SurveyQuestion_Fixture",
            ["It_reuses_stable_collection_item_ids_for_retained_matrices_and_response_choices_on_changed_put"]
        ),
        PgSmokeNoOp(
            "SampleSurveyQuestion",
            "Sample SurveyQuestion repeat-PUT is a guarded no-op.",
            SurveyQuestionSmoke,
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_SurveyQuestion_Fixture",
            ["It_keeps_rowsets_and_content_version_unchanged_for_a_repeat_put"]
        ),
        // --- Reference-identity and read-back: cross-engine Both rows on their own boundaries -
        NoProfileBoth(
            "NoProfile/ReferenceIdentityRuntime",
            "Runtime reference-identity: descriptor/reference-backed reference columns are populated on create, repopulated on changed PUT, and participate in identity-propagation cascade/trigger fallback. Covered on both engines (different resources; per-mechanic parity).",
            ProductionBoundary.ReferenceIdentityRuntime,
            [
                Loc(
                    SeoaSmoke,
                    "Given_A_Postgresql_Relational_Write_Propagated_Reference_Identity_Cascade_With_The_Authoritative_Sample_StudentEducationOrganizationAssociation_Fixture",
                    [
                        "It_should_store_runtime_written_reference_identity_columns_in_all_or_none_shape",
                        "It_should_cascade_abstract_reference_identity_updates_into_runtime_written_reference_columns",
                    ]
                ),
                Loc(
                    SsaSmoke,
                    "Given_A_Postgresql_Relational_Write_Propagated_Reference_Identity_Runtime_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture",
                    [
                        "It_populates_persisted_reference_identity_columns_on_create",
                        "It_repopulates_persisted_reference_identity_columns_from_resolved_references_on_changed_put",
                    ]
                ),
                Loc(
                    SsaSmoke,
                    "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture",
                    [
                        "It_extracts_descriptor_valued_collection_reference_members_from_concrete_paths_via_the_shared_document_info_helper",
                    ]
                ),
            ],
            [
                Loc(
                    "MssqlRelationalWriteAuthoritativeDs52SurveySmokeTests.cs",
                    "Given_A_Mssql_Relational_Write_Propagated_Reference_Identity_Runtime_With_The_Authoritative_DS52_Survey_Fixture",
                    [
                        "It_populates_persisted_reference_identity_columns_on_create",
                        "It_repopulates_persisted_reference_identity_columns_from_resolved_references_on_changed_put",
                        "It_should_keep_runtime_written_rows_participating_in_native_identity_cascades",
                    ]
                ),
                Loc(
                    "MssqlRelationalWriteAuthoritativeSampleStudentArtProgramAssociationSmokeTests.cs",
                    "Given_A_Mssql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentArtProgramAssociation_Fixture",
                    [
                        "It_extracts_descriptor_backed_root_reference_members_via_the_shared_document_info_helper",
                        "It_populates_root_reference_columns_from_descriptor_backed_reference_members_on_create",
                    ]
                ),
            ],
            notes: "Not one of the eight no-profile write families; a first-class cross-engine mechanic outside SupportingSmoke."
        ),
        NoProfileBoth(
            "NoProfile/RelationalReadback",
            "Relational GET-by-id read-back parity: served create ETag, ResourceLinks If-Match against current relational state, semantic-JSON-equivalence + metadata, and readable-profile projection. Covered on both engines.",
            ProductionBoundary.RelationalReadback,
            [
                Loc(
                    SsaSmoke,
                    "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture",
                    [
                        "It_returns_the_create_etag_from_follow_up_get_by_id",
                        "It_matches_ResourceLinks_IfMatch_against_the_current_relational_state",
                        "It_reads_back_the_written_document_via_relational_get_by_id_with_semantic_json_equivalence_and_metadata",
                        "It_reads_back_the_written_document_via_relational_get_by_id_with_readable_profile_projection",
                    ]
                ),
                Loc(
                    SeoaSmoke,
                    "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentEducationOrganizationAssociation_Fixture",
                    [
                        "It_reads_back_the_written_document_via_relational_get_by_id_with_readable_profile_projection_for_collection_aligned_extensions",
                    ]
                ),
            ],
            [
                Loc(
                    "MssqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationSmokeTests.cs",
                    "Given_A_Mssql_Relational_Write_Then_Read_Smoke_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture",
                    [
                        "It_returns_the_create_etag_from_follow_up_get_by_id",
                        "It_matches_ResourceLinks_IfMatch_against_the_current_relational_state",
                        "It_reads_back_the_written_document_via_relational_get_by_id_with_semantic_json_equivalence_and_metadata",
                        "It_reads_back_the_written_document_via_relational_get_by_id_with_readable_profile_projection",
                    ]
                ),
            ],
            notes: "Not one of the eight no-profile write families; a first-class cross-engine read-path mechanic outside SupportingSmoke."
        ),
        Gap(
            "NoProfile/RelationalReadback/ChangedPutEtag",
            "The served ETag from a follow-up GET-by-id after a changed PUT matches the current relational state (PostgreSQL-only today; no audited SQL Server equivalent).",
            ProductionBoundary.RelationalReadback,
            SsaSmoke,
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture",
            ["It_returns_the_changed_put_etag_from_follow_up_get_by_id"],
            providerSpecificRationale: ReadbackProviderSpecificRationale
        ),
        Gap(
            "NoProfile/RelationalReadback/RepeatPutEtag",
            "The served ETag from a follow-up GET-by-id after a repeat (no-op) PUT matches the current relational state (PostgreSQL-only today; no audited SQL Server equivalent).",
            ProductionBoundary.RelationalReadback,
            SsaSmoke,
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture",
            ["It_returns_the_repeat_put_etag_from_follow_up_get_by_id"],
            providerSpecificRationale: ReadbackProviderSpecificRationale
        ),
    ];

    private static ScenarioLocation Loc(string file, string fixture, string[] methods) =>
        new(file, fixture, [.. methods]);

    private static ParityScenario Gap(
        string id,
        string contract,
        ProductionBoundary boundary,
        string pgFile,
        string pgFixture,
        string[] pgMethods,
        string sharedEntryPoint = "",
        string? boundaryDetail = null,
        string? notes = null,
        DialectDifference? diff = null,
        string? providerSpecificRationale = null
    ) =>
        new()
        {
            Id = id,
            Layer = ParityLayer.NoProfile,
            BehavioralContract = contract,
            SharedEntryPoint = sharedEntryPoint,
            Boundary = boundary,
            BoundaryDetail = boundaryDetail,
            PgsqlLocations = [new ScenarioLocation(pgFile, pgFixture, [.. pgMethods])],
            PgsqlCoverage = EngineCoverage.Covered,
            MssqlCoverage = EngineCoverage.Gap,
            DialectDifference = diff,
            Classification = ParityClassification.KnownGap,
            MssqlGapOwner = DmsGapOwner,
            ProviderSpecificEntryPointRationale = providerSpecificRationale,
            Notes = notes,
        };

    // The relational read-back ETag gaps are a PostgreSQL-only read-path mechanic with no extracted provider-
    // neutral shared contract (their Both parent NoProfile/RelationalReadback is itself provider-specific), so
    // they resolve ProviderSpecific from the PostgreSQL fixture recorded on the row.
    private const string ReadbackProviderSpecificRationale =
        "PostgreSQL-only relational read-back ETag check with no extracted provider-neutral shared contract; the "
        + "PostgreSQL fixture recorded on this row is the effective assertion entry point.";

    private static ParityScenario CreateVariant(string variant, string contract, string method) =>
        Gap(
            $"NoProfileFullSurfaceCreate/{variant}",
            contract,
            ProductionBoundary.NoProfilePersister,
            "PostgresqlRelationalWriteCreateBaselineTests.cs",
            "Given_A_Postgresql_Relational_Write_Create_Baseline_With_A_Focused_Stable_Key_Fixture",
            [method]
        );

    private static ParityScenario UpdateVariant(
        string variant,
        string contract,
        string file,
        string fixture,
        string method
    ) =>
        Gap(
            $"NoProfileChangedPutOmissionSemantics/{variant}",
            contract,
            ProductionBoundary.NoProfileMerge,
            file,
            fixture,
            [method]
        );

    private static ParityScenario ReorderVariant(string variant, string contract, string method) =>
        Gap(
            $"FullSurfaceCollectionReorder/{variant}",
            contract,
            ProductionBoundary.NoProfileMerge,
            "PostgresqlRelationalWriteCollectionReorderTests.cs",
            "Given_A_Postgresql_Relational_Write_Full_Surface_Collection_Reorder_With_A_Focused_Stable_Key_Fixture",
            [method]
        );

    private static ParityScenario GuardedNoOp(string variant, string pgFixture, string[] pgMethods) =>
        Gap(
            $"NoProfileGuardedNoOp/{variant}",
            $"Guarded no-op variant: {variant}.",
            ProductionBoundary.GuardedNoOp,
            "PostgresqlRelationalWriteGuardedNoOpTests.cs",
            pgFixture,
            pgMethods
        );

    private static ParityScenario PgSmokeCreate(
        string suite,
        string contract,
        string file,
        string fixture,
        string[] methods
    ) =>
        PgSmoke(
            $"NoProfile/AuthoritativeSmoke/{suite}/Create",
            contract,
            ProductionBoundary.NoProfilePersister,
            "NoProfileFullSurfaceCreate",
            file,
            fixture,
            methods
        );

    private static ParityScenario PgSmokeChangedPut(
        string suite,
        string contract,
        string file,
        string fixture,
        string[] methods,
        string coveredBy = "FullSurfaceCollectionReorder"
    ) =>
        PgSmoke(
            $"NoProfile/AuthoritativeSmoke/{suite}/ChangedPut",
            contract,
            ProductionBoundary.NoProfileMerge,
            coveredBy,
            file,
            fixture,
            methods
        );

    private static ParityScenario PgSmokeNoOp(
        string suite,
        string contract,
        string file,
        string fixture,
        string[] methods
    ) =>
        PgSmoke(
            $"NoProfile/AuthoritativeSmoke/{suite}/RepeatPutNoOp",
            contract,
            ProductionBoundary.GuardedNoOp,
            "NoProfileGuardedNoOp",
            file,
            fixture,
            methods
        );

    private static ParityScenario PgSmoke(
        string id,
        string contract,
        ProductionBoundary boundary,
        string coveredBy,
        string file,
        string fixture,
        string[] methods
    ) =>
        new()
        {
            Id = id,
            Layer = ParityLayer.NoProfile,
            BehavioralContract = contract,
            Boundary = boundary,
            PgsqlLocations = [new ScenarioLocation(file, fixture, [.. methods])],
            PgsqlCoverage = EngineCoverage.Covered,
            MssqlCoverage = EngineCoverage.Mapped,
            Classification = ParityClassification.SupportingSmoke,
            CoveredByScenarioId = coveredBy,
        };

    // A first-class cross-engine mechanic already covered on both engines by provider-specific fixtures, with no
    // extracted provider-neutral shared contract; it resolves ProviderSpecific from its per-engine locations.
    private const string NoProfileBothProviderSpecificRationale =
        "Already covered on both PostgreSQL and SQL Server by the provider-specific fixtures recorded on this row; "
        + "this first-class cross-engine mechanic has no extracted provider-neutral shared contract, so those "
        + "existing per-engine fixtures are the effective entry points.";

    private static ParityScenario NoProfileBoth(
        string id,
        string contract,
        ProductionBoundary boundary,
        ImmutableArray<ScenarioLocation> pgsql,
        ImmutableArray<ScenarioLocation> mssql,
        string? notes = null
    ) =>
        new()
        {
            Id = id,
            Layer = ParityLayer.NoProfile,
            BehavioralContract = contract,
            Boundary = boundary,
            PgsqlLocations = pgsql,
            MssqlLocations = mssql,
            PgsqlCoverage = EngineCoverage.Covered,
            MssqlCoverage = EngineCoverage.Covered,
            Classification = ParityClassification.Both,
            ProviderSpecificEntryPointRationale = NoProfileBothProviderSpecificRationale,
            Notes = notes,
        };
}
