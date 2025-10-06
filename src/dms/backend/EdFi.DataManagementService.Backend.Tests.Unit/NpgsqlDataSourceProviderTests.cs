// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.Configuration;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_NpgsqlDataSourceProvider
{
    private IRequestConnectionStringProvider _connectionStringProvider = null!;
    private NpgsqlDataSourceCache _cache = null!;
    private NpgsqlDataSourceProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        var applicationLifetime = A.Fake<IHostApplicationLifetime>();
        var logger = A.Fake<ILogger<NpgsqlDataSourceCache>>();

        _connectionStringProvider = A.Fake<IRequestConnectionStringProvider>();
        _cache = new NpgsqlDataSourceCache(applicationLifetime, logger);
        _provider = new NpgsqlDataSourceProvider(_connectionStringProvider, _cache);
    }

    [TearDown]
    public void TearDown()
    {
        _cache?.Dispose();
    }

    [Test]
    public void It_should_retrieve_data_source_from_cache_using_request_connection_string()
    {
        // Arrange
        const string connectionString = "Host=localhost;Database=test;Username=user;Password=pass";
        A.CallTo(() => _connectionStringProvider.GetConnectionString()).Returns(connectionString);

        // Act
        var dataSource = _provider.DataSource;

        // Assert
        dataSource.Should().NotBeNull();
        A.CallTo(() => _connectionStringProvider.GetConnectionString()).MustHaveHappenedOnceExactly();
    }

    [Test]
    public void It_should_cache_data_source_for_same_provider_instance()
    {
        // Arrange
        const string connectionString = "Host=localhost;Database=test;Username=user;Password=pass";
        A.CallTo(() => _connectionStringProvider.GetConnectionString()).Returns(connectionString);

        // Act
        var dataSource1 = _provider.DataSource;
        var dataSource2 = _provider.DataSource;

        // Assert
        dataSource1.Should().BeSameAs(dataSource2);
        A.CallTo(() => _connectionStringProvider.GetConnectionString()).MustHaveHappenedOnceExactly();
    }

    [Test]
    public void It_should_reuse_cached_data_source_across_provider_instances_for_same_connection_string()
    {
        // Arrange
        const string connectionString = "Host=localhost;Database=test;Username=user;Password=pass";

        var connectionStringProvider1 = A.Fake<IRequestConnectionStringProvider>();
        var connectionStringProvider2 = A.Fake<IRequestConnectionStringProvider>();

        A.CallTo(() => connectionStringProvider1.GetConnectionString()).Returns(connectionString);
        A.CallTo(() => connectionStringProvider2.GetConnectionString()).Returns(connectionString);

        var provider1 = new NpgsqlDataSourceProvider(connectionStringProvider1, _cache);
        var provider2 = new NpgsqlDataSourceProvider(connectionStringProvider2, _cache);

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
        const string connectionString1 = "Host=localhost;Database=test1;Username=user;Password=pass";
        const string connectionString2 = "Host=localhost;Database=test2;Username=user;Password=pass";

        var connectionStringProvider1 = A.Fake<IRequestConnectionStringProvider>();
        var connectionStringProvider2 = A.Fake<IRequestConnectionStringProvider>();

        A.CallTo(() => connectionStringProvider1.GetConnectionString()).Returns(connectionString1);
        A.CallTo(() => connectionStringProvider2.GetConnectionString()).Returns(connectionString2);

        var provider1 = new NpgsqlDataSourceProvider(connectionStringProvider1, _cache);
        var provider2 = new NpgsqlDataSourceProvider(connectionStringProvider2, _cache);

        // Act
        var dataSource1 = provider1.DataSource;
        var dataSource2 = provider2.DataSource;

        // Assert
        dataSource1.Should().NotBeSameAs(dataSource2);
    }
}
