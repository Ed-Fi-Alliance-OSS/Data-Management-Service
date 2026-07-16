// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;

namespace EdFi.DataManagementService.Backend.Tests.Common.Parity;

public static partial class ParityScenarioCatalog
{
    private const string DmsGapOwner = "DMS-1285";

    /// <summary>
    /// DMS-984 no-profile relational-write scenarios. Every canonical family is PostgreSQL-only
    /// today and owed a SQL Server twin by DMS-1285 (KnownGap). Real-world authoritative smokes are
    /// split into one row per behavioral mechanic, each deferring to a canonical family at the same
    /// no-profile production boundary (SupportingSmoke).
    /// </summary>
    internal static readonly ImmutableArray<ParityScenario> NoProfileScenarios =
    [
        // --- NoProfileFullSurfaceCreate -----------------------------------------------------
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
            sharedEntryPoint: "NoProfileCreateBaselineScenarios (DMS-1023 shared contract, later unit)"
        ),
        // --- NoProfileChangedPutOmissionSemantics -------------------------------------------
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
            sharedEntryPoint: "NoProfileUpdateSemanticsScenarios (DMS-1023 shared contract, later unit)",
            notes: "Consolidating contract; omission semantics are currently distributed across the variants below."
        ),
        Gap(
            "NoProfileChangedPutOmissionSemantics/DeletedBaseCollectionRows",
            "Changed PUT reduces a large base collection to the retained rows, deleting omitted rows in batches.",
            ProductionBoundary.NoProfileMerge,
            "PostgresqlRelationalWriteMultiBatchCollectionTests.cs",
            "Given_A_Postgresql_Relational_Write_Multi_Batch_Collection_Delete_Update_With_A_Focused_Stable_Key_Fixture",
            ["It_returns_update_success_and_persists_only_the_retained_rows_after_delete_batches"]
        ),
        Gap(
            "NoProfileChangedPutOmissionSemantics/DeletedAndReplacedChildCollectionRows",
            "Changed PUT reuses retained child-collection ids and replaces omitted rows across multiple child tables.",
            ProductionBoundary.NoProfileMerge,
            "PostgresqlRelationalWritePostAsUpdateSmokeTests.cs",
            "Given_A_Postgresql_Relational_Post_As_Update_With_The_Authoritative_Sample_StudentAcademicRecord_Fixture",
            ["It_reuses_stable_collection_item_ids_for_retained_child_rows_and_replaces_omitted_rows"]
        ),
        new ParityScenario
        {
            Id = "NoProfileChangedPutOmissionSemantics/DeletedStandaloneExtensionChildCollection",
            Layer = ParityLayer.NoProfile,
            BehavioralContract =
                "Changed PUT that omits a standalone extension-child collection deletes those rows without disturbing base rows.",
            SharedEntryPoint = "NoProfileUpdateSemanticsScenarios (DMS-1023 shared contract, later unit)",
            Boundary = ProductionBoundary.NoProfileMerge,
            Pgsql = null,
            Mssql = null,
            PgsqlCoverage = EngineCoverage.Gap,
            MssqlCoverage = EngineCoverage.Gap,
            Classification = ParityClassification.KnownGap,
            PgsqlGapOwner = "DMS-1023",
            MssqlGapOwner = DmsGapOwner,
            Notes =
                "No existing PostgreSQL test (extension child collections are exercised only on create). DMS-1023 adds the PostgreSQL proof (G1 ruling); the SQL Server twin is owed to DMS-1285.",
        },
        // --- NoProfileWriteBehavior (umbrella changed-write control) ------------------------
        Gap(
            "NoProfileWriteBehavior",
            "Umbrella no-profile changed-write control path: standard full-surface present/absent semantics including an omitted non-collection scope and a no-profile _ext case.",
            ProductionBoundary.NoProfilePersister,
            "PostgresqlRelationalWriteUpdateSemanticsTests.cs",
            "Given_A_Postgresql_Relational_Write_Update_Baseline_With_A_Focused_Stable_Key_Fixture",
            ["It_returns_update_success_and_bumps_content_version_for_the_put_flow"],
            sharedEntryPoint: "NoProfileCreateBaselineScenarios + NoProfileUpdateSemanticsScenarios (umbrella; DMS-1023 shared contracts, later unit)",
            notes: "Realized by NoProfileFullSurfaceCreate (no-profile _ext create) and NoProfileChangedPutOmissionSemantics (omitted non-collection scope)."
        ),
        // --- FullSurfaceCollectionReorder ---------------------------------------------------
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
            sharedEntryPoint: "NoProfileCollectionReorderScenarios (DMS-1023 shared contract, later unit)"
        ),
        // --- NoProfileGuardedNoOp (10 PUT/POST-as-update variants) --------------------------
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
            sharedEntryPoint: "NoProfileGuardedNoOpScenarios (DMS-1023 shared contract, later unit)",
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
        // --- NoProfileMultiBatchCollection --------------------------------------------------
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
            sharedEntryPoint: "NoProfileMultiBatchCollectionScenarios (DMS-1023 shared contract, later unit)",
            boundaryDetail: "WritePlanBatchSqlEmitter / PlanWriteBatchingConventions",
            diff: new DialectDifference(
                "PostgreSQL reserves collection ids via generate_series and caps at 65535 parameters / 1000 rows; SQL Server has no generate_series equivalent and caps at 2100 parameters / 1000 rows.",
                "Dialect parameter limits and id-reservation strategy differ; behavioral parity is the persisted rowset, contiguous 0-based ordinals, and batch partition counts, not the SQL text."
            )
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
                "It_reuses_stable_collection_item_ids_across_large_collection_tables_for_a_changed_put",
            ]
        ),
        // --- NoProfilePostAsUpdate ----------------------------------------------------------
        Gap(
            "NoProfilePostAsUpdate",
            "POST that resolves to an existing document updates in place, preserving the document row and inserting no duplicate rows.",
            ProductionBoundary.NoProfilePersister,
            "PostgresqlRelationalWritePostAsUpdateSmokeTests.cs",
            "Given_A_Postgresql_Relational_Post_As_Update_With_A_Focused_Stable_Key_Fixture",
            [
                "It_returns_update_success_and_preserves_the_existing_document_row_for_post_as_update",
                "It_applies_changed_full_surface_state_without_inserting_new_rows_for_post_as_update",
            ],
            sharedEntryPoint: "NoProfilePostAsUpdateScenarios (DMS-1023 shared contract, later unit)"
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
            "Authoritative StudentAcademicRecord POST-as-update updates root/extension in place, retains child stable ids with omitted-row replacement, and repeat POST-as-update is a no-op.",
            ProductionBoundary.NoProfilePersister,
            "PostgresqlRelationalWritePostAsUpdateSmokeTests.cs",
            "Given_A_Postgresql_Relational_Post_As_Update_With_The_Authoritative_Sample_StudentAcademicRecord_Fixture",
            [
                "It_returns_update_success_for_authoritative_post_as_update_and_preserves_the_existing_document_uuid",
                "It_updates_root_and_extension_rows_in_place_for_authoritative_student_academic_record_post_as_update",
                "It_reuses_stable_collection_item_ids_for_retained_child_rows_and_replaces_omitted_rows",
                "It_keeps_rowsets_and_content_version_unchanged_for_a_repeat_authoritative_post_as_update",
            ]
        ),
        // --- NoProfileRollbackSafety --------------------------------------------------------
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
            sharedEntryPoint: "NoProfileAtomicRollbackAssertions (DMS-1023 shared contract, later unit)"
        ),
        Gap(
            "NoProfileRollbackSafety/KeyUnificationConflictRejectedAtomically",
            "A key-unification conflict is rejected as a validation failure and leaves the document and authoritative tables unchanged (atomic rollback).",
            ProductionBoundary.KeyUnificationValidation,
            "PostgresqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationSmokeTests.cs",
            "Given_A_Postgresql_Relational_Write_Key_Unification_Conflict_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture",
            ["It_returns_a_validation_failure_and_leaves_document_and_authoritative_tables_unchanged"],
            sharedEntryPoint: "NoProfileAtomicRollbackAssertions (DMS-1023 shared contract, later unit)"
        ),
        // --- Authoritative PostgreSQL breadth smokes (one row per mechanic/boundary) --------
        // DS-5.2 Contact
        PgSmokeCreate(
            "Ds52Contact",
            "DS-5.2 Contact create with two-level nested grandchild collections.",
            "PostgresqlRelationalWriteAuthoritativeDs52ContactSmokeTests.cs",
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Ds52_Contact_Fixture",
            ["It_persists_authoritative_ds52_contact_addresses_and_nested_address_periods_on_create"]
        ),
        PgSmokeChangedPut(
            "Ds52Contact",
            "DS-5.2 Contact changed-PUT reuses stable ids for retained addresses and nested periods.",
            "PostgresqlRelationalWriteAuthoritativeDs52ContactSmokeTests.cs",
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Ds52_Contact_Fixture",
            ["It_reuses_stable_collection_item_ids_for_retained_addresses_and_nested_periods_on_changed_put"]
        ),
        PgSmokeNoOp(
            "Ds52Contact",
            "DS-5.2 Contact repeat-PUT is a guarded no-op.",
            "PostgresqlRelationalWriteAuthoritativeDs52ContactSmokeTests.cs",
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Ds52_Contact_Fixture",
            ["It_keeps_rowsets_and_content_version_unchanged_for_a_repeat_put"]
        ),
        // DS-5.2 School
        PgSmokeCreate(
            "Ds52School",
            "DS-5.2 School create across parallel root collections.",
            "PostgresqlRelationalWriteAuthoritativeDs52SchoolSmokeTests.cs",
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Ds52_School_Fixture",
            ["It_persists_authoritative_ds52_school_root_and_collection_rows_on_create"]
        ),
        PgSmokeChangedPut(
            "Ds52School",
            "DS-5.2 School changed-PUT reuses stable ids and updates ordinals (reorder).",
            "PostgresqlRelationalWriteAuthoritativeDs52SchoolSmokeTests.cs",
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Ds52_School_Fixture",
            ["It_reuses_stable_collection_item_ids_and_updates_ordinals_for_a_changed_put"]
        ),
        PgSmokeNoOp(
            "Ds52School",
            "DS-5.2 School repeat-PUT is a guarded no-op.",
            "PostgresqlRelationalWriteAuthoritativeDs52SchoolSmokeTests.cs",
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Ds52_School_Fixture",
            ["It_keeps_rowsets_and_content_version_unchanged_for_a_repeat_put"]
        ),
        // Sample StudentEducationOrganizationAssociation
        PgSmokeCreate(
            "SampleStudentEducationOrganizationAssociation",
            "Sample SEOA create with collection-aligned extension children (also exercises runtime reference-identity cascade in a separate fixture).",
            "PostgresqlRelationalWriteAuthoritativeSampleSmokeTests.cs",
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentEducationOrganizationAssociation_Fixture",
            ["It_persists_authoritative_sample_base_root_and_extension_rows_on_create"]
        ),
        PgSmokeChangedPut(
            "SampleStudentEducationOrganizationAssociation",
            "Sample SEOA changed-PUT reuses stable ids and updates root extension data.",
            "PostgresqlRelationalWriteAuthoritativeSampleSmokeTests.cs",
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentEducationOrganizationAssociation_Fixture",
            ["It_reuses_stable_collection_item_ids_and_updates_root_extension_data_for_a_changed_put"]
        ),
        PgSmokeNoOp(
            "SampleStudentEducationOrganizationAssociation",
            "Sample SEOA repeat-PUT is a guarded no-op.",
            "PostgresqlRelationalWriteAuthoritativeSampleSmokeTests.cs",
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentEducationOrganizationAssociation_Fixture",
            ["It_keeps_rowsets_and_content_version_unchanged_for_a_repeat_put"]
        ),
        // Sample StudentSchoolAssociation (the conflict/rollback fixture is a separate KnownGap row)
        PgSmokeCreate(
            "SampleStudentSchoolAssociation",
            "Sample StudentSchoolAssociation create with root extension and child rows.",
            "PostgresqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationSmokeTests.cs",
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture",
            ["It_persists_authoritative_student_school_association_root_extension_and_child_rows_on_create"]
        ),
        PgSmokeChangedPut(
            "SampleStudentSchoolAssociation",
            "Sample StudentSchoolAssociation changed-PUT reuses stable ids and updates authoritative state.",
            "PostgresqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationSmokeTests.cs",
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture",
            ["It_reuses_stable_collection_item_ids_and_updates_authoritative_state_on_changed_put"]
        ),
        PgSmokeNoOp(
            "SampleStudentSchoolAssociation",
            "Sample StudentSchoolAssociation repeat-PUT is a guarded no-op.",
            "PostgresqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationSmokeTests.cs",
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture",
            ["It_keeps_rowsets_and_content_version_unchanged_for_a_repeat_put"]
        ),
        // Sample StudentSectionAssociation
        PgSmokeCreate(
            "SampleStudentSectionAssociation",
            "Sample StudentSectionAssociation create with a reference-backed extension child collection.",
            "PostgresqlRelationalWriteAuthoritativeSampleStudentSectionAssociationSmokeTests.cs",
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentSectionAssociation_Fixture",
            [
                "It_persists_the_authoritative_sample_root_extension_and_extension_child_collection_rows_on_create",
            ]
        ),
        PgSmokeChangedPut(
            "SampleStudentSectionAssociation",
            "Sample StudentSectionAssociation changed-PUT reuses stable ids when extension children are reordered/removed/replaced.",
            "PostgresqlRelationalWriteAuthoritativeSampleStudentSectionAssociationSmokeTests.cs",
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentSectionAssociation_Fixture",
            [
                "It_reuses_stable_collection_item_ids_when_extension_children_are_reordered_removed_and_replaced",
            ]
        ),
        PgSmokeNoOp(
            "SampleStudentSectionAssociation",
            "Sample StudentSectionAssociation repeat-PUT is a guarded no-op.",
            "PostgresqlRelationalWriteAuthoritativeSampleStudentSectionAssociationSmokeTests.cs",
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentSectionAssociation_Fixture",
            ["It_keeps_rowsets_and_content_version_unchanged_for_a_repeat_put"]
        ),
        // Sample SurveyQuestion
        PgSmokeCreate(
            "SampleSurveyQuestion",
            "Sample SurveyQuestion create with two sibling root child collections and a composite reference.",
            "PostgresqlRelationalWriteAuthoritativeSampleSurveyQuestionSmokeTests.cs",
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_SurveyQuestion_Fixture",
            ["It_persists_authoritative_sample_survey_question_root_and_child_rows_on_create"]
        ),
        PgSmokeChangedPut(
            "SampleSurveyQuestion",
            "Sample SurveyQuestion changed-PUT reuses stable ids for retained matrices and response choices.",
            "PostgresqlRelationalWriteAuthoritativeSampleSurveyQuestionSmokeTests.cs",
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_SurveyQuestion_Fixture",
            ["It_reuses_stable_collection_item_ids_for_retained_matrices_and_response_choices_on_changed_put"]
        ),
        PgSmokeNoOp(
            "SampleSurveyQuestion",
            "Sample SurveyQuestion repeat-PUT is a guarded no-op.",
            "PostgresqlRelationalWriteAuthoritativeSampleSurveyQuestionSmokeTests.cs",
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_SurveyQuestion_Fixture",
            ["It_keeps_rowsets_and_content_version_unchanged_for_a_repeat_put"]
        ),
        // --- Existing SQL Server authoritative breadth (one row per mechanic/boundary) ------
        MssqlSmokeCreate(
            "SampleStudentArtProgramAssociation",
            "SQL Server no-profile create populating descriptor-backed root reference columns.",
            "MssqlRelationalWriteAuthoritativeSampleStudentArtProgramAssociationSmokeTests.cs",
            "Given_A_Mssql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentArtProgramAssociation_Fixture",
            [
                "It_extracts_descriptor_backed_root_reference_members_via_the_shared_document_info_helper",
                "It_populates_root_reference_columns_from_descriptor_backed_reference_members_on_create",
            ]
        ),
        MssqlSmokeCreate(
            "Ds52Survey",
            "SQL Server no-profile create populating runtime reference-identity columns.",
            "MssqlRelationalWriteAuthoritativeDs52SurveySmokeTests.cs",
            "Given_A_Mssql_Relational_Write_Propagated_Reference_Identity_Runtime_With_The_Authoritative_DS52_Survey_Fixture",
            ["It_populates_persisted_reference_identity_columns_on_create"]
        ),
        MssqlSmokeChangedPut(
            "Ds52Survey",
            "SQL Server changed-PUT repopulates runtime reference-identity columns and keeps rows in the identity-propagation trigger fallback.",
            "MssqlRelationalWriteAuthoritativeDs52SurveySmokeTests.cs",
            "Given_A_Mssql_Relational_Write_Propagated_Reference_Identity_Runtime_With_The_Authoritative_DS52_Survey_Fixture",
            [
                "It_repopulates_persisted_reference_identity_columns_from_resolved_references_on_changed_put",
                "It_should_keep_runtime_written_rows_participating_in_identity_propagation_trigger_fallback",
            ]
        ),
        MssqlSmokeCreate(
            "SampleStudentSchoolAssociation",
            "SQL Server no-profile create with read-back etag / If-Match / semantic-JSON-equivalence / readable-profile projection.",
            "MssqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationSmokeTests.cs",
            "Given_A_Mssql_Relational_Write_Then_Read_Smoke_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture",
            [
                "It_returns_the_create_etag_from_follow_up_get_by_id",
                "It_matches_ResourceLinks_IfMatch_against_the_current_relational_state",
                "It_reads_back_the_written_document_via_relational_get_by_id_with_semantic_json_equivalence_and_metadata",
                "It_reads_back_the_written_document_via_relational_get_by_id_with_readable_profile_projection",
            ]
        ),
    ];

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
        DialectDifference? diff = null
    ) =>
        new()
        {
            Id = id,
            Layer = ParityLayer.NoProfile,
            BehavioralContract = contract,
            SharedEntryPoint = sharedEntryPoint,
            Boundary = boundary,
            BoundaryDetail = boundaryDetail,
            Pgsql = new ScenarioLocation(pgFile, pgFixture, [.. pgMethods]),
            Mssql = null,
            PgsqlCoverage = EngineCoverage.Covered,
            MssqlCoverage = EngineCoverage.Gap,
            DialectDifference = diff,
            Classification = ParityClassification.KnownGap,
            MssqlGapOwner = DmsGapOwner,
            Notes = notes,
        };

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
        string[] methods,
        string? notes = null
    ) =>
        PgSmoke(
            $"NoProfile/AuthoritativeSmoke/{suite}/Create",
            contract,
            ProductionBoundary.NoProfilePersister,
            "NoProfileFullSurfaceCreate",
            file,
            fixture,
            methods,
            notes
        );

    private static ParityScenario PgSmokeChangedPut(
        string suite,
        string contract,
        string file,
        string fixture,
        string[] methods,
        string? notes = null
    ) =>
        PgSmoke(
            $"NoProfile/AuthoritativeSmoke/{suite}/ChangedPutStableId",
            contract,
            ProductionBoundary.NoProfileMerge,
            "FullSurfaceCollectionReorder",
            file,
            fixture,
            methods,
            notes
        );

    private static ParityScenario PgSmokeNoOp(
        string suite,
        string contract,
        string file,
        string fixture,
        string[] methods,
        string? notes = null
    ) =>
        PgSmoke(
            $"NoProfile/AuthoritativeSmoke/{suite}/RepeatPutNoOp",
            contract,
            ProductionBoundary.GuardedNoOp,
            "NoProfileGuardedNoOp",
            file,
            fixture,
            methods,
            notes
        );

    private static ParityScenario PgSmoke(
        string id,
        string contract,
        ProductionBoundary boundary,
        string coveredBy,
        string file,
        string fixture,
        string[] methods,
        string? notes
    ) =>
        new()
        {
            Id = id,
            Layer = ParityLayer.NoProfile,
            BehavioralContract = contract,
            Boundary = boundary,
            Pgsql = new ScenarioLocation(file, fixture, [.. methods]),
            Mssql = null,
            PgsqlCoverage = EngineCoverage.Covered,
            MssqlCoverage = EngineCoverage.Gap,
            Classification = ParityClassification.SupportingSmoke,
            CoveredByScenarioId = coveredBy,
            Notes = notes,
        };

    private static ParityScenario MssqlSmokeCreate(
        string suite,
        string contract,
        string file,
        string fixture,
        string[] methods,
        string? notes = null
    ) =>
        MssqlSmoke(
            $"NoProfile/MssqlAuthoritativeSmoke/{suite}/Create",
            contract,
            ProductionBoundary.NoProfilePersister,
            "NoProfileFullSurfaceCreate",
            file,
            fixture,
            methods,
            notes
        );

    private static ParityScenario MssqlSmokeChangedPut(
        string suite,
        string contract,
        string file,
        string fixture,
        string[] methods,
        string? notes = null
    ) =>
        MssqlSmoke(
            $"NoProfile/MssqlAuthoritativeSmoke/{suite}/ChangedPutStableId",
            contract,
            ProductionBoundary.NoProfileMerge,
            "FullSurfaceCollectionReorder",
            file,
            fixture,
            methods,
            notes
        );

    private static ParityScenario MssqlSmoke(
        string id,
        string contract,
        ProductionBoundary boundary,
        string coveredBy,
        string file,
        string fixture,
        string[] methods,
        string? notes
    ) =>
        new()
        {
            Id = id,
            Layer = ParityLayer.NoProfile,
            BehavioralContract = contract,
            Boundary = boundary,
            Pgsql = null,
            Mssql = new ScenarioLocation(file, fixture, [.. methods]),
            PgsqlCoverage = EngineCoverage.Gap,
            MssqlCoverage = EngineCoverage.Covered,
            Classification = ParityClassification.SupportingSmoke,
            CoveredByScenarioId = coveredBy,
            Notes = notes,
        };
}
