// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

public sealed class Given_Postgresql_WriteIgnoresIfNoneMatch : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.ProfileRootOnlyMerge;

    [Test]
    public Task It_ignores_a_wildcard_if_none_match_on_a_post_of_a_new_document() =>
        WriteIgnoresIfNoneMatchScenario.It_ignores_a_wildcard_if_none_match_on_a_post_of_a_new_document(
            Harness
        );

    [Test]
    public Task It_ignores_a_wildcard_if_none_match_on_a_post_to_an_existing_document() =>
        WriteIgnoresIfNoneMatchScenario.It_ignores_a_wildcard_if_none_match_on_a_post_to_an_existing_document(
            Harness
        );

    [Test]
    public Task It_ignores_a_matching_specific_if_none_match_on_a_post_to_an_existing_document() =>
        WriteIgnoresIfNoneMatchScenario.It_ignores_a_matching_specific_if_none_match_on_a_post_to_an_existing_document(
            Harness
        );

    [Test]
    public Task It_ignores_a_wildcard_if_none_match_on_a_put_to_an_existing_document() =>
        WriteIgnoresIfNoneMatchScenario.It_ignores_a_wildcard_if_none_match_on_a_put_to_an_existing_document(
            Harness
        );

    [Test]
    public Task It_returns_not_found_for_a_put_to_a_missing_target_under_a_wildcard_if_none_match() =>
        WriteIgnoresIfNoneMatchScenario.It_returns_not_found_for_a_put_to_a_missing_target_under_a_wildcard_if_none_match(
            Harness
        );

    [Test]
    public Task It_ignores_a_matching_specific_if_none_match_on_a_put_to_an_existing_document() =>
        WriteIgnoresIfNoneMatchScenario.It_ignores_a_matching_specific_if_none_match_on_a_put_to_an_existing_document(
            Harness
        );

    [Test]
    public Task It_ignores_a_matching_tag_in_an_if_none_match_list_on_a_put_to_an_existing_document() =>
        WriteIgnoresIfNoneMatchScenario.It_ignores_a_matching_tag_in_an_if_none_match_list_on_a_put_to_an_existing_document(
            Harness
        );

    [Test]
    public Task It_ignores_a_wildcard_if_none_match_on_a_delete_of_an_existing_document() =>
        WriteIgnoresIfNoneMatchScenario.It_ignores_a_wildcard_if_none_match_on_a_delete_of_an_existing_document(
            Harness
        );

    [Test]
    public Task It_prefers_if_match_when_both_headers_are_present() =>
        WriteIgnoresIfNoneMatchScenario.It_prefers_if_match_when_both_headers_are_present(Harness);
}

/// <summary>
/// Exercises the deferred (post-proposed-authorization) precondition branch, which requires the real
/// authorization middleware and a resource with a relationship authorization boundary on Update.
/// </summary>
public sealed class Given_Postgresql_WriteIgnoresIfNoneMatch_DeferredAuthorizationPath
    : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override IReadOnlyList<long> ClientEducationOrganizationIds =>
        [RelationshipAuthorizationProblemDetailsScenario.ClaimEducationOrganizationId];

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        RelationshipAuthorizationProblemDetailsScenario.CreateReadDeleteUpdateClaimSetProvider(fixture);

    [Test]
    public Task It_ignores_a_wildcard_if_none_match_on_the_deferred_path_for_an_existing_put() =>
        WriteIgnoresIfNoneMatchScenario.It_ignores_a_wildcard_if_none_match_on_the_deferred_path_for_an_existing_put(
            Harness
        );
}
