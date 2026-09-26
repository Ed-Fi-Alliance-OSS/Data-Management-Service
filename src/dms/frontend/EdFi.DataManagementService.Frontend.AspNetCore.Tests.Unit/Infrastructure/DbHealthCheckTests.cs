// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

public class DbHealthCheckTests
{
    // Nothing listens on port 1, so opening this fails at the socket rather than being rejected
    // before any connection attempt the way an empty connection string is.
    private const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=dms;Username=dms;Timeout=1";

    private static HealthCheckContext CreateContext(DbHealthCheck check) =>
        new() { Registration = new HealthCheckRegistration("DbHealthCheck", check, null, null) };

    [TestFixture]
    [Parallelizable]
    public class Given_No_Data_Store_Is_Loaded : DbHealthCheckTests
    {
        private HealthCheckResult _result;

        [SetUp]
        public async Task Setup()
        {
            var connectionStringProvider = A.Fake<IConnectionStringProvider>();
            A.CallTo(() => connectionStringProvider.GetHealthCheckConnectionString()).Returns(null);

            var check = new DbHealthCheck(
                connectionStringProvider,
                "postgresql",
                NullLogger<DbHealthCheck>.Instance
            );
            _result = await check.CheckHealthAsync(CreateContext(check));
        }

        [Test]
        public void It_reports_healthy()
        {
            _result.Status.Should().Be(HealthStatus.Healthy);
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

    // The regression this guards: the check is a singleton, so resolving the connection string
    // once at construction froze the zero-tenant answer from the first probe for the life of the
    // pod. A data store loaded afterwards must be the one the next check connects to.
    [TestFixture]
    [Parallelizable]
    public class Given_A_Data_Store_Loads_After_The_First_Check : DbHealthCheckTests
    {
        private IConnectionStringProvider _connectionStringProvider = null!;
        private HealthCheckResult _beforeLoad;
        private HealthCheckResult _afterLoad;

        [SetUp]
        public async Task Setup()
        {
            _connectionStringProvider = A.Fake<IConnectionStringProvider>();
            A.CallTo(() => _connectionStringProvider.GetHealthCheckConnectionString())
                .ReturnsNextFromSequence(null, UnreachableConnectionString);

            var check = new DbHealthCheck(
                _connectionStringProvider,
                "postgresql",
                NullLogger<DbHealthCheck>.Instance
            );
            HealthCheckContext context = CreateContext(check);

            _beforeLoad = await check.CheckHealthAsync(context);
            _afterLoad = await check.CheckHealthAsync(context);
        }

        [Test]
        public void It_reports_healthy_before_the_data_store_loads()
        {
            _beforeLoad.Status.Should().Be(HealthStatus.Healthy);
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
            // A connection-level failure, not "The ConnectionString property has not been
            // initialized", which is what a stale empty string produces.
            _afterLoad.Exception.Should().NotBeNull().And.NotBeOfType<InvalidOperationException>();
        }
    }
}
