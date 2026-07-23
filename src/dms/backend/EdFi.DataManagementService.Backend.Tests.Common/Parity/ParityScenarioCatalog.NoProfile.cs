// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;

namespace EdFi.DataManagementService.Backend.Tests.Common.Parity;

public static partial class ParityScenarioCatalog
{
    /// <summary>
    /// DMS-984 no-profile relational-write scenarios: the eight canonical families and the variants
    /// that decompose the behavior each family requires. DMS-1285 closed the SQL Server twins for
    /// all eight families, so every row is Covered on both engines; intentional dialect differences
    /// (multi-batch shapes, the commit-window race scheduling) are recorded on their rows. This
    /// catalog records the closed parity matrix; it does not define or expand it.
    /// </summary>
    internal static readonly ImmutableArray<ParityScenario> NoProfileScenarios =
    [
        // --- NoProfileFullSurfaceCreate + variants ------------------------------------------
        NoProfile(
            "NoProfileFullSurfaceCreate",
            "Creates the full no-profile surface (root, nested collections, root extension, collection extension, extension-child collections) with stable ids and contiguous 0-based ordinals.",
            ProductionBoundary.NoProfilePersister,
            "RelationalWriteCreateBaselineTests",
            "Given_A_Postgresql_Relational_Write_Create_Baseline_With_A_Focused_Stable_Key_Fixture",
            "Given_A_Mssql_Relational_Write_Create_Baseline_With_A_Focused_Stable_Key_Fixture",
            [
                "It_returns_insert_success_for_the_repository_create_flow",
                "It_persists_root_and_nested_collection_rows_with_stable_collection_ids",
                "It_persists_root_extensions_collection_extensions_and_extension_child_collections",
            ],
            sharedEntryPoint: "NoProfileCreateBaselineScenarios.AssertInsertSuccess"
                + " + NoProfileCreateBaselineScenarios.AssertRootAndNestedCollectionRows"
                + " + NoProfileCreateBaselineScenarios.AssertRootAndCollectionExtensionAndExtensionChildRows"
        ),
        CreateVariant(
            "InsertSuccess",
            "Create returns InsertSuccess with a persisted Document row.",
            "It_returns_insert_success_for_the_repository_create_flow",
            "NoProfileCreateBaselineScenarios.AssertInsertSuccess"
        ),
        CreateVariant(
            "RootAndNestedCollectionStableIds",
            "Root and nested collection rows persist with unique, positive, stable CollectionItemIds and 0-based ordinals.",
            "It_persists_root_and_nested_collection_rows_with_stable_collection_ids",
            "NoProfileCreateBaselineScenarios.AssertRootAndNestedCollectionRows"
        ),
        CreateVariant(
            "RootAndCollectionExtensionAndExtensionChild",
            "Root extension, collection-aligned extension, and extension-child collection rows all persist on create.",
            "It_persists_root_extensions_collection_extensions_and_extension_child_collections",
            "NoProfileCreateBaselineScenarios.AssertRootAndCollectionExtensionAndExtensionChildRows"
        ),
        // --- NoProfileChangedPutOmissionSemantics + variants --------------------------------
        NoProfile(
            "NoProfileChangedPutOmissionSemantics",
            "Changed PUT bumps ContentVersion, clears omitted inlined columns, and deletes omitted collection-aligned extension scope rows without deleting base rows.",
            ProductionBoundary.NoProfileMerge,
            "RelationalWriteUpdateSemanticsTests",
            "Given_A_Postgresql_Relational_Write_Update_Baseline_With_A_Focused_Stable_Key_Fixture",
            "Given_A_Mssql_Relational_Write_Update_Baseline_With_A_Focused_Stable_Key_Fixture",
            [
                "It_returns_update_success_and_bumps_content_version_for_the_put_flow",
                "It_clears_omitted_inlined_root_columns_instead_of_preserving_the_old_value",
                "It_deletes_omitted_collection_aligned_extension_scope_rows_without_deleting_base_rows",
            ],
            sharedEntryPoint: "NoProfileUpdateSemanticsScenarios.AssertUpdateSuccessAndContentVersionBump"
                + " + NoProfileUpdateSemanticsScenarios.AssertClearedOmittedInlinedColumn"
                + " + NoProfileUpdateSemanticsScenarios.AssertDeletedOmittedAlignedExtensionScope",
            notes: "Core omission semantics are backed by the shared contract; the variants below decompose them."
        ),
        UpdateVariant(
            "ClearedInlinedColumn",
            "A changed PUT clears an omitted inlined root column instead of preserving the old value.",
            "RelationalWriteUpdateSemanticsTests",
            "Given_A_Postgresql_Relational_Write_Update_Baseline_With_A_Focused_Stable_Key_Fixture",
            "Given_A_Mssql_Relational_Write_Update_Baseline_With_A_Focused_Stable_Key_Fixture",
            "It_clears_omitted_inlined_root_columns_instead_of_preserving_the_old_value",
            "NoProfileUpdateSemanticsScenarios.AssertClearedOmittedInlinedColumn"
        ),
        UpdateVariant(
            "DeletedAlignedExtensionScope",
            "A changed PUT deletes omitted collection-aligned extension scope rows without deleting the base rows.",
            "RelationalWriteUpdateSemanticsTests",
            "Given_A_Postgresql_Relational_Write_Update_Baseline_With_A_Focused_Stable_Key_Fixture",
            "Given_A_Mssql_Relational_Write_Update_Baseline_With_A_Focused_Stable_Key_Fixture",
            "It_deletes_omitted_collection_aligned_extension_scope_rows_without_deleting_base_rows",
            "NoProfileUpdateSemanticsScenarios.AssertDeletedOmittedAlignedExtensionScope"
        ),
        UpdateVariant(
            "ContentVersionBump",
            "A changed PUT returns UpdateSuccess and bumps ContentVersion.",
            "RelationalWriteUpdateSemanticsTests",
            "Given_A_Postgresql_Relational_Write_Update_Baseline_With_A_Focused_Stable_Key_Fixture",
            "Given_A_Mssql_Relational_Write_Update_Baseline_With_A_Focused_Stable_Key_Fixture",
            "It_returns_update_success_and_bumps_content_version_for_the_put_flow",
            "NoProfileUpdateSemanticsScenarios.AssertUpdateSuccessAndContentVersionBump"
        ),
        UpdateVariant(
            "DeletedBaseCollectionRows",
            "A changed PUT reduces a large base collection to the retained rows, deleting omitted rows in batches.",
            "RelationalWriteMultiBatchCollectionTests",
            "Given_A_Postgresql_Relational_Write_Multi_Batch_Collection_Delete_Update_With_A_Focused_Stable_Key_Fixture",
            "Given_A_Mssql_Relational_Write_Multi_Batch_Collection_Delete_Update_With_A_Focused_Stable_Key_Fixture",
            "It_returns_update_success_and_persists_only_the_retained_rows_after_delete_batches",
            "NoProfileMultiBatchCollectionScenarios.AssertMultiBatchDeleteUpdateReducedToRetainedRow"
        ),
        UpdateVariant(
            "DeletedAndReplacedChildCollectionRows",
            "A POST-as-update that resolves to an existing document reuses retained child-collection ids and replaces omitted rows across multiple child tables.",
            "RelationalWritePostAsUpdateSmokeTests",
            "Given_A_Postgresql_Relational_Post_As_Update_With_The_Authoritative_Sample_StudentAcademicRecord_Fixture",
            "Given_A_Mssql_Relational_Post_As_Update_With_The_Authoritative_Sample_StudentAcademicRecord_Fixture",
            "It_reuses_stable_collection_item_ids_for_retained_child_rows_and_replaces_omitted_rows",
            "NoProfilePostAsUpdateScenarios.AssertRetainedChildCollectionIdReuse",
            notes: "POST-as-update execution of the shared NoProfileMerge retained-id/omitted-row mechanic (the fixture "
                + "reaches the merge through UpsertDocument, not a changed PUT). The changed-PUT twin of this "
                + "collection-identity behavior is recorded by NoProfileMultiBatchCollection/AuthoritativeChangedPutIdentity."
        ),
        // --- NoProfileWriteBehavior (umbrella) ----------------------------------------------
        NoProfile(
            "NoProfileWriteBehavior",
            "Umbrella no-profile changed-write control path at the persister boundary: the changed-write flow is orchestrated to update success and bumps ContentVersion.",
            ProductionBoundary.NoProfilePersister,
            "RelationalWriteUpdateSemanticsTests",
            "Given_A_Postgresql_Relational_Write_Update_Baseline_With_A_Focused_Stable_Key_Fixture",
            "Given_A_Mssql_Relational_Write_Update_Baseline_With_A_Focused_Stable_Key_Fixture",
            ["It_returns_update_success_and_bumps_content_version_for_the_put_flow"],
            sharedEntryPoint: "NoProfileUpdateSemanticsScenarios.AssertUpdateSuccessAndContentVersionBump",
            notes: "The no-profile _ext create mechanic is recorded on NoProfileWriteBehavior/NoProfileExt; merge-level omission and deletion semantics are owned by NoProfileChangedPutOmissionSemantics at the NoProfileMerge boundary."
        ),
        NoProfile(
            "NoProfileWriteBehavior/OmittedNonCollectionScope",
            "A changed PUT clears an omitted non-collection (inlined) scope rather than preserving hidden data.",
            ProductionBoundary.NoProfileMerge,
            "RelationalWriteUpdateSemanticsTests",
            "Given_A_Postgresql_Relational_Write_Update_Baseline_With_A_Focused_Stable_Key_Fixture",
            "Given_A_Mssql_Relational_Write_Update_Baseline_With_A_Focused_Stable_Key_Fixture",
            ["It_clears_omitted_inlined_root_columns_instead_of_preserving_the_old_value"],
            sharedEntryPoint: "NoProfileUpdateSemanticsScenarios.AssertClearedOmittedInlinedColumn"
        ),
        NoProfile(
            "NoProfileWriteBehavior/NoProfileExt",
            "The control full-surface write persists the no-profile _ext surface (root extension, collection-aligned extension, and extension-child collection rows) on create.",
            ProductionBoundary.NoProfilePersister,
            "RelationalWriteCreateBaselineTests",
            "Given_A_Postgresql_Relational_Write_Create_Baseline_With_A_Focused_Stable_Key_Fixture",
            "Given_A_Mssql_Relational_Write_Create_Baseline_With_A_Focused_Stable_Key_Fixture",
            ["It_persists_root_extensions_collection_extensions_and_extension_child_collections"],
            sharedEntryPoint: "NoProfileCreateBaselineScenarios.AssertRootAndCollectionExtensionAndExtensionChildRows",
            boundaryDetail: "No-profile _ext create surface persisted via NoProfileFullSurfaceCreate/RootAndCollectionExtensionAndExtensionChild; the omitted-aligned-extension deletion on a changed PUT is a merge mechanic cataloged under NoProfileChangedPutOmissionSemantics/DeletedAlignedExtensionScope."
        ),
        // --- FullSurfaceCollectionReorder + variants ----------------------------------------
        NoProfile(
            "FullSurfaceCollectionReorder",
            "No-profile full-surface reorder matches stored rows by semantic identity, reuses CollectionItemIds, and recomputes contiguous 0-based ordinals.",
            ProductionBoundary.NoProfileMerge,
            "RelationalWriteCollectionReorderTests",
            "Given_A_Postgresql_Relational_Write_Full_Surface_Collection_Reorder_With_A_Focused_Stable_Key_Fixture",
            "Given_A_Mssql_Relational_Write_Full_Surface_Collection_Reorder_With_A_Focused_Stable_Key_Fixture",
            [
                "It_returns_update_success_and_bumps_content_version_for_a_full_surface_reorder",
                "It_reuses_collection_item_ids_while_recomputing_ordinals_for_a_full_surface_reorder",
                "It_succeeds_for_a_two_row_swap_under_the_db_sibling_ordinal_uniqueness_constraint",
            ],
            sharedEntryPoint: "NoProfileCollectionReorderScenarios.AssertUpdateSuccessAndContentVersionBump"
                + " + NoProfileCollectionReorderScenarios.AssertReusesCollectionItemIdsWhileRecomputingOrdinals"
                + " + NoProfileCollectionReorderScenarios.AssertTwoRowSwapCommitsUnderSiblingUniqueness"
        ),
        ReorderVariant(
            "OrdinalReuseStableIds",
            "Reorder reuses CollectionItemIds while recomputing ordinals.",
            "It_reuses_collection_item_ids_while_recomputing_ordinals_for_a_full_surface_reorder",
            "NoProfileCollectionReorderScenarios.AssertReusesCollectionItemIdsWhileRecomputingOrdinals"
        ),
        ReorderVariant(
            "TwoRowSwapUnderSiblingUniqueness",
            "A two-row ordinal swap commits under the sibling-ordinal uniqueness constraint.",
            "It_succeeds_for_a_two_row_swap_under_the_db_sibling_ordinal_uniqueness_constraint",
            "NoProfileCollectionReorderScenarios.AssertTwoRowSwapCommitsUnderSiblingUniqueness"
        ),
        ReorderVariant(
            "ContentVersionBump",
            "A full-surface reorder returns UpdateSuccess and bumps ContentVersion.",
            "It_returns_update_success_and_bumps_content_version_for_a_full_surface_reorder",
            "NoProfileCollectionReorderScenarios.AssertUpdateSuccessAndContentVersionBump"
        ),
        // --- NoProfileGuardedNoOp (10 variants) ---------------------------------------------
        NoProfile(
            "NoProfileGuardedNoOp",
            "An unchanged PUT compares the post-merge rowset to current state and skips DML, revalidating freshness before returning no-op: the full persisted rowset (including referential identity), ContentVersion, every stored update-tracking stamp — document and root-table — and the engine's max ChangeVersion allocation all stay unchanged.",
            ProductionBoundary.GuardedNoOp,
            "RelationalWriteGuardedNoOpTests",
            "Given_A_Postgresql_Relational_Guarded_No_Op_Put_With_A_Focused_Stable_Key_Fixture",
            "Given_A_Mssql_Relational_Guarded_No_Op_Put_With_A_Focused_Stable_Key_Fixture",
            [
                "It_returns_update_success_for_an_unchanged_put",
                "It_keeps_rowsets_and_content_version_unchanged_for_a_guarded_no_op_put",
            ],
            sharedEntryPoint: "NoProfileGuardedNoOpScenarios.AssertPutNoOpOutcome"
                + " + NoProfileGuardedNoOpScenarios.AssertRowsetUnchanged",
            boundaryDetail: "RelationalWriteGuardedNoOp + IRelationalWriteFreshnessChecker/IRelationalWriteCurrentStateLoader",
            notes: "This row proves only the unchanged-PUT mechanic its own location executes. POST-as-update, post-reorder, stale-compare, current-state-refresh, and commit-window race semantics (including retained concurrent content stamps) are decomposed into the explicit NoProfileGuardedNoOp/* variant rows with their own entry points."
        ),
        GuardedNoOp(
            "Put",
            "Given_A_Postgresql_Relational_Guarded_No_Op_Put_With_A_Focused_Stable_Key_Fixture",
            "Given_A_Mssql_Relational_Guarded_No_Op_Put_With_A_Focused_Stable_Key_Fixture",
            [
                "It_returns_update_success_for_an_unchanged_put",
                "It_keeps_rowsets_and_content_version_unchanged_for_a_guarded_no_op_put",
            ],
            "NoProfileGuardedNoOpScenarios.AssertPutNoOpOutcome"
                + " + NoProfileGuardedNoOpScenarios.AssertRowsetUnchanged"
        ),
        GuardedNoOp(
            "PostAsUpdate",
            "Given_A_Postgresql_Relational_Guarded_No_Op_Post_As_Update_With_A_Focused_Stable_Key_Fixture",
            "Given_A_Mssql_Relational_Guarded_No_Op_Post_As_Update_With_A_Focused_Stable_Key_Fixture",
            [
                "It_returns_update_success_and_preserves_the_existing_document_for_an_unchanged_post_as_update",
                "It_keeps_rowsets_and_content_version_unchanged_for_a_guarded_no_op_post_as_update",
            ],
            "NoProfileGuardedNoOpScenarios.AssertPostAsUpdateNoOpOutcome"
                + " + NoProfileGuardedNoOpScenarios.AssertRowsetUnchanged"
        ),
        GuardedNoOp(
            "PutCurrentStateRefresh",
            "Given_A_Postgresql_Relational_Guarded_No_Op_Put_When_Current_State_Refreshes_Content_Version",
            "Given_A_Mssql_Relational_Guarded_No_Op_Put_When_Current_State_Refreshes_Content_Version",
            [
                "It_returns_update_success_without_a_repository_retry_when_current_state_refreshes_the_content_version",
                "It_preserves_rowsets_and_avoids_an_extra_content_version_bump_during_the_guarded_no_op_put",
            ],
            "NoProfileGuardedNoOpScenarios.AssertPutNoOpOutcome"
                + " + NoProfileGuardedNoOpScenarios.AssertCurrentStateRefreshObservations"
                + " + NoProfileGuardedNoOpScenarios.AssertRowsetUnchangedExceptOneContentVersionBump"
        ),
        GuardedNoOp(
            "PostAsUpdateCurrentStateRefresh",
            "Given_A_Postgresql_Relational_Guarded_No_Op_Post_As_Update_When_Current_State_Refreshes_Content_Version",
            "Given_A_Mssql_Relational_Guarded_No_Op_Post_As_Update_When_Current_State_Refreshes_Content_Version",
            [
                "It_returns_update_success_without_a_repository_retry_when_post_as_update_refreshes_current_state_freshness",
                "It_preserves_rowsets_and_avoids_an_extra_content_version_bump_during_the_guarded_no_op_post_as_update",
            ],
            "NoProfileGuardedNoOpScenarios.AssertPostAsUpdateNoOpOutcome"
                + " + NoProfileGuardedNoOpScenarios.AssertCurrentStateRefreshObservations"
                + " + NoProfileGuardedNoOpScenarios.AssertRowsetUnchangedExceptOneContentVersionBump"
        ),
        GuardedNoOp(
            "PutAfterReorder",
            "Given_A_Postgresql_Relational_Guarded_No_Op_Put_After_A_Full_Surface_Collection_Reorder_With_A_Focused_Stable_Key_Fixture",
            "Given_A_Mssql_Relational_Guarded_No_Op_Put_After_A_Full_Surface_Collection_Reorder_With_A_Focused_Stable_Key_Fixture",
            [
                "It_returns_update_success_for_an_unchanged_put_after_reorder",
                "It_keeps_rowsets_and_content_version_unchanged_for_a_guarded_no_op_put_after_reorder",
            ],
            "NoProfileGuardedNoOpScenarios.AssertPutNoOpOutcome"
                + " + NoProfileGuardedNoOpScenarios.AssertRowsetUnchangedAfterReorder"
        ),
        GuardedNoOp(
            "PostAsUpdateAfterReorder",
            "Given_A_Postgresql_Relational_Guarded_No_Op_Post_As_Update_After_A_Full_Surface_Collection_Reorder_With_A_Focused_Stable_Key_Fixture",
            "Given_A_Mssql_Relational_Guarded_No_Op_Post_As_Update_After_A_Full_Surface_Collection_Reorder_With_A_Focused_Stable_Key_Fixture",
            [
                "It_returns_update_success_and_preserves_the_existing_document_for_an_unchanged_post_as_update_after_reorder",
                "It_keeps_rowsets_and_content_version_unchanged_for_a_guarded_no_op_post_as_update_after_reorder",
            ],
            "NoProfileGuardedNoOpScenarios.AssertPostAsUpdateNoOpOutcome"
                + " + NoProfileGuardedNoOpScenarios.AssertRowsetUnchangedAfterReorder"
        ),
        GuardedNoOp(
            "StalePut",
            "Given_A_Postgresql_Relational_Stale_Guarded_No_Op_Put_With_A_Focused_Stable_Key_Fixture",
            "Given_A_Mssql_Relational_Stale_Guarded_No_Op_Put_With_A_Focused_Stable_Key_Fixture",
            [
                "It_retries_and_returns_update_success_after_the_no_op_compare_goes_stale",
                "It_preserves_the_rowsets_but_keeps_the_concurrent_content_version_bump",
            ],
            "NoProfileGuardedNoOpScenarios.AssertPutNoOpOutcome"
                + " + NoProfileGuardedNoOpScenarios.AssertRowsetUnchangedExceptOneContentVersionBump"
        ),
        GuardedNoOp(
            "StalePostAsUpdate",
            "Given_A_Postgresql_Relational_Stale_Guarded_No_Op_Post_As_Update_With_A_Focused_Stable_Key_Fixture",
            "Given_A_Mssql_Relational_Stale_Guarded_No_Op_Post_As_Update_With_A_Focused_Stable_Key_Fixture",
            [
                "It_retries_and_returns_update_success_for_a_stale_post_as_update_no_op_compare",
                "It_preserves_the_existing_rowsets_but_keeps_the_concurrent_content_version_bump",
            ],
            "NoProfileGuardedNoOpScenarios.AssertPostAsUpdateNoOpOutcome"
                + " + NoProfileGuardedNoOpScenarios.AssertRowsetUnchangedExceptOneContentVersionBump"
        ),
        GuardedNoOp(
            "PutCommitWindowRace",
            "Given_A_Postgresql_Relational_Guarded_No_Op_Put_With_A_Commit_Window_Race",
            "Given_A_Mssql_Relational_Guarded_No_Op_Put_With_A_Commit_Window_Race",
            [
                "It_retries_the_no_op_after_the_commit_window_race_and_returns_update_success",
                "It_preserves_rowsets_but_keeps_the_concurrent_content_version_bump",
            ],
            "NoProfileGuardedNoOpScenarios.AssertPutNoOpOutcome"
                + " + NoProfileGuardedNoOpScenarios.AssertCommitWindowFreshnessObservations"
                + " + NoProfileGuardedNoOpScenarios.AssertRowsetUnchangedExceptOneContentVersionBump",
            diff: new DialectDifference(
                "PostgreSQL snapshot reads let the first freshness check run while the competing bump is still uncommitted, so its twin starts the competing transaction before the repository call; SQL Server READ COMMITTED locking reads would block the repository's initial document lookup on the competing X-lock, so its twin begins the competing transaction inside the first freshness-check invocation and the inner freshness read blocks on that X-lock until the competing commit releases.",
                "Commit-window scheduling and blocking behavior differ by dialect; behavioral parity is the unchanged observable outcome — freshness observations [false, true], exactly one retry, and the competing committed content version and stamp preserved."
            )
        ),
        GuardedNoOp(
            "PostAsUpdateCommitWindowRace",
            "Given_A_Postgresql_Relational_Guarded_No_Op_Post_As_Update_With_A_Commit_Window_Race",
            "Given_A_Mssql_Relational_Guarded_No_Op_Post_As_Update_With_A_Commit_Window_Race",
            [
                "It_retries_the_no_op_after_the_commit_window_race_and_preserves_the_existing_document",
                "It_preserves_existing_rowsets_but_keeps_the_concurrent_content_version_bump",
            ],
            "NoProfileGuardedNoOpScenarios.AssertPostAsUpdateNoOpOutcome"
                + " + NoProfileGuardedNoOpScenarios.AssertCommitWindowFreshnessObservations"
                + " + NoProfileGuardedNoOpScenarios.AssertRowsetUnchangedExceptOneContentVersionBump",
            diff: new DialectDifference(
                "PostgreSQL snapshot reads let the first freshness check run while the competing bump is still uncommitted, so its twin starts the competing transaction before the repository call; SQL Server READ COMMITTED locking reads would block the repository's initial document lookup on the competing X-lock, so its twin begins the competing transaction inside the first freshness-check invocation and the inner freshness read blocks on that X-lock until the competing commit releases.",
                "Commit-window scheduling and blocking behavior differ by dialect; behavioral parity is the unchanged observable outcome — freshness observations [false, true], exactly one retry, and the competing committed content version and stamp preserved."
            )
        ),
        // --- NoProfileMultiBatchCollection + variants ---------------------------------------
        NoProfile(
            "NoProfileMultiBatchCollection",
            "A collection create exceeding the compiled MaxRowsPerBatch persists the full requested collection (every row checked in order) and partitions its id-reservation and insert commands at the compiled MaxRowsPerBatch / ParametersPerRow limits.",
            ProductionBoundary.BatchSqlEmitter,
            "RelationalWriteMultiBatchCollectionTests",
            "Given_A_Postgresql_Relational_Write_Multi_Batch_Collection_Create_With_A_Focused_Stable_Key_Fixture",
            "Given_A_Mssql_Relational_Write_Multi_Batch_Collection_Create_With_A_Focused_Stable_Key_Fixture",
            [
                "It_returns_insert_success_and_persists_the_full_large_collection",
                "It_partitions_collection_id_reservation_and_insert_commands_using_the_compiled_batch_limit",
            ],
            sharedEntryPoint: "NoProfileMultiBatchCollectionScenarios.AssertLargeCollectionCreatePersisted"
                + " + NoProfileMultiBatchCollectionScenarios.AssertCreateBatchPartitions",
            boundaryDetail: "WritePlanBatchSqlEmitter / PlanWriteBatchingConventions",
            notes: "This row proves only the multi-batch create mechanic its own location executes. Update/delete batching, aligned-extension batching, parameter pressure, and changed-write identity are decomposed into the explicit NoProfileMultiBatchCollection/* variant rows with their own entry points.",
            diff: new DialectDifference(
                "PostgreSQL reserves collection ids via generate_series and caps at 65535 parameters / 1000 rows; SQL Server has no generate_series equivalent and caps at 2100 parameters / 1000 rows.",
                "Dialect parameter limits and id-reservation strategy differ; behavioral parity is the persisted rowset, contiguous 0-based ordinals, and batch partition counts, not the SQL text."
            )
        ),
        NoProfile(
            "NoProfileMultiBatchCollection/Create",
            "Multi-batch create persists the full large collection and partitions id-reservation and insert commands at the compiled batch limit.",
            ProductionBoundary.BatchSqlEmitter,
            "RelationalWriteMultiBatchCollectionTests",
            "Given_A_Postgresql_Relational_Write_Multi_Batch_Collection_Create_With_A_Focused_Stable_Key_Fixture",
            "Given_A_Mssql_Relational_Write_Multi_Batch_Collection_Create_With_A_Focused_Stable_Key_Fixture",
            [
                "It_returns_insert_success_and_persists_the_full_large_collection",
                "It_partitions_collection_id_reservation_and_insert_commands_using_the_compiled_batch_limit",
            ],
            sharedEntryPoint: "NoProfileMultiBatchCollectionScenarios.AssertLargeCollectionCreatePersisted"
                + " + NoProfileMultiBatchCollectionScenarios.AssertCreateBatchPartitions",
            diff: new DialectDifference(
                "PostgreSQL reserves collection ids via generate_series and caps at 65535 parameters / 1000 rows; SQL Server has no generate_series equivalent for id reservation and caps at 2100 parameters / 1000 rows.",
                "Dialect id-reservation strategy and parameter limits shape the compiled create batches; behavioral parity is the persisted rowset, contiguous 0-based ordinals, and batch partition counts, not the SQL text."
            )
        ),
        NoProfile(
            "NoProfileMultiBatchCollection/DeleteUpdate",
            "Multi-batch delete/update reduces a large collection, partitioning delete commands at the compiled batch limit.",
            ProductionBoundary.BatchSqlEmitter,
            "RelationalWriteMultiBatchCollectionTests",
            "Given_A_Postgresql_Relational_Write_Multi_Batch_Collection_Delete_Update_With_A_Focused_Stable_Key_Fixture",
            "Given_A_Mssql_Relational_Write_Multi_Batch_Collection_Delete_Update_With_A_Focused_Stable_Key_Fixture",
            [
                "It_returns_update_success_and_persists_only_the_retained_rows_after_delete_batches",
                "It_partitions_collection_delete_commands_using_the_compiled_batch_limit",
            ],
            sharedEntryPoint: "NoProfileMultiBatchCollectionScenarios.AssertMultiBatchDeleteUpdateReducedToRetainedRow"
                + " + NoProfileMultiBatchCollectionScenarios.AssertDeleteBatchPartitions"
        ),
        NoProfile(
            "NoProfileMultiBatchCollection/AlignedExtensionCreate",
            "Multi-batch create of a large collection-aligned extension scope, aligned to base row ids and partitioned at the compiled batch limit.",
            ProductionBoundary.BatchSqlEmitter,
            "RelationalWriteMultiBatchCollectionTests",
            "Given_A_Postgresql_Relational_Write_Multi_Batch_Collection_Aligned_Extension_Create_With_A_Focused_Stable_Key_Fixture",
            "Given_A_Mssql_Relational_Write_Multi_Batch_Collection_Aligned_Extension_Create_With_A_Focused_Stable_Key_Fixture",
            [
                "It_returns_insert_success_and_persists_the_full_large_collection_aligned_extension_scope",
                "It_partitions_collection_aligned_extension_insert_commands_using_the_compiled_batch_limit",
            ],
            sharedEntryPoint: "NoProfileMultiBatchCollectionScenarios.AssertLargeCollectionAlignedExtensionCreatePersisted"
                + " + NoProfileMultiBatchCollectionScenarios.AssertAlignedExtensionInsertBatchPartitions",
            diff: new DialectDifference(
                "PostgreSQL caps at 65535 parameters / 1000 rows; SQL Server caps at 2100 parameters / 1000 rows (aligned extension rows are keyed to base collection ids, so no id reservation applies on either dialect).",
                "Dialect parameter limits shape the compiled aligned-extension insert batches; behavioral parity is the persisted rowset and batch partition counts, not the SQL text."
            )
        ),
        NoProfile(
            "NoProfileMultiBatchCollection/AuthoritativeParameterPressure",
            "Authoritative StudentAcademicRecord large-collection create exercises real parameter pressure (28 rows, >300 insert parameters).",
            ProductionBoundary.BatchSqlEmitter,
            "RelationalWritePostAsUpdateSmokeTests",
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentAcademicRecord_Fixture",
            "Given_A_Mssql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentAcademicRecord_Fixture",
            [
                "It_persists_authoritative_student_academic_record_root_extension_and_large_collection_rows_on_create",
                "It_uses_a_payload_large_enough_to_exercise_real_parameter_pressure",
            ],
            sharedEntryPoint: "NoProfileMultiBatchCollectionScenarios.AssertAuthoritativeLargeCollectionCreatePersisted"
                + " + NoProfileMultiBatchCollectionScenarios.AssertParameterPressurePayload"
        ),
        NoProfile(
            "NoProfileMultiBatchCollection/AuthoritativeChangedPutIdentity",
            "Authoritative StudentAcademicRecord changed PUT reuses stable collection item ids across the large-collection tables.",
            ProductionBoundary.NoProfileMerge,
            "RelationalWritePostAsUpdateSmokeTests",
            "Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentAcademicRecord_Fixture",
            "Given_A_Mssql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentAcademicRecord_Fixture",
            ["It_reuses_stable_collection_item_ids_across_large_collection_tables_for_a_changed_put"],
            sharedEntryPoint: "NoProfileMultiBatchCollectionScenarios.AssertAuthoritativeLargeCollectionChangedPutIdentity"
                + " + NoProfileMultiBatchCollectionScenarios.AssertChangedCollectionReusesRetainedIdsAndReplacesOthers"
        ),
        NoProfile(
            "NoProfileMultiBatchCollection/ChangedUpdateBatchPartitions",
            "A changed PUT that replaces a non-identity attribute on more than MaxRowsPerBatch existing rows (keeping each city and order) partitions the collection update-by-stable-row-identity commands into two batches at the compiled limit, preserving the full rowset, stable ids, parent, and contiguous ordinals.",
            ProductionBoundary.BatchSqlEmitter,
            "RelationalWriteMultiBatchCollectionTests",
            "Given_A_Postgresql_Relational_Write_Multi_Batch_Collection_Changed_Descriptor_Update_With_A_Focused_Stable_Key_Fixture",
            "Given_A_Mssql_Relational_Write_Multi_Batch_Collection_Changed_Descriptor_Update_With_A_Focused_Stable_Key_Fixture",
            [
                "It_returns_update_success_and_applies_the_changed_descriptor_to_every_row",
                "It_partitions_collection_update_commands_using_the_compiled_batch_limit",
            ],
            diff: new DialectDifference(
                "PostgreSQL caps at 65535 parameters / 1000 rows; SQL Server caps at 2100 parameters / 1000 rows (updates target existing stable row identities, so no id reservation applies on either dialect).",
                "Dialect parameter limits shape the compiled update-by-stable-row-identity batches; behavioral parity is the persisted rowset and batch partition counts, not the SQL text."
            ),
            sharedEntryPoint: "NoProfileMultiBatchCollectionScenarios.AssertLargeCollectionChangedDescriptorUpdatePersisted"
                + " + NoProfileMultiBatchCollectionScenarios.AssertUpdateBatchPartitions",
            boundaryDetail: "RelationalWriteNoProfilePersister batching through WritePlanBatchSqlEmitter.EmitCollectionUpdateByStableRowIdentityBatch"
        ),
        // --- NoProfilePostAsUpdate + variants -----------------------------------------------
        NoProfile(
            "NoProfilePostAsUpdate",
            "POST that resolves to an existing document (FocusedStableKey) updates in place, preserving the document row and inserting no duplicate rows.",
            ProductionBoundary.NoProfilePersister,
            "RelationalWritePostAsUpdateSmokeTests",
            "Given_A_Postgresql_Relational_Post_As_Update_With_A_Focused_Stable_Key_Fixture",
            "Given_A_Mssql_Relational_Post_As_Update_With_A_Focused_Stable_Key_Fixture",
            [
                "It_returns_update_success_and_preserves_the_existing_document_row_for_post_as_update",
                "It_applies_changed_full_surface_state_without_inserting_new_rows_for_post_as_update",
            ],
            sharedEntryPoint: "NoProfilePostAsUpdateScenarios.AssertUpdatedExistingDocumentInPlace"
                + " + NoProfilePostAsUpdateScenarios.AssertFocusedFullSurfaceStateApplied"
        ),
        NoProfile(
            "NoProfilePostAsUpdate/FocusedStableKey",
            "POST-as-update on the focused stable-key fixture preserves the existing document row and applies changed full-surface state without inserting new rows.",
            ProductionBoundary.NoProfilePersister,
            "RelationalWritePostAsUpdateSmokeTests",
            "Given_A_Postgresql_Relational_Post_As_Update_With_A_Focused_Stable_Key_Fixture",
            "Given_A_Mssql_Relational_Post_As_Update_With_A_Focused_Stable_Key_Fixture",
            [
                "It_returns_update_success_and_preserves_the_existing_document_row_for_post_as_update",
                "It_applies_changed_full_surface_state_without_inserting_new_rows_for_post_as_update",
            ],
            sharedEntryPoint: "NoProfilePostAsUpdateScenarios.AssertUpdatedExistingDocumentInPlace"
                + " + NoProfilePostAsUpdateScenarios.AssertFocusedFullSurfaceStateApplied"
        ),
        NoProfile(
            "NoProfilePostAsUpdate/ImmutableIdentityRejected",
            "POST-as-update that changes an immutable identity is rejected with UpsertFailureImmutableIdentity and commits no row changes: the document row including ContentVersion and every stored update-tracking stamp (IdentityVersion, ContentLastModifiedAt, IdentityLastModifiedAt, CreatedAt), the root, the base and aligned-extension collections, and the referential identity are all unchanged.",
            ProductionBoundary.IdentityStability,
            "RelationalWritePostAsUpdateSmokeTests",
            "Given_A_Postgresql_Relational_Post_As_Update_Immutable_Identity_Change_With_A_Focused_Stable_Key_Fixture",
            "Given_A_Mssql_Relational_Post_As_Update_Immutable_Identity_Change_With_A_Focused_Stable_Key_Fixture",
            [
                "It_returns_explicit_immutable_identity_failure_for_post_as_update",
                "It_does_not_commit_row_changes_for_rejected_post_as_update",
            ],
            sharedEntryPoint: "NoProfilePostAsUpdateScenarios.AssertImmutableIdentityRejected"
                + " + NoProfilePostAsUpdateScenarios.AssertRejectedPostAsUpdateCommittedNoChanges",
            boundaryDetail: "RelationalWriteIdentityStability.TryBuildFailureResult"
        ),
        NoProfile(
            "NoProfilePostAsUpdate/CreateRaceConvertedToUpdate",
            "A stale create candidate converts to POST-as-update after a competing create commits, applying last-writer state without duplicate rows.",
            ProductionBoundary.NoProfilePersister,
            "RelationalWritePostAsUpdateSmokeTests",
            "Given_A_Postgresql_Relational_Post_Create_Race_With_The_Focused_Stable_Key_Fixture",
            "Given_A_Mssql_Relational_Post_Create_Race_With_The_Focused_Stable_Key_Fixture",
            [
                "It_converts_the_stale_create_candidate_into_post_as_update_after_the_competing_create_commits",
                "It_applies_last_writer_state_to_the_existing_document_instead_of_creating_duplicate_rows",
            ],
            sharedEntryPoint: "NoProfilePostAsUpdateScenarios.AssertStaleCreateConvertedToPostAsUpdate"
                + " + NoProfilePostAsUpdateScenarios.AssertLastWriterStateApplied"
        ),
        NoProfile(
            "NoProfilePostAsUpdate/AuthoritativeDs52SchoolYearType",
            "Authoritative DS-5.2 SchoolYearType POST-as-update preserves the existing document uuid and updates the row in place.",
            ProductionBoundary.NoProfilePersister,
            "RelationalWritePostAsUpdateSmokeTests",
            "Given_A_Postgresql_Relational_Post_As_Update_With_The_Authoritative_Ds52_SchoolYearType_Fixture",
            "Given_A_Mssql_Relational_Post_As_Update_With_The_Authoritative_Ds52_SchoolYearType_Fixture",
            [
                "It_returns_update_success_for_authoritative_post_as_update_and_preserves_the_existing_document_uuid",
                "It_updates_the_authoritative_ds52_row_in_place_for_post_as_update",
            ],
            sharedEntryPoint: "NoProfilePostAsUpdateScenarios.AssertUpdatedExistingDocumentInPlace"
                + " + NoProfilePostAsUpdateScenarios.AssertAuthoritativeSchoolYearTypeRowInPlace"
        ),
        NoProfile(
            "NoProfilePostAsUpdate/AuthoritativeStudentAcademicRecord",
            "Authoritative StudentAcademicRecord POST-as-update preserves the existing document uuid and updates root/extension rows in place.",
            ProductionBoundary.NoProfilePersister,
            "RelationalWritePostAsUpdateSmokeTests",
            "Given_A_Postgresql_Relational_Post_As_Update_With_The_Authoritative_Sample_StudentAcademicRecord_Fixture",
            "Given_A_Mssql_Relational_Post_As_Update_With_The_Authoritative_Sample_StudentAcademicRecord_Fixture",
            [
                "It_returns_update_success_for_authoritative_post_as_update_and_preserves_the_existing_document_uuid",
                "It_updates_root_and_extension_rows_in_place_for_authoritative_student_academic_record_post_as_update",
            ],
            sharedEntryPoint: "NoProfilePostAsUpdateScenarios.AssertUpdatedExistingDocumentInPlace"
                + " + NoProfilePostAsUpdateScenarios.AssertAuthoritativeRootAndExtensionInPlace"
        ),
        // --- NoProfileRollbackSafety + variants ---------------------------------------------
        NoProfile(
            "NoProfileRollbackSafety",
            "A failure injected at the last write of a full-surface create rolls the whole request back to its exact pre-state: document and tracking stamps, referential identity, root, nested child/grandchild, root extension, aligned extension, extension-child and visit rows, and tracked-change rowsets all return to empty.",
            ProductionBoundary.NoProfilePersister,
            "RelationalWriteRollbackSafetyTests",
            "Given_A_Postgresql_Relational_Write_Create_Failure_After_Early_Writes_With_A_Focused_Stable_Key_Fixture",
            "Given_A_Mssql_Relational_Write_Create_Failure_After_Early_Writes_With_A_Focused_Stable_Key_Fixture",
            [
                "It_surfaces_the_injected_failure_only_after_the_early_write_commands_are_attempted",
                "It_leaves_no_partial_relational_state_after_the_transaction_rolls_back",
            ],
            sharedEntryPoint: "NoProfileAtomicRollbackAssertions.AssertInjectedFailureAfterOrderedEarlyWrites"
                + " + NoProfileAtomicRollbackAssertions.AssertFullSurfaceRollbackToPreState"
        ),
        NoProfile(
            "NoProfileRollbackSafety/CreateFailureAfterEarlyWrites",
            "An injected failure at the write plan's final aligned-extension address write rolls back fully after every earlier full-surface write category was attempted in plan order; the post-rollback snapshot equals the empty pre-state across document, referential-identity, root, child/grandchild, extension, aligned-extension, extension-child, visit, and tracked-change surfaces.",
            ProductionBoundary.NoProfilePersister,
            "RelationalWriteRollbackSafetyTests",
            "Given_A_Postgresql_Relational_Write_Create_Failure_After_Early_Writes_With_A_Focused_Stable_Key_Fixture",
            "Given_A_Mssql_Relational_Write_Create_Failure_After_Early_Writes_With_A_Focused_Stable_Key_Fixture",
            [
                "It_surfaces_the_injected_failure_only_after_the_early_write_commands_are_attempted",
                "It_leaves_no_partial_relational_state_after_the_transaction_rolls_back",
            ],
            sharedEntryPoint: "NoProfileAtomicRollbackAssertions.AssertInjectedFailureAfterOrderedEarlyWrites"
                + " + NoProfileAtomicRollbackAssertions.AssertFullSurfaceRollbackToPreState"
        ),
    ];

