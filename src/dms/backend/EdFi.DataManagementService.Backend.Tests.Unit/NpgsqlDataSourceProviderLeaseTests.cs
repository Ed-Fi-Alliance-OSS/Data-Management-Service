// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// The request-scoped half of the leased lifetime: the provider holds its claim for the life of the
/// scope, because several seams open connections from the same data source during one request, and the
/// scope's disposal is what gives the claim back.
/// </summary>
[TestFixture]
public class Given_NpgsqlDataSourceProvider_Holding_Leases
{
    private const string PrimaryConnectionString = "Host=primary;Database=dms;Username=u;Password=p";
    private const string SnapshotConnectionString = "Host=snapshot;Database=dms;Username=u;Password=p";

    private GatedNpgsqlDataSourceLifetime _lifetime = null!;
    private NpgsqlDataSourceCache _cache = null!;

    [SetUp]
    public void Setup()
    {
        _lifetime = new GatedNpgsqlDataSourceLifetime();
        _cache = new NpgsqlDataSourceCache(new CapturingLogger<NpgsqlDataSourceCache>(), _lifetime);
        _lifetime.Cache = _cache;
    }

    [TearDown]
    public void TearDown()
    {
        _cache.Dispose();
        _lifetime.LockViolations.Should().BeEmpty();
        _lifetime.Dispose();
    }

    private static IDataStoreSelection SelectionOf(params EffectiveDataStoreTarget[] targets)
    {
        IDataStoreSelection selection = A.Fake<IDataStoreSelection>();

        if (targets.Length == 1)
        {
            A.CallTo(() => selection.GetEffectiveTarget()).Returns(targets[0]);
        }
        else
        {
            A.CallTo(() => selection.GetEffectiveTarget()).ReturnsNextFromSequence(targets);
        }

        return selection;
    }

    private NpgsqlDataSourceProvider ProviderFor(params EffectiveDataStoreTarget[] targets) =>
        new(SelectionOf(targets), _cache, A.Fake<ILogger<NpgsqlDataSourceProvider>>());

    [Test]
    public async Task It_should_take_one_lease_per_string_however_often_the_source_is_read()
    {
        _cache.Reconcile(OwnershipSnapshots.Of(1, PrimaryConnectionString));

        EffectiveDataStoreTarget target = EffectiveDataStoreTarget.Primary(PrimaryConnectionString);
        NpgsqlDataSourceProvider provider = ProviderFor(target, target, target);

        NpgsqlDataSource first = provider.DataSource;
        NpgsqlDataSource second = provider.DataSource;
        NpgsqlDataSource third = provider.DataSource;

        second.Should().BeSameAs(first);
        third.Should().BeSameAs(first);
        _lifetime.BuildCount.Should().Be(1);

        // One lease taken, so retiring the string and disposing the scope must be enough to dispose it.
        _cache.Reconcile(OwnershipSnapshots.Empty(2));
        _lifetime.DisposeCountOf(first).Should().Be(0, "the request scope still holds its claim");

        await provider.DisposeAsync();
        _lifetime.DisposeCountOf(first).Should().Be(1);
    }

    [Test]
    public async Task It_should_release_every_lease_it_took_when_the_scope_is_disposed()
    {
        _cache.Reconcile(OwnershipSnapshots.Of(1, PrimaryConnectionString, SnapshotConnectionString));

        NpgsqlDataSourceProvider provider = ProviderFor(
            EffectiveDataStoreTarget.Primary(PrimaryConnectionString),
            new EffectiveDataStoreTarget(EffectiveTargetKind.Snapshot, SnapshotConnectionString)
        );

        NpgsqlDataSource primary = provider.DataSource;
        NpgsqlDataSource snapshot = provider.DataSource;

        snapshot.Should().NotBeSameAs(primary);

        _cache.Reconcile(OwnershipSnapshots.Empty(2));
        _lifetime.DisposeCountOf(primary).Should().Be(0);
        _lifetime.DisposeCountOf(snapshot).Should().Be(0);

        await provider.DisposeAsync();

        _lifetime.DisposeCountOf(primary).Should().Be(1);
        _lifetime.DisposeCountOf(snapshot).Should().Be(1);
    }

