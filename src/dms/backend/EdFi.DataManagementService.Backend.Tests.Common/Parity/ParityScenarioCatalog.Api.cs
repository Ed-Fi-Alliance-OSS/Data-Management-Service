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
            "limit/offset paging returns the expected window sizes and the union covers every seeded row.",
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
}
