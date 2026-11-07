// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Services;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Postgresql;

/// <summary>
/// Step 3.9 (6.1 step 3): an admin connection built over a source reads the current resolved value at every
/// use, so a secret rotated between operations reaches the next connection, with the provisioning overrides
/// applied to each value; one built over a fixed value still refuses a value the driver cannot read at
/// construction.
/// </summary>
public class PostgresqlAdminConnectionTests
{
    [TestFixture]
    public class Given_a_source_whose_value_rotates
    {
        private NpgsqlConnectionStringBuilder _before = null!;
        private NpgsqlConnectionStringBuilder _after = null!;
        private LibpqConnectionSettings _libpqAfter = null!;

        [SetUp]
        public void Setup()
        {
            string current = "Host=db.example;Username=dms_admin;Password=before;Pooling=true";
            PostgresqlAdminConnection connection = new(() => current, TimeSpan.FromSeconds(30));
            _before = new(connection.ConnectionStringFor("postgres", "dmsprov-session"));
            current = "Host=db.example;Username=dms_admin;Password=after;Pooling=true";
            _after = new(connection.ConnectionStringFor("postgres", "dmsprov-session"));
            _libpqAfter = connection.ToLibpqSettings();
        }

        [Test]
        public void It_connects_with_the_value_current_at_each_use() =>
            (_before.Password, _after.Password, _libpqAfter.Password)
                .Should()
                .Be(("before", "after", "after"));

        [Test]
        public void It_applies_the_provisioning_overrides_to_the_rotated_value() =>
            (_after.Pooling, _after.TcpKeepAlive, _after.CommandTimeout).Should().Be((false, true, 30));
    }

    /// <summary>
    /// Step 3.9 review: one admin connection serves overlapping operations, each bound to its own resolution;
    /// the parse cache it keeps for the last value never hands one operation's server to the other.
    /// </summary>
    [TestFixture]
    public class Given_overlapping_operations_bound_to_different_servers
    {
        private string _firstBefore = null!;
        private string _second = null!;
        private string _firstAfter = null!;

        [SetUp]
        public async Task Setup()
        {
            SecretsOptions secrets = new();
            DmsAdminConnectionSource source = new(
                Options.Create(new DmsDataStoreProvisioningOptions { AdminConnectionString = "Host=unused" }),
                new SecretValueCache(Options.Create(secrets)),
                Options.Create(secrets),
                TimeProvider.System
            );
            PostgresqlAdminConnection connection = new(() => source.Current, TimeSpan.FromSeconds(30));
            TaskCompletionSource firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource secondRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

            Task<(string Before, string After)> first = Task.Run(async () =>
            {
                using IDisposable binding = source.Bind(
                    new DmsAdminConnectionString("Host=server-a;Username=dms_admin")
                );
                string before = HostOf(connection);
                firstRead.SetResult();
                await secondRead.Task;
                return (before, HostOf(connection));
            });
            await firstRead.Task.WaitAsync(TimeSpan.FromSeconds(30));
            using (source.Bind(new DmsAdminConnectionString("Host=server-b;Username=dms_admin")))
            {
                _second = HostOf(connection);

                // The first operation reads again while this one is still bound.
                secondRead.SetResult();
                (_firstBefore, _firstAfter) = await first.WaitAsync(TimeSpan.FromSeconds(30));
            }
        }

        private static string HostOf(PostgresqlAdminConnection connection) =>
            new NpgsqlConnectionStringBuilder(
                connection.ConnectionStringFor("postgres", "dmsprov-session")
            ).Host!;

        [Test]
        public void It_keeps_each_operation_on_its_own_server() =>
            (_firstBefore, _second, _firstAfter).Should().Be(("server-a", "server-b", "server-a"));
    }

    [TestFixture]
    public class Given_a_fixed_value_the_driver_cannot_read
    {
        [Test]
        public void It_refuses_it_at_construction() =>
            FluentActions
                .Invoking(() => new PostgresqlAdminConnection("Port=not-a-number", TimeSpan.FromSeconds(30)))
                .Should()
                .Throw<Exception>();
    }
}
