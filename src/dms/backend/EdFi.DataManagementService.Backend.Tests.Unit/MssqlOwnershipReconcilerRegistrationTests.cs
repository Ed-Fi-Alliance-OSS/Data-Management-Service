// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Core.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// How the SQL Server acquisition boundary reaches ownership reconciliation. A second instance would
/// hold its own realization memo and pool state, and only that one would ever be reconciled - so the
/// identity of the registered reconciler is the thing worth asserting.
/// </summary>
[TestFixture]
public class Given_Mssql_Ownership_Reconciler_Registration
{
    private static IConfiguration EmptyConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection([]).Build();

    [Test]
    public void It_should_register_the_acquisition_boundary_itself_as_the_ownership_reconciler()
    {
        ServiceCollection services = [];
        services.AddLogging();
        services.AddMssqlDatastore(EmptyConfiguration());

        using ServiceProvider provider = services.BuildServiceProvider();

        MssqlConnectionAcquisition acquisition = provider.GetRequiredService<MssqlConnectionAcquisition>();

        provider
            .GetRequiredService<IMssqlConnectionAcquisition>()
            .Should()
            .BeSameAs(acquisition, "every seam must share one realization memo and pool state");

        provider
            .GetServices<IDataStoreOwnershipReconciler>()
            .Should()
            .ContainSingle()
            .Which.Should()
            .BeSameAs(acquisition);
    }

    /// <summary>
    /// Both SQL Server registration entry points declare the reconciler, and an application that calls
    /// both must still end up with exactly one.
    /// </summary>
    [Test]
    public void It_should_register_one_reconciler_when_both_entry_points_are_used()
    {
        ServiceCollection services = [];
        services.AddLogging();
        services.AddMssqlDatastore(EmptyConfiguration());
        services.AddMssqlReferenceResolver();

        using ServiceProvider provider = services.BuildServiceProvider();

        provider
            .GetServices<IDataStoreOwnershipReconciler>()
            .Should()
            .ContainSingle()
            .Which.Should()
            .BeSameAs(provider.GetRequiredService<MssqlConnectionAcquisition>());
    }

    /// <summary>
    /// The reconciler descriptor must be distinguishable from any other implementation of the same
    /// interface. A factory typed to the interface rather than to the acquisition is not, and is
    /// rejected when the registration runs rather than when anything is resolved.
    /// </summary>
    [Test]
    public void It_should_register_without_an_indistinguishable_descriptor()
    {
        ServiceCollection services = [];
        services.AddLogging();

        FluentActions
            .Invoking(() => services.AddMssqlReferenceResolver())
            .Should()
            .NotThrow<ArgumentException>();
    }

    /// <summary>
    /// The exact-pool clearing adapter has to be present wherever the acquisition is, or the
    /// acquisition cannot be constructed at all and retirement has nothing to call.
    /// </summary>
    [Test]
    public void It_should_register_exact_pool_clearing_alongside_the_acquisition()
    {
        ServiceCollection services = [];
        services.AddLogging();
        services.AddMssqlReferenceResolver();

        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<ISqlServerPoolClearing>().Should().BeOfType<SqlClientPoolClearing>();
    }
}
