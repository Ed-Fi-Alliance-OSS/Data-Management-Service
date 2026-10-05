// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

/// <summary>
/// The education-organization projection endpoint over HTTP against SQL Server. See
/// <see cref="EducationOrganizationProjectionScenario"/>.
/// </summary>
public sealed class Given_Mssql_EducationOrganizationProjection : MssqlEducationOrganizationProjectionTestBase
{
    /// <summary>
    /// SQL Server's <c>nvarchar(75)</c> counts UTF-16 code units, so the longest serialized name is 37
    /// supplementary characters, each escaped as a surrogate pair of 12 bytes, and one more unit: a
    /// non-ASCII character the frontend writes as raw UTF-8.
    /// </summary>
    private static readonly string _longestName = string.Concat(Enumerable.Repeat("\U0001F600", 37)) + "é";

    [Test]
    public Task It_walks_the_hierarchy_in_identifier_order_with_parent_precedence() =>
        EducationOrganizationProjectionScenario.It_walks_the_hierarchy_in_identifier_order_with_parent_precedence(
            Context
        );

    [Test]
    public Task It_refuses_a_changed_set_and_honors_the_cursor_lifetime() =>
        EducationOrganizationProjectionScenario.It_refuses_a_changed_set_and_honors_the_cursor_lifetime(
            Context
        );

    [Test]
    public Task It_answers_the_authorization_matrix() =>
        EducationOrganizationProjectionScenario.It_answers_the_authorization_matrix(Context);

    [Test]
    public Task It_isolates_tenants_stores_and_route_contexts() =>
        EducationOrganizationProjectionScenario.It_isolates_tenants_stores_and_route_contexts(Context);

    [Test]
    public Task It_answers_each_target_state() =>
        EducationOrganizationProjectionScenario.It_answers_each_target_state(Context);

    [Test]
    public Task It_maps_read_failures_behind_a_cached_fingerprint_verdict() =>
        EducationOrganizationProjectionScenario.It_maps_read_failures_behind_a_cached_fingerprint_verdict(
            Context
        );

    [Test]
    public Task It_answers_an_unavailable_mapping_without_its_diagnostics() =>
        EducationOrganizationProjectionScenario.It_answers_an_unavailable_mapping_without_its_diagnostics(
            Context
        );

    [Test]
    public Task It_fails_closed_on_contradictory_relationships() =>
        EducationOrganizationProjectionScenario.It_fails_closed_on_contradictory_relationships(Context);

    [Test]
    public Task It_refuses_invalid_parameters_cursors_and_versions() =>
        EducationOrganizationProjectionScenario.It_refuses_invalid_parameters_cursors_and_versions(Context);

    [Test]
    public Task It_abandons_the_read_when_the_client_disconnects() =>
        EducationOrganizationProjectionScenario.It_abandons_the_read_when_the_client_disconnects(Context);

    /// <summary>SQL Server only: PostgreSQL text cannot hold a lone surrogate.</summary>
    [Test]
    [Ignore(
        "DMS-1440 step 2.8 finding, awaiting decision: Microsoft.Data.SqlClient decodes a stored lone "
            + "surrogate as U+FFFD, so the read never sees malformed UTF-16 and the projection answers 200 "
            + "with the replaced name instead of 409 projection-data-invalid."
    )]
    public Task It_refuses_a_name_that_is_not_well_formed_utf16() =>
        EducationOrganizationProjectionScenario.It_refuses_a_name_that_is_not_well_formed_utf16(Context);

    [Test]
    public Task It_bounds_the_response_body_with_the_longest_names() =>
        EducationOrganizationProjectionScenario.It_bounds_the_response_body(
            Context,
            "37 supplementary characters and one BMP character",
            _longestName
        );

    [Test]
    public Task It_bounds_the_response_body_with_control_characters() =>
        EducationOrganizationProjectionScenario.It_bounds_the_response_body(
            Context,
            "75 control characters",
            new string('\u0001', 75)
        );
}
