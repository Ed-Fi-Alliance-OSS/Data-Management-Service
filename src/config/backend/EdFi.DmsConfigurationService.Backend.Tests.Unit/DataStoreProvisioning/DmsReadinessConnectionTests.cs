// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Sockets;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

/// <summary>
/// Startup readiness (7.3) against a server that accepts the TCP connection and never answers the login, with the
/// connection string's own connect timeout disabled (<c>Timeout=0</c>, <c>Connect Timeout=0</c>), so the driver
/// alone would wait forever and keep the host from starting. Each engine's server probe ends once
/// <c>CommandTimeout</c> has passed on the engine's clock and records the server as unavailable, so the verdict is
/// <c>NotReady</c>; the caller's cancellation during the same stall still propagates as a cancellation.
/// </summary>
public class DmsReadinessConnectionTests
{
    private static readonly TimeSpan _commandTimeout = TimeSpan.FromSeconds(30);

    // Guards a hang only: on the fixed code the validation ends as soon as the clock is advanced.
    private static readonly TimeSpan _hangGuard = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Accepts every connection, reads what the client sends and never sends a byte; only disposal closes them, so
    /// <see cref="ClientClosed"/> completes only when a client ends its own connection.
    /// </summary>
    internal sealed class StalledLoginServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<TcpClient> _clients = [];
        private readonly Task _accepting;
        private volatile bool _disposing;

