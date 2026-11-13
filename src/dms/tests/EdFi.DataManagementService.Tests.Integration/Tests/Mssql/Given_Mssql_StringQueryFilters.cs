// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

/// <summary>
/// DMS-1563 string query filters against SQL Server: plan reuse across filter lengths, and the invalid-filter
/// rejections for values over the declared maxLength or wider than their column in UTF-16 code units.
/// </summary>
public sealed class Given_Mssql_StringQueryFilters : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override IReadOnlyList<string> ClientNamespacePrefixes =>
        NamespaceAuthorizationProblemDetailsScenario.ConfiguredPrefixes;

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        NamespaceAuthorizationProblemDetailsScenario.CreateReadUpdateDeleteClaimSetProvider(fixture);

    [Test]
    public Task It_reuses_the_cached_plans_for_string_filters_of_varying_lengths() =>
        StringQueryFilterScenario.It_reuses_the_cached_sql_server_plans_for_string_filters_of_varying_lengths(
            Harness
        );

    [Test]
    public Task It_rejects_a_filter_over_the_declared_max_length_rather_than_matching_its_prefix() =>
        StringQueryFilterScenario.It_rejects_a_filter_over_the_declared_max_length_rather_than_matching_its_prefix(
            Harness
        );

    [Test]
    public Task It_rejects_a_filter_wider_than_its_column() =>
        StringQueryFilterScenario.It_answers_a_filter_wider_than_its_sql_server_column_by_dialect(
            Harness,
            sqlServer: true
        );

    [Test]
    public Task It_rejects_over_long_and_over_wide_descriptor_filters() =>
        StringQueryFilterScenario.It_answers_over_long_descriptor_filters_by_dialect(
            Harness,
            sqlServer: true
        );
}
