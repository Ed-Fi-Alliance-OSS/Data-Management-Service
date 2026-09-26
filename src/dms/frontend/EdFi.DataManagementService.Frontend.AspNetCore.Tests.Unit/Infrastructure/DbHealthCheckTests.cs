// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

internal static class DbHealthCheckTestSupport
{
    // Nothing listens on port 1, so opening this fails at the socket rather than being rejected
    // before any connection attempt the way an empty connection string is.
    public const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=dms;Username=dms;Timeout=1";

    public static DbHealthCheck CreateCheck(
        IConnectionStringProvider connectionStringProvider,
        string providerName = "postgresql"
    ) => new(connectionStringProvider, providerName, NullLogger<DbHealthCheck>.Instance);

    public static HealthCheckContext CreateContext(DbHealthCheck check) =>
        new() { Registration = new HealthCheckRegistration("DbHealthCheck", check, null, null) };
}

[TestFixture]
[Parallelizable]
public class Given_A_DbHealthCheck_With_No_Data_Store_Loaded
{
    private HealthCheckResult _result;

    [SetUp]
    public async Task Setup()
    {
        var connectionStringProvider = A.Fake<IConnectionStringProvider>();
        A.CallTo(() => connectionStringProvider.GetHealthCheckConnectionString()).Returns(null);

        var check = DbHealthCheckTestSupport.CreateCheck(connectionStringProvider);
        _result = await check.CheckHealthAsync(DbHealthCheckTestSupport.CreateContext(check));
    }

    // Degraded, not Healthy: expected at multi-tenant startup, but a cache refresh can also empty a
    // working tenant's data stores, and that must remain distinguishable from a verified database.
    [Test]
    public void It_reports_degraded()
    {
        _result.Status.Should().Be(HealthStatus.Degraded);
    }

    [Test]
    public void It_says_there_is_no_database_to_check()
    {
        _result.Description.Should().Be(DbHealthCheck.NoDataStoresDescription);
    }

    [Test]
    public void It_does_not_attach_an_exception()
    {
        _result.Exception.Should().BeNull();
    }
}

// The regression this guards: the check is a singleton, so resolving the connection string once at
// construction froze the zero-tenant answer from the first probe for the life of the pod. A data
// store loaded afterwards must be the one the next check connects to.
[TestFixture]
[Parallelizable]
public class Given_A_DbHealthCheck_Whose_Data_Store_Loads_After_The_First_Check
{
    private IConnectionStringProvider _connectionStringProvider = null!;
    private HealthCheckResult _beforeLoad;
    private HealthCheckResult _afterLoad;

    [SetUp]
    public async Task Setup()
    {
        _connectionStringProvider = A.Fake<IConnectionStringProvider>();
        A.CallTo(() => _connectionStringProvider.GetHealthCheckConnectionString())
            .ReturnsNextFromSequence(null, DbHealthCheckTestSupport.UnreachableConnectionString);

        var check = DbHealthCheckTestSupport.CreateCheck(_connectionStringProvider);
        HealthCheckContext context = DbHealthCheckTestSupport.CreateContext(check);

        _beforeLoad = await check.CheckHealthAsync(context);
        _afterLoad = await check.CheckHealthAsync(context);
    }

    [Test]
    public void It_reports_degraded_before_the_data_store_loads()
    {
        _beforeLoad.Status.Should().Be(HealthStatus.Degraded);
        _beforeLoad.Description.Should().Be(DbHealthCheck.NoDataStoresDescription);
    }

    [Test]
    public void It_resolves_the_connection_string_on_every_check()
    {
        A.CallTo(() => _connectionStringProvider.GetHealthCheckConnectionString())
            .MustHaveHappenedTwiceExactly();
    }

    [Test]
    public void It_attempts_a_connection_with_the_newly_loaded_data_store()
    {
        _afterLoad.Status.Should().Be(HealthStatus.Unhealthy);
        _afterLoad.Description.Should().Be("Database connection is unhealthy.");
        // The failure happened at the connection - which a stale empty connection string never
        // reaches ("The ConnectionString property has not been initialized" is not a DbException).
        _afterLoad.Exception.Should().BeAssignableTo<DbException>();
    }
}

[TestFixture]
[Parallelizable]
public class Given_A_DbHealthCheck_With_An_Unsupported_Provider
{
    [TestCase("")]
    [TestCase("oracle")]
    public void It_rejects_the_provider_at_construction(string providerName)
    {
        var connectionStringProvider = A.Fake<IConnectionStringProvider>();

        // At construction, not at the first connection attempt: with no data store loaded the
        // check never attempts a connection, so a bad value would otherwise go unnoticed.
        Action construct = () => DbHealthCheckTestSupport.CreateCheck(connectionStringProvider, providerName);

        construct.Should().Throw<ArgumentException>().WithParameterName("providerName");
    }

    [TestCase("postgresql")]
    [TestCase("MSSQL")]
    public void It_accepts_a_supported_provider_in_any_case(string providerName)
    {
        var connectionStringProvider = A.Fake<IConnectionStringProvider>();

        Action construct = () => DbHealthCheckTestSupport.CreateCheck(connectionStringProvider, providerName);

        construct.Should().NotThrow();
    }
}
