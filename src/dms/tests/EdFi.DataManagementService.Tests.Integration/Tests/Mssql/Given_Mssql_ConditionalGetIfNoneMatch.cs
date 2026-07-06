// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

public sealed class Given_Mssql_ConditionalGetIfNoneMatch : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.ProfileRootOnlyMerge;

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
    public Task It_returns_200_when_only_the_variant_key_differs() =>
        ConditionalGetIfNoneMatchScenario.It_returns_200_when_only_the_variant_key_differs(Harness);
}
