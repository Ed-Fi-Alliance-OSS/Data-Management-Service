// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

/// <summary>
/// The education-organization projection endpoint over HTTP against PostgreSQL. See
/// <see cref="EducationOrganizationProjectionScenario"/>.
/// </summary>
public sealed class Given_Postgresql_EducationOrganizationProjection
    : PostgresqlEducationOrganizationProjectionTestBase
{
    /// <summary>
    /// PostgreSQL's <c>varchar(75)</c> counts code points, so the longest serialized name is 75
    /// supplementary characters, each escaped as a surrogate pair of 12 bytes.
    /// </summary>
    private static readonly string _longestName = string.Concat(Enumerable.Repeat("\U0001F600", 75));

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

    [Test]
    public Task It_bounds_the_response_body_with_the_longest_names() =>
        EducationOrganizationProjectionScenario.It_bounds_the_response_body(
            Context,
            "75 supplementary characters",
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