    // One row of the no-profile matrix, covered on both engines. The stem derives the per-engine
    // source files ("Postgresql{stem}.cs" / "Mssql{stem}.cs") and both engines execute the
    // identically named test methods; the fixtures are recorded independently per engine.
    private static ParityScenario NoProfile(
        string id,
        string contract,
        ProductionBoundary boundary,
        string stem,
        string pgFixture,
        string mssqlFixture,
        string[] methods,
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
            PgsqlLocations = [PgLoc(stem, pgFixture, methods)],
            MssqlLocations = [MsLoc(stem, mssqlFixture, methods)],
            PgsqlCoverage = EngineCoverage.Covered,
            MssqlCoverage = EngineCoverage.Covered,
            DialectDifference = diff,
            Classification = ParityClassification.Both,
            Notes = notes,
        };

    // Every variant names its own SharedEntryPoint: belonging to a canonical family does not resolve a contract
    // (a shared production boundary does not imply running the family's assertion helpers), so each variant must
    // declare the exact reusable helper(s) its adapter executes.
    private static ParityScenario CreateVariant(
        string variant,
        string contract,
        string method,
        string sharedEntryPoint
    ) =>
        NoProfile(
            $"NoProfileFullSurfaceCreate/{variant}",
            contract,
            ProductionBoundary.NoProfilePersister,
            "RelationalWriteCreateBaselineTests",
            "Given_A_Postgresql_Relational_Write_Create_Baseline_With_A_Focused_Stable_Key_Fixture",
            "Given_A_Mssql_Relational_Write_Create_Baseline_With_A_Focused_Stable_Key_Fixture",
            [method],
            sharedEntryPoint: sharedEntryPoint
        );

