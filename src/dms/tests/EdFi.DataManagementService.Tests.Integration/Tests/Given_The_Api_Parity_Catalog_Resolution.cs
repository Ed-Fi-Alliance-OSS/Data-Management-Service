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
[TestFixture]
public class Given_The_Api_Parity_Catalog_Resolution
{
    [Test]
    public void It_resolves_every_covered_api_parity_location_to_a_declared_test_method() =>
        ParityCatalogResolution
            .ResolveApiCoveredLocations(Assembly.GetExecutingAssembly())
            .Should()
            .BeEmpty();
}
