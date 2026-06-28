// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Postgresql;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_NpgsqlDataSourceCache
{
    private IHostApplicationLifetime _applicationLifetime = null!;
    private ILogger<NpgsqlDataSourceCache> _logger = null!;
    private NpgsqlDataSourceCache _cache = null!;

    [SetUp]
    public void Setup()
    {
        _applicationLifetime = A.Fake<IHostApplicationLifetime>();
        _logger = A.Fake<ILogger<NpgsqlDataSourceCache>>();
        _cache = new NpgsqlDataSourceCache(_applicationLifetime, _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _cache?.Dispose();
    }

    [Test]
    public void It_should_return_same_instance_for_identical_connection_strings()
    {
        // Arrange
        const string ConnectionString = "Host=localhost;Database=test;Username=user;Password=pass";

        // Act
        var dataSource1 = _cache.GetOrCreate(ConnectionString);
        var dataSource2 = _cache.GetOrCreate(ConnectionString);

        // Assert
        dataSource1.Should().BeSameAs(dataSource2);
    }

    [Test]
    public void It_should_return_different_instances_for_different_connection_strings()
    {
        // Arrange
        const string ConnectionString1 = "Host=localhost;Database=test1;Username=user;Password=pass";
        const string ConnectionString2 = "Host=localhost;Database=test2;Username=user;Password=pass";

        // Act
        var dataSource1 = _cache.GetOrCreate(ConnectionString1);
        var dataSource2 = _cache.GetOrCreate(ConnectionString2);

        // Assert
        dataSource1.Should().NotBeSameAs(dataSource2);
    }

    [Test]
    public void It_should_throw_when_connection_string_is_null()
    {
        // Act & Assert
        var act = () => _cache.GetOrCreate(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_should_throw_when_connection_string_is_empty()
    {
        // Act & Assert
        var act = () => _cache.GetOrCreate(string.Empty);
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_should_throw_when_connection_string_is_whitespace()
    {
        // Act & Assert
        var act = () => _cache.GetOrCreate("   ");
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_should_dispose_all_cached_data_sources()
    {
        // Arrange
        const string ConnectionString1 = "Host=localhost;Database=test1;Username=user;Password=pass";
        const string ConnectionString2 = "Host=localhost;Database=test2;Username=user;Password=pass";

        _ = _cache.GetOrCreate(ConnectionString1);
        _ = _cache.GetOrCreate(ConnectionString2);

        // Act
        _cache.Dispose();

        // Assert - verify that getting data source after disposal throws
        var act = () => _cache.GetOrCreate(ConnectionString1);
        act.Should().Throw<ObjectDisposedException>();
    }

    [Test]
    public void It_should_throw_when_getting_data_source_after_disposal()
    {
        // Arrange
        _cache.Dispose();

        // Act & Assert
        var act = () => _cache.GetOrCreate("Host=localhost;Database=test;Username=user;Password=pass");
        act.Should().Throw<ObjectDisposedException>();
    }

    [Test]
    public void It_should_allow_multiple_dispose_calls()
    {
        // Arrange
        var cache = new NpgsqlDataSourceCache(_applicationLifetime, _logger);
        cache.Dispose();

        // Act & Assert
        var act = () => cache.Dispose();
        act.Should().NotThrow();
    }
}
