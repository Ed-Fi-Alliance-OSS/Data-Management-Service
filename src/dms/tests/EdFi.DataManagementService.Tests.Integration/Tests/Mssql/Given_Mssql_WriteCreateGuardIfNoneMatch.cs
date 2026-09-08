// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

public sealed class Given_Mssql_WriteCreateGuardIfNoneMatch : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.ProfileRootOnlyMerge;

    [Test]
    public Task It_permits_a_post_insert_under_a_wildcard_if_none_match() =>
        WriteCreateGuardIfNoneMatchScenario.It_permits_a_post_insert_under_a_wildcard_if_none_match(Harness);

    [Test]
    public Task It_rejects_a_post_upsert_to_an_existing_document_under_a_wildcard_if_none_match() =>
        WriteCreateGuardIfNoneMatchScenario.It_rejects_a_post_upsert_to_an_existing_document_under_a_wildcard_if_none_match(
            Harness
        );

    [Test]
    public Task It_rejects_an_existing_put_under_a_wildcard_if_none_match() =>
        WriteCreateGuardIfNoneMatchScenario.It_rejects_an_existing_put_under_a_wildcard_if_none_match(
            Harness
        );

    [Test]
    public Task It_returns_not_found_for_a_missing_put_under_a_wildcard_if_none_match() =>
        WriteCreateGuardIfNoneMatchScenario.It_returns_not_found_for_a_missing_put_under_a_wildcard_if_none_match(
            Harness
        );

    [Test]
    public Task It_rejects_an_existing_put_under_a_matching_specific_if_none_match() =>
        WriteCreateGuardIfNoneMatchScenario.It_rejects_an_existing_put_under_a_matching_specific_if_none_match(
            Harness
        );

    [Test]
    public Task It_prefers_if_match_when_both_headers_are_present() =>
        WriteCreateGuardIfNoneMatchScenario.It_prefers_if_match_when_both_headers_are_present(Harness);

    [Test]
    public Task It_rejects_an_existing_put_when_a_matching_tag_is_in_a_list() =>
        WriteCreateGuardIfNoneMatchScenario.It_rejects_an_existing_put_when_a_matching_tag_is_in_a_list(
            Harness
        );

    [Test]
    public Task It_permits_an_existing_put_when_no_tag_in_a_list_matches() =>
        WriteCreateGuardIfNoneMatchScenario.It_permits_an_existing_put_when_no_tag_in_a_list_matches(Harness);
}

/// <summary>
/// Exercises the deferred (post-proposed-authorization) precondition branch, which requires the real
/// authorization middleware and a resource with a relationship authorization boundary on Update.
/// </summary>
public sealed class Given_Mssql_WriteCreateGuardIfNoneMatch_DeferredAuthorizationPath
    : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override IReadOnlyList<long> ClientEducationOrganizationIds =>
        [RelationshipAuthorizationProblemDetailsScenario.ClaimEducationOrganizationId];

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        RelationshipAuthorizationProblemDetailsScenario.CreateReadDeleteUpdateClaimSetProvider(fixture);

    [Test]
    public Task It_returns_precondition_failure_on_the_deferred_path_for_an_existing_put_under_a_wildcard_if_none_match() =>
        WriteCreateGuardIfNoneMatchScenario.It_returns_precondition_failure_on_the_deferred_path_for_an_existing_put_under_a_wildcard_if_none_match(
            Harness
        );
}
