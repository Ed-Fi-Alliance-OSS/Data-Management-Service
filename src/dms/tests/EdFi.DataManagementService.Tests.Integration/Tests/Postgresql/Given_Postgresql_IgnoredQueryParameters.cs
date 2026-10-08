// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

/// <summary>
/// Query parameters an operation ignores, served over seeded tracked changes so an ignored parameter
/// that filtered or paged would change a nonempty result.
/// </summary>
public sealed class Given_Postgresql_IgnoredQueryParameters : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    // A tracked key change only exists if an identity was updated, which DMS refuses unless the
    // resource is named here.
    protected override string AllowIdentityUpdateOverrides => "Student";

    // The Change Queries are served rather than refused, so the outcome is the operation's.
    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        new AllowAllClaimSetProvider(fixture, grantReadChanges: true);

    [SetUp]
    public Task Seed() => IgnoredQueryParameterScenario.SeedAsync(Harness);

    [Test]
    public Task It_serves_deletes_unchanged_by_ignored_filters_and_unknown_names() =>
        IgnoredQueryParameterScenario.It_serves_deletes_unchanged_by_ignored_filters_and_unknown_names(
            Harness
        );

    [Test]
    public Task It_serves_key_changes_unchanged_by_ignored_filters_and_unknown_names() =>
        IgnoredQueryParameterScenario.It_serves_key_changes_unchanged_by_ignored_filters_and_unknown_names(
            Harness
        );

    [Test]
    public Task It_still_pages_deletes_by_limit_and_offset() =>
        IgnoredQueryParameterScenario.It_still_pages_deletes_by_limit_and_offset(Harness);

    [Test]
    public Task It_serves_the_whole_collection_for_a_mistyped_filter() =>
        IgnoredQueryParameterScenario.It_serves_the_whole_collection_for_a_mistyped_filter(Harness);

    [Test]
    public Task It_serves_descriptors_unchanged_by_an_unknown_parameter() =>
        IgnoredQueryParameterScenario.It_serves_descriptors_unchanged_by_an_unknown_parameter(Harness);
}
