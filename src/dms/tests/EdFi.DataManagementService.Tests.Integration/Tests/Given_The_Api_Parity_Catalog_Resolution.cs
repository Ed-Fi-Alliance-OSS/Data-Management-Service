// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DataManagementService.Backend.Tests.Common.Parity;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Tests;

/// <summary>
/// Reflection meta-test: every Api-layer parity-catalog row must resolve each declared covered
/// PostgreSQL and SQL Server location to exactly one <c>[Test]</c> method in this API integration
/// assembly. Pure reflection — it requires no database connection.
/// </summary>
/// <remarks>
/// Carries the API CI-selection categories so both API integration lanes select it: each lane runs an
/// AND filter (<c>Category=ApiIntegration&amp;Category=PostgresqlIntegration</c> or
/// <c>Category=ApiIntegration&amp;Category=MssqlIntegration</c>). It deliberately does not inherit the API
/// integration database base, so it leases no database despite carrying the engine categories.
/// <see cref="ApiParityCatalogResolutionCiSelectionGuardrail"/> keeps these categories from being dropped.
/// </remarks>
[TestFixture]
[Category("ApiIntegration")]
[Category("PostgresqlIntegration")]
[Category("MssqlIntegration")]
public class Given_The_Api_Parity_Catalog_Resolution
{
    [Test]
    public void It_resolves_every_covered_api_parity_location_to_a_declared_test_method() =>
        ParityCatalogResolution
            .ResolveApiCoveredLocations(Assembly.GetExecutingAssembly())
            .Should()
            .BeEmpty();
}

/// <summary>
/// Pure-reflection guardrail that keeps <see cref="Given_The_Api_Parity_Catalog_Resolution"/> selected by
/// both API CI lanes. It carries the same three CI-selection categories so it runs in each lane, and asserts
/// that the target meta-test still declares at least those categories. If a category is accidentally removed
/// from the target, this guardrail — still selected by the lane it shares — fails with an actionable message.
/// It inherits no database base, leases no database, and does not inspect or string-match workflow YAML.
/// </summary>
[TestFixture]
[Category("ApiIntegration")]
[Category("PostgresqlIntegration")]
[Category("MssqlIntegration")]
public class ApiParityCatalogResolutionCiSelectionGuardrail
{
    private static readonly string[] RequiredCiSelectionCategories =
    [
        "ApiIntegration",
        "PostgresqlIntegration",
        "MssqlIntegration",
    ];

    [Test]
    public void It_keeps_the_api_parity_catalog_resolution_meta_test_selected_by_both_api_ci_lanes()
    {
        string[] declaredCategories =
        [
            .. typeof(Given_The_Api_Parity_Catalog_Resolution)
                .GetCustomAttributes<CategoryAttribute>(inherit: true)
                .Select(category => category.Name),
        ];

        string[] missingCategories = [.. RequiredCiSelectionCategories.Except(declaredCategories)];

        missingCategories
            .Should()
            .BeEmpty(
                "{0} must declare the API CI-selection categories {1} so both API lanes "
                    + "(Category=ApiIntegration&Category=PostgresqlIntegration and "
                    + "Category=ApiIntegration&Category=MssqlIntegration) keep selecting it",
                nameof(Given_The_Api_Parity_Catalog_Resolution),
                string.Join(", ", RequiredCiSelectionCategories)
            );
    }
}
