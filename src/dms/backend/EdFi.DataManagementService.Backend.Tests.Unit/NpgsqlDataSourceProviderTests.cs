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
    private IDmsInstanceSelection _dmsInstanceSelection = null!;
    private NpgsqlDataSourceCache _cache = null!;
    private NpgsqlDataSourceProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        var applicationLifetime = A.Fake<IHostApplicationLifetime>();
        var cacheLogger = A.Fake<ILogger<NpgsqlDataSourceCache>>();
        var providerLogger = A.Fake<ILogger<NpgsqlDataSourceProvider>>();

        _dmsInstanceSelection = A.Fake<IDmsInstanceSelection>();
        _cache = new NpgsqlDataSourceCache(applicationLifetime, cacheLogger);
        _provider = new NpgsqlDataSourceProvider(_dmsInstanceSelection, _cache, providerLogger);
    }

    [TearDown]
    public void TearDown()
    {
        _cache?.Dispose();
    }

    [Test]
    public void It_should_retrieve_data_source_from_cache_using_selected_dms_instance()
    {
        // Arrange
        const string ConnectionString = "Host=localhost;Database=test;Username=user;Password=pass";
        var dmsInstance = new DmsInstance(
            Id: 1,
            InstanceType: "Test",
            InstanceName: "Test Instance",
            ConnectionString: ConnectionString,
            RouteContext: []
        );
        A.CallTo(() => _dmsInstanceSelection.GetSelectedDmsInstance()).Returns(dmsInstance);

        // Act
        var dataSource = _provider.DataSource;

        // Assert
        dataSource.Should().NotBeNull();
        A.CallTo(() => _dmsInstanceSelection.GetSelectedDmsInstance()).MustHaveHappenedOnceExactly();
    }

    [Test]
    public void It_should_cache_data_source_for_same_dms_instance()
    {
        // Arrange
        const string ConnectionString = "Host=localhost;Database=test;Username=user;Password=pass";
        var dmsInstance = new DmsInstance(
            Id: 1,
            InstanceType: "Test",
            InstanceName: "Test Instance",
            ConnectionString: ConnectionString,
            RouteContext: []
        );
        A.CallTo(() => _dmsInstanceSelection.GetSelectedDmsInstance()).Returns(dmsInstance);

        // Act
        var dataSource1 = _provider.DataSource;
        var dataSource2 = _provider.DataSource;

        // Assert - data source should be cached and reused
        dataSource1.Should().BeSameAs(dataSource2);
        // With true scoping, GetSelectedDmsInstance is called once and then cached
        A.CallTo(() => _dmsInstanceSelection.GetSelectedDmsInstance()).MustHaveHappenedOnceExactly();
    }

    [Test]
    public void It_should_reuse_cached_data_source_across_provider_instances_for_same_connection_string()
    {
        // Arrange
        const string ConnectionString = "Host=localhost;Database=test;Username=user;Password=pass";

        var dmsInstanceSelection1 = A.Fake<IDmsInstanceSelection>();
        var dmsInstanceSelection2 = A.Fake<IDmsInstanceSelection>();

        var dmsInstance1 = new DmsInstance(
            Id: 1,
            InstanceType: "Test",
            InstanceName: "Test Instance 1",
            ConnectionString: ConnectionString,
            RouteContext: []
        );
        var dmsInstance2 = new DmsInstance(
            Id: 2,
            InstanceType: "Test",
            InstanceName: "Test Instance 2",
            ConnectionString: ConnectionString,
            RouteContext: []
        );

        A.CallTo(() => dmsInstanceSelection1.GetSelectedDmsInstance()).Returns(dmsInstance1);
        A.CallTo(() => dmsInstanceSelection2.GetSelectedDmsInstance()).Returns(dmsInstance2);

        var providerLogger = A.Fake<ILogger<NpgsqlDataSourceProvider>>();
        var provider1 = new NpgsqlDataSourceProvider(dmsInstanceSelection1, _cache, providerLogger);
        var provider2 = new NpgsqlDataSourceProvider(dmsInstanceSelection2, _cache, providerLogger);

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
        const string ConnectionString1 = "Host=localhost;Database=test1;Username=user;Password=pass";
        const string ConnectionString2 = "Host=localhost;Database=test2;Username=user;Password=pass";

        var dmsInstanceSelection1 = A.Fake<IDmsInstanceSelection>();
        var dmsInstanceSelection2 = A.Fake<IDmsInstanceSelection>();

        var dmsInstance1 = new DmsInstance(
            Id: 1,
            InstanceType: "Test",
            InstanceName: "Test Instance 1",
            ConnectionString: ConnectionString1,
            RouteContext: []
        );
        var dmsInstance2 = new DmsInstance(
            Id: 2,
            InstanceType: "Test",
            InstanceName: "Test Instance 2",
            ConnectionString: ConnectionString2,
            RouteContext: []
        );

        A.CallTo(() => dmsInstanceSelection1.GetSelectedDmsInstance()).Returns(dmsInstance1);
        A.CallTo(() => dmsInstanceSelection2.GetSelectedDmsInstance()).Returns(dmsInstance2);

        var providerLogger = A.Fake<ILogger<NpgsqlDataSourceProvider>>();
        var provider1 = new NpgsqlDataSourceProvider(dmsInstanceSelection1, _cache, providerLogger);
        var provider2 = new NpgsqlDataSourceProvider(dmsInstanceSelection2, _cache, providerLogger);

        // Act
        var dataSource1 = provider1.DataSource;
        var dataSource2 = provider2.DataSource;

        // Assert
        dataSource1.Should().NotBeSameAs(dataSource2);
    }
}
