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
    /// Both/Covered on the profile persist-executor boundary with mirrored PostgreSQL and SQL Server
    /// fixtures (fixture names are recorded per engine because they are not mechanically mirrorable;
    /// the per-fixture test-method names are identical across engines). A scenario that has more than
    /// one provider fixture records one ScenarioLocation per fixture. Three documented creatability
    /// variants exist only as synthesizer unit tests and are recorded Na.
    /// </summary>
    internal static readonly ImmutableArray<ParityScenario> ProfileScenarios =
    [
        Profile(
            "ProfileRootCreateRejectedWhenNonCreatable",
            "A profiled POST create whose root is not creatable is rejected as a profile data-policy failure.",
            "ProfileExecutorRoutingTests",
            "Given_A_Profiled_Post_Create_Where_Root_Is_Not_Creatable",
            "Given_A_Mssql_Profiled_Post_Create_Where_Root_Is_Not_Creatable",
            ["It_returns_profile_data_policy_failure_for_creatability_rejection"]
        ),
        // ProfileHiddenInlinedColumnPreservation + variants
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
            ["It_preserves_the_stored_value_at_the_hidden_path"],
            sharedEntryPoint: NestedCollectionSharedEntryPoint
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
        // ProfileVisibleButAbsentNonCollectionScope + variant
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
        // ProfileHiddenExtensionRowPreservation + variants
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
            ["It_updates_the_visible_scalar", "It_preserves_the_hidden_descriptor_fk"]
        ),
        // ProfileVisibleRowUpdateWithHiddenRowPreservation + variants
        Profile(
            "ProfileVisibleRowUpdateWithHiddenRowPreservation",
            "A profiled PUT updates the visible collection rows and preserves the hidden sibling rows.",
            "ProfileTopLevelCollectionMergeTests",
            "Given_A_Postgresql_Profiled_TopLevelCollection_Merge",
            "Given_A_Mssql_Profiled_TopLevelCollection_Merge",
            ["It_updates_visible_rows_and_preserves_hidden_rows"]
        ),
        Profile(
            "ProfileVisibleRowUpdateWithHiddenRowPreservation/TopLevel",
            "Top-level-collection shape: a profiled PUT updates visible rows and preserves hidden rows, in both the "
                + "ordinary and the reference-backed-identity top-level forms.",
            [
                PgLoc(
                    "ProfileTopLevelCollectionMergeTests",
                    "Given_A_Postgresql_Profiled_TopLevelCollection_Merge",
                    ["It_updates_visible_rows_and_preserves_hidden_rows"]
                ),
                PgLoc(
                    "ProfileTopLevelCollectionReferenceBackedMergeTests",
                    "Given_A_Postgresql_Profiled_TopLevelCollection_ReferenceBackedIdentity_Merge",
                    ["It_updates_in_place_when_request_item_matches_stored_visible_row_by_reference_identity"]
                ),
            ],
            [
                MsLoc(
                    "ProfileTopLevelCollectionMergeTests",
                    "Given_A_Mssql_Profiled_TopLevelCollection_Merge",
                    ["It_updates_visible_rows_and_preserves_hidden_rows"]
                ),
                MsLoc(
                    "ProfileTopLevelCollectionReferenceBackedMergeTests",
                    "Given_A_Mssql_Profiled_TopLevelCollection_ReferenceBackedIdentity_Merge",
                    ["It_updates_in_place_when_request_item_matches_stored_visible_row_by_reference_identity"]
                ),
            ],
            providerSpecificRationale: ProfileMixedFixtureProviderSpecificRationale
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
            "A profiled PUT interleaves an in-place update of a matched visible row with an inserted new creatable item (reference-backed top-level and aligned-extension-child forms).",
            [
                PgLoc(
                    "ProfileTopLevelCollectionReferenceBackedMergeTests",
                    "Given_A_Postgresql_Profiled_TopLevelCollection_ReferenceBackedIdentity_Merge",
                    ["It_inserts_new_visible_item_when_creatable_and_no_prior_match_exists"]
                ),
                PgLoc(
                    "ProfileCollectionAlignedExtensionMergeTests",
                    "Given_a_Postgresql_ProfileCollectionAlignedExtension_update_request_reordering_and_inserting_aligned_extension_children",
                    ["It_assigns_aligned_extension_child_ordinals_in_new_request_order"]
                ),
            ],
            [
                MsLoc(
                    "ProfileTopLevelCollectionReferenceBackedMergeTests",
                    "Given_A_Mssql_Profiled_TopLevelCollection_ReferenceBackedIdentity_Merge",
                    ["It_inserts_new_visible_item_when_creatable_and_no_prior_match_exists"]
                ),
                MsLoc(
                    "ProfileCollectionAlignedExtensionMergeTests",
                    "Given_a_ProfileCollectionAlignedExtension_update_request_reordering_and_inserting_aligned_extension_children",
                    ["It_assigns_aligned_extension_child_ordinals_in_new_request_order"]
                ),
            ],
            providerSpecificRationale: ProfileMixedFixtureProviderSpecificRationale
        ),
        Profile(
            "ProfileVisibleRowUpdateWithHiddenRowPreservation/NestedCollection",
            "A profiled PUT updates visible nested-collection children and preserves the hidden sibling row byte-for-byte, with retained CollectionItemIds, parent linkage, and the exact deterministic contiguous sibling order.",
            "ProfileNestedCollectionMergeTests",
            "Given_a_ProfileNested_put_request_updating_visible_children_with_a_hidden_sibling_in_storage",
            "Given_a_ProfileNested_put_request_updating_visible_children_with_a_hidden_sibling_in_storage",
            ["It_updates_the_visible_child_rows", "It_preserves_the_hidden_sibling_row_unchanged"],
            sharedEntryPoint: NestedCollectionSharedEntryPoint
                + " + ProfileNestedCollectionScenarios.AssertVisibleChildUpdatePreservesHiddenSiblingAndIdentities"
        ),
        Profile(
            "ProfileVisibleRowUpdateWithHiddenRowPreservation/RootLevelExtensionChildCollection",
            "A profiled PUT updates a root-level extension child collection (nested form).",
            "ProfileNestedCollectionMergeTests",
            "Given_a_ProfileNested_put_request_updating_root_extension_child_collection",
            "Given_a_ProfileNested_put_request_updating_root_extension_child_collection",
            ["It_updates_the_root_extension_child_values", "It_updates_the_root_extension_scalars"],
            sharedEntryPoint: NestedCollectionSharedEntryPoint
        ),
        Profile(
            "ProfileVisibleRowUpdateWithHiddenRowPreservation/CollectionAlignedExtensionChildCollection",
            "A profiled PUT updates matched collection-aligned extension child rows in place, preserving their CollectionItemIds.",
            "ProfileCollectionAlignedExtensionMergeTests",
            "Given_a_Postgresql_ProfileCollectionAlignedExtension_update_request_modifying_an_aligned_extension_child_value",
            "Given_a_ProfileCollectionAlignedExtension_update_request_modifying_an_aligned_extension_child_value",
            ["It_updates_matched_aligned_extension_child_rows_in_place_preserving_collection_item_ids"],
            sharedEntryPoint: AlignedExtensionSharedEntryPoint
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
            "A profiled aligned-extension-child reorder+insert assigns ordinals in request order (preserving matched CollectionItemIds), and omitting an aligned extension child recomputes the surviving ordinal.",
            [
                PgLoc(
                    "ProfileCollectionAlignedExtensionMergeTests",
                    "Given_a_Postgresql_ProfileCollectionAlignedExtension_update_request_reordering_and_inserting_aligned_extension_children",
                    [
                        "It_assigns_aligned_extension_child_ordinals_in_new_request_order",
                        "It_preserves_collection_item_ids_for_matched_aligned_extension_children_and_assigns_a_new_id_to_the_inserted_child",
                    ]
                ),
                PgLoc(
                    "ProfileCollectionAlignedExtensionMergeTests",
                    "Given_a_Postgresql_ProfileCollectionAlignedExtension_update_request_omitting_an_aligned_extension_child",
                    ["It_recomputes_the_surviving_aligned_extension_child_ordinal"]
                ),
            ],
            [
                MsLoc(
                    "ProfileCollectionAlignedExtensionMergeTests",
                    "Given_a_ProfileCollectionAlignedExtension_update_request_reordering_and_inserting_aligned_extension_children",
                    [
                        "It_assigns_aligned_extension_child_ordinals_in_new_request_order",
                        "It_preserves_collection_item_ids_for_matched_aligned_extension_children_and_assigns_a_new_id_to_the_inserted_child",
                    ]
                ),
                MsLoc(
                    "ProfileCollectionAlignedExtensionMergeTests",
                    "Given_a_ProfileCollectionAlignedExtension_update_request_omitting_an_aligned_extension_child",
                    ["It_recomputes_the_surviving_aligned_extension_child_ordinal"]
                ),
            ],
            sharedEntryPoint: AlignedExtensionSharedEntryPoint
        ),
        // ProfileVisibleRowDeleteWithHiddenRowPreservation + variants
        Profile(
            "ProfileVisibleRowDeleteWithHiddenRowPreservation",
            "A profiled PUT deletes omitted visible rows and preserves the hidden rows.",
            "ProfileTopLevelCollectionMergeTests",
            "Given_A_Postgresql_Profiled_TopLevelCollection_Merge",
            "Given_A_Mssql_Profiled_TopLevelCollection_Merge",
            ["It_deletes_omitted_visible_rows_and_preserves_hidden_rows"]
        ),
        Profile(
            "ProfileVisibleRowDeleteWithHiddenRowPreservation/DeleteOmittedVisible",
            "A profiled PUT deletes visible rows absent from the request while preserving hidden rows (top-level and reference-backed forms).",
            [
                PgLoc(
                    "ProfileTopLevelCollectionMergeTests",
                    "Given_A_Postgresql_Profiled_TopLevelCollection_Merge",
                    ["It_deletes_omitted_visible_rows_and_preserves_hidden_rows"]
                ),
                PgLoc(
                    "ProfileTopLevelCollectionReferenceBackedMergeTests",
                    "Given_A_Postgresql_Profiled_TopLevelCollection_ReferenceBackedIdentity_Merge",
                    ["It_deletes_visible_row_absent_from_request_while_preserving_hidden_rows"]
                ),
            ],
            [
                MsLoc(
                    "ProfileTopLevelCollectionMergeTests",
                    "Given_A_Mssql_Profiled_TopLevelCollection_Merge",
                    ["It_deletes_omitted_visible_rows_and_preserves_hidden_rows"]
                ),
                MsLoc(
                    "ProfileTopLevelCollectionReferenceBackedMergeTests",
                    "Given_A_Mssql_Profiled_TopLevelCollection_ReferenceBackedIdentity_Merge",
                    ["It_deletes_visible_row_absent_from_request_while_preserving_hidden_rows"]
                ),
            ]
        ),
        Profile(
            "ProfileVisibleRowDeleteWithHiddenRowPreservation/DeleteAllVisibleWhileHiddenRemain",
            "A profiled PUT that omits all visible rows deletes them while the hidden rows remain (top-level and nested forms).",
            [
                PgLoc(
                    "ProfileTopLevelCollectionMergeTests",
                    "Given_A_Postgresql_Profiled_TopLevelCollection_Merge",
                    ["It_deletes_all_visible_rows_while_hidden_rows_remain"]
                ),
                PgLoc(
                    "ProfileNestedCollectionMergeTests",
                    "Given_a_ProfileNested_put_request_omitting_all_visible_children_with_hidden_remaining",
                    ["It_deletes_both_visible_child_rows", "It_preserves_only_the_hidden_child_row"]
                ),
            ],
            [
                MsLoc(
                    "ProfileTopLevelCollectionMergeTests",
                    "Given_A_Mssql_Profiled_TopLevelCollection_Merge",
                    ["It_deletes_all_visible_rows_while_hidden_rows_remain"]
                ),
                MsLoc(
                    "ProfileNestedCollectionMergeTests",
                    "Given_a_ProfileNested_put_request_omitting_all_visible_children_with_hidden_remaining",
                    ["It_deletes_both_visible_child_rows", "It_preserves_only_the_hidden_child_row"]
                ),
            ],
            providerSpecificRationale: ProfileMixedFixtureProviderSpecificRationale
        ),
        // ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable + variants
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
            [
                PgLoc(
                    "ProfileSeparateTableMergeFixtureTests",
                    "Given_A_ProfiledUpsert_With_Creatable_False_ForNewSeparateTableScope_Rejects",
                    ["It_returns_profile_data_policy_failure"]
                ),
                PgLoc(
                    "ProfileSeparateTableMergeFixtureTests",
                    "Given_A_ProfiledUpdate_WithExistingSeparateTableScope_And_Creatable_False_AllowsUpdate",
                    ["It_updates_the_separate_table_visible_scalar"]
                ),
            ],
            [
                MsLoc(
                    "ProfileSeparateTableMergeFixtureTests",
                    "Given_A_Mssql_ProfiledUpsert_With_Creatable_False_ForNewSeparateTableScope_Rejects",
                    ["It_returns_profile_data_policy_failure"]
                ),
                MsLoc(
                    "ProfileSeparateTableMergeFixtureTests",
                    "Given_A_Mssql_ProfiledUpdate_WithExistingSeparateTableScope_And_Creatable_False_AllowsUpdate",
                    ["It_updates_the_separate_table_visible_scalar"]
                ),
            ]
        ),
        Profile(
            "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/ExtensionScope",
            "A profiled update of a non-creatable aligned extension scope with no matching stored row is rejected.",
            "ProfileCollectionAlignedExtensionMergeTests",
            "Given_a_ProfileCollectionAlignedExtension_update_request_for_a_non_creatable_aligned_extension_scope_with_no_matching_stored_row",
            "Given_a_ProfileCollectionAlignedExtension_update_request_for_a_non_creatable_aligned_extension_scope_with_no_matching_stored_row",
            ["It_returns_profile_data_policy_failure"],
            sharedEntryPoint: AlignedExtensionSharedEntryPoint
        ),
        Profile(
            "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/TwoLevelCreatableFalseChildrenRejected",
            "A profiled PUT with creatable=false on nested children rejects new children (the provider companion to the synthesizer-level three-level chain).",
            "ProfileNestedCollectionMergeTests",
            "Given_a_ProfileNested_put_request_with_creatable_false_on_children_rejects_new_children",
            "Given_a_ProfileNested_put_request_with_creatable_false_on_children_rejects_new_children",
            ["It_returns_a_profile_data_policy_failure"],
            sharedEntryPoint: NestedCollectionSharedEntryPoint
        ),
        ProfileNa(
            "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/NestedCommonTypeScope",
            "A non-creatable new nested/common-type scope is rejected; provider-independent, validated at the synthesizer unit level.",
            "Given_nested_visible_request_item_with_no_visible_stored_match_when_creatable_is_false",
            ["It_identifies_the_nested_children_scope_in_the_rejection"]
        ),
        ProfileNa(
            "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/ExtensionCollectionItem",
            "A non-creatable new extension-child collection item is rejected even though its parent extension scope is visible-and-creatable for matched-update; provider-independent, validated at the synthesizer unit level.",
            "Given_extension_child_non_creatable_insert_with_existing_visible_parent_update_allowed",
            ["It_identifies_the_extension_child_collection_scope_as_the_rejected_scope"]
        ),
        ProfileNa(
            "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/ThreeLevelChain",
            "A three-level chain contrasting descendant create under an existing visible middle-level parent (allowed — the new grandchild is inserted) with descendant create under a newly created, non-creatable visible middle-level parent (rejected at the middle scope, the descendant never reached). This row proves the downstream synthesizer merge/rejection behavior from supplied creatability flags; the upstream hidden-required-member derivation that produces those flags is proved by ThreeLevelChainCreatabilityDerivation. Provider-independent, validated at the merge-synthesizer unit level.",
            "Given_three_level_chain_contrasts_descendant_create_under_an_existing_versus_a_newly_created_middle_parent",
            [
                "It_allows_the_descendant_create_under_an_existing_middle_parent",
                "It_inserts_the_descendant_row_under_the_existing_middle_parent",
                "It_rejects_the_descendant_create_under_a_newly_created_middle_parent",
                "It_identifies_the_new_middle_parent_scope_in_the_rejection",
            ]
        ),
        ProfileNa(
            "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/ThreeLevelChainCreatabilityDerivation",
            "The upstream creatability derivation for the three-level chain: the Core CreatabilityAnalyzer marks a descendant collection item creatable under an existing parent instance but non-creatable under a newly created parent instance that a hidden required member makes non-creatable, emitting the parent-scope and descendant-item rejection failures. This is the derivation that produces the creatability flags the synthesizer-level ThreeLevelChain row consumes; provider-independent, validated at the Core analyzer unit level.",
            "Given_Collection_Items_Under_Different_Parent_Instances",
            [
                "It_should_mark_alpha_collection_item_as_creatable",
                "It_should_mark_beta_collection_item_as_non_creatable",
                "It_should_emit_failure_for_beta_parent",
                "It_should_emit_failure_for_beta_collection_item",
            ],
            ProductionBoundary.ProfileCreatabilityAnalysis,
            "CreatabilityAnalyzerTests.cs",
            UnitTestAssembly.CoreTestsUnit,
            ProfileCreatabilityDerivationProviderSpecificRationale
        ),
        // ProfileHiddenExtensionChildCollectionPreservation + variant
        Profile(
            "ProfileHiddenExtensionChildCollectionPreservation",
            "A profiled PUT with a hidden root-extension scope preserves the root-extension row and both child rows byte-for-byte: identities, linkage, values, ordinals, and order all unchanged.",
            "ProfileNestedCollectionMergeTests",
            "Given_a_ProfileNested_put_request_with_hidden_root_extension_scope_preserves_children",
            "Given_a_ProfileNested_put_request_with_hidden_root_extension_scope_preserves_children",
            ["It_preserves_both_root_extension_child_rows"],
            sharedEntryPoint: NestedCollectionSharedEntryPoint
                + " + ProfileNestedCollectionScenarios.AssertHiddenRootExtensionScopePreservedExactly"
        ),
        Profile(
            "ProfileHiddenExtensionChildCollectionPreservation/CollectionAlignedExtensionHidden",
            "A profiled update preserves an aligned extension row that is hidden in storage.",
            "ProfileCollectionAlignedExtensionMergeTests",
            "Given_a_ProfileCollectionAlignedExtension_update_request_for_an_existing_resource_with_an_aligned_extension_scope_hidden_in_storage",
            "Given_a_ProfileCollectionAlignedExtension_update_request_for_an_existing_resource_with_an_aligned_extension_scope_hidden_in_storage",
            ["It_preserves_the_aligned_extension_row"],
            sharedEntryPoint: AlignedExtensionSharedEntryPoint
        ),
        // ProfileUnchangedWriteGuardedNoOp + variants
        Profile(
            "ProfileUnchangedWriteGuardedNoOp",
            "An unchanged profiled PUT is a guarded no-op that changes no rowsets or stamps.",
            "ProfileGuardedNoOpTests",
            "Given_A_Postgresql_Relational_Profile_Guarded_No_Op_Put_With_Root_Only_Shape",
            "Given_A_Mssql_Relational_Profile_Guarded_No_Op_Put_With_Root_Only_Shape",
            [
                "It_returns_update_success_for_an_unchanged_profiled_put",
                "It_does_not_change_rowsets",
                "It_does_not_change_content_version",
                "It_does_not_change_content_last_modified_at",
                "It_does_not_change_identity_version",
                "It_does_not_change_identity_last_modified_at",
            ]
        ),
        Profile(
            "ProfileUnchangedWriteGuardedNoOp/RootOnlyPut",
            "Root-only shape: an unchanged profiled PUT is a guarded no-op that changes no rowsets or stamps.",
            "ProfileGuardedNoOpTests",
            "Given_A_Postgresql_Relational_Profile_Guarded_No_Op_Put_With_Root_Only_Shape",
            "Given_A_Mssql_Relational_Profile_Guarded_No_Op_Put_With_Root_Only_Shape",
            [
                "It_returns_update_success_for_an_unchanged_profiled_put",
                "It_does_not_change_rowsets",
                "It_does_not_change_content_version",
                "It_does_not_change_content_last_modified_at",
                "It_does_not_change_identity_version",
                "It_does_not_change_identity_last_modified_at",
            ]
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

    // Profile rows whose per-engine fixtures have no extracted provider-neutral shared contract resolve
    // ProviderSpecific from their recorded per-engine locations. Rows whose PostgreSQL and SQL Server fixtures do
    // consume a shared Backend.Tests.Common contract (e.g. ProfileCollectionAlignedExtensionScenarios /
    // ProfileNestedCollectionScenarios) are recorded Direct against that contract instead — profiled runtime
    // execution is owned by DMS-1124, but that ownership does not itself preclude a shared fixture contract.
    private const string ProfileProviderSpecificRationale =
        "No provider-neutral shared contract exists in Backend.Tests.Common for this profile scenario's per-engine "
        + "merge fixtures, so the per-engine fixtures recorded on this row are the effective entry points.";

    // A profile scenario that spans more than one per-engine fixture where the fixtures do not all share a single
    // provider-neutral contract; it cannot name one Direct contract, so its per-engine fixtures are the entry points.
    private const string ProfileMixedFixtureProviderSpecificRationale =
        "This profile scenario spans per-engine fixtures that do not all share a single provider-neutral contract, "
        + "so the per-engine fixtures recorded on this row are the effective entry points.";

    // The shared Backend.Tests.Common contract entry points both engines delegate to for the aligned-extension and
    // nested-collection profile-merge suites (the provider-neutral request/scenario builder each suite consumes).
    private const string AlignedExtensionSharedEntryPoint =
        "ProfileCollectionAlignedExtensionScenarios.CreateProfileContext";
    private const string NestedCollectionSharedEntryPoint =
        "ProfileNestedCollectionScenarios.CreateProfileContext";

    private const string ProfileNaProviderSpecificRationale =
        "Provider-independent creatability/rejection behavior validated at the profile merge-synthesizer unit "
        + "level; the unit fixture recorded on this row is the effective assertion entry point and no cross-engine "
        + "shared contract applies.";

    // The upstream creatability derivation is proved in Core (CreatabilityAnalyzer), a different unit assembly than
    // the synthesizer proofs, so its row records a Core.Tests.Unit-owned unit location resolved against that assembly.
    private const string ProfileCreatabilityDerivationProviderSpecificRationale =
        "Provider-independent hidden-required-member creatability derivation validated at the Core CreatabilityAnalyzer "
        + "unit level; the Core.Tests.Unit fixture recorded on this row is the effective assertion entry point and no "
        + "cross-engine shared contract applies.";

    private static ScenarioLocation PgLoc(string stem, string fixture, string[] methods) =>
        new($"Postgresql{stem}.cs", fixture, [.. methods]);

    private static ScenarioLocation MsLoc(string stem, string fixture, string[] methods) =>
        new($"Mssql{stem}.cs", fixture, [.. methods]);

    private static ParityScenario Profile(
        string id,
        string contract,
        ImmutableArray<ScenarioLocation> pgsql,
        ImmutableArray<ScenarioLocation> mssql,
        string sharedEntryPoint = "",
        string? providerSpecificRationale = null,
        string? notes = null
    )
    {
        bool isDirect = !string.IsNullOrWhiteSpace(sharedEntryPoint);

        return new ParityScenario
        {
            Id = id,
            Layer = ParityLayer.Profile,
            BehavioralContract = contract,
            SharedEntryPoint = sharedEntryPoint,
            Boundary = ProductionBoundary.ProfilePersistExecutor,
            PgsqlLocations = pgsql,
            MssqlLocations = mssql,
            PgsqlCoverage = EngineCoverage.Covered,
            MssqlCoverage = EngineCoverage.Covered,
            Classification = ParityClassification.Both,
            ProviderSpecificEntryPointRationale = isDirect
                ? null
                : providerSpecificRationale ?? ProfileProviderSpecificRationale,
            Notes = notes,
        };
    }

    private static ParityScenario Profile(
        string id,
        string contract,
        string stem,
        string pgFixture,
        string mssqlFixture,
        string[] methods,
        string sharedEntryPoint = "",
        string? providerSpecificRationale = null,
        string? notes = null
    ) =>
        Profile(
            id,
            contract,
            [PgLoc(stem, pgFixture, methods)],
            [MsLoc(stem, mssqlFixture, methods)],
            sharedEntryPoint,
            providerSpecificRationale,
            notes
        );

    private static ParityScenario ProfileNa(
        string id,
        string contract,
        string unitFixture,
        string[] unitMethods,
        ProductionBoundary boundary = ProductionBoundary.ProfileMergeSynthesizer,
        string unitFile = "RelationalWriteProfileMergeSynthesizerTests.cs",
        UnitTestAssembly owner = UnitTestAssembly.BackendTestsUnit,
        string? providerSpecificRationale = null
    ) =>
        new()
        {
            Id = id,
            Layer = ParityLayer.Profile,
            BehavioralContract = contract,
            Boundary = boundary,
            UnitLocations =
            [
                new ScenarioLocation(unitFile, unitFixture, [.. unitMethods]) { UnitOwner = owner },
            ],
            PgsqlCoverage = EngineCoverage.NotApplicable,
            MssqlCoverage = EngineCoverage.NotApplicable,
            Classification = ParityClassification.Na,
            ProviderSpecificEntryPointRationale =
                providerSpecificRationale ?? ProfileNaProviderSpecificRationale,
        };
}
