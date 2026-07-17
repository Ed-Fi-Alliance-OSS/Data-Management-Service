// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DataManagementService.Backend.Tests.Common.Parity;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Parity;

/// <summary>
/// Reflection meta-tests proving the catalog's effective entry points name real members: every unit-test
/// location resolves to a declared <c>[Test]</c> method in this Backend.Tests.Unit assembly (the provider-
/// independent Na synthesizer entry points), and every Direct/Inherited Backend.Tests.Common shared entry point
/// resolves every named type in the common assembly. The API shared entry points are validated separately by
/// <c>Given_The_Api_Parity_Catalog_Resolution</c> against the API assembly, and the backend provider locations
/// by the per-engine backend meta-tests. Pure reflection — no database.
/// </summary>
[TestFixture]
public class Given_The_Parity_Catalog_Entry_Point_Resolution
{
    [Test]
    public void It_resolves_every_unit_location_to_a_declared_test_method() =>
        ParityCatalogResolution.ResolveUnitLocations(Assembly.GetExecutingAssembly()).Should().BeEmpty();

    [Test]
    public void It_resolves_every_common_shared_entry_point_to_a_real_type() =>
        ParityCatalogResolution
            .ResolveCommonSharedEntryPoints(typeof(ParityScenarioCatalog).Assembly)
            .Should()
            .BeEmpty();
}