    [Test]
    public async Task It_should_release_its_leases_only_once_when_disposed_twice()
    {
        _cache.Reconcile(OwnershipSnapshots.Of(1, PrimaryConnectionString));

        EffectiveDataStoreTarget target = EffectiveDataStoreTarget.Primary(PrimaryConnectionString);
        NpgsqlDataSourceProvider provider = ProviderFor(target);

        NpgsqlDataSource dataSource = provider.DataSource;

        // A second scope holds the same string, so a double release would show up as an early disposal.
        NpgsqlDataSourceLease concurrent = _cache.AcquireLease(PrimaryConnectionString);
        _cache.Reconcile(OwnershipSnapshots.Empty(2));

        await DoubleDisposal.OfAsync(provider);

        _lifetime.DisposeCountOf(dataSource).Should().Be(0, "the other holder's claim must still be counted");

        await concurrent.DisposeAsync();
        _lifetime.DisposeCountOf(dataSource).Should().Be(1);
    }

    /// <summary>
    /// A service that is only asynchronously disposable makes a synchronous scope disposal throw
    /// rather than release anything, and the existing PostgreSQL repository, write-session, and
    /// hydration fixtures all build synchronous scopes around a database operation. Both disposal
    /// forms therefore have to release.
    /// </summary>
    [Test]
    public void It_should_release_its_leases_through_a_synchronous_scope_disposal()
    {
        _cache.Reconcile(OwnershipSnapshots.Of(1, PrimaryConnectionString));

        ServiceCollection services = [];
        services.AddSingleton(_cache);
        services.AddSingleton(A.Fake<ILogger<NpgsqlDataSourceProvider>>());
        services.AddScoped(_ => SelectionOf(EffectiveDataStoreTarget.Primary(PrimaryConnectionString)));
        services.AddScoped<NpgsqlDataSourceProvider>();

        using ServiceProvider serviceProvider = services.BuildServiceProvider();
        NpgsqlDataSource dataSource;

        // A real synchronous scope, disposed synchronously - exactly what those fixtures do.
        using (IServiceScope scope = serviceProvider.CreateScope())
        {
            dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSourceProvider>().DataSource;

            _cache.Reconcile(OwnershipSnapshots.Empty(2));
            _lifetime.DisposeCountOf(dataSource).Should().Be(0, "the scope still holds its claim");
        }

        _lifetime.DisposeCountOf(dataSource).Should().Be(1);
    }

    [Test]
    public async Task It_should_release_its_leases_only_once_across_both_disposal_forms()
    {
        _cache.Reconcile(OwnershipSnapshots.Of(1, PrimaryConnectionString));

        NpgsqlDataSourceProvider provider = ProviderFor(
            EffectiveDataStoreTarget.Primary(PrimaryConnectionString)
        );

        NpgsqlDataSource dataSource = provider.DataSource;

        // A second holder, so a double release would show up as a disposal while a claim is still out.
        NpgsqlDataSourceLease concurrent = _cache.AcquireLease(PrimaryConnectionString);
        _cache.Reconcile(OwnershipSnapshots.Empty(2));

        await DoubleDisposal.ThroughBothFormsAsync(provider);

        _lifetime.DisposeCountOf(dataSource).Should().Be(0, "the other holder's claim must still be counted");

        await concurrent.DisposeAsync();
        _lifetime.DisposeCountOf(dataSource).Should().Be(1);
    }

    [Test]
    public async Task It_should_refuse_to_hand_out_a_data_source_after_the_scope_is_disposed()
    {
        _cache.Reconcile(OwnershipSnapshots.Of(1, PrimaryConnectionString));

        NpgsqlDataSourceProvider provider = ProviderFor(
            EffectiveDataStoreTarget.Primary(PrimaryConnectionString)
        );

        await provider.DisposeAsync();

        FluentActions.Invoking(() => provider.DataSource).Should().Throw<ObjectDisposedException>();
    }
}
