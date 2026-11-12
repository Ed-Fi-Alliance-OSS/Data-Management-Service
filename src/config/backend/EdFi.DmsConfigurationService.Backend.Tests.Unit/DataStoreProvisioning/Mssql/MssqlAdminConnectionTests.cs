// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Time.Testing;
using StalledLoginServer = EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.DmsReadinessConnectionTests.StalledLoginServer;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Mssql;

/// <summary>
/// The SQL Server admin connection (D-12, 6.1 step 3): every connection is dedicated (no pooling, no
/// transparent reconnect, no MARS), opened on the database and with the application name the caller names
/// rather than the configured ones, with the command timeout applied; a rotated source value reaches the next
/// connection; a value the driver cannot read is refused at construction.
/// </summary>
public class MssqlAdminConnectionTests
{
    [TestFixture]
    public class Given_a_configured_value_with_its_own_choices
    {
        private SqlConnectionStringBuilder _built = null!;

        [SetUp]
        public void Setup()
        {
            MssqlAdminConnection connection = new(
                "Server=db.example,1433;User Id=dms_admin;Password=secret;Database=edfi_configurationservice;Application Name=configured;Pooling=true;ConnectRetryCount=3;MultipleActiveResultSets=true;Command Timeout=5;TrustServerCertificate=true",
                TimeSpan.FromSeconds(30)
            );
            _built = new(connection.ConnectionStringFor("edfi_target", "dmsprov-session"));
        }

        [Test]
        public void It_turns_pooling_reconnects_and_mars_off() =>
            (_built.Pooling, _built.ConnectRetryCount, _built.MultipleActiveResultSets)
                .Should()
                .Be((false, 0, false));

        [Test]
        public void It_opens_the_named_database_with_the_named_application() =>
            (_built.InitialCatalog, _built.ApplicationName).Should().Be(("edfi_target", "dmsprov-session"));

        [Test]
        public void It_applies_the_command_timeout() => _built.CommandTimeout.Should().Be(30);

        [Test]
        public void It_keeps_the_server_and_credentials() =>
            (_built.DataSource, _built.UserID, _built.Password, _built.TrustServerCertificate)
                .Should()
                .Be(("db.example,1433", "dms_admin", "secret", true));
    }

    [TestFixture]
    public class Given_a_source_whose_value_rotates
    {
        private SqlConnectionStringBuilder _before = null!;
        private SqlConnectionStringBuilder _after = null!;

        [SetUp]
        public void Setup()
        {
            string current = "Server=db.example;User Id=dms_admin;Password=before;Pooling=true";
            MssqlAdminConnection connection = new(() => current, TimeSpan.FromMilliseconds(1500));
            _before = new(connection.ConnectionStringFor("master", "dmsprov-session"));
            current = "Server=db.example;User Id=dms_admin;Password=after;Pooling=true";
            _after = new(connection.ConnectionStringFor("master", "dmsprov-session"));
        }

        [Test]
        public void It_connects_with_the_value_current_at_each_use() =>
            (_before.Password, _after.Password).Should().Be(("before", "after"));

        [Test]
        public void It_applies_the_overrides_to_the_rotated_value_rounding_the_timeout_up() =>
            (_after.Pooling, _after.ConnectRetryCount, _after.CommandTimeout).Should().Be((false, 0, 2));
    }

    [TestFixture]
    public class Given_a_configured_connect_timeout
    {
        [TestCase("Connect Timeout=0", 30, TestName = "It_bounds_a_login_that_would_wait_forever")]
        [TestCase("Connect Timeout=5", 5, TestName = "It_keeps_a_shorter_positive_timeout")]
        [TestCase("Connect Timeout=120", 30, TestName = "It_bounds_a_longer_timeout")]
        public void It_ends_the_physical_login_within_the_command_timeout(
            string connectTimeout,
            int expected
        ) =>
            new SqlConnectionStringBuilder(
                new MssqlAdminConnection(
                    $"Server=db.example;User Id=dms_admin;Password=secret;{connectTimeout}",
                    TimeSpan.FromSeconds(30)
                ).ConnectionStringFor("master", "dmsprov-session")
            )
                .ConnectTimeout.Should()
                .Be(expected);
    }

    /// <summary>
    /// A server that accepts the TCP connection and never answers the login, with <c>Connect Timeout=0</c>. Ending
    /// the caller's wait does not end SqlClient's physical nonpooled login, which keeps a worker thread, the socket
    /// and a process-wide nonpooled-open slot until it ends. The server stays open and never closes a connection
    /// before disposal, so only the driver ending its login can close the socket.
    /// </summary>
    [TestFixture]
    public class Given_an_open_abandoned_while_its_login_never_answers
    {
        // Guards a hang only: on the fixed code the driver ends the login one second after it starts.
        private static readonly TimeSpan _hangGuard = TimeSpan.FromMinutes(2);
        private bool _waitEnded;
        private bool _loginEnded;

        [SetUp]
        public async Task Setup()
        {
            await using StalledLoginServer server = new();

            // The wait's own bound never expires on this clock, so the caller's cancellation abandons the open.
            MssqlAdminConnection connection = new(
                $"Server=127.0.0.1,{server.Port};User Id=dms_admin;Password=x;Connect Timeout=0;Encrypt=false;TrustServerCertificate=true",
                TimeSpan.FromSeconds(1),
                new FakeTimeProvider()
            );
            using CancellationTokenSource caller = new();
            Task<SqlConnection> opening = connection.OpenMaintenanceAsync(caller.Token);
            await Task.WhenAny(server.Accepted.Task, opening, Task.Delay(_hangGuard));
            server
                .Accepted.Task.IsCompleted.Should()
                .BeTrue("the open must connect to the stalled server instead of ending earlier");

            await caller.CancelAsync();
            _waitEnded = await Task.WhenAny(opening, Task.Delay(_hangGuard)) == opening;
            _ = opening.Exception;

            // Observed before the server's disposal, which would close the socket from its side.
            _loginEnded =
                await Task.WhenAny(server.ClientClosed.Task, Task.Delay(_hangGuard))
                == server.ClientClosed.Task;
        }

        [Test]
        public void It_ends_the_callers_wait() => _waitEnded.Should().BeTrue();

        [Test]
        public void It_ends_the_physical_login_and_closes_its_socket() => _loginEnded.Should().BeTrue();
    }

    [TestFixture]
    public class Given_values_it_must_refuse
    {
        [Test]
        public void It_refuses_a_value_the_driver_cannot_read_at_construction() =>
            FluentActions
                .Invoking(() => new MssqlAdminConnection("Connect Timeout=soon", TimeSpan.FromSeconds(30)))
                .Should()
                .Throw<Exception>();

        [Test]
        public void It_refuses_a_command_timeout_that_is_not_positive() =>
            FluentActions
                .Invoking(() => new MssqlAdminConnection("Server=db.example", TimeSpan.Zero))
                .Should()
                .Throw<ArgumentOutOfRangeException>();
    }
}
