// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

public sealed class Given_Mssql_WriteIgnoresIfNoneMatch : MssqlApiIntegrationTestBase
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
    public Task It_honors_a_matching_if_match_and_ignores_if_none_match_when_both_are_present() =>
        WriteIgnoresIfNoneMatchScenario.It_honors_a_matching_if_match_and_ignores_if_none_match_when_both_are_present(
            Harness
        );

    [Test]
    public Task It_rejects_a_stale_if_match_even_when_if_none_match_is_present() =>
        WriteIgnoresIfNoneMatchScenario.It_rejects_a_stale_if_match_even_when_if_none_match_is_present(
            Harness
        );
}
