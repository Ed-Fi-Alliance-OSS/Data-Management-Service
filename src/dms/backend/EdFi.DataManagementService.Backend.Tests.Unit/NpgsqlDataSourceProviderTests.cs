// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_NpgsqlDataSourceProvider
{
    private const string ConnectionString = "Host=localhost;Database=test;Username=user;Password=pass";

    private IDataStoreSelection _dataStoreSelection = null!;
    private NpgsqlDataSourceCache _cache = null!;
    private NpgsqlDataSourceProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        var cacheLogger = A.Fake<ILogger<NpgsqlDataSourceCache>>();
        var providerLogger = A.Fake<ILogger<NpgsqlDataSourceProvider>>();

        _dataStoreSelection = A.Fake<IDataStoreSelection>();
        _cache = new NpgsqlDataSourceCache(cacheLogger);
        _provider = new NpgsqlDataSourceProvider(_dataStoreSelection, _cache, providerLogger);
    }

    [TearDown]
    public void TearDown()
    {
        _cache?.Dispose();
    }

    private static NpgsqlDataSourceProvider ProviderFor(
        IDataStoreSelection dataStoreSelection,
        NpgsqlDataSourceCache cache
    ) => new(dataStoreSelection, cache, A.Fake<ILogger<NpgsqlDataSourceProvider>>());

    private static IDataStoreSelection SelectionOf(EffectiveDataStoreTarget target)
    {
        var dataStoreSelection = A.Fake<IDataStoreSelection>();
        A.CallTo(() => dataStoreSelection.GetEffectiveTarget()).Returns(target);
        return dataStoreSelection;
    }

    [Test]
    public void It_should_retrieve_data_source_from_cache_using_the_effective_target()
    {
        // Arrange
        A.CallTo(() => _dataStoreSelection.GetEffectiveTarget())
            .Returns(EffectiveDataStoreTarget.Primary(ConnectionString));

        // Act
        var dataSource = _provider.DataSource;

        // Assert
        dataSource.Should().NotBeNull();
        A.CallTo(() => _dataStoreSelection.GetEffectiveTarget()).MustHaveHappenedOnceExactly();
    }

    [Test]
    public void It_should_cache_data_source_for_the_same_target()
    {
        // Arrange
        A.CallTo(() => _dataStoreSelection.GetEffectiveTarget())
            .Returns(EffectiveDataStoreTarget.Primary(ConnectionString));

        // Act
        var dataSource1 = _provider.DataSource;
        var dataSource2 = _provider.DataSource;

        // Assert - data source should be cached and reused
        dataSource1.Should().BeSameAs(dataSource2);
        // The target is read on each access for defensive validation
        A.CallTo(() => _dataStoreSelection.GetEffectiveTarget()).MustHaveHappenedTwiceExactly();
    }

    [Test]
    public void It_should_reuse_cached_data_source_across_provider_instances_for_same_connection_string()
    {
        // Arrange
        var provider1 = ProviderFor(SelectionOf(EffectiveDataStoreTarget.Primary(ConnectionString)), _cache);
        var provider2 = ProviderFor(SelectionOf(EffectiveDataStoreTarget.Primary(ConnectionString)), _cache);

        // Act
        var dataSource1 = provider1.DataSource;
        var dataSource2 = provider2.DataSource;

        // Assert
        dataSource1.Should().BeSameAs(dataSource2);
    }

    [Test]
    public void It_should_create_different_data_sources_for_different_connection_strings()
    {
        // Arrange
        var provider1 = ProviderFor(
            SelectionOf(
                EffectiveDataStoreTarget.Primary("Host=localhost;Database=test1;Username=user;Password=pass")
            ),
            _cache
        );
        var provider2 = ProviderFor(
            SelectionOf(
                EffectiveDataStoreTarget.Primary("Host=localhost;Database=test2;Username=user;Password=pass")
            ),
            _cache
        );

        // Act
        var dataSource1 = provider1.DataSource;
        var dataSource2 = provider2.DataSource;

        // Assert
        dataSource1.Should().NotBeSameAs(dataSource2);
    }

    /// <summary>
    /// A derivative and its parent are one data store with one id, so a per-request memo keyed by id
    /// would hand a replica request the primary's data source. This pins the key to the database the
    /// target actually names.
    /// </summary>
    [Test]
    public void It_should_create_a_different_data_source_for_a_derivative_of_the_same_data_store()
    {
        // Arrange
        const string ReplicaConnectionString = "Host=replica;Database=test;Username=user;Password=pass";

        var primaryProvider = ProviderFor(
            SelectionOf(EffectiveDataStoreTarget.Primary(ConnectionString)),
            _cache
        );
        var replicaProvider = ProviderFor(
            SelectionOf(
                new EffectiveDataStoreTarget(EffectiveTargetKind.ReadReplica, ReplicaConnectionString)
            ),
            _cache
        );

        // Act
        var primaryDataSource = primaryProvider.DataSource;
        var replicaDataSource = replicaProvider.DataSource;

        // Assert
        replicaDataSource.Should().NotBeSameAs(primaryDataSource);
        replicaDataSource.ConnectionString.Should().Contain("Host=replica");
    }
}
