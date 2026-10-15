// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

/// <summary>
/// NamespaceBased ProblemDetails at the public HTTP boundary against PostgreSQL, for stored-value denials and
/// the DELETE authorization-before-precondition ordering.
/// </summary>
public sealed class Given_Postgresql_NamespaceAuthorizationProblemDetails_For_Read_And_Delete
    : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override IReadOnlyList<string> ClientNamespacePrefixes =>
        NamespaceAuthorizationProblemDetailsScenario.ConfiguredPrefixes;

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        NamespaceAuthorizationProblemDetailsScenario.CreateReadUpdateDeleteClaimSetProvider(fixture);

    [Test]
    public Task It_returns_namespace_mismatch_problem_details_for_get_by_id() =>
        NamespaceAuthorizationProblemDetailsScenario.It_returns_namespace_mismatch_problem_details_for_get_by_id(
            Harness
        );

    [Test]
    public Task It_returns_stored_uninitialized_problem_details_for_get_by_id() =>
        NamespaceAuthorizationProblemDetailsScenario.It_returns_stored_uninitialized_problem_details_for_get_by_id(
            Harness
        );

    [Test]
    public Task It_returns_403_rather_than_412_for_an_unauthorized_delete_with_a_stale_if_match() =>
        NamespaceAuthorizationProblemDetailsScenario.It_returns_403_rather_than_412_for_an_unauthorized_delete_with_a_stale_if_match(
            Harness
        );

    [Test]
    public Task It_returns_412_for_a_stale_delete_if_match_once_namespace_authorization_passes() =>
        NamespaceAuthorizationProblemDetailsScenario.It_returns_412_for_a_stale_delete_if_match_once_namespace_authorization_passes(
            Harness
        );
}

/// <summary>
/// NamespaceBased ProblemDetails for proposed values on POST create, where the omitted-namespace case proves a
/// missing proposed value reaches authorization through the real pipeline rather than being intercepted by JSON
/// schema validation. On PostgreSQL it also proves the untyped null proposed-namespace parameter is accepted.
/// </summary>
public sealed class Given_Postgresql_NamespaceAuthorizationProblemDetails_For_Create
    : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override IReadOnlyList<string> ClientNamespacePrefixes =>
        NamespaceAuthorizationProblemDetailsScenario.ConfiguredPrefixes;

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        NamespaceAuthorizationProblemDetailsScenario.CreateCreateClaimSetProvider(fixture);

    [Test]
    public Task It_returns_proposed_namespace_required_problem_details_for_post_create() =>
        NamespaceAuthorizationProblemDetailsScenario.It_returns_proposed_namespace_required_problem_details_for_post_create(
            Harness
        );

    [Test]
    public Task It_returns_proposed_namespace_mismatch_problem_details_for_post_create() =>
        NamespaceAuthorizationProblemDetailsScenario.It_returns_proposed_namespace_mismatch_problem_details_for_post_create(
            Harness
        );
}
