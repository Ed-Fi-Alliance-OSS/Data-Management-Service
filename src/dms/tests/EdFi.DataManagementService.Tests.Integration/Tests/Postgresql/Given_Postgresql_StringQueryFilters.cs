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
/// DMS-1563 string query filters against PostgreSQL: the declared maxLength rejection applies, while the SQL Server
/// storage-width rejection does not.
/// </summary>
public sealed class Given_Postgresql_StringQueryFilters : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override IReadOnlyList<string> ClientNamespacePrefixes =>
        NamespaceAuthorizationProblemDetailsScenario.ConfiguredPrefixes;

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        NamespaceAuthorizationProblemDetailsScenario.CreateReadUpdateDeleteClaimSetProvider(fixture);

    [Test]
    public Task It_rejects_a_filter_over_the_declared_max_length_rather_than_matching_its_prefix() =>
        StringQueryFilterScenario.It_rejects_a_filter_over_the_declared_max_length_rather_than_matching_its_prefix(
            Harness
        );

    [Test]
    public Task It_accepts_a_filter_wider_than_a_sql_server_column_would_hold() =>
        StringQueryFilterScenario.It_answers_a_filter_wider_than_its_sql_server_column_by_dialect(
            Harness,
            sqlServer: false
        );

    [Test]
    public Task It_rejects_over_long_descriptor_filters_and_accepts_over_wide_ones() =>
        StringQueryFilterScenario.It_answers_over_long_descriptor_filters_by_dialect(
            Harness,
            sqlServer: false
        );
}
