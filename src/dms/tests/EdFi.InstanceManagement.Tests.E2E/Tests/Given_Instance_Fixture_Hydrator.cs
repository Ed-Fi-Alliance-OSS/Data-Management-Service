// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.InstanceManagement.Tests.E2E.Management;
using FluentAssertions;

namespace EdFi.InstanceManagement.Tests.E2E.UnitTests;

[TestFixture]
[Category("InstanceFixtureUnit")]
public class Given_The_Fixture_Is_Hydrated_Into_A_Fresh_Context
{
    private InstanceManagementContext _context = null!;
    private InstanceFixtureState _state = null!;

    [SetUp]
    public void Setup()
    {
        _state = InstanceFixtureState.Parse(FixtureEnvironmentBuilder.Valid());
        _context = new InstanceManagementContext();
        InstanceFixtureHydrator.HydrateAll(_context, _state);
    }

    [Test]
    public void It_hydrates_credentials_for_both_tenants()
    {
        _context.CredentialsByTenant.Should().ContainKey("Tenant_255901");
        _context.CredentialsByTenant.Should().ContainKey("Tenant_255902");
        _context.CredentialsByTenant["Tenant_255901"].Key.Should().Be(FixtureEnvironmentBuilder.Tenant1Key);
    }

    [Test]
    public void It_hydrates_the_route_to_data_store_maps()
    {
        _context.RouteQualifierToDataStoreId["255901/2024"].Should().Be(201);
        _context.RouteQualifierToDataStoreId["255902/2024"].Should().Be(203);
        _context.DataStoreIdToTenant[203].Should().Be("Tenant_255902");
    }

    [Test]
    public void It_sets_the_immutable_fixture_guard_sets()
    {
        _context.FixtureApplicationIds.Should().BeEquivalentTo(new List<int> { 301, 302 });
        _context.FixtureVendorIds.Should().BeEquivalentTo(new List<int> { 101, 102 });
        _context.FixtureDataStoreIds.Should().BeEquivalentTo(new List<int> { 201, 202, 203, 204 });
    }

    [Test]
    public void It_never_marks_hydrated_fixture_records_as_scenario_owned()
    {
        _context.ScenarioOwnedApplications.Should().BeEmpty();
        _context.ScenarioOwnedDataStores.Should().BeEmpty();
        _context.ScenarioOwnedVendors.Should().BeEmpty();
    }

    [Test]
    public void It_defaults_legacy_credentials_to_the_primary_fixture_tenant()
    {
        _context.ClientKey.Should().Be(FixtureEnvironmentBuilder.Tenant1Key);
        _context.ApplicationId.Should().Be(301);
    }

    [Test]
    public void It_is_idempotent_when_hydrated_again()
    {
        InstanceFixtureHydrator.HydrateAll(_context, _state);

        _context.TenantNames.Should().BeEquivalentTo(new List<string> { "Tenant_255901", "Tenant_255902" });
        _context.DataStoreIds.Should().BeEquivalentTo(new List<int> { 201, 202, 203, 204 });
        _context.ScenarioOwnedApplications.Should().BeEmpty();
    }
}
