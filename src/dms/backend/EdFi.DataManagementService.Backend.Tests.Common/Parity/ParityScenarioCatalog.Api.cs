// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;

namespace EdFi.DataManagementService.Backend.Tests.Common.Parity;

public static partial class ParityScenarioCatalog
{
    /// <summary>
    /// DMS-1022 API-level HTTP scenarios (commit 2cf6856f), mapped rather than duplicated. Each is
    /// a shared static scenario method driven by mirrored PostgreSQL and SQL Server wrapper
    /// fixtures, so all are Both/Covered on the HTTP pipeline boundary.
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
            "Api/EducationOrganizationProjection/IsolatesTenantsStoresAndRouteContexts",
            "A store answers only in its own tenant and route context (404 target-not-found otherwise, a qualifier mismatch indistinguishable from an unknown store), a cursor is bound to its tenant and qualifiers, a store registered after startup is found by one catalog reload, and an empty provisioned store reads as one empty page.",
            "EducationOrganizationProjectionScenario.It_isolates_tenants_stores_and_route_contexts",
            "It_isolates_tenants_stores_and_route_contexts"
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
    ];

    private static ParityScenario Api(
        string id,
        string contract,
        string sharedEntryPoint,
        string method,
        bool profiled = false
    )
    {
        string pgFixture = profiled
            ? "Given_Postgresql_ProfileRootOnlyMerge_ProfiledHttp"
            : "Given_Postgresql_CrudRoundTrip";
        string mssqlFixture = profiled
            ? "Given_Mssql_ProfileRootOnlyMerge_ProfiledHttp"
            : "Given_Mssql_CrudRoundTrip";

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