        public StalledLoginServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _accepting = AcceptAsync();
        }

        public int Port { get; }

        public TaskCompletionSource Accepted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ClientClosed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync();
                    lock (_clients)
                    {
                        _clients.Add(client);
                    }

                    Accepted.TrySetResult();
                    _ = DrainAsync(client);
                }
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
                // The listener was stopped.
            }
        }

        /// <summary>Reads until the client closes or resets the connection.</summary>
        private async Task DrainAsync(TcpClient client)
        {
            byte[] buffer = new byte[4096];
            try
            {
                NetworkStream stream = client.GetStream();
                int read;
                do
                {
                    read = await stream.ReadAsync(buffer);
                } while (read > 0);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // A reset by the client, or this server's own disposal.
            }

            if (!_disposing)
            {
                ClientClosed.TrySetResult();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _disposing = true;
            _listener.Stop();
            await _accepting;
            lock (_clients)
            {
                foreach (TcpClient client in _clients)
                {
                    client.Dispose();
                }
            }
        }
    }

    private static PostgresqlAdminConnection PostgresqlAdmin(int port) =>
        new(
            $"Host=127.0.0.1;Port={port};Username=dms_admin;Password=x;SSL Mode=Disable;Timeout=0",
            _commandTimeout
        );

    private static MssqlAdminConnection MssqlAdmin(int port) =>
        new(
            $"Server=127.0.0.1,{port};User Id=dms_admin;Password=x;Connect Timeout=0;Encrypt=false;TrustServerCertificate=true",
            _commandTimeout
        );

    private static IDmsTargetEngine PostgresqlEngine(int port, TimeProvider timeProvider) =>
        new PostgresqlDmsTargetEngine(
            PostgresqlAdmin(port),
            new PostgresqlDmsEngineSettings(
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMinutes(5),
                Path.Combine(AppContext.BaseDirectory, "psql-not-available")
            ),
            timeProvider,
            NullLogger.Instance
        );

    private static IDmsTargetEngine MssqlEngine(int port, TimeProvider timeProvider) =>
        new MssqlDmsTargetEngine(
            MssqlAdmin(port),
            new MssqlDmsEngineSettings(
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMinutes(5),
                new MssqlStagingSettings(
                    Path.Combine(Path.GetTempPath(), "dms1438-readiness-" + Guid.NewGuid().ToString("N")),
                    "/var/opt/dms-staging",
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
                    3,
                    TimeSpan.FromSeconds(10)
                )
            ),
            timeProvider,
            NullLogger.Instance
        );

    /// <summary>Starts the validation against a stalled server and returns once the driver has connected to it.</summary>
    private static async Task<Task<DmsEngineEnvironment>> StallAsync(
        StalledLoginServer server,
        IDmsTargetEngine engine,
        CancellationToken cancellationToken
    )
    {
        Task<DmsEngineEnvironment> validation = engine.ValidateEnvironmentAsync(cancellationToken);
        await Task.WhenAny(server.Accepted.Task, validation);
        server
            .Accepted.Task.IsCompleted.Should()
            .BeTrue("the probe must connect to the stalled server instead of ending earlier");
        return validation;
    }

    private static async Task<DmsReadinessReason> StalledServerReasonAsync(
        Func<int, TimeProvider, IDmsTargetEngine> engine
    )
    {
        await using StalledLoginServer server = new();
        FakeTimeProvider time = new();
        Task<DmsEngineEnvironment> validation = await StallAsync(
            server,
            engine(server.Port, time),
            CancellationToken.None
        );
        time.Advance(_commandTimeout);
        DmsEngineEnvironment environment = await validation.WaitAsync(_hangGuard);
        return environment
            .Should()
            .BeOfType<DmsEngineEnvironment.NotReady>()
            .Subject.Reasons.Single(reason => reason.Probe == "TargetServer");
    }

    [TestFixture]
    public class Given_a_PostgreSQL_server_that_never_answers_the_login
    {
        private DmsReadinessReason _reason = null!;

        [SetUp]
        public async Task Setup() => _reason = await StalledServerReasonAsync(PostgresqlEngine);

        [Test]
        public void It_records_the_server_as_unavailable_at_the_connect() =>
            (_reason.Code, _reason.Diagnostic!.Operation)
                .Should()
                .Be((DmsProvisioningFailureCode.ServerUnavailable, PostgresqlEnvironment.ConnectOperation));

        [Test]
        public void It_names_the_readiness_bound_in_the_type_chain() =>
            _reason.Diagnostic!.ExceptionTypeChain.Should().StartWith(typeof(TimeoutException).FullName);
    }

    [TestFixture]
    public class Given_a_SQL_Server_that_never_answers_the_login
    {
        private DmsReadinessReason _reason = null!;

        [SetUp]
        public async Task Setup() => _reason = await StalledServerReasonAsync(MssqlEngine);

        [Test]
        public void It_records_the_server_as_unavailable_at_the_connect() =>
            (_reason.Code, _reason.Diagnostic!.Operation)
                .Should()
                .Be((DmsProvisioningFailureCode.ServerUnavailable, MssqlEnvironment.ConnectOperation));

        [Test]
        public void It_names_the_readiness_bound_in_the_type_chain() =>
            _reason.Diagnostic!.ExceptionTypeChain.Should().StartWith(typeof(TimeoutException).FullName);
    }

    [TestFixture]
    public class Given_the_caller_cancels_while_the_login_stalls
    {
        private (Exception? Outcome, CancellationToken Caller) _postgresql;
        private (Exception? Outcome, CancellationToken Caller) _mssql;

        private static async Task<(Exception? Outcome, CancellationToken Caller)> CancelledDuringStallAsync(
            Func<int, TimeProvider, IDmsTargetEngine> engine
        )
        {
            await using StalledLoginServer server = new();
            using CancellationTokenSource caller = new();
            Task<DmsEngineEnvironment> validation = await StallAsync(
                server,
                engine(server.Port, new FakeTimeProvider()),
                caller.Token
            );
            await caller.CancelAsync();
            Exception? outcome = await Packaging.Record.ExceptionAsync(() =>
                validation.WaitAsync(_hangGuard)
            );
            return (outcome, caller.Token);
        }

        [SetUp]
        public async Task Setup()
        {
            _postgresql = await CancelledDuringStallAsync(PostgresqlEngine);
            _mssql = await CancelledDuringStallAsync(MssqlEngine);
        }

        [Test]
        public void It_propagates_the_cancellation_on_PostgreSQL_instead_of_a_readiness_reason() =>
            _postgresql
                .Outcome.Should()
                .BeAssignableTo<OperationCanceledException>()
                .Which.CancellationToken.Should()
                .Be(_postgresql.Caller);

        [Test]
        public void It_propagates_the_cancellation_on_SQL_Server_instead_of_a_readiness_reason() =>
            _mssql
                .Outcome.Should()
                .BeAssignableTo<OperationCanceledException>()
                .Which.CancellationToken.Should()
                .Be(_mssql.Caller);
    }

    /// <summary>
    /// The probes that run once the server connect succeeded, each opening a connection of its own: the lock probe on
    /// both engines and the SQL Server staging visibility probe. A login that stalls on that second connection ends at
    /// <c>CommandTimeout</c> too: the lock probes return a failure (<c>LockUnavailable</c> at readiness), and the
    /// staging probe deletes its probe file and lets the timeout propagate for readiness to classify.
    /// </summary>
    [TestFixture]
    public class Given_a_later_probe_connects_to_a_server_that_never_answers_the_login
    {
        // Each probe's result, or what it threw, so a probe that never ends fails only its own test.
        private object _postgresqlLock = null!;
        private object _mssqlLock = null!;
        private object _stagingProbe = null!;
        private bool _probeFileDeleted;

        private static async Task<object> StalledAsync<T>(Func<int, FakeTimeProvider, Task<T>> start)
            where T : notnull
        {
            try
            {
                return await StartStalledAsync(start);
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        private static async Task<T> StartStalledAsync<T>(Func<int, FakeTimeProvider, Task<T>> start)
        {
            await using StalledLoginServer server = new();
            FakeTimeProvider time = new();
            Task<T> running = start(server.Port, time);
            await Task.WhenAny(server.Accepted.Task, running);
            server
                .Accepted.Task.IsCompleted.Should()
                .BeTrue("the probe must connect to the stalled server instead of ending earlier");
            time.Advance(_commandTimeout);
            if (await Task.WhenAny(running, Task.Delay(_hangGuard)) != running)
            {
                throw new InvalidOperationException("The probe did not end at its readiness bound.");
            }

            return await running;
        }

        [OneTimeSetUp]
        public async Task Setup()
        {
            _postgresqlLock = await StalledAsync(
                (port, time) =>
                    PostgresqlTargetLockSession.AcquireKeyAsync(
                        PostgresqlAdmin(port),
                        PostgresqlTargetLockSession.ProbeLockKey,
                        DmsOperationId.New(),
                        TimeSpan.FromSeconds(5),
                        TimeSpan.FromSeconds(5),
                        time,
                        NullLogger.Instance,
                        connectionOpened: null,
                        CancellationToken.None,
                        openBound: _commandTimeout
                    )
            );
            _mssqlLock = await StalledAsync(
                (port, time) =>
                    MssqlTargetLockSession.AcquireResourceAsync(
                        MssqlAdmin(port),
                        MssqlTargetLockSession.ProbeResource,
                        DmsOperationId.New(),
                        TimeSpan.FromSeconds(5),
                        TimeSpan.FromSeconds(5),
                        time,
                        NullLogger.Instance,
                        connectionOpened: null,
                        CancellationToken.None,
                        openBound: _commandTimeout
                    )
            );
            using Mssql.MssqlBackupStagerTests.StagingDirectory directory = new();
            _stagingProbe = await StalledAsync(
                (port, time) =>
                    new MssqlStagingProbe(directory.Stager(), MssqlAdmin(port), NullLogger.Instance)
                    {
                        TimeProvider = time,
                    }.RunAsync(CancellationToken.None)
            );
            _probeFileDeleted = directory.Entries().Length == 0;
        }

        [Test]
        public void It_fails_the_PostgreSQL_lock_probe_at_the_readiness_bound() =>
            _postgresqlLock
                .Should()
                .BeOfType<DmsTargetLockResult.Failure>()
                .Which.Diagnostic.ExceptionTypeChain.Should()
                .StartWith(typeof(TimeoutException).FullName);

        [Test]
        public void It_fails_the_SQL_Server_lock_probe_at_the_readiness_bound() =>
            _mssqlLock
                .Should()
                .BeOfType<DmsTargetLockResult.Failure>()
                .Which.Diagnostic.ExceptionTypeChain.Should()
                .StartWith(typeof(TimeoutException).FullName);

        [Test]
        public void It_ends_the_staging_probe_at_the_readiness_bound_and_deletes_its_file()
        {
            _stagingProbe.Should().BeOfType<TimeoutException>();
            _probeFileDeleted.Should().BeTrue();
        }
    }
}
