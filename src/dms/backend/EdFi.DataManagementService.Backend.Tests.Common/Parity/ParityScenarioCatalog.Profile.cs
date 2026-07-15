// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;

namespace EdFi.DataManagementService.Backend.Tests.Common.Parity;

public static partial class ParityScenarioCatalog
{
    /// <summary>
    /// DMS-1124 profile-aware relational-write scenarios, mapped rather than rewritten. All are
    /// Both/Covered on the profile persist-executor boundary with mirrored PostgreSQL and SQL
    /// Server fixtures (fixture names are recorded per engine because they are not mechanically
    /// mirrorable; the per-fixture test-method names are identical across engines). Three
    /// documented creatability variants exist only as synthesizer unit tests and are recorded Na.
    /// </summary>
    internal static readonly ImmutableArray<ParityScenario> ProfileScenarios =
    [
        // --- ProfileRootCreateRejectedWhenNonCreatable --------------------------------------
        Profile(
            "ProfileRootCreateRejectedWhenNonCreatable",
            "A profiled POST create whose root is not creatable is rejected as a profile data-policy failure.",
            "ProfileExecutorRoutingTests",
            "Given_A_Profiled_Post_Create_Where_Root_Is_Not_Creatable",
            "Given_A_Mssql_Profiled_Post_Create_Where_Root_Is_Not_Creatable",
            ["It_returns_profile_data_policy_failure_for_creatability_rejection"]
        ),
        // --- ProfileHiddenInlinedColumnPreservation -----------------------------------------
        Profile(
            "ProfileHiddenInlinedColumnPreservation",
            "A profiled PUT that never names a hidden inlined scalar preserves its stored value.",
            "ProfileRootTableOnlyMergeTests",
            "Given_A_Profiled_Put_With_Hidden_Inlined_Column_Preservation",
            "Given_A_Mssql_Profiled_Put_With_Hidden_Inlined_Column_Preservation",
            ["It_preserves_the_hidden_scalar_value"]
        ),
        Profile(
            "ProfileHiddenInlinedColumnPreservation/RootScopePreservedText",
            "A profiled PUT preserves the hidden preserved-text on a root scope while updating the clearable text.",
            "ProfileRootTableOnlyMergeFixtureTests",
            "Given_A_Profiled_Put_With_Hidden_Inlined_PreservedText_On_Root_Scope",
            "Given_A_Mssql_Profiled_Put_With_Hidden_Inlined_PreservedText_On_Root_Scope",
            ["It_preserves_profile_scope_preserved_text"]
        ),
        Profile(
            "ProfileHiddenInlinedColumnPreservation/HiddenMemberPathOnVisibleChild",
            "A profiled PUT preserves a stored value at a hidden member path on an otherwise visible child.",
            "ProfileNestedCollectionMergeTests",
            "Given_a_ProfileNested_put_request_with_a_hidden_member_path_on_a_visible_child",
            "Given_a_ProfileNested_put_request_with_a_hidden_member_path_on_a_visible_child",
            ["It_preserves_the_stored_value_at_the_hidden_path"]
        ),
        Profile(
            "ProfileHiddenInlinedColumnPreservation/KeyUnifiedCanonicalStorage",
            "A profiled PUT with a hidden key-unification member preserves the unified descriptor canonical FK.",
            "ProfileRootTableOnlyMergeFixtureTests",
            "Given_ProfiledRootOnly_KeyUnificationHiddenMember_AgreementSucceeds",
            "Given_Mssql_ProfiledRootOnly_KeyUnificationHiddenMember_AgreementSucceeds",
            ["It_preserves_unified_descriptor_canonical_fk"]
        ),
        Profile(
            "ProfileHiddenInlinedColumnPreservation/SyntheticPresenceFlag",
            "A profiled PUT preserves the synthetic presence flags for hidden descriptor members.",
            "ProfileRootTableOnlyMergeFixtureTests",
            "Given_ProfiledRootOnly_KeyUnificationHiddenMember_AgreementSucceeds",
            "Given_Mssql_ProfiledRootOnly_KeyUnificationHiddenMember_AgreementSucceeds",
            [
                "It_preserves_primary_descriptor_synthetic_presence",
                "It_preserves_secondary_descriptor_synthetic_presence",
            ]
        ),
        Profile(
            "ProfileHiddenInlinedColumnPreservation/HiddenReferenceBinding",
            "A profiled PUT preserves a hidden document-reference FK and its propagated identity.",
            "ProfileRootTableOnlyMergeFixtureTests",
            "Given_ProfiledRootOnly_HiddenSubReferenceMember_PreservesFKAndPropagatedIdentity",
            "Given_Mssql_ProfiledRootOnly_HiddenSubReferenceMember_PreservesFKAndPropagatedIdentity",
            ["It_preserves_student_reference_document_id", "It_preserves_student_reference_student_unique_id"]
        ),
        // --- ProfileVisibleButAbsentNonCollectionScope --------------------------------------
        Profile(
            "ProfileVisibleButAbsentNonCollectionScope",
            "A profiled PUT that omits a visible inlined scope clears the clearable value and preserves the hidden value.",
            "ProfileRootTableOnlyMergeFixtureTests",
            "Given_A_Profiled_Put_With_VisibleAbsent_Inlined_Scope_Clears_Clearable_And_Preserves_Hidden",
            "Given_A_Mssql_Profiled_Put_With_VisibleAbsent_Inlined_Scope_Clears_Clearable_And_Preserves_Hidden",
            ["It_clears_profile_scope_clearable_text", "It_preserves_profile_scope_preserved_text"]
        ),
        Profile(
            "ProfileVisibleButAbsentNonCollectionScope/SeparateTable",
            "A profiled PUT that omits a visible separate-table scope deletes that row.",
            "ProfileSeparateTableMergeFixtureTests",
            "Given_A_ProfiledUpdate_With_VisibleAbsent_SeparateTableScope_DeletesIt",
            "Given_A_Mssql_ProfiledUpdate_With_VisibleAbsent_SeparateTableScope_DeletesIt",
            ["It_deletes_the_separate_table_row"]
        ),
        // --- ProfileHiddenExtensionRowPreservation ------------------------------------------
        Profile(
            "ProfileHiddenExtensionRowPreservation",
            "A profiled PUT preserves a hidden extension row on a separate table.",
            "ProfileSeparateTableMergeFixtureTests",
            "Given_A_ProfiledUpdate_With_Hidden_Extension_Row_PreservesIt",
            "Given_A_Mssql_ProfiledUpdate_With_Hidden_Extension_Row_PreservesIt",
            ["It_preserves_the_hidden_extension_row"]
        ),
        Profile(
            "ProfileHiddenExtensionRowPreservation/WholeSeparateTableScope",
            "A profiled PUT preserves a wholly hidden separate-table scope's scalars untouched.",
            "ProfileSeparateTableMergeFixtureTests",
            "Given_A_ProfiledUpdate_WithHiddenWholeSeparateTableScope_PreservesRow",
            "Given_A_Mssql_ProfiledUpdate_WithHiddenWholeSeparateTableScope_PreservesRow",
            ["It_preserves_both_separate_table_scalars_untouched"]
        ),
        Profile(
            "ProfileHiddenExtensionRowPreservation/HiddenDescriptorFkOnSeparateTable",
            "A profiled PUT preserves a hidden descriptor FK on a separate table while updating the visible scalar.",
            "ProfileSeparateTableMergeFixtureTests",
            "Given_A_ProfiledUpdate_WithHiddenDescriptorFKOn_SeparateTable_PreservesFK",
            "Given_A_Mssql_ProfiledUpdate_WithHiddenDescriptorFKOn_SeparateTable_PreservesFK",
            ["It_preserves_the_hidden_descriptor_fk"]
        ),
        // --- ProfileVisibleRowUpdateWithHiddenRowPreservation -------------------------------
        Profile(
            "ProfileVisibleRowUpdateWithHiddenRowPreservation",
            "A profiled PUT updates the visible collection rows and preserves the hidden sibling rows.",
            "ProfileTopLevelCollectionMergeTests",
            "Given_A_Postgresql_Profiled_TopLevelCollection_Merge",
            "Given_A_Mssql_Profiled_TopLevelCollection_Merge",
            ["It_updates_visible_rows_and_preserves_hidden_rows"]
        ),
        Profile(
            "ProfileVisibleRowUpdateWithHiddenRowPreservation/NoPreviouslyVisibleRows",
            "A profiled PUT inserts visible rows when none were previously visible and preserves hidden rows.",
            "ProfileTopLevelCollectionMergeTests",
            "Given_A_Postgresql_Profiled_TopLevelCollection_Merge",
            "Given_A_Mssql_Profiled_TopLevelCollection_Merge",
            ["It_inserts_when_no_rows_were_previously_visible_and_preserves_hidden_rows"]
        ),
        Profile(
            "ProfileVisibleRowUpdateWithHiddenRowPreservation/InterleavedUpdatePlusInsert",
            "A reference-backed profiled PUT updates a matched visible row in place and inserts a new creatable visible item.",
            "ProfileTopLevelCollectionReferenceBackedMergeTests",
            "Given_A_Postgresql_Profiled_TopLevelCollection_ReferenceBackedIdentity_Merge",
            "Given_A_Mssql_Profiled_TopLevelCollection_ReferenceBackedIdentity_Merge",
            ["It_inserts_new_visible_item_when_creatable_and_no_prior_match_exists"]
        ),
        Profile(
            "ProfileVisibleRowUpdateWithHiddenRowPreservation/NestedCollection",
            "A profiled PUT updates visible nested-collection children and preserves the hidden sibling row.",
            "ProfileNestedCollectionMergeTests",
            "Given_a_ProfileNested_put_request_updating_visible_children_with_a_hidden_sibling_in_storage",
            "Given_a_ProfileNested_put_request_updating_visible_children_with_a_hidden_sibling_in_storage",
            ["It_updates_the_visible_child_rows", "It_preserves_the_hidden_sibling_row_unchanged"]
        ),
        Profile(
            "ProfileVisibleRowUpdateWithHiddenRowPreservation/RootLevelExtensionChildCollection",
            "A profiled PUT updates a root-level extension child collection's values and scalars.",
            "ProfileNestedCollectionMergeTests",
            "Given_a_ProfileNested_put_request_updating_root_extension_child_collection",
            "Given_a_ProfileNested_put_request_updating_root_extension_child_collection",
            ["It_updates_the_root_extension_child_values", "It_updates_the_root_extension_scalars"]
        ),
        Profile(
            "ProfileVisibleRowUpdateWithHiddenRowPreservation/CollectionAlignedExtensionChildCollection",
            "A profiled PUT updates matched collection-aligned extension child rows in place, preserving their CollectionItemIds.",
            "ProfileCollectionAlignedExtensionMergeTests",
            "Given_a_Postgresql_ProfileCollectionAlignedExtension_update_request_modifying_an_aligned_extension_child_value",
            "Given_a_ProfileCollectionAlignedExtension_update_request_modifying_an_aligned_extension_child_value",
            ["It_updates_matched_aligned_extension_child_rows_in_place_preserving_collection_item_ids"]
        ),
        Profile(
            "ProfileVisibleRowUpdateWithHiddenRowPreservation/HiddenDescriptorBinding",
            "A profiled PUT preserves the hidden descriptor binding on a matched visible collection row.",
            "ProfileTopLevelCollectionMergeTests",
            "Given_A_Postgresql_Profiled_TopLevelCollection_Merge",
            "Given_A_Mssql_Profiled_TopLevelCollection_Merge",
            ["It_preserves_hidden_descriptor_binding_on_matched_visible_row"]
        ),
        Profile(
            "ProfileVisibleRowUpdateWithHiddenRowPreservation/SiblingOrdinalRenumbering",
            "A profiled reorder+insert assigns aligned extension child ordinals in request order and preserves matched CollectionItemIds.",
            "ProfileCollectionAlignedExtensionMergeTests",
            "Given_a_Postgresql_ProfileCollectionAlignedExtension_update_request_reordering_and_inserting_aligned_extension_children",
            "Given_a_ProfileCollectionAlignedExtension_update_request_reordering_and_inserting_aligned_extension_children",
            [
                "It_assigns_aligned_extension_child_ordinals_in_new_request_order",
                "It_preserves_collection_item_ids_for_matched_aligned_extension_children_and_assigns_a_new_id_to_the_inserted_child",
            ]
        ),
        // --- ProfileVisibleRowDeleteWithHiddenRowPreservation -------------------------------
        Profile(
            "ProfileVisibleRowDeleteWithHiddenRowPreservation",
            "A profiled PUT deletes omitted visible rows and preserves the hidden rows.",
            "ProfileTopLevelCollectionMergeTests",
            "Given_A_Postgresql_Profiled_TopLevelCollection_Merge",
            "Given_A_Mssql_Profiled_TopLevelCollection_Merge",
            ["It_deletes_omitted_visible_rows_and_preserves_hidden_rows"]
        ),
        Profile(
            "ProfileVisibleRowDeleteWithHiddenRowPreservation/DeleteAllVisibleWhileHiddenRemain",
            "A profiled PUT that omits all visible rows deletes them while the hidden rows remain.",
            "ProfileTopLevelCollectionMergeTests",
            "Given_A_Postgresql_Profiled_TopLevelCollection_Merge",
            "Given_A_Mssql_Profiled_TopLevelCollection_Merge",
            ["It_deletes_all_visible_rows_while_hidden_rows_remain"]
        ),
        Profile(
            "ProfileVisibleRowDeleteWithHiddenRowPreservation/NestedDeleteAllVisible",
            "A profiled PUT that omits all visible nested children preserves only the hidden child row.",
            "ProfileNestedCollectionMergeTests",
            "Given_a_ProfileNested_put_request_omitting_all_visible_children_with_hidden_remaining",
            "Given_a_ProfileNested_put_request_omitting_all_visible_children_with_hidden_remaining",
            ["It_deletes_both_visible_child_rows", "It_preserves_only_the_hidden_child_row"]
        ),
        // --- ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable ------------------------
        Profile(
            "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable",
            "A profiled write rejects a non-creatable new visible collection item before any DML.",
            "ProfileTopLevelCollectionMergeTests",
            "Given_A_Postgresql_Profiled_TopLevelCollection_Merge",
            "Given_A_Mssql_Profiled_TopLevelCollection_Merge",
            ["It_rejects_non_creatable_new_visible_items_before_dml"]
        ),
        Profile(
            "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/CollectionOrCommonTypeItem",
            "With creatable=false, existing visible collection items still update while new items are rejected.",
            "ProfileTopLevelCollectionMergeTests",
            "Given_A_Postgresql_Profiled_TopLevelCollection_Merge",
            "Given_A_Mssql_Profiled_TopLevelCollection_Merge",
            ["It_allows_existing_visible_updates_when_creatable_false_and_rejects_new_items"]
        ),
        Profile(
            "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/NewVisible1To1Scope",
            "A profiled upsert of a new non-creatable separate-table (1:1) scope is rejected; an existing scope still updates.",
            "ProfileSeparateTableMergeFixtureTests",
            "Given_A_ProfiledUpsert_With_Creatable_False_ForNewSeparateTableScope_Rejects",
            "Given_A_Mssql_ProfiledUpsert_With_Creatable_False_ForNewSeparateTableScope_Rejects",
            ["It_returns_profile_data_policy_failure"],
            notes: "Update-allowed companion: Given_A_ProfiledUpdate_WithExistingSeparateTableScope_And_Creatable_False_AllowsUpdate (Mssql twin)."
        ),
        Profile(
            "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/ExtensionScope",
            "A profiled update of a non-creatable aligned extension scope with no matching stored row is rejected.",
            "ProfileCollectionAlignedExtensionMergeTests",
            "Given_a_ProfileCollectionAlignedExtension_update_request_for_a_non_creatable_aligned_extension_scope_with_no_matching_stored_row",
            "Given_a_ProfileCollectionAlignedExtension_update_request_for_a_non_creatable_aligned_extension_scope_with_no_matching_stored_row",
            ["It_returns_profile_data_policy_failure"]
        ),
        ProfileNa(
            "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/NestedCommonTypeScope",
            "A non-creatable new nested/common-type scope is rejected; provider-independent, validated at the synthesizer unit level.",
            "unit: EdFi.DataManagementService.Backend.Tests.Unit.Profile.RelationalWriteProfileMergeSynthesizerTests.Given_nested_visible_request_item_with_no_visible_stored_match_when_creatable_is_false::It_identifies_the_nested_children_scope_in_the_rejection"
        ),
        ProfileNa(
            "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/ExtensionCollectionItem",
            "A non-creatable new extension-scope item is rejected; provider-independent, validated at the synthesizer unit level.",
            "unit: EdFi.DataManagementService.Backend.Tests.Unit.Profile.RelationalWriteProfileMergeSynthesizerTests.Given_Synthesizer_SeparateTable_VisiblePresent_NoStored_Creatable_False::It_identifies_the_extension_scope_as_the_rejected_scope"
        ),
        ProfileNa(
            "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/ThreeLevelChain",
            "The three-level chain (existing middle parent allows descendant create; a new middle parent denies it) is provider-independent, validated at the synthesizer unit level.",
            "unit: EdFi.DataManagementService.Backend.Tests.Unit.Profile.RelationalWriteProfileMergeSynthesizerTests.Given_three_level_chain_with_update_allowed_at_levels_1_and_2_create_denied_at_level_3::It_returns_a_rejection"
        ),
        // --- ProfileHiddenExtensionChildCollectionPreservation ------------------------------
        Profile(
            "ProfileHiddenExtensionChildCollectionPreservation",
            "A profiled PUT with a hidden root-extension scope preserves both root-extension child rows.",
            "ProfileNestedCollectionMergeTests",
            "Given_a_ProfileNested_put_request_with_hidden_root_extension_scope_preserves_children",
            "Given_a_ProfileNested_put_request_with_hidden_root_extension_scope_preserves_children",
            ["It_preserves_both_root_extension_child_rows"]
        ),
        Profile(
            "ProfileHiddenExtensionChildCollectionPreservation/CollectionAlignedExtensionHidden",
            "A profiled update preserves an aligned extension row that is hidden in storage.",
            "ProfileCollectionAlignedExtensionMergeTests",
            "Given_a_ProfileCollectionAlignedExtension_update_request_for_an_existing_resource_with_an_aligned_extension_scope_hidden_in_storage",
            "Given_a_ProfileCollectionAlignedExtension_update_request_for_an_existing_resource_with_an_aligned_extension_scope_hidden_in_storage",
            ["It_preserves_the_aligned_extension_row"]
        ),
        // --- ProfileUnchangedWriteGuardedNoOp -----------------------------------------------
        Profile(
            "ProfileUnchangedWriteGuardedNoOp",
            "An unchanged profiled PUT is a guarded no-op that changes no rowsets or stamps.",
            "ProfileGuardedNoOpTests",
            "Given_A_Postgresql_Relational_Profile_Guarded_No_Op_Put_With_Root_Only_Shape",
            "Given_A_Mssql_Relational_Profile_Guarded_No_Op_Put_With_Root_Only_Shape",
            ["It_returns_update_success_for_an_unchanged_profiled_put", "It_does_not_change_rowsets"]
        ),
        Profile(
            "ProfileUnchangedWriteGuardedNoOp/RootOnlyPostAsUpdate",
            "An unchanged profiled POST-as-update is a guarded no-op that keeps the existing document uuid.",
            "ProfileGuardedNoOpTests",
            "Given_A_Postgresql_Relational_Profile_Guarded_No_Op_Post_As_Update_With_Root_Only_Shape",
            "Given_A_Mssql_Relational_Profile_Guarded_No_Op_Post_As_Update_With_Root_Only_Shape",
            [
                "It_returns_update_success_with_the_existing_document_uuid",
                "It_does_not_insert_the_incoming_document_uuid",
            ]
        ),
        Profile(
            "ProfileUnchangedWriteGuardedNoOp/StalePut",
            "A stale profiled no-op PUT retries and bumps the content version by exactly one.",
            "ProfileGuardedNoOpTests",
            "Given_A_Postgresql_Relational_Profile_Stale_Guarded_No_Op_Put",
            "Given_A_Mssql_Relational_Profile_Stale_Guarded_No_Op_Put",
            [
                "It_retries_and_returns_update_success_after_the_profiled_no_op_compare_goes_stale",
                "It_bumps_the_content_version_by_exactly_one",
            ]
        ),
        Profile(
            "ProfileUnchangedWriteGuardedNoOp/StalePostAsUpdate",
            "A stale profiled no-op POST-as-update retries without inserting the incoming document uuid.",
            "ProfileGuardedNoOpTests",
            "Given_A_Postgresql_Relational_Profile_Stale_Guarded_No_Op_Post_As_Update",
            "Given_A_Mssql_Relational_Profile_Stale_Guarded_No_Op_Post_As_Update",
            [
                "It_retries_and_returns_update_success_after_the_profiled_no_op_compare_goes_stale",
                "It_does_not_insert_the_incoming_document_uuid",
            ]
        ),
        Profile(
            "ProfileUnchangedWriteGuardedNoOp/SeparateTablePut",
            "An unchanged profiled PUT with a separate-table shape leaves the extension row contents unchanged.",
            "ProfileGuardedNoOpTests",
            "Given_A_Postgresql_Relational_Profile_Guarded_No_Op_Put_With_Separate_Table_Shape",
            "Given_A_Mssql_Relational_Profile_Guarded_No_Op_Put_With_Separate_Table_Shape",
            ["It_does_not_change_ext_row_contents"]
        ),
        Profile(
            "ProfileUnchangedWriteGuardedNoOp/TopLevelCollectionPut",
            "An unchanged profiled PUT with a top-level-collection shape leaves the collection rows unchanged.",
            "ProfileGuardedNoOpTests",
            "Given_A_Postgresql_Relational_Profile_Guarded_No_Op_Put_With_Top_Level_Collection_Shape",
            "Given_A_Mssql_Relational_Profile_Guarded_No_Op_Put_With_Top_Level_Collection_Shape",
            ["It_does_not_change_collection_rows"]
        ),
        Profile(
            "ProfileUnchangedWriteGuardedNoOp/OrdinalAlignmentAcrossNoProfilePath",
            "A profiled guarded no-op PUT over a collection created via the no-profile path does not modify the collection (0-based ordinal alignment).",
            "ProfileGuardedNoOpOrdinalAlignmentTests",
            "Given_A_Postgresql_Relational_Profile_Guarded_No_Op_Put_With_Top_Level_Collection_Created_Via_No_Profile_Path",
            "Given_A_Mssql_Relational_Profile_Guarded_No_Op_Put_With_Top_Level_Collection_Created_Via_No_Profile_Path",
            ["It_does_not_modify_the_addresses_collection"]
        ),
    ];