    private static ParityScenario UpdateVariant(
        string variant,
        string contract,
        string stem,
        string pgFixture,
        string mssqlFixture,
        string method,
        string sharedEntryPoint,
        string? notes = null
    ) =>
        NoProfile(
            $"NoProfileChangedPutOmissionSemantics/{variant}",
            contract,
            ProductionBoundary.NoProfileMerge,
            stem,
            pgFixture,
            mssqlFixture,
            [method],
            sharedEntryPoint: sharedEntryPoint,
            notes: notes
        );

    private static ParityScenario ReorderVariant(
        string variant,
        string contract,
        string method,
        string sharedEntryPoint
    ) =>
        NoProfile(
            $"FullSurfaceCollectionReorder/{variant}",
            contract,
            ProductionBoundary.NoProfileMerge,
            "RelationalWriteCollectionReorderTests",
            "Given_A_Postgresql_Relational_Write_Full_Surface_Collection_Reorder_With_A_Focused_Stable_Key_Fixture",
            "Given_A_Mssql_Relational_Write_Full_Surface_Collection_Reorder_With_A_Focused_Stable_Key_Fixture",
            [method],
            sharedEntryPoint: sharedEntryPoint
        );

    private static ParityScenario GuardedNoOp(
        string variant,
        string pgFixture,
        string mssqlFixture,
        string[] methods,
        string sharedEntryPoint,
        DialectDifference? diff = null
    ) =>
        NoProfile(
            $"NoProfileGuardedNoOp/{variant}",
            $"Guarded no-op variant: {variant}.",
            ProductionBoundary.GuardedNoOp,
            "RelationalWriteGuardedNoOpTests",
            pgFixture,
            mssqlFixture,
            methods,
            sharedEntryPoint: sharedEntryPoint,
            diff: diff
        );
}
