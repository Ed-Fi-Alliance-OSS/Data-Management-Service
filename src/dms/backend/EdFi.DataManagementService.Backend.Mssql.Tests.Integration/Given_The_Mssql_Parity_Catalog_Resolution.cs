// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DataManagementService.Backend.Mssql.Tests.Integration;
using EdFi.DataManagementService.Backend.Tests.Common.Parity;
using FluentAssertions;
using NUnit.Framework;

// Sibling namespace outside the MSSQL integration setup-fixture scope so this pure-reflection
// meta-test never triggers database provisioning; the Integration namespace is imported only for the
// internal MssqlCiShards.Shard4 category constant.
namespace EdFi.DataManagementService.Backend.Mssql.Tests.Parity;

/// <summary>
/// Reflection meta-test: every parity-catalog Profile/NoProfile row that declares SQL Server coverage
/// must resolve each declared location to exactly one <c>[Test]</c> method in this assembly. Pure
/// reflection — it requires no database connection, and carries a single MssqlCiShards category so the
/// CI shard guardrail stays valid.
/// </summary>
[TestFixture]
[Category(MssqlCiShards.Shard4)]
public class Given_The_Mssql_Parity_Catalog_Resolution
{
    [Test]
    public void It_resolves_every_covered_sql_server_backend_parity_location_to_a_declared_test_method() =>
        ParityCatalogResolution
            .ResolveBackendCoveredLocations(ParityEngine.Mssql, Assembly.GetExecutingAssembly())
            .Should()
            .BeEmpty();
}
