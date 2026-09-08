// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

public sealed class Given_Postgresql_ConditionalGetIfNoneMatch : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.ProfileRootOnlyMerge;
    protected override bool EnableAspNetCompression => true;

    [Test]
    public Task It_returns_304_for_a_matching_quoted_if_none_match() =>
        ConditionalGetIfNoneMatchScenario.It_returns_304_for_a_matching_quoted_if_none_match(Harness);

    [Test]
    public Task It_returns_304_for_a_matching_unquoted_if_none_match() =>
        ConditionalGetIfNoneMatchScenario.It_returns_304_for_a_matching_unquoted_if_none_match(Harness);

    [Test]
    public Task It_returns_304_for_a_wildcard_if_none_match() =>
        ConditionalGetIfNoneMatchScenario.It_returns_304_for_a_wildcard_if_none_match(Harness);

    [Test]
    public Task It_returns_200_for_a_stale_if_none_match() =>
        ConditionalGetIfNoneMatchScenario.It_returns_200_for_a_stale_if_none_match(Harness);

    [Test]
    public Task It_returns_200_when_only_the_content_coding_differs() =>
        ConditionalGetIfNoneMatchScenario.It_returns_200_when_only_the_content_coding_differs(Harness);

    [Test]
    public Task It_uses_distinct_validators_for_identity_and_gzip() =>
        ConditionalGetIfNoneMatchScenario.It_uses_distinct_validators_for_identity_and_gzip(Harness);

    [Test]
    public Task It_returns_304_when_a_matching_tag_is_in_a_list() =>
        ConditionalGetIfNoneMatchScenario.It_returns_304_when_a_matching_tag_is_in_a_list(Harness);

    [Test]
    public Task It_returns_304_for_a_weak_tag_in_a_list() =>
        ConditionalGetIfNoneMatchScenario.It_returns_304_for_a_weak_tag_in_a_list(Harness);

    [Test]
    public Task It_returns_200_when_no_tag_in_a_list_matches() =>
        ConditionalGetIfNoneMatchScenario.It_returns_200_when_no_tag_in_a_list_matches(Harness);
}
