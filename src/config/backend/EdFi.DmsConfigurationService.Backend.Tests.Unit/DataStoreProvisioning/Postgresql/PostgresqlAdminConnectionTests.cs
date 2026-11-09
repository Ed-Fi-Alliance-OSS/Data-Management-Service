// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
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

    /// <summary>
    /// Review finding 1: the <c>psql</c> password file cannot represent a line break, while Npgsql connects with
    /// one. A value carrying one in its host, user, or password, configured or rotated in by a secret, is refused
    /// wherever the value is read, before anything dials the server (127.0.0.1:1 would otherwise fail as a
    /// refused connection): readiness reports it as a startup validation failure, and an operation's first
    /// step, the target lock, fails before any staging database can be created. No refusal quotes the value.
    /// </summary>
    [TestFixture]
    public class Given_a_value_with_a_line_break_the_password_file_cannot_carry
    {
        private const string Secret = "SENTINEL-PASSWORD-4be1";

        private static string WithLineBreak(string field, string lineBreak)
        {
            NpgsqlConnectionStringBuilder builder = new()
            {
                Host = "127.0.0.1",
                Port = 1,
                Username = "dms_admin",
                Password = Secret,
                Timeout = 1,
            };
            builder[field] = builder[field] + lineBreak + "tail";
            return builder.ConnectionString;
        }

        private readonly Dictionary<string, Exception?> _rotated = [];
        private Exception? _configured;
        private Exception? _handoff;
        private DmsEngineEnvironment _readiness = null!;
        private DmsTargetLockResult _lock = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            foreach (string field in new[] { "Host", "Username", "Password" })
            {
                foreach (string lineBreak in new[] { "\n", "\r" })
                {
                    string current = "Host=127.0.0.1;Port=1;Username=dms_admin;Password=" + Secret;
                    PostgresqlAdminConnection rotating = new(() => current, TimeSpan.FromSeconds(30));
                    _ = rotating.ConnectionStringFor("postgres", "dmsprov-session");
                    current = WithLineBreak(field, lineBreak);
                    _rotated[field + " " + (lineBreak == "\n" ? "LF" : "CR")] = Packaging.Record.Exception(
                        () =>
                            rotating.ConnectionStringFor("postgres", "dmsprov-session")
                    );
                }
            }

            _configured = Packaging.Record.Exception(() =>
                new PostgresqlAdminConnection(WithLineBreak("Password", "\n"), TimeSpan.FromSeconds(30))
            );

            PostgresqlAdminConnection broken = new(
                () => WithLineBreak("Password", "\r\n"),
                TimeSpan.FromSeconds(30)
            );
            _handoff = Packaging.Record.Exception(() => broken.ToLibpqSettings());
            PostgresqlDmsTargetEngine engine = new(
                broken,
                new PostgresqlDmsEngineSettings(
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromMinutes(5),
                    Path.Combine(AppContext.BaseDirectory, "psql-not-available")
                ),
                TimeProvider.System,
                NullLogger.Instance
            );
            _readiness = await engine.ValidateEnvironmentAsync(CancellationToken.None);
            _lock = await PostgresqlTargetLockSession.AcquireKeyAsync(
                broken,
                PostgresqlTargetLockSession.ProbeLockKey,
                DmsOperationId.New(),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                TimeProvider.System,
                NullLogger.Instance,
                connectionOpened: null,
                CancellationToken.None
            );
        }

        [Test]
        public void It_refuses_a_rotated_value_with_a_line_break_in_any_handed_off_field()
        {
            _rotated.Should().HaveCount(6);
            _rotated
                .Values.Should()
                .AllSatisfy(exception =>
                    exception
                        .Should()
                        .BeOfType<InvalidOperationException>()
                        .Which.Message.Should()
                        .Contain("line break")
                        .And.NotContain(Secret)
                        .And.NotContain("127.0.0.1")
                );
        }

        [Test]
        public void It_refuses_a_configured_value_at_construction() =>
            _configured.Should().BeOfType<InvalidOperationException>();

        [Test]
        public void It_refuses_the_psql_handoff() => _handoff.Should().BeOfType<InvalidOperationException>();

        [Test]
        public void It_reports_readiness_as_a_startup_validation_failure_of_the_server_probe() =>
            _readiness
                .Should()
                .BeOfType<DmsEngineEnvironment.NotReady>()
                .Which.Reasons.Should()
                .ContainEquivalentOf(
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.StartupValidationFailed,
                        PostgresqlEnvironment.TargetServerProbe,
                        new DmsProvisioningDiagnostic(
                            "System.InvalidOperationException",
                            null,
                            PostgresqlEnvironment.ConnectOperation
                        )
                    )
                );

        [Test]
        public void It_fails_the_target_lock_before_dialing_so_no_staging_database_is_created() =>
            _lock
                .Should()
                .BeOfType<DmsTargetLockResult.Failure>()
                .Which.Diagnostic.ExceptionTypeChain.Should()
                .Be("System.InvalidOperationException");
    }
}
