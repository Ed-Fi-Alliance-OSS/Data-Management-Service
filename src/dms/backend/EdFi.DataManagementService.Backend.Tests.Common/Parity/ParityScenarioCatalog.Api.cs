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
            "Api/CrudRoundTrip/PutBodyId",
            "PUT uses the URL ID for absent, null, matching, different and empty GUID body IDs; rejects malformed IDs without changing either seeded resource.",
            "PutBodyIdScenario.It_uses_the_url_id_for_student_puts",
            "It_uses_the_url_id_for_student_puts"
        ),
        Api(
            "Api/DescriptorRuntime/PutBodyId",
            "Descriptor PUT uses the URL ID and validates supplied body IDs before normalization.",
            "PutBodyIdScenario.It_uses_the_url_id_for_descriptor_puts",
            "It_uses_the_url_id_for_descriptor_puts",
            fixtureSuffix: "DescriptorRuntime"
        ),
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
            "Api/IdentityConflict/PostOnSharedAbstractIdentity",
            "A POST whose identity collides with another subclass on a shared abstract identity is rejected as 409 urn:ed-fi:api:data-conflict:non-unique-identity, naming each duplicate value by its full property path so the two educationOrganizationId values stay distinct.",
            "IdentityConflictScenario.It_reports_non_unique_identity_with_qualified_names_for_a_post_on_a_shared_abstract_identity",
            "It_reports_non_unique_identity_with_qualified_names_for_a_post_on_a_shared_abstract_identity",
            fixtureSuffix: "IdentityConflict"
        ),
        Api(
            "Api/IdentityConflict/PutOnNaturalKey",
            "A PUT that renames a resource onto another resource's natural key is rejected as 409 urn:ed-fi:api:data-conflict:non-unique-identity, naming each duplicate value by its full property path.",
            "IdentityConflictScenario.It_reports_non_unique_identity_with_qualified_names_for_a_put_that_collides_on_a_natural_key",
            "It_reports_non_unique_identity_with_qualified_names_for_a_put_that_collides_on_a_natural_key",
            fixtureSuffix: "IdentityConflict"
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
        Projection(
            "Api/EducationOrganizationProjection/WalksTheHierarchyWithParentPrecedence",
            "Paged and single-page reads return every core education organization in ascending int64 order (negative, beyond int32 and beyond double precision), each parent chosen by precedence, five members per item, edfi.* discriminators only, post-secondary institutions excluded, and an unchanged cursor replays the identical page.",
            "EducationOrganizationProjectionScenario.It_walks_the_hierarchy_in_identifier_order_with_parent_precedence",
            "It_walks_the_hierarchy_in_identifier_order_with_parent_precedence"
        ),
        Projection(
            "Api/EducationOrganizationProjection/RefusesAChangedSetAndHonorsTheCursorLifetime",
            "A committed rename between pages refuses later pages as 409 projection-changed and a restarted read completes; a cursor is honored for its lifetime from the start of its read and refused as invalid-cursor after it.",
            "EducationOrganizationProjectionScenario.It_refuses_a_changed_set_and_honors_the_cursor_lifetime",
            "It_refuses_a_changed_set_and_honors_the_cursor_lifetime"
        ),
        Projection(
            "Api/EducationOrganizationProjection/AnswersTheAuthorizationMatrix",
            "Missing, non-bearer and unknown tokens are 401; resource-only, identity-only, Read-less and unknown claim sets are 403; a Read grant under another strategy is 500 security configuration; unbound and unavailable client bindings are 401 and 503; unknown and malformed tenants are 404 and 400; all before parameters are read. The projection credential reads with no data stores and is denied by the resource API; the smoke claim set never grants the claim.",
            "EducationOrganizationProjectionScenario.It_answers_the_authorization_matrix",
            "It_answers_the_authorization_matrix"
        ),
        Projection(
            "Api/EducationOrganizationProjection/RejectsABearerTokenOutsideItsLifetime",
            "A bearer token signed for the projection credential and checked by the production JWT validator is 401 once expired and before its start, and reads the projection within its lifetime.",
            "EducationOrganizationProjectionScenario.It_rejects_a_bearer_token_outside_its_lifetime",
            "It_rejects_a_bearer_token_outside_its_lifetime"
        ),
        Projection(
            "Api/EducationOrganizationProjection/IsolatesTenantsStoresAndRouteContexts",
            "A store answers only in its own tenant and route context (404 target-not-found otherwise, a qualifier mismatch indistinguishable from an unknown store), a cursor is bound to its tenant and qualifiers, a store id requested before the catalog holds it is 404 after exactly one reload and served after one more reload once registered, and an empty provisioned store reads as one empty page.",
            "EducationOrganizationProjectionScenario.It_isolates_tenants_stores_and_route_contexts",
            "It_isolates_tenants_stores_and_route_contexts"
        ),
        Projection(
            "Api/EducationOrganizationProjection/ServesAChangedRouteContextOnlyAfterACatalogRefresh",
            "A cached store whose route context changes is 404 target-not-found under the new context with no catalog reload, and is served under it once the expiry-driven refresh replaces the cache, the old context then being the mismatch.",
            "EducationOrganizationProjectionScenario.It_serves_a_changed_route_context_only_after_a_catalog_refresh",
            "It_serves_a_changed_route_context_only_after_a_catalog_refresh"
        ),
        Projection(
            "Api/EducationOrganizationProjection/AnswersEachTargetState",
            "Missing provider metadata is served by the registered dialect; the other dialect and an unknown token are 409 target-provider-unsupported; no connection string is 503 service-configuration-error; an absent database is 503 target-unavailable; mismatched and malformed fingerprints are 409 target-schema-incompatible; no fingerprint row is 503 database-not-provisioned; an unloadable catalog is 503 service-unavailable; nothing provider-supplied is disclosed.",
            "EducationOrganizationProjectionScenario.It_answers_each_target_state",
            "It_answers_each_target_state"
        ),
        Projection(
            "Api/EducationOrganizationProjection/MapsReadFailuresBehindACachedFingerprintVerdict",
            "With the fingerprint verdict cached, an unresolvable host fails the read's own acquisition as 503 target-unavailable and a dropped column fails its execution as 409 target-schema-incompatible, classified by the provider's code, with neither host nor column disclosed.",
            "EducationOrganizationProjectionScenario.It_maps_read_failures_behind_a_cached_fingerprint_verdict",
            "It_maps_read_failures_behind_a_cached_fingerprint_verdict"
        ),
        Projection(
            "Api/EducationOrganizationProjection/AnswersAnUnavailableMappingWithoutItsDiagnostics",
            "A mapping set that cannot be produced is the fixed 409 projection-unsupported body; its message and diagnostics reach neither the response nor a log.",
            "EducationOrganizationProjectionScenario.It_answers_an_unavailable_mapping_without_its_diagnostics",
            "It_answers_an_unavailable_mapping_without_its_diagnostics"
        ),
        Projection(
            "Api/EducationOrganizationProjection/FailsClosedOnContradictoryRelationships",
            "A parent cycle or a self-parent anywhere in the set refuses every page as 409 projection-data-invalid, including outside the requested page and ahead of the digest comparison, and a repaired set reads again.",
            "EducationOrganizationProjectionScenario.It_fails_closed_on_contradictory_relationships",
            "It_fails_closed_on_contradictory_relationships"
        ),
        Projection(
            "Api/EducationOrganizationProjection/RefusesInvalidParametersCursorsAndVersions",
            "Invalid and repeated parameters are 400 parameter-validation-failed with exact errors; undecodable, padded and other-store cursors are 400 invalid-cursor; an unsupported contract version is 400 unsupported-contract-version; the supported version is echoed.",
            "EducationOrganizationProjectionScenario.It_refuses_invalid_parameters_cursors_and_versions",
            "It_refuses_invalid_parameters_cursors_and_versions"
        ),
        Projection(
            "Api/EducationOrganizationProjection/AbandonsTheReadWhenTheClientDisconnects",
            "A client that disconnects while the read waits on a lock ends the read: its session stops waiting, its transaction ends, and the store serves the next request.",
            "EducationOrganizationProjectionScenario.It_abandons_the_read_when_the_client_disconnects",
            "It_abandons_the_read_when_the_client_disconnects"
        ),
        Projection(
            "Api/EducationOrganizationProjection/ServesStoredNamesUnchanged",
            "A supplementary character, a literal U+FFFD, an empty short name and a null short name are served exactly as stored.",
            "EducationOrganizationProjectionScenario.It_serves_stored_names_unchanged",
            "It_serves_stored_names_unchanged"
        ),
        Projection(
            "Api/EducationOrganizationProjection/BoundsTheResponseBodyWithTheLongestNames",
            "Items with 20-character int64 identifiers and the engine's longest serialized names stay within the 2,048-byte item, 512-byte envelope and page bounds, each name within 6 bytes per UTF-16 unit and decoded unchanged.",
            "EducationOrganizationProjectionScenario.It_bounds_the_response_body",
            "It_bounds_the_response_body_with_the_longest_names"
        ),
        Projection(
            "Api/EducationOrganizationProjection/BoundsTheResponseBodyWithControlCharacters",
            "The same bounds hold for names of 75 control characters, which the serializer escapes.",
            "EducationOrganizationProjectionScenario.It_bounds_the_response_body",
            "It_bounds_the_response_body_with_control_characters"
        ),
        Projection(
            "Api/EducationOrganizationProjection/RefusesASetLargerThanTheRowCap",
            "A set at the configured row cap is served; one more row is 409 projection-too-large.",
            "EducationOrganizationProjectionScenario.It_refuses_a_set_larger_than_the_row_cap",
            "It_refuses_a_set_larger_than_the_row_cap",
            "Limits"
        ),
        Projection(
            "Api/EducationOrganizationProjection/AnswersALockTimeoutAsTargetUnavailable",
            "A read that cannot take its locks within the lock timeout is 503 target-unavailable, classified in the execute stage.",
            "EducationOrganizationProjectionScenario.It_answers_a_lock_timeout_as_target_unavailable",
            "It_answers_a_lock_timeout_as_target_unavailable",
            "Limits"
        ),
        Projection(
            "Api/EducationOrganizationProjection/ReadsThePrimaryWhenTheStorePublishesDerivatives",
            "With a snapshot and a read replica published and unreachable, and the snapshot requested, the read and its fingerprint use only the primary.",
            "EducationOrganizationProjectionScenario.It_reads_the_primary_when_the_store_publishes_derivatives",
            "It_reads_the_primary_when_the_store_publishes_derivatives",
            "Derivatives"
        ),
        Projection(
            "Api/EducationOrganizationProjection/AnswersTheRateLimitWithTheInheritedProblem",
            "A request over the host rate limit is the inherited 429 too-many-requests problem, marked no-store.",
            "EducationOrganizationProjectionScenario.It_answers_the_rate_limit_with_the_inherited_problem",
            "It_answers_the_rate_limit_with_the_inherited_problem",
            "RateLimit"
        ),
        Api(
            "Api/DescriptorRuntime/StoredWinsPostIdentity",
            "An authorized POST matched by descriptor identity keeps the stored components: an equal-URI re-split alone is a no-op with the same ETag, and a descriptive change updates with the stored components, retained IDs and advanced stamps.",
            "DescriptorRuntimeScenario.It_keeps_the_stored_components_for_an_equal_whole_URI_post",
            "It_keeps_the_stored_components_for_an_equal_whole_URI_post",
            fixtureSuffix: "CompactDescriptorRuntime"
        ),
        Api(
            "Api/DescriptorRuntime/StoredWinsCaseVariantPost",
            "An authorized POST that differs from the stored descriptor only in casing keeps the stored casing: alone it is a no-op with the same ETag, and with a descriptive change it updates with the stored identity, retained IDs and advanced stamps.",
            "DescriptorRuntimeScenario.It_keeps_the_stored_casing_for_a_case_variant_post",
            "It_keeps_the_stored_casing_for_a_case_variant_post",
            fixtureSuffix: "CompactDescriptorRuntime"
        ),
        Api(
            "Api/DescriptorRuntime/EqualIdentityPut",
            "A PUT whose components reconstruct the stored whole URI, or differ from it only in casing, keeps the stored identity and returns 204 with unchanged stamps and ETag.",
            "DescriptorRuntimeScenario.It_keeps_the_stored_identity_for_an_equal_identity_put",
            "It_keeps_the_stored_identity_for_an_equal_identity_put",
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
            "Api/DescriptorRuntime/QualifiedTypeIsolation",
            "Identical URIs in distinct types and same-named cross-project types create independent rows; updates and deletes route to the qualified type.",
            "DescriptorRuntimeScenario.It_isolates_identical_URIs_across_descriptor_types_and_projects",
            "It_isolates_identical_URIs_across_descriptor_types_and_projects",
            fixtureSuffix: "CompactDescriptorRuntime"
        ),
        Api(
            "Api/DescriptorRuntime/ImmutablePutIdentity",
            "A PUT whose whole URI is a different descriptor identity under the engine's comparison (not a casing change or component re-split) returns 400 with exact problem details and unchanged storage/stamps.",
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
            "Repeated resource POSTs and transitive descriptor-bearing document identities resolve mixed-case URIs by natural key with stable document IDs and exact no-op stamps.",
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
            "Regular-resource descriptor identity updates propagate compact keys and canonical URIs through two reference levels, advance stamps, keep natural-key matching and reject stale references.",
            "CompactDescriptorResourceScenario.It_maintains_copied_descriptor_keys_after_resource_identity_updates",
            "It_maintains_copied_descriptor_keys_after_resource_identity_updates",
            fixtureSuffix: "CompactDescriptorResources"
        ),
        Api(
            "Api/CompactDescriptorResources/NamespaceAuthorization",
            "Scalar root namespace authorization is preserved on resources with compact descriptor references; denied read/write/delete and paging operations preserve rows and stamps.",
            "CompactDescriptorResourceScenario.It_preserves_scalar_namespace_authorization_with_compact_descriptor_references",
            "It_preserves_scalar_namespace_authorization_with_compact_descriptor_references",
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
            "Deleted descriptor/document rows retain exact external IDs, type-specific component snapshots and versions; same-named cross-project recreation suppresses only its own ResourceKeyId.",
            "CompactDescriptorHistoryScenario.It_routes_deleted_descriptors_by_qualified_resource_key_without_live_rows",
            "It_routes_deleted_descriptors_by_qualified_resource_key_without_live_rows",
            fixtureSuffix: "DerivativeTrackedChanges_CompactDescriptors"
        ),
        Api(
            "Api/CompactDescriptorHistory/LoweredUriRecreation",
            "Descriptor recreation suppresses by lowered whole URI plus ResourceKeyId on both providers: casing and equal whole URIs with different components suppress, a namespace trailing space does not, and extra delimiters compare exactly.",
            "CompactDescriptorHistoryScenario.It_suppresses_descriptor_recreation_by_lowered_uri_and_resource_key",
            "It_suppresses_descriptor_recreation_by_lowered_uri_and_resource_key",
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
            "Recreated resources suppress their old deletes through the qualified descriptor compact key found by lowered whole URI, preserving mixed-case natural-key matching and exact stored int values.",
            "CompactDescriptorHistoryScenario.It_recreates_resources_using_compact_descriptor_identity_joins",
            "It_recreates_resources_using_compact_descriptor_identity_joins",
            fixtureSuffix: "DerivativeTrackedChanges_CompactDescriptors"
        ),
        Api(
            "Api/CompactDescriptorHistory/ComponentUpdateAndNoOpHistory",
            "An equal-whole-URI descriptor component POST or PUT keeps the stored components and is a no-op without stamps, ETag change or key history; delete snapshots retain the stored components.",
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
        Api(
            "Api/CaseVariantIdentity/ResolvesCaseVariantPostByIdentityCollation",
            "A POST whose root identity differs from a stored document's only in case targets the stored document where the identity collation equates them, keeps the stored casing, and is a guarded no-op that records no key change and leaves the referrer's copy unchanged.",
            "CaseVariantIdentityScenario.It_resolves_a_case_variant_class_period_post_by_the_identity_collation",
            "It_resolves_a_case_variant_class_period_post_by_the_identity_collation",
            fixtureSuffix: "CaseVariantIdentity",
            diff: new DialectDifference(
                "SQL Server answers 200 against the stored document with its stored casing; PostgreSQL answers 201 with a second document.",
                "SQL Server identity text columns use the case-insensitive SQL_Latin1_General_CP1_CI_AS collation, so the natural-key capture finds the stored document and the identity-stability guard rebinds the CI-equal value; PostgreSQL identity text compares ordinally, so the case variant is a different identity."
            )
        ),
    ];

    private static ParityScenario Api(
        string id,
        string contract,
        string sharedEntryPoint,
        string method,
        bool profiled = false,
        string fixtureSuffix = "",
        DialectDifference? diff = null
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
            DialectDifference = diff,
        };
    }

    /// <summary>
    /// A DMS-1440 education-organization projection row, covered by the mirrored
    /// <c>Given_{Engine}_EducationOrganizationProjection{fixtureSuffix}</c> fixtures.
    /// </summary>
    private static ParityScenario Projection(
        string id,
        string contract,
        string sharedEntryPoint,
        string method,
        string fixtureSuffix = ""
    )
    {
        string pgFixture = $"Given_Postgresql_EducationOrganizationProjection{fixtureSuffix}";
        string mssqlFixture = $"Given_Mssql_EducationOrganizationProjection{fixtureSuffix}";

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
