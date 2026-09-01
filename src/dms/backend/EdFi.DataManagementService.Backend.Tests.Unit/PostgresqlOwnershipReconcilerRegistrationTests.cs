// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// How the PostgreSQL data-source cache reaches ownership reconciliation. Registering the cache type a
/// second time would build a second cache holding its own data sources, and only that second one would
/// ever be reconciled - so the identity of the registered reconciler is the thing worth asserting.
/// </summary>
[TestFixture]
public class Given_Postgresql_Ownership_Reconciler_Registration
{
    private static IConfiguration EmptyConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection([]).Build();

    [Test]
    public void It_should_register_the_cache_itself_as_the_ownership_reconciler()
    {
        ServiceCollection services = [];
        services.AddLogging();
        services.AddPostgresqlDatastore(EmptyConfiguration());

        using ServiceProvider provider = services.BuildServiceProvider();

        NpgsqlDataSourceCache cache = provider.GetRequiredService<NpgsqlDataSourceCache>();
        IDataStoreOwnershipReconciler reconciler = provider
            .GetServices<IDataStoreOwnershipReconciler>()
            .Should()
            .ContainSingle()
            .Subject;

        reconciler.Should().BeSameAs(cache);
    }

    /// <summary>
    /// Both PostgreSQL registration entry points declare the reconciler, and an application that calls
    /// both must still end up with exactly one.
    /// </summary>
    [Test]
    public void It_should_register_one_reconciler_when_both_entry_points_are_used()
    {
        ServiceCollection services = [];
        services.AddLogging();
        services.AddPostgresqlDatastore(EmptyConfiguration());
        services.AddPostgresqlDmsCdcControlPlane();

        using ServiceProvider provider = services.BuildServiceProvider();

        provider
            .GetServices<IDataStoreOwnershipReconciler>()
            .Should()
            .ContainSingle()
            .Which.Should()
            .BeSameAs(provider.GetRequiredService<NpgsqlDataSourceCache>());
    }

    /// <summary>
    /// The reconciler descriptor must be distinguishable from any other implementation of the same
    /// interface. A factory typed to the interface rather than to the cache is not, and is rejected
    /// when the registration runs rather than when anything is resolved.
    /// </summary>
    [Test]
    public void It_should_register_without_an_indistinguishable_descriptor()
    {
        ServiceCollection services = [];
        services.AddLogging();

        FluentActions
            .Invoking(() => services.AddPostgresqlDmsCdcControlPlane())
            .Should()
            .NotThrow<ArgumentException>();
    }
}
