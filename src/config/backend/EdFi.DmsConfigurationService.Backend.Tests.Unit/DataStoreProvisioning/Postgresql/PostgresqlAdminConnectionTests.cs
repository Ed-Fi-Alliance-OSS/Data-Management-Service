// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
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

    private static PostgresqlDmsTargetEngine EngineOver(PostgresqlAdminConnection connection) =>
        new(
            connection,
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

    /// <summary>
    /// Review finding 2, user: Npgsql 8 takes an omitted user from <c>PGUSER</c> (or the Kerberos or operating-system
    /// user) and would connect, while the <c>psql</c> handoff has no user to give. The value is refused wherever it is
    /// read, before anything dials 127.0.0.1:1, so readiness reports it as a startup validation failure instead of
    /// passing (or reporting an unreachable server) and failing every replay.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class Given_a_value_without_a_username_while_PGUSER_is_set
    {
        private const string WithoutUser = "Host=127.0.0.1;Port=1;Password=SENTINEL-PASSWORD-2c8d;Timeout=1";

        private string? _priorUser;
        private Exception? _configured;
        private Exception? _rotated;
        private Exception? _handoff;
        private DmsEngineEnvironment _readiness = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            _priorUser = Environment.GetEnvironmentVariable("PGUSER");
            Environment.SetEnvironmentVariable("PGUSER", "ambient_admin");

            _configured = Packaging.Record.Exception(() =>
                new PostgresqlAdminConnection(WithoutUser, TimeSpan.FromSeconds(30))
            );

            string current = "Host=127.0.0.1;Port=1;Username=dms_admin;Password=x;Timeout=1";
            PostgresqlAdminConnection rotating = new(() => current, TimeSpan.FromSeconds(30));
            _ = rotating.ConnectionStringFor("postgres", "dmsprov-session");
            current = WithoutUser;
            _rotated = await Packaging.Record.ExceptionAsync(async () =>
                await (await rotating.OpenMaintenanceAsync(CancellationToken.None)).DisposeAsync()
            );

            PostgresqlAdminConnection broken = new(() => WithoutUser, TimeSpan.FromSeconds(30));
            _handoff = Packaging.Record.Exception(() => broken.ToLibpqSettings());
            _readiness = await EngineOver(broken).ValidateEnvironmentAsync(CancellationToken.None);
        }

        [OneTimeTearDown]
        public void TearDown() => Environment.SetEnvironmentVariable("PGUSER", _priorUser);

        [Test]
        public void It_refuses_a_configured_value_at_construction() =>
            _configured
                .Should()
                .BeOfType<InvalidOperationException>()
                .Which.Message.Should()
                .NotContain("SENTINEL");

        [Test]
        public void It_refuses_a_rotated_value_before_dialing() =>
            _rotated.Should().BeOfType<InvalidOperationException>();

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
    }

    /// <summary>
    /// Review finding 2, password: without a configured password Npgsql 8 finds one in <c>PGPASSWORD</c> or a
    /// password file, which the replay never sees. Each ambient source is set in turn and the real driver logs in to
    /// a server that asks for a cleartext password: the readiness rule refuses exactly the configurations in which the
    /// driver sends a password other than the configured one, admits a configured password and a source the driver
    /// cannot read, and admits passwordless login. The home directory points at an empty directory except where the
    /// default password file is the source, so this machine's own file cannot interfere.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class Given_each_ambient_password_source_the_driver_consults
    {
        private const string AmbientPassword = "SENTINEL-AMBIENT-5d0e";
        private const string ConfiguredPassword = "configured-password";

        private static readonly string _homeVariable = OperatingSystem.IsWindows() ? "APPDATA" : "HOME";

        private readonly Dictionary<string, string?> _prior = [];
        private readonly Dictionary<string, (string? Sent, bool Refused)> _outcomes = [];
        private string _directory = null!;
        private Exception? _passwordless;
        private bool _passwordlessRefused;

        private static string ValueFor(int port, string? password) =>
            $"Host=127.0.0.1;Port={port};Username=dms_admin;SSL Mode=Disable;Timeout=10"
            + (password is null ? string.Empty : ";Password=" + password);

        private static async Task<(string? Sent, Exception? Failure)> LogInAsync(
            string? password,
            bool requestPassword
        )
        {
            await using FakePostgresqlServer server = new(null, requestPassword);
            PostgresqlAdminConnection admin = new(ValueFor(server.Port, password), TimeSpan.FromSeconds(30));
            Exception? failure = await Packaging.Record.ExceptionAsync(async () =>
                await (await admin.OpenMaintenanceAsync(CancellationToken.None)).DisposeAsync()
            );
            await server.Completion;
            return (server.ReceivedPassword, failure);
        }

        private static bool RefusedNow(string? configuredPassword) =>
            PostgresqlEnvironment.EvaluateAmbientPassword(
                configuredPassword,
                Environment.GetEnvironmentVariable("PGPASSWORD"),
                Environment.GetEnvironmentVariable("PGPASSFILE"),
                Environment.GetEnvironmentVariable(_homeVariable),
                File.Exists
            )
                is not null;

        private async Task ObserveAsync(
            string scenario,
            string? passwordVariable,
            string? passwordFileVariable,
            string home,
            string? configuredPassword
        )
        {
            Environment.SetEnvironmentVariable("PGPASSWORD", passwordVariable);
            Environment.SetEnvironmentVariable("PGPASSFILE", passwordFileVariable);
            Environment.SetEnvironmentVariable(_homeVariable, home);
            _outcomes[scenario] = (
                (await LogInAsync(configuredPassword, requestPassword: true)).Sent,
                RefusedNow(configuredPassword)
            );
        }

        [OneTimeSetUp]
        public async Task Setup()
        {
            foreach (string name in new[] { "PGPASSWORD", "PGPASSFILE", _homeVariable })
            {
                _prior[name] = Environment.GetEnvironmentVariable(name);
            }

            _directory = Directory.CreateTempSubdirectory("dms1438-pgpass-").FullName;
            string emptyHome = Directory.CreateDirectory(Path.Combine(_directory, "empty-home")).FullName;
            string home = Directory.CreateDirectory(Path.Combine(_directory, "home")).FullName;
            string entry = "*:*:*:*:" + AmbientPassword + "\n";
            string defaultFile = PostgresqlEnvironment.DefaultPasswordFilePath(home);
            Directory.CreateDirectory(Path.GetDirectoryName(defaultFile)!);
            await File.WriteAllTextAsync(defaultFile, entry);
            string namedFile = Path.Combine(_directory, "pgpass");
            await File.WriteAllTextAsync(namedFile, entry);

            await ObserveAsync("PGPASSWORD", AmbientPassword, null, emptyHome, null);
            await ObserveAsync("PGPASSFILE", null, namedFile, emptyHome, null);
            await ObserveAsync("default password file", null, null, home, null);
            await ObserveAsync(
                "PGPASSFILE naming no file",
                null,
                Path.Combine(_directory, "missing"),
                home,
                null
            );
            await ObserveAsync("configured password", AmbientPassword, namedFile, home, ConfiguredPassword);
            await ObserveAsync("no source", null, null, emptyHome, null);

            // Still with no source: a server that asks for no password.
            _passwordless = (await LogInAsync(null, requestPassword: false)).Failure;
            _passwordlessRefused = RefusedNow(null);
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            foreach ((string name, string? value) in _prior)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            Directory.Delete(_directory, recursive: true);
        }

        [Test]
        public void It_refuses_exactly_the_configurations_in_which_the_driver_sends_an_unconfigured_password()
        {
            _outcomes.Should().HaveCount(6);
            _outcomes
                .Should()
                .AllSatisfy(outcome =>
                    outcome
                        .Value.Refused.Should()
                        .Be(
                            outcome.Value.Sent is not null && outcome.Value.Sent != ConfiguredPassword,
                            outcome.Key
                        )
                );
        }

        [Test]
        public void It_sees_the_driver_take_the_ambient_password_from_each_source()
        {
            foreach (string source in new[] { "PGPASSWORD", "PGPASSFILE", "default password file" })
            {
                _outcomes[source].Should().Be((AmbientPassword, true), source);
            }
        }

        [Test]
        public void It_admits_a_configured_password_whatever_else_is_set() =>
            _outcomes["configured password"].Should().Be((ConfiguredPassword, false));

        [Test]
        public void It_admits_a_password_file_the_driver_cannot_read() =>
            _outcomes["PGPASSFILE naming no file"].Should().Be(((string?)null, false));

        [Test]
        public void It_admits_passwordless_login_without_an_ambient_source()
        {
            _outcomes["no source"].Should().Be(((string?)null, false));
            _passwordlessRefused.Should().BeFalse();
            _passwordless
                .Should()
                .BeOfType<PostgresException>()
                .Which.SqlState.Should()
                .Be(FakePostgresqlServer.FinalSqlState);
        }
    }

    /// <summary>
    /// Review finding 2, password, wired into readiness: with <c>PGPASSWORD</c> set and no configured password the
    /// startup validation is not ready and names the probe without any value; with a configured password the rule
    /// is silent. The server (127.0.0.1:1) is unreachable either way, which readiness reports separately.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class Given_readiness_while_PGPASSWORD_is_set
    {
        private const string AmbientPassword = "SENTINEL-AMBIENT-7a41";
        private const string Value = "Host=127.0.0.1;Port=1;Username=dms_admin;Timeout=1";

        private string? _prior;
        private DmsEngineEnvironment _withoutPassword = null!;
        private DmsEngineEnvironment _withPassword = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            _prior = Environment.GetEnvironmentVariable("PGPASSWORD");
            Environment.SetEnvironmentVariable("PGPASSWORD", AmbientPassword);
            _withoutPassword = await EngineOver(
                    new PostgresqlAdminConnection(Value, TimeSpan.FromSeconds(30))
                )
                .ValidateEnvironmentAsync(CancellationToken.None);
            _withPassword = await EngineOver(
                    new PostgresqlAdminConnection(Value + ";Password=configured", TimeSpan.FromSeconds(30))
                )
                .ValidateEnvironmentAsync(CancellationToken.None);
        }

        [OneTimeTearDown]
        public void TearDown() => Environment.SetEnvironmentVariable("PGPASSWORD", _prior);

        [Test]
        public void It_reports_the_ambient_password_as_a_startup_validation_failure() =>
            _withoutPassword
                .Should()
                .BeOfType<DmsEngineEnvironment.NotReady>()
                .Which.Reasons.Should()
                .ContainEquivalentOf(
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.StartupValidationFailed,
                        PostgresqlEnvironment.AmbientPasswordProbe,
                        DmsProvisioningDiagnostic.ForOperation(PostgresqlEnvironment.AmbientPasswordProbe)
                    )
                );

        [Test]
        public void It_reports_nothing_about_it_when_a_password_is_configured() =>
            _withPassword
                .Should()
                .BeOfType<DmsEngineEnvironment.NotReady>()
                .Which.Reasons.Should()
                .NotContain(reason => reason.Probe == PostgresqlEnvironment.AmbientPasswordProbe);
    }

    /// <summary>
    /// Review finding 3, observed through real TLS handshakes of the driver: with a verifying mode the
    /// administrative connection trusts what <c>psql</c> trusts with <c>sslrootcert</c> naming the file. A server
    /// certificate issued by the configured root is accepted; one from another root is refused in both modes;
    /// <c>VerifyFull</c> refuses a host-name mismatch that <c>VerifyCA</c>, as in <c>libpq</c>, accepts; and the
    /// file is read as PEM only, as <c>libpq</c> reads it, where Npgsql's own check would also accept DER.
    /// </summary>
    [TestFixture]
    public class Given_a_verifying_connection_to_a_server_presenting_a_certificate
    {
        private string _directory = null!;
        private readonly Dictionary<string, (bool Accepted, Exception? Failure)> _attempts = [];

        private static async Task<(bool Accepted, Exception? Failure)> AttemptAsync(
            X509Certificate2 serverCertificate,
            string mode,
            string rootCertificate
        )
        {
            await using FakePostgresqlServer server = new(serverCertificate, requestPassword: false);
            PostgresqlAdminConnection admin = new(
                $"Host=127.0.0.1;Port={server.Port};Username=dms_admin;Password=x;SSL Mode={mode};Root Certificate={rootCertificate};Timeout=10",
                TimeSpan.FromSeconds(30)
            );
            Exception? failure = await Packaging.Record.ExceptionAsync(async () =>
                await (await admin.OpenMaintenanceAsync(CancellationToken.None)).DisposeAsync()
            );
            await server.Completion;
            return (server.StartupReceived, failure);
        }

        [OneTimeSetUp]
        public async Task Setup()
        {
            _directory = Directory.CreateTempSubdirectory("dms1438-trust-").FullName;
            using X509Certificate2 configuredRoot = TestCertificates.CreateRoot("Configured Root");
            using X509Certificate2 otherRoot = TestCertificates.CreateRoot("Other Root");
            string pem = Path.Combine(_directory, "root.crt");
            await File.WriteAllTextAsync(pem, configuredRoot.ExportCertificatePem());
            string der = Path.Combine(_directory, "root-der.crt");
            await File.WriteAllBytesAsync(der, configuredRoot.RawData);

            using X509Certificate2 fromConfigured = TestCertificates.CreateServer(
                configuredRoot,
                "127.0.0.1"
            );
            using X509Certificate2 fromOther = TestCertificates.CreateServer(otherRoot, "127.0.0.1");
            using X509Certificate2 otherName = TestCertificates.CreateServer(
                configuredRoot,
                "other.example.test"
            );

            _attempts["configured root, VerifyFull"] = await AttemptAsync(fromConfigured, "VerifyFull", pem);
            _attempts["configured root, VerifyCA"] = await AttemptAsync(fromConfigured, "VerifyCA", pem);
            _attempts["other root, VerifyFull"] = await AttemptAsync(fromOther, "VerifyFull", pem);
            _attempts["other root, VerifyCA"] = await AttemptAsync(fromOther, "VerifyCA", pem);
            _attempts["other name, VerifyFull"] = await AttemptAsync(otherName, "VerifyFull", pem);
            _attempts["other name, VerifyCA"] = await AttemptAsync(otherName, "VerifyCA", pem);
            _attempts["configured root as DER, VerifyFull"] = await AttemptAsync(
                fromConfigured,
                "VerifyFull",
                der
            );
        }

        [OneTimeTearDown]
        public void TearDown() => Directory.Delete(_directory, recursive: true);

        private void ShouldBeAccepted(string attempt)
        {
            _attempts[attempt].Accepted.Should().BeTrue(attempt);
            _attempts[attempt]
                .Failure.Should()
                .BeOfType<PostgresException>(attempt)
                .Which.SqlState.Should()
                .Be(FakePostgresqlServer.FinalSqlState);
        }

        private void ShouldBeRefusedInTheHandshake(string attempt)
        {
            _attempts[attempt].Accepted.Should().BeFalse(attempt);
            _attempts[attempt].Failure.Should().BeOfType<NpgsqlException>(attempt);
        }

        [Test]
        public void It_accepts_a_server_certificate_issued_by_the_configured_root()
        {
            ShouldBeAccepted("configured root, VerifyFull");
            ShouldBeAccepted("configured root, VerifyCA");
        }

        [Test]
        public void It_refuses_a_server_certificate_issued_by_another_root()
        {
            ShouldBeRefusedInTheHandshake("other root, VerifyFull");
            ShouldBeRefusedInTheHandshake("other root, VerifyCA");
        }

        [Test]
        public void It_refuses_a_host_name_mismatch_with_VerifyFull() =>
            ShouldBeRefusedInTheHandshake("other name, VerifyFull");

        [Test]
        public void It_ignores_the_host_name_with_VerifyCA_as_libpq_does() =>
            ShouldBeAccepted("other name, VerifyCA");

        [Test]
        public void It_trusts_nothing_from_a_root_file_libpq_cannot_read_as_PEM() =>
            ShouldBeRefusedInTheHandshake("configured root as DER, VerifyFull");
    }

    /// <summary>
    /// Review finding 3, machine trust: the TLS layer reports no error at all when the machine store trusts the
    /// server's chain and the name matches. Npgsql 8 accepts that before it reads the configured file; <c>libpq</c>
    /// verifies only against the file. The check refuses such a certificate unless it chains to the file.
    /// </summary>
    [TestFixture]
    public class Given_a_server_certificate_the_machine_store_trusts
    {
        private string _directory = null!;
        private bool _otherRoot;
        private bool _configuredRoot;
        private bool _nameMismatchVerifyFull;

        [OneTimeSetUp]
        public void Setup()
        {
            _directory = Directory.CreateTempSubdirectory("dms1438-machine-trust-").FullName;
            using X509Certificate2 configuredRoot = TestCertificates.CreateRoot("Configured Root");
            using X509Certificate2 otherRoot = TestCertificates.CreateRoot("Machine-Trusted Root");
            string pem = Path.Combine(_directory, "root.crt");
            File.WriteAllText(pem, configuredRoot.ExportCertificatePem());
            using X509Certificate2 fromOther = TestCertificates.CreateServer(otherRoot, "db.example.test");
            using X509Certificate2 fromConfigured = TestCertificates.CreateServer(
                configuredRoot,
                "db.example.test"
            );

            using (X509Chain chain = new())
            {
                _otherRoot = PostgresqlAdminConnection.IsTrustedServerCertificate(
                    fromOther,
                    chain,
                    SslPolicyErrors.None,
                    pem,
                    verifyHostName: true
                );
            }

            using (X509Chain chain = new())
            {
                _configuredRoot = PostgresqlAdminConnection.IsTrustedServerCertificate(
                    fromConfigured,
                    chain,
                    SslPolicyErrors.None,
                    pem,
                    verifyHostName: true
                );
            }

            using (X509Chain chain = new())
            {
                _nameMismatchVerifyFull = PostgresqlAdminConnection.IsTrustedServerCertificate(
                    fromConfigured,
                    chain,
                    SslPolicyErrors.RemoteCertificateNameMismatch,
                    pem,
                    verifyHostName: true
                );
            }
        }

        [OneTimeTearDown]
        public void TearDown() => Directory.Delete(_directory, recursive: true);

        [Test]
        public void It_refuses_a_machine_trusted_certificate_from_a_root_outside_the_file() =>
            _otherRoot.Should().BeFalse();

        [Test]
        public void It_accepts_a_certificate_that_chains_to_the_file() => _configuredRoot.Should().BeTrue();

        [Test]
        public void It_refuses_a_name_mismatch_with_VerifyFull_even_from_the_configured_root() =>
            _nameMismatchVerifyFull.Should().BeFalse();
    }
}
