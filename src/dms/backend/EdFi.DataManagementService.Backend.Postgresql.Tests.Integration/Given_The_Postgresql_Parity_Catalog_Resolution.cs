// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DataManagementService.Backend.Tests.Common.Parity;
using FluentAssertions;
using NUnit.Framework;

// Sibling namespace outside the PostgreSQL integration setup-fixture scope so this pure-reflection
// meta-test never triggers database provisioning.
namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Parity;

/// <summary>
/// Reflection meta-test: every parity-catalog Profile/NoProfile row that declares PostgreSQL coverage
/// must resolve each declared location to exactly one <c>[Test]</c> method in this assembly. Pure
/// reflection — it requires no database connection.
/// </summary>
[TestFixture]
public class Given_The_Postgresql_Parity_Catalog_Resolution
{
    [Test]
    public void It_resolves_every_covered_postgresql_backend_parity_location_to_a_declared_test_method() =>
        ParityCatalogResolution
            .ResolveBackendCoveredLocations(ParityEngine.Pgsql, Assembly.GetExecutingAssembly())
            .Should()
            .BeEmpty();
}