    private static ParityScenario Profile(
        string id,
        string contract,
        string stem,
        string pgFixture,
        string mssqlFixture,
        string[] methods,
        string sharedEntryPoint = "",
        string? notes = null
    ) =>
        new()
        {
            Id = id,
            Layer = ParityLayer.Profile,
            BehavioralContract = contract,
            SharedEntryPoint = sharedEntryPoint,
            Boundary = ProductionBoundary.ProfilePersistExecutor,
            Pgsql = new ScenarioLocation($"Postgresql{stem}.cs", pgFixture, [.. methods]),
            Mssql = new ScenarioLocation($"Mssql{stem}.cs", mssqlFixture, [.. methods]),
            PgsqlCoverage = EngineCoverage.Covered,
            MssqlCoverage = EngineCoverage.Covered,
            Classification = ParityClassification.Both,
            Notes = notes,
        };

    private static ParityScenario ProfileNa(string id, string contract, string unitEntryPoint) =>
        new()
        {
            Id = id,
            Layer = ParityLayer.Profile,
            BehavioralContract = contract,
            Boundary = ProductionBoundary.ProfilePersistExecutor,
            Pgsql = null,
            Mssql = null,
            PgsqlCoverage = EngineCoverage.NotApplicable,
            MssqlCoverage = EngineCoverage.NotApplicable,
            Classification = ParityClassification.Na,
            Notes = unitEntryPoint,
        };
}
