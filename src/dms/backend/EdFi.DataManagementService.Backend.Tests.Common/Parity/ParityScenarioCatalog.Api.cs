// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;

namespace EdFi.DataManagementService.Backend.Tests.Common.Parity;

public static partial class ParityScenarioCatalog
{
    /// <summary>
    /// Shared API-level HTTP scenarios driven by mirrored PostgreSQL and SQL Server wrappers.
    /// Includes the DMS-1022 CRUD/profile baseline and DMS-1404 compact descriptor contracts.
    /// All are Both/Covered on the HTTP pipeline boundary.
    /// </summary>
    internal static readonly ImmutableArray<ParityScenario> ApiScenarios =
    [
        Api(
            "Api/CrudRoundTrip/CreatesAndReadsAStudent",
            "POST create then GET-by-id round-trips the resource and emits an ETag; one row persists.",
            "CrudRoundTripScenario.It_creates_and_reads_a_student",
            "It_creates_and_reads_a_student"
        ),
        Api(
            "Api/CrudRoundTrip/UpdatesAStudentViaPut",
            "PUT with If-Match advances the ETag and the GET reflects the update.",
            "CrudRoundTripScenario.It_updates_a_student_via_put",
            "It_updates_a_student_via_put"
        ),
        Api(
            "Api/CrudRoundTrip/UpsertsAStudentViaPost",
            "POST on an existing natural key upserts through the update path (200), same Location, no duplicate row.",
            "CrudRoundTripScenario.It_upserts_a_student_via_post",
            "It_upserts_a_student_via_post"
        ),
        Api(
            "Api/CrudRoundTrip/DeletesAStudent",
            "DELETE returns 204, a subsequent GET returns 404, and the relational row is removed.",
            "CrudRoundTripScenario.It_deletes_a_student",
            "It_deletes_a_student"
        ),
        Api(
            "Api/CrudRoundTrip/PagesStudentsViaQuery",
            "limit/offset paging returns deterministic DocumentId-ordered windows with complete, non-overlapping coverage of every seeded row, asserted twice after a changed non-identity PUT physically relocates the first row; the natural keys are seeded in non-lexical order, so neither a dropped production ORDER BY nor a wrong deterministic natural-key sort can stay green.",
            "CrudRoundTripScenario.It_pages_students_via_query",
            "It_pages_students_via_query"
        ),
        Api(
            "Api/CrudRoundTrip/RejectsCreateWithMissingReference",
            "An unresolved reference is rejected as a 409 data-conflict unresolved-reference problem.",
            "CrudRoundTripScenario.It_rejects_create_with_missing_reference",
            "It_rejects_create_with_missing_reference"
        ),
        Api(
            "Api/CrudRoundTrip/RejectsDeleteWhenReferenced",
            "Deleting a referenced resource is rejected as 409 and the referenced row remains.",
            "CrudRoundTripScenario.It_rejects_delete_when_referenced",
            "It_rejects_delete_when_referenced"
        ),
        Api(
            "Api/ProfileRootOnlyMerge/CreatesAndReadsViaVisibleProfile",
            "Profiled POST+GET via visible content types persists visible columns and never returns the hidden column.",
            "ProfileRootOnlyMergeProfileScenario.It_creates_and_reads_via_visible_profile",
            "It_creates_and_reads_via_visible_profile",
            profiled: true
        ),
        Api(
            "Api/ProfileRootOnlyMerge/PreservesHiddenFieldOnProfiledPut",
            "A profiled PUT that never names the hidden column preserves its stored value.",
            "ProfileRootOnlyMergeProfileScenario.It_preserves_hidden_field_on_profiled_put",
            "It_preserves_hidden_field_on_profiled_put",
            profiled: true
        ),
        Api(
            "Api/ProfileRootOnlyMerge/RejectsWriteAgainstReadOnlyProfile",
            "A write under a read-only profile returns 405 and creates no row.",
            "ProfileRootOnlyMergeProfileScenario.It_rejects_write_against_read_only_profile",
            "It_rejects_write_against_read_only_profile",
            profiled: true
        ),
        Api(
            "Api/DescriptorRuntime/RIPostComponentAndCasingUpdates",
            "RI-matching authorized POST applies incoming equal-URI components and casing, retains independent IDs, and advances stamps and ETags.",
            "DescriptorRuntimeScenario.It_applies_RI_matching_post_component_and_casing_updates",
            "It_applies_RI_matching_post_component_and_casing_updates",
            fixtureSuffix: "CompactDescriptorRuntime"
        ),
        Api(
            "Api/DescriptorRuntime/EqualWholeUriPut",
            "Ordinal PUT accepts changed components reconstructing the same whole URI and stamps a representation update.",
            "DescriptorRuntimeScenario.It_accepts_equal_whole_URI_components_on_put",
            "It_accepts_equal_whole_URI_components_on_put",
            fixtureSuffix: "CompactDescriptorRuntime"
        ),
        Api(
            "Api/DescriptorRuntime/IdenticalPostAndPutNoOps",
            "Identical POST returns 200 and PUT 204 with unchanged IDs, ETags, document/mirror stamps and change-query responses.",
            "DescriptorRuntimeScenario.It_preserves_all_stamps_and_change_queries_on_identical_post_and_put",
            "It_preserves_all_stamps_and_change_queries_on_identical_post_and_put",
            fixtureSuffix: "CompactDescriptorRuntime"
        ),
        Api(
            "Api/DescriptorRuntime/DuplicateWithoutRIMatch",
            "Equal whole-URI storage without a matching RI retains the write-conflict outcome (HTTP 500 after retries) and leaves the existing representation and stamps intact.",
            "DescriptorRuntimeScenario.It_rejects_a_duplicate_whole_URI_without_an_RI_match",
            "It_rejects_a_duplicate_whole_URI_without_an_RI_match",
            fixtureSuffix: "CompactDescriptorRuntime"
        ),
        Api(
            "Api/DescriptorRuntime/QualifiedTypeIsolation",
            "Identical URIs in distinct types and same-named cross-project types create independent rows; updates and deletes route to the qualified type.",
            "DescriptorRuntimeScenario.It_isolates_identical_URIs_across_descriptor_types_and_projects",
            "It_isolates_identical_URIs_across_descriptor_types_and_projects",
            fixtureSuffix: "CompactDescriptorRuntime"
        ),
        Api(
            "Api/DescriptorRuntime/ImmutablePutIdentity",
            "Ordinal PUT rejects changed whole URI, including casing-only changes, with exact 400 problem details and unchanged storage/stamps.",
            "DescriptorRuntimeScenario.It_rejects_descriptor_identity_changes",
            "It_rejects_descriptor_identity_changes",
            fixtureSuffix: "CompactDescriptorRuntime"
        ),
        Api(
            "Api/DescriptorRuntime/DescriptiveAndDateUpdates",
            "Authorized descriptive/date PUT preserves both IDs, advances metadata, and returns the follow-up GET ETag.",
            "DescriptorRuntimeScenario.It_updates_descriptor_non_identity_fields_and_advances_metadata",
            "It_updates_descriptor_non_identity_fields_and_advances_metadata",
            fixtureSuffix: "CompactDescriptorRuntime"
        ),
        Api(
            "Api/DescriptorRuntime/NamespaceAuthorization",
            "Denied descriptor CRUD and equal-whole-URI proposed namespace changes return 403 without storage or stamp side effects.",
            "DescriptorRuntimeScenario.It_enforces_namespace_authorization_without_stamp_side_effects",
            "It_enforces_namespace_authorization_without_stamp_side_effects",
            fixtureSuffix: "CompactDescriptorRuntime"
        ),
        Api(
            "Api/CompactDescriptorResources/StorageAndHydration",
            "Root, nested collections, extensions and copied identities store exact int keys and serve original-case whole URIs across qualified descriptor types.",
            "CompactDescriptorResourceScenario.It_stores_compact_keys_and_hydrates_root_collection_extension_and_copied_URIs",
            "It_stores_compact_keys_and_hydrates_root_collection_extension_and_copied_URIs",
            fixtureSuffix: "CompactDescriptorResources"
        ),
        Api(
            "Api/CompactDescriptorResources/MixedCaseIdentityMatching",
            "Repeated resource POSTs and transitive descriptor-bearing document identities resolve mixed-case URIs through RI with stable document IDs and exact no-op stamps.",
            "CompactDescriptorResourceScenario.It_matches_repeated_POST_and_transitive_document_identities_with_mixed_case_descriptors",
            "It_matches_repeated_POST_and_transitive_document_identities_with_mixed_case_descriptors",
            fixtureSuffix: "CompactDescriptorResources"
        ),
        Api(
            "Api/CompactDescriptorResources/WholeUriFilters",
            "Direct and copied-identity filters preserve exact/mixed-case matches, missing/wrong-type empty pages, extra delimiters and internal pre-delimiter spaces.",
            "CompactDescriptorResourceScenario.It_filters_whole_descriptor_URIs_on_direct_and_transitive_identity_paths",
            "It_filters_whole_descriptor_URIs_on_direct_and_transitive_identity_paths",
            fixtureSuffix: "CompactDescriptorResources"
        ),
        Api(
            "Api/CompactDescriptorResources/ReferencedDeleteProtection",
            "Referenced descriptor DELETE returns 409 with unchanged compact keys, resource representation and document stamps; deletion succeeds after removing the reference.",
            "CompactDescriptorResourceScenario.It_protects_referenced_descriptors_without_changing_rows_or_stamps",
            "It_protects_referenced_descriptors_without_changing_rows_or_stamps",
            fixtureSuffix: "CompactDescriptorResources"
        ),
        Api(
            "Api/CompactDescriptorResources/IdentityUpdatePropagation",
            "Regular-resource descriptor identity updates propagate compact keys and canonical URIs through two reference levels, advance stamps, maintain RI matching and reject stale references.",
            "CompactDescriptorResourceScenario.It_maintains_RI_and_copied_descriptor_keys_after_resource_identity_updates",
            "It_maintains_RI_and_copied_descriptor_keys_after_resource_identity_updates",
            fixtureSuffix: "CompactDescriptorResources"
        ),
        Api(
            "Api/CompactDescriptorResources/NamespaceAuthorization",
            "Stored/proposed namespace authorization dereferences compact keys for read/write/delete and paging; denied operations preserve rows and stamps.",
            "CompactDescriptorResourceScenario.It_authorizes_stored_and_proposed_namespaces_through_compact_descriptor_keys",
            "It_authorizes_stored_and_proposed_namespaces_through_compact_descriptor_keys",
            fixtureSuffix: "CompactDescriptorResources"
        ),
        Api(
            "Api/CompactDescriptorResources/LookupBatching",
            "Repeated root/collection descriptor occurrences share one lookup in the existing two-command create/update stream without an additional ID lookup round trip.",
            "CompactDescriptorResourceScenario.It_batches_repeated_descriptor_lookups_in_the_existing_write_command_stream",
            "It_batches_repeated_descriptor_lookups_in_the_existing_write_command_stream",
            fixtureSuffix: "CompactDescriptorResources"
        ),
        Api(
            "Api/CompactDescriptorResources/CustomViewDocumentBridge",
            "Direct and transitive custom-view bases bridge compact keys to owning bigint document membership; stored/proposed/page denials preserve data and stamps.",
            "CompactDescriptorResourceScenario.It_authorizes_custom_view_membership_by_the_owning_descriptor_document",
            "It_authorizes_custom_view_membership_by_the_owning_descriptor_document",
            fixtureSuffix: "CompactDescriptorCustomView"
        ),
        Api(
            "Api/CompactDescriptorHistory/QualifiedDeleteRouting",
            "Deleted descriptor/document/RI rows retain exact external IDs, type-specific component snapshots and versions; same-named cross-project recreation suppresses only its own ResourceKeyId.",
            "CompactDescriptorHistoryScenario.It_routes_deleted_descriptors_by_qualified_resource_key_without_live_rows",
            "It_routes_deleted_descriptors_by_qualified_resource_key_without_live_rows",
            fixtureSuffix: "DerivativeTrackedChanges_CompactDescriptors"
        ),
        Api(
            "Api/CompactDescriptorHistory/ProviderRecreationComparisons",
            "Descriptor recreation retains provider component collation verdicts for casing, namespace delimiter spaces, equal whole URIs with different components and extra delimiters.",
            "CompactDescriptorHistoryScenario.It_preserves_provider_component_comparisons_for_descriptor_recreation",
            "It_preserves_provider_component_comparisons_for_descriptor_recreation",
            fixtureSuffix: "DerivativeTrackedChanges_CompactDescriptors"
        ),
        Api(
            "Api/CompactDescriptorHistory/ResourceIdentitySnapshots",
            "Direct and copied descriptor identity changes snapshot exact old/new URIs from compact keys; no-ops add no history and deletes survive loss of both live descriptors.",
            "CompactDescriptorHistoryScenario.It_snapshots_direct_and_copied_descriptor_identities_for_resource_histories",
            "It_snapshots_direct_and_copied_descriptor_identities_for_resource_histories",
            fixtureSuffix: "DerivativeTrackedChanges_CompactDescriptors"
        ),
        Api(
            "Api/CompactDescriptorHistory/ResourceRecreation",
            "Recreated resources suppress their old deletes through the qualified descriptor compact key, preserving RI mixed-case matching and exact stored int values.",
            "CompactDescriptorHistoryScenario.It_recreates_resources_using_compact_descriptor_identity_joins",
            "It_recreates_resources_using_compact_descriptor_identity_joins",
            fixtureSuffix: "DerivativeTrackedChanges_CompactDescriptors"
        ),
        Api(
            "Api/CompactDescriptorHistory/ComponentUpdateAndNoOpHistory",
            "Accepted equal-whole-URI descriptor component POST advances representation stamps without key history; identical POST/PUT preserve metadata and delete snapshots retain final components.",
            "CompactDescriptorHistoryScenario.It_keeps_descriptor_component_updates_and_no_ops_out_of_delete_and_key_history",
            "It_keeps_descriptor_component_updates_and_no_ops_out_of_delete_and_key_history",
            fixtureSuffix: "DerivativeTrackedChanges_CompactDescriptors"
        ),
        Api(
            "Api/CompactDescriptorHistory/NamespaceHistoryAuthorization",
            "ReadChanges namespace filters use retained descriptor components and old resource identities after all live rows are deleted, with exact allow/deny results and counts.",
            "CompactDescriptorHistoryScenario.It_authorizes_retained_namespaces_and_old_descriptor_identity_snapshots",
            "It_authorizes_retained_namespaces_and_old_descriptor_identity_snapshots",
            fixtureSuffix: "DerivativeTrackedChanges_CompactDescriptors"
        ),
        Api(
            "Api/CompactDescriptorHistory/CustomViewLiveSeek",
            "Descriptor-basis custom views use owning DocumentId for live seeks and old-key authorization; compact keys or equal-URI cross-project membership cannot authorize histories.",
            "CompactDescriptorHistoryScenario.It_authorizes_descriptor_history_live_seeks_by_owning_document_membership",
            "It_authorizes_descriptor_history_live_seeks_by_owning_document_membership",
            fixtureSuffix: "DerivativeTrackedChanges_CustomView"
        ),
        Api(
            "Api/CompactDescriptorHistory/CustomViewTombstoneProbe",
            "IncludingDeletes seeks qualified descriptor tombstones and checks retained owning DocumentId membership after basis deletion, with cross-project and compact-key denial cases.",
            "CompactDescriptorHistoryScenario.It_authorizes_descriptor_history_tombstone_probes_by_qualified_document_membership",
            "It_authorizes_descriptor_history_tombstone_probes_by_qualified_document_membership",
            fixtureSuffix: "DerivativeTrackedChanges_CustomViewIncludingDeletes"
        ),
    ];

    private static ParityScenario Api(
        string id,
        string contract,
        string sharedEntryPoint,
        string method,
        bool profiled = false,
        string fixtureSuffix = ""
    )
    {
        string suffix = fixtureSuffix;
        if (suffix.Length == 0)
        {
            suffix = profiled ? "ProfileRootOnlyMerge_ProfiledHttp" : "CrudRoundTrip";
        }
        string pgFixture = $"Given_Postgresql_{suffix}";
        string mssqlFixture = $"Given_Mssql_{suffix}";

        return new ParityScenario
        {
            Id = id,
            Layer = ParityLayer.Api,
            BehavioralContract = contract,
            SharedEntryPoint = sharedEntryPoint,
            Boundary = ProductionBoundary.HttpPipeline,
            PgsqlLocations = [new ScenarioLocation($"{pgFixture}.cs", pgFixture, [method])],
            MssqlLocations = [new ScenarioLocation($"{mssqlFixture}.cs", mssqlFixture, [method])],
            PgsqlCoverage = EngineCoverage.Covered,
            MssqlCoverage = EngineCoverage.Covered,
            Classification = ParityClassification.Both,
        };
    }
}
