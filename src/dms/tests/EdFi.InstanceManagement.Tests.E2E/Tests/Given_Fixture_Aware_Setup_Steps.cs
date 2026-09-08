// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.InstanceManagement.Tests.E2E.Management;
using EdFi.InstanceManagement.Tests.E2E.StepDefinitions;
using FluentAssertions;

namespace EdFi.InstanceManagement.Tests.E2E.UnitTests;

[TestFixture]
[Category("InstanceFixtureUnit")]
public class Given_Fixture_Aware_Setup_Steps
{
    private InstanceManagementContext _context = null!;
    private InstanceSetupStepDefinitions _steps = null!;
    private InstanceFixtureState _state = null!;

    [SetUp]
    public void Setup()
    {
        _state = InstanceFixtureState.Parse(FixtureEnvironmentBuilder.Valid());
        _context = new InstanceManagementContext();
        _steps = new InstanceSetupStepDefinitions(_context);
    }

    [Test]
    public void It_hydrates_a_canonical_tenant_setup_without_creating_anything()
    {
        _steps.HydrateFixtureTenantSetup(_state, "Tenant_255901", ["255901/2024", "255901/2025"]);

        _context.CurrentTenant.Should().Be("Tenant_255901");
        _context.RouteQualifierToDataStoreId.Should().ContainKey("255901/2024");
        // Zero CMS creation: no scenario-owned records were produced.
        _context.ScenarioOwnedVendors.Should().BeEmpty();
        _context.ScenarioOwnedDataStores.Should().BeEmpty();
    }

    [Test]
    public void It_fails_when_a_requested_route_is_not_owned_by_the_canonical_tenant()
    {
        // 255902/2024 belongs to Tenant_255902, not Tenant_255901.
        var act = () => _steps.HydrateFixtureTenantSetup(_state, "Tenant_255901", ["255902/2024"]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*255902/2024*Tenant_255901*");
    }

    [Test]
    public void It_hydrates_a_canonical_tenant_application_without_creating_anything()
    {
        _steps.HydrateFixtureApplication(_state, "Tenant_255902", "255902");

        _context.ClientKey.Should().Be(FixtureEnvironmentBuilder.Tenant2Key);
        _context.ClientSecret.Should().Be(FixtureEnvironmentBuilder.Tenant2Secret);
        _context.ApplicationId.Should().Be(302);
        _context.ScenarioOwnedApplications.Should().BeEmpty();
    }

    [Test]
    public void It_fails_when_the_canonical_tenant_does_not_own_the_requested_district()
    {
        var act = () => _steps.HydrateFixtureApplication(_state, "Tenant_255901", "999999");

        act.Should().Throw<InvalidOperationException>().WithMessage("*Tenant_255901*999999*");
    }
}
