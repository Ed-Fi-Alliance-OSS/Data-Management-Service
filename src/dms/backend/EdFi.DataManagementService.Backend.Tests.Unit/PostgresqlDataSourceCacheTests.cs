// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Postgresql;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_PostgresqlDataSourceCache
{
    private PostgresqlDataSourceCache _cache = null!;

    [SetUp]
    public void Setup()
    {
        _cache = new PostgresqlDataSourceCache(NullLogger<PostgresqlDataSourceCache>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _cache?.Dispose();
    }

    [Test]
    public void It_returns_the_same_data_source_for_identical_connection_strings()
    {
        const string connectionString = "Host=localhost;Database=test;Username=user;Password=pass";

        var dataSource1 = _cache.GetOrCreate(connectionString);
        var dataSource2 = _cache.GetOrCreate(connectionString);

        dataSource1.Should().BeSameAs(dataSource2);
    }

    [Test]
    public void It_returns_different_data_sources_for_different_connection_strings()
    {
        const string connectionString1 = "Host=localhost;Database=test1;Username=user;Password=pass";
        const string connectionString2 = "Host=localhost;Database=test2;Username=user;Password=pass";

        var dataSource1 = _cache.GetOrCreate(connectionString1);
        var dataSource2 = _cache.GetOrCreate(connectionString2);

        dataSource1.Should().NotBeSameAs(dataSource2);
    }

    [Test]
    public void It_applies_the_expected_builder_settings_to_created_data_sources()
    {
        const string connectionString = "Host=localhost;Database=test;Username=user;Password=pass";

        using var connection = _cache.GetOrCreate(connectionString).CreateConnection();
        var connectionStringBuilder = new NpgsqlConnectionStringBuilder(connection.ConnectionString);

        connectionStringBuilder.NoResetOnClose.Should().BeTrue();
        connectionStringBuilder.ApplicationName.Should().Be("EdFi.DMS");
        connectionStringBuilder.AutoPrepareMinUsages.Should().Be(3);
        connectionStringBuilder.MaxAutoPrepare.Should().Be(256);
    }

    [Test]
    public void It_preserves_a_supplied_application_name()
    {
        const string connectionString =
            "Host=localhost;Database=test;Username=user;Password=pass;Application Name=IntegrationHarness";

        using var connection = _cache.GetOrCreate(connectionString).CreateConnection();
        var connectionStringBuilder = new NpgsqlConnectionStringBuilder(connection.ConnectionString);

        connectionStringBuilder.ApplicationName.Should().Be("IntegrationHarness");
    }

    [Test]
    public void It_throws_when_connection_string_is_null()
    {
        var act = () => _cache.GetOrCreate(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_throws_when_connection_string_is_empty()
    {
        var act = () => _cache.GetOrCreate(string.Empty);
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_throws_when_connection_string_is_whitespace()
    {
        var act = () => _cache.GetOrCreate("   ");
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_disposes_all_cached_data_sources()
    {
        _ = _cache.GetOrCreate("Host=localhost;Database=test1;Username=user;Password=pass");
        _ = _cache.GetOrCreate("Host=localhost;Database=test2;Username=user;Password=pass");

        _cache.Dispose();

        var act = () => _cache.GetOrCreate("Host=localhost;Database=test1;Username=user;Password=pass");
        act.Should().Throw<ObjectDisposedException>();
    }

    [Test]
    public void It_allows_multiple_dispose_calls()
    {
        var cache = new PostgresqlDataSourceCache(NullLogger<PostgresqlDataSourceCache>.Instance);
        cache.Dispose();

        var act = () => cache.Dispose();
        act.Should().NotThrow();
    }
}
