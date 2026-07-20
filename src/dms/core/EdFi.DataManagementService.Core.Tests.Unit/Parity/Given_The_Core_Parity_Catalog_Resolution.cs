// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DataManagementService.Backend.Tests.Common.Parity;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Parity;

/// <summary>
/// Reflection meta-test: every parity-catalog unit location owned by Core.Tests.Unit (the upstream profile
/// creatability-derivation proofs in <c>CreatabilityAnalyzer</c>) must resolve to exactly one <c>[Test]</c>
/// method in this assembly. The Backend.Tests.Unit-owned merge-synthesizer unit locations are resolved by the
/// backend meta-test against their own assembly; this pass resolves only the Core-owned locations so the
/// authoritative catalog machine-verifies the derivation half of the creatability contract. Pure reflection —
/// it requires no database connection.
/// </summary>
[TestFixture]
public class Given_The_Core_Parity_Catalog_Resolution
{
    [Test]
    public void It_resolves_every_core_owned_unit_location_to_a_declared_test_method() =>
        ParityCatalogResolution
            .ResolveUnitLocations(Assembly.GetExecutingAssembly(), UnitTestAssembly.CoreTestsUnit)
            .Should()
            .BeEmpty();
}
