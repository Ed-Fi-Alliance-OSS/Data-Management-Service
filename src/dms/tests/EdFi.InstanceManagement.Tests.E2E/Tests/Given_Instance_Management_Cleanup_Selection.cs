// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.InstanceManagement.Tests.E2E.Hooks;
using EdFi.InstanceManagement.Tests.E2E.Management;
using FluentAssertions;

namespace EdFi.InstanceManagement.Tests.E2E.UnitTests;

[TestFixture]
[Category("InstanceFixtureUnit")]
public class Given_Per_Scenario_Cleanup_Selection
{
    [Test]
    public void It_selects_scenario_owned_records_that_are_not_fixture_records()
    {
        List<OwnedRecord> owned = [new("Tenant_Setup_255901", 500), new("Tenant_Setup_255901", 501)];
        IReadOnlySet<int> fixtureIds = new HashSet<int> { 301, 302 };

        var deletable = InstanceManagementCleanupHooks.SelectDeletable(owned, fixtureIds);

        deletable.Select(r => r.Id).Should().BeEquivalentTo(new List<int> { 500, 501 });
    }

    [Test]
    public void It_excludes_a_fixture_id_even_if_it_appears_as_scenario_owned()
    {
        // Defensive: a fixture id must never be deleted by per-scenario cleanup.
        List<OwnedRecord> owned = [new("Tenant_255901", 301), new("Tenant_255901", 500)];
        IReadOnlySet<int> fixtureIds = new HashSet<int> { 301, 302 };

        var deletable = InstanceManagementCleanupHooks.SelectDeletable(owned, fixtureIds);

        deletable.Select(r => r.Id).Should().BeEquivalentTo(new List<int> { 500 });
    }

    [Test]
    public void It_selects_a_replacement_application_under_a_fixture_tenant_while_sparing_the_fixture_application()
    {
        // The claim-set overload creates a scenario-owned application (600) under fixture tenant Tenant_255901
        // whose fixture application is 301. Ownership must not be inferred from the tenant name.
        List<OwnedRecord> owned = [new("Tenant_255901", 600)];
        IReadOnlySet<int> fixtureIds = new HashSet<int> { 301, 302 };

        var deletable = InstanceManagementCleanupHooks.SelectDeletable(owned, fixtureIds);

        deletable.Select(r => r.Id).Should().BeEquivalentTo(new List<int> { 600 });
    }

    [Test]
    public void It_orders_deletions_newest_first()
    {
        List<OwnedRecord> owned = [new("t", 10), new("t", 30), new("t", 20)];
        IReadOnlySet<int> fixtureIds = new HashSet<int>();

        var deletable = InstanceManagementCleanupHooks.SelectDeletable(owned, fixtureIds);

        deletable.Select(r => r.Id).Should().ContainInOrder(30, 20, 10);
    }
}
