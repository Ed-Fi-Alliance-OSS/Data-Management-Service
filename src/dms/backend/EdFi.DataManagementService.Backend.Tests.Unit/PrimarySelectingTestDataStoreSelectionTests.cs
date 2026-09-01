// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// The test-support selection assigns both phases at once so integration fixtures that exercise only
/// the primary need no pipeline. These fixtures pin the difference between it and the production
/// selection, because the production one filling in a target on its own is exactly the behavior the
/// design forbids.
/// </summary>
[TestFixture]
public class PrimarySelectingTestDataStoreSelectionTests
{
    private const string ConnectionString = "Server=localhost;Database=edfi";

    private static DataStore TestDataStore() =>
        new(
            Id: 1,
            DataStoreType: "Test",
            Name: "Test Instance",
            ConnectionString: ConnectionString,
            RouteContext: []
        );

    [Test]
    public void It_assigns_the_primary_target_from_the_selected_data_store()
    {
        PrimarySelectingTestDataStoreSelection selection = new();

        selection.SetSelectedDataStore(TestDataStore());

        selection.IsEffectiveTargetSet.Should().BeTrue();
        selection.GetEffectiveTarget().Kind.Should().Be(EffectiveTargetKind.Primary);
        selection.GetEffectiveTarget().ConnectionString.Should().Be(ConnectionString);
    }

    [Test]
    public void It_still_exposes_the_parent()
    {
        PrimarySelectingTestDataStoreSelection selection = new();

        selection.SetSelectedDataStore(TestDataStore());

        selection.IsSet.Should().BeTrue();
        selection.GetSelectedDataStore().Id.Should().Be(1);
    }

    [Test]
    public void It_keeps_the_write_once_target_contract()
    {
        PrimarySelectingTestDataStoreSelection selection = new();
        selection.SetSelectedDataStore(TestDataStore());

        Action reassign = () =>
            selection.SetEffectiveTarget(
                new EffectiveDataStoreTarget(EffectiveTargetKind.Snapshot, "Server=snapshot")
            );

        reassign.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// The contrast that matters: production selection leaves the target unassigned, so a pipeline
    /// missing its selection step fails instead of silently reading the primary.
    /// </summary>
    [Test]
    public void It_differs_from_the_production_selection()
    {
        DataStoreSelection production = new();

        production.SetSelectedDataStore(TestDataStore());

        production.IsEffectiveTargetSet.Should().BeFalse();
        FluentActions
            .Invoking(() => production.GetEffectiveTarget())
            .Should()
            .Throw<InvalidOperationException>();
    }

    /// <summary>
    /// It reaches a test service collection through exactly one seam - the relational backend
    /// integration-test registration - and nothing else registers it.
    /// </summary>
    [Test]
    public void It_is_the_selection_registered_by_the_integration_test_seam()
    {
        ServiceCollection services = new();

        services.AddSelectedDataStoreIntegrationTestProvider();

        services
            .Single(descriptor => descriptor.ServiceType == typeof(IDataStoreSelection))
            .ImplementationType.Should()
            .Be<PrimarySelectingTestDataStoreSelection>();
    }

    /// <summary>
    /// Backend integration fixtures build their own service collection and register the production
    /// selection before calling the seam. The seam has to win that, and has to win it by leaving one
    /// descriptor rather than by out-ordering the fixture, so no fixture needs to know the helper
    /// exists.
    /// </summary>
    [Test]
    public void It_replaces_a_production_selection_a_fixture_registered_first()
    {
        ServiceCollection services = new();
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();

        services.AddSelectedDataStoreIntegrationTestProvider();

        services
            .Single(descriptor => descriptor.ServiceType == typeof(IDataStoreSelection))
            .ImplementationType.Should()
            .Be<PrimarySelectingTestDataStoreSelection>();
    }

    [Test]
    public void It_is_what_a_fixture_service_provider_resolves()
    {
        ServiceCollection services = new();
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.AddSelectedDataStoreIntegrationTestProvider();

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .Should()
            .BeOfType<PrimarySelectingTestDataStoreSelection>();
    }
}
