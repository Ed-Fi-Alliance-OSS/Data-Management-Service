// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_PostgresqlRequestDbConnectionProvider
{
    private IRequestConnectionProvider _requestConnectionProvider = null!;
    private PostgresqlDataSourceCache _cache = null!;
    private PostgresqlRequestDbConnectionProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        _requestConnectionProvider = A.Fake<IRequestConnectionProvider>();
        _cache = new PostgresqlDataSourceCache(NullLogger<PostgresqlDataSourceCache>.Instance);
        _provider = new PostgresqlRequestDbConnectionProvider(_requestConnectionProvider, _cache);
    }

    [TearDown]
    public void TearDown()
    {
        _cache?.Dispose();
    }

    [Test]
    public void It_reuses_the_same_data_source_for_repeated_access_to_the_same_selected_instance()
    {
        const string connectionString = "Host=localhost;Database=test;Username=user;Password=pass";
        A.CallTo(() => _requestConnectionProvider.GetRequestConnection())
            .Returns(new RequestConnection(new DmsInstanceId(1), connectionString));

        var dataSource1 = _provider.GetDataSource();
        var dataSource2 = _provider.GetDataSource();

        dataSource1.Should().BeSameAs(dataSource2);
        A.CallTo(() => _requestConnectionProvider.GetRequestConnection()).MustHaveHappenedTwiceExactly();
    }

    [Test]
    public void It_does_not_cache_data_sources_by_instance_id_when_the_connection_string_changes()
    {
        A.CallTo(() => _requestConnectionProvider.GetRequestConnection())
            .ReturnsNextFromSequence(
                new RequestConnection(
                    new DmsInstanceId(1),
                    "Host=localhost;Database=test1;Username=user;Password=pass"
                ),
                new RequestConnection(
                    new DmsInstanceId(1),
                    "Host=localhost;Database=test2;Username=user;Password=pass"
                )
            );

        var dataSource1 = _provider.GetDataSource();
        var dataSource2 = _provider.GetDataSource();

        dataSource1.Should().NotBeSameAs(dataSource2);
        A.CallTo(() => _requestConnectionProvider.GetRequestConnection()).MustHaveHappenedTwiceExactly();
    }

    [Test]
    public void It_reuses_the_shared_cache_when_the_selected_instance_changes_within_the_scope()
    {
        const string connectionString = "Host=localhost;Database=test;Username=user;Password=pass";
        A.CallTo(() => _requestConnectionProvider.GetRequestConnection())
            .ReturnsNextFromSequence(
                new RequestConnection(new DmsInstanceId(1), connectionString),
                new RequestConnection(new DmsInstanceId(2), connectionString)
            );

        var dataSource1 = _provider.GetDataSource();
        var dataSource2 = _provider.GetDataSource();

        dataSource1.Should().BeSameAs(dataSource2);
        A.CallTo(() => _requestConnectionProvider.GetRequestConnection()).MustHaveHappenedTwiceExactly();
    }

    [Test]
    public void It_reuses_cached_data_sources_across_provider_instances_for_the_same_connection_string()
    {
        const string connectionString = "Host=localhost;Database=test;Username=user;Password=pass";

        var requestConnectionProvider1 = A.Fake<IRequestConnectionProvider>();
        var requestConnectionProvider2 = A.Fake<IRequestConnectionProvider>();

        A.CallTo(() => requestConnectionProvider1.GetRequestConnection())
            .Returns(new RequestConnection(new DmsInstanceId(1), connectionString));
        A.CallTo(() => requestConnectionProvider2.GetRequestConnection())
            .Returns(new RequestConnection(new DmsInstanceId(2), connectionString));

        var provider1 = new PostgresqlRequestDbConnectionProvider(requestConnectionProvider1, _cache);
        var provider2 = new PostgresqlRequestDbConnectionProvider(requestConnectionProvider2, _cache);

        var dataSource1 = provider1.GetDataSource();
        var dataSource2 = provider2.GetDataSource();

        dataSource1.Should().BeSameAs(dataSource2);
    }

    [Test]
    public void It_returns_different_data_sources_for_different_connection_strings()
    {
        var requestConnectionProvider1 = A.Fake<IRequestConnectionProvider>();
        var requestConnectionProvider2 = A.Fake<IRequestConnectionProvider>();

        A.CallTo(() => requestConnectionProvider1.GetRequestConnection())
            .Returns(
                new RequestConnection(
                    new DmsInstanceId(1),
                    "Host=localhost;Database=test1;Username=user;Password=pass"
                )
            );
        A.CallTo(() => requestConnectionProvider2.GetRequestConnection())
            .Returns(
                new RequestConnection(
                    new DmsInstanceId(2),
                    "Host=localhost;Database=test2;Username=user;Password=pass"
                )
            );

        var provider1 = new PostgresqlRequestDbConnectionProvider(requestConnectionProvider1, _cache);
        var provider2 = new PostgresqlRequestDbConnectionProvider(requestConnectionProvider2, _cache);

        var dataSource1 = provider1.GetDataSource();
        var dataSource2 = provider2.GetDataSource();

        dataSource1.Should().NotBeSameAs(dataSource2);
    }

    [Test]
    public void It_preserves_request_connection_provider_failures()
    {
        A.CallTo(() => _requestConnectionProvider.GetRequestConnection())
            .Throws(
                new InvalidOperationException(
                    "Selected DMS instance '7' does not have a valid connection string."
                )
            );

        var act = () => _provider.GetDataSource();

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("Selected DMS instance '7' does not have a valid connection string.");
    }
}
