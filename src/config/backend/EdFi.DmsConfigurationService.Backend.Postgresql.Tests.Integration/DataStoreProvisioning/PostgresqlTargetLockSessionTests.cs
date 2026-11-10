// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning.DmsProvisioningTestServer;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning;

/// <summary>
/// The target lock holder session against a live server (D-12, Step 3.2): the dedicated holder session and
/// its advisory lock, deterministic contention and timeout, release, loss on termination without any reopen,
/// and the execution gate under a command that blocks on the server while the heartbeat becomes due. Every
/// wait is on a server-observed condition or a completion signal under a generous bound, never a fixed delay;
/// the gate fixtures drive the heartbeat and the command deadline from a fake clock.
/// </summary>
[Category(Category)]
public class PostgresqlTargetLockSessionTests
{
    private const string AdvisoryLockWait = "Lock:advisory";

    private static readonly TimeSpan _bound = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan _commandDeadline = TimeSpan.FromMinutes(1);
    private static readonly ProtectedDatabaseNames _protectedNames = ProtectedDatabaseNames.Create(
        "edfi_configurationservice",
        []
    );

    private static DmsTargetDatabaseName TargetName(string purpose)
    {
        string candidate = $"dms1438_{purpose}_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}";
        return
            DmsTargetDatabaseName.TryCreate(candidate, _protectedNames, out DmsTargetDatabaseName name)
            == DmsTargetNameValidation.Valid
            ? name
            : throw new InvalidOperationException("The target name does not satisfy the rules.");
    }

    private static async Task<PostgresqlTargetLockSession> AcquireAsync(
        PostgresqlDmsTargetEngine engine,
        DmsTargetDatabaseName name,
        DmsOperationId operation
    ) =>
        await engine.AcquireTargetLockAsync(name, operation, default) is DmsTargetLockResult.Acquired acquired
            ? (PostgresqlTargetLockSession)acquired.Holder
            : throw new InvalidOperationException("The target lock was not acquired.");

    /// <summary>A gated <c>SELECT 1</c> on the holder's own connection.</summary>
    private static Task<int> SelectOneAsync(PostgresqlTargetLockSession holder) =>
        holder.RunAsync(
            async token =>
            {
                await using NpgsqlCommand command = new("SELECT 1;", holder.Connection);
                return (int)(await command.ExecuteScalarAsync(token))!;
            },
            _commandDeadline,
            CancellationToken.None
        );

    /// <summary>
    /// A gated session-level advisory lock on <paramref name="key"/>, which blocks on the server while another
    /// session holds it. No driver timeout, so only the gate's <paramref name="deadline"/> bounds it.
    /// </summary>
    private static Task<int> LockOnHolderAsync(
        PostgresqlTargetLockSession holder,
        long key,
        TimeSpan deadline
    ) =>
        holder.RunAsync(
            async token =>
            {
                await using NpgsqlCommand command = new(
                    "SELECT pg_catalog.pg_advisory_lock(@key);",
                    holder.Connection
                )
                {
                    CommandTimeout = 0,
                };
                command.Parameters.AddWithValue("key", key);
                await command.ExecuteScalarAsync(token);
                return 1;
            },
            deadline,
            CancellationToken.None
        );

    private static Task<bool> WaitingOnAdvisoryLockAsync(long pid) =>
        WaitUntilAsync(async () => await WaitEventOfAsync(pid) == AdvisoryLockWait, _bound);

    /// <summary>A second ordinary session holding the advisory lock <paramref name="key"/>; released explicitly or by disposal.</summary>
    private sealed class Blocker(NpgsqlConnection connection, long key, long pid) : IAsyncDisposable
    {
        public long Pid { get; } = pid;

        public static async Task<Blocker> HoldAsync(long key)
        {
            NpgsqlConnection connection = await AdminConnection().OpenMaintenanceAsync(default);
            try
            {
                await using NpgsqlCommand command = new(
                    "SELECT pg_catalog.pg_backend_pid() FROM pg_catalog.pg_advisory_lock(@key);",
                    connection
                );
                command.Parameters.AddWithValue("key", key);
                int pid = (int)(await command.ExecuteScalarAsync())!;
                return new Blocker(connection, key, pid);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        public async Task ReleaseAsync()
        {
            await using NpgsqlCommand command = new(
                "SELECT pg_catalog.pg_advisory_unlock(@key);",
                connection
            );
            command.Parameters.AddWithValue("key", key);
            await command.ExecuteScalarAsync();
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    /// <summary>
    /// F-02 against a real server: a login role that took a lock is then made <c>NOLOGIN</c> (class 28, which the
    /// server raises under any authentication method). The next acquisition is <c>AccessDenied</c> with the SQLSTATE
    /// only; create and delete through the orchestrator are <c>AdminPrivilegeMissing</c>, permanent, at the lock
    /// step, and read nothing else; neither a diagnostic nor a log line carries the password or the connection
    /// string. A server-reported condition that is not a refusal (the role's connection limit reached, <c>53300</c>)
    /// and a server nobody listens on stay a lock infrastructure <c>Failure</c>.
    /// </summary>
    [TestFixture]
    public class Given_the_admin_login_is_refused_after_it_was_accepted
    {
        private readonly CapturingLogger _logs = new();
        private string? _role;
        private Lane? _lane;
        private DmsTargetLockResult _accepted = null!;
        private DmsTargetLockResult _limited = null!;
        private DmsTargetLockResult _refused = null!;
        private DmsTargetLockResult _unreachable = null!;
        private DmsDataStoreCreateResult _create = null!;
        private DmsDataStoreDeleteResult _delete = null!;
        private string[] _members = [];

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _role = await CreateLoginRoleAsync("lockrefused");
            PostgresqlDmsTargetEngine engine = new(
                AdminConnectionAs(_role),
                Settings(),
                TimeProvider.System,
                _logs
            );

            _accepted = await engine.AcquireTargetLockAsync(
                TargetName("accepted"),
                DmsOperationId.New(),
                default
            );
            if (_accepted is DmsTargetLockResult.Acquired acquired)
            {
                await acquired.Holder.DisposeAsync();
            }

            string role = PostgresqlIdentifiers.Quote(_role);
            await ExecuteOnServerAsync($"ALTER ROLE {role} CONNECTION LIMIT 0;");
            _limited = await engine.AcquireTargetLockAsync(
                TargetName("limited"),
                DmsOperationId.New(),
                default
            );

            await ExecuteOnServerAsync($"ALTER ROLE {role} CONNECTION LIMIT -1 NOLOGIN;");
            _refused = await engine.AcquireTargetLockAsync(
                TargetName("refused"),
                DmsOperationId.New(),
                default
            );

            _lane = await Lane.BuildAsync(Lane.ShapedSql(), new LaneHooks());
            Lane.Deployment refusing = _lane.WithEngine(engine);
            _create = await refusing.Service.CreateAsync(_lane.Request, default);
            _delete = await refusing.Service.DeleteAsync(_lane.DeleteRequest, default);
            _members = [.. refusing.Engine.Calls.Select(call => call.Member)];

            using TcpListener vacated = new(IPAddress.Loopback, 0);
            vacated.Start();
            int port = ((IPEndPoint)vacated.LocalEndpoint).Port;
            vacated.Stop();
            PostgresqlDmsTargetEngine unreachable = new(
                new PostgresqlAdminConnection(
                    new NpgsqlConnectionStringBuilder(AdminConnectionString!)
                    {
                        Host = "127.0.0.1",
                        Port = port,
                    }.ConnectionString,
                    TimeSpan.FromSeconds(30)
                ),
                Settings(),
                TimeProvider.System,
                _logs
            );
            _unreachable = await unreachable.AcquireTargetLockAsync(
                TargetName("unreachable"),
                DmsOperationId.New(),
                default
            );
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_lane is not null)
            {
                await _lane.DisposeAsync();
            }

            if (_role is not null)
            {
                await DropRoleAsync(_role);
            }
        }

        [Test]
        public void It_acquired_the_lock_while_the_login_was_accepted() =>
            _accepted.Should().BeOfType<DmsTargetLockResult.Acquired>();

        [Test]
        public void It_reports_the_refused_login_as_access_denied_with_the_sqlstate_only() =>
            _refused
                .Should()
                .BeOfType<DmsTargetLockResult.AccessDenied>()
                .Which.Diagnostic.Should()
                .Be(
                    new DmsProvisioningDiagnostic(
                        typeof(PostgresException).FullName!,
                        "28000",
                        PostgresqlTargetLockSession.AcquireStep
                    )
                );

        [Test]
        public void It_reports_a_refused_create_and_delete_as_a_permanent_missing_privilege()
        {
            foreach (DmsProvisioningFailure failure in new[] { Failure(_create), Failure(_delete) })
            {
                (
                    failure.Code,
                    failure.Category,
                    failure.Diagnostic.Operation,
                    failure.Diagnostic.ProviderErrorCode
                )
                    .Should()
                    .Be(
                        (
                            DmsProvisioningFailureCode.AdminPrivilegeMissing,
                            DmsProvisioningFailureCategory.Permanent,
                            DmsDataStoreProvisioningService.AcquireTargetLockStep,
                            "28000"
                        )
                    );
            }
        }

        [Test]
        public void It_reads_nothing_after_the_refused_acquisitions() =>
            _members
                .Should()
                .Equal(
                    nameof(IDmsTargetEngine.AcquireTargetLockAsync),
                    nameof(IDmsTargetEngine.AcquireTargetLockAsync)
                );

        [Test]
        public void It_exposes_neither_the_password_nor_the_connection_string()
        {
            string[] diagnostics =
            [
                .. new[]
                {
                    ((DmsTargetLockResult.AccessDenied)_refused).Diagnostic,
                    Failure(_create).Diagnostic,
                    Failure(_delete).Diagnostic,
                }.SelectMany(diagnostic =>
                    new[]
                    {
                        diagnostic.ExceptionTypeChain,
                        diagnostic.ProviderErrorCode ?? string.Empty,
                        diagnostic.Operation,
                    }
                ),
            ];
            string[] logged = [.. _logs.Entries];
            diagnostics
                .Concat(logged)
                .Should()
                .AllSatisfy(text =>
                    text.Should()
                        .NotContain(LoginRolePassword)
                        .And.NotContain("Password=", "a connection string must not be quoted")
                );
        }

        [Test]
        public void It_keeps_an_unreachable_server_a_lock_infrastructure_failure() =>
            _unreachable.Should().BeOfType<DmsTargetLockResult.Failure>();

        [Test]
        public void It_keeps_a_server_condition_that_is_not_a_refusal_a_lock_infrastructure_failure() =>
            _limited
                .Should()
                .BeOfType<DmsTargetLockResult.Failure>()
                .Which.Diagnostic.ProviderErrorCode.Should()
                .Be("53300");

        private static DmsProvisioningFailure Failure(DmsDataStoreCreateResult result) =>
            result.Should().BeOfType<DmsDataStoreCreateResult.Failure>().Subject.Detail;

        private static DmsProvisioningFailure Failure(DmsDataStoreDeleteResult result) =>
            result.Should().BeOfType<DmsDataStoreDeleteResult.Failure>().Subject.Detail;

        /// <summary>Every log line the engine and its lock sessions write, formatted, with any exception's full text.</summary>
        private sealed class CapturingLogger : ILogger
        {
            private readonly System.Collections.Concurrent.ConcurrentQueue<string> _entries = new();

            public IEnumerable<string> Entries => _entries;

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            ) => _entries.Enqueue(formatter(state, exception) + (exception?.ToString() ?? string.Empty));
        }
    }

    [TestFixture]
    public class Given_a_lock_is_acquired_and_released
    {
        private DmsTargetDatabaseName _name;
        private DmsOperationId _operation;
        private PostgresqlTargetLockSession _holder = null!;
        private string _applicationName = string.Empty;
        private string _databaseName = string.Empty;
        private DateTime _backendStart;
        private bool _grantedWhileHeld;
        private bool _lostWhileHeld;
        private int _gatedResult;
        private string _lockTimeoutAfterAcquire = string.Empty;
        private bool _grantedAfterRelease;
        private bool _sessionGoneAfterRelease;
        private Exception? _secondDisposal;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _name = TargetName("lock");
            _operation = DmsOperationId.New();
            _holder = await AcquireAsync(Engine(), _name, _operation);
            int pid = (int)_holder.Session.Id;
            _applicationName = await ScalarOnServerAsync<string>(
                "SELECT application_name FROM pg_stat_activity WHERE pid = @pid;",
                ("pid", pid)
            );
            _databaseName = await ScalarOnServerAsync<string>(
                "SELECT datname FROM pg_stat_activity WHERE pid = @pid;",
                ("pid", pid)
            );
            _backendStart = await ScalarOnServerAsync<DateTime>(
                "SELECT backend_start FROM pg_stat_activity WHERE pid = @pid;",
                ("pid", pid)
            );
            _grantedWhileHeld = await AdvisoryLockGrantedAsync(pid, _holder.Key);
            _lostWhileHeld = _holder.IsLost;
            _gatedResult = await SelectOneAsync(_holder);
            _lockTimeoutAfterAcquire = await _holder.RunAsync(
                async token =>
                {
                    await using NpgsqlCommand command = new("SHOW lock_timeout;", _holder.Connection);
                    return (string)(await command.ExecuteScalarAsync(token))!;
                },
                _commandDeadline,
                CancellationToken.None
            );

            await _holder.DisposeAsync();
            _grantedAfterRelease = await AdvisoryLockGrantedAsync(pid, _holder.Key);
            _sessionGoneAfterRelease = await WaitUntilAsync(
                async () => !await SessionExistsAsync(pid),
                _bound
            );
            _secondDisposal = await Record.ExceptionAsync(() => _holder.DisposeAsync().AsTask());
        }

        [Test]
        public void It_names_the_holder_session_by_the_operation() =>
            _applicationName.Should().Be(_operation.HolderApplicationName).And.HaveLength(20);

        [Test]
        public void It_binds_the_holder_to_the_maintenance_database() =>
            _databaseName.Should().Be(PostgresqlAdminConnection.MaintenanceDatabaseName);

        [Test]
        public void It_captures_the_backend_start_time() =>
            _holder.Session.StartedAt.Should().Be(new DateTimeOffset(_backendStart, TimeSpan.Zero));

        [Test]
        public void It_holds_the_advisory_lock_for_the_derived_key() =>
            (_grantedWhileHeld, _holder.Key)
                .Should()
                .Be((true, PostgresqlTargetLockSession.LockKeyFor(_name)));

        [Test]
        public void It_runs_commands_on_the_holder_connection_through_the_gate() =>
            _gatedResult.Should().Be(1);

        [Test]
        public void It_leaves_no_lock_timeout_on_the_holder_once_the_lock_is_held() =>
            _lockTimeoutAfterAcquire.Should().Be("0");

        [Test]
        public void It_declares_no_loss_while_healthy() => _lostWhileHeld.Should().BeFalse();

        [Test]
        public void It_releases_the_lock_on_disposal() => _grantedAfterRelease.Should().BeFalse();

        [Test]
        public void It_closes_the_holder_session_on_disposal() => _sessionGoneAfterRelease.Should().BeTrue();

        [Test]
        public void It_is_safe_to_dispose_again() => _secondDisposal.Should().BeNull();
    }

    [TestFixture]
    public class Given_two_acquisitions_for_one_name
    {
        private PostgresqlTargetLockSession? _first;
        private DmsTargetLockResult? _second;
        private IDmsTargetLockHolder? _secondHolder;
        private bool _contenderObservedWaiting;
        private bool _secondCompletedWhileFirstHeld;
        private bool _firstGrantedWhileContended;
        private bool _firstLostByContention;
        private bool _secondGranted;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            DmsTargetDatabaseName name = TargetName("contend");
            DmsOperationId contender = DmsOperationId.New();
            _first = await AcquireAsync(Engine(), name, DmsOperationId.New());

            Task<DmsTargetLockResult> second = Engine(lockAcquireTimeout: TimeSpan.FromSeconds(30))
                .AcquireTargetLockAsync(name, contender, default);
            _contenderObservedWaiting = await WaitUntilAsync(
                async () =>
                {
                    int pid = await PidNamedAsync(contender.HolderApplicationName);
                    return pid != 0 && await WaitEventOfAsync(pid) == AdvisoryLockWait;
                },
                _bound
            );
            _secondCompletedWhileFirstHeld = second.IsCompleted;
            _firstGrantedWhileContended = await AdvisoryLockGrantedAsync(_first.Session.Id, _first.Key);
            _firstLostByContention = _first.IsLost;

            await _first.DisposeAsync();
            _second = await second;
            _secondHolder = (_second as DmsTargetLockResult.Acquired)?.Holder;
            _secondGranted =
                _secondHolder is not null
                && await AdvisoryLockGrantedAsync(_secondHolder.Session.Id, _first.Key);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_secondHolder is not null)
            {
                await _secondHolder.DisposeAsync();
            }

            if (_first is not null)
            {
                await _first.DisposeAsync();
            }
        }

        [Test]
        public void It_blocks_the_contender_on_the_server_lock() =>
            _contenderObservedWaiting.Should().BeTrue();

        [Test]
        public void It_does_not_complete_the_contender_while_the_holder_lives() =>
            _secondCompletedWhileFirstHeld.Should().BeFalse();

        [Test]
        public void It_keeps_the_holder_lock_and_declares_no_loss_under_contention() =>
            (_firstGrantedWhileContended, _firstLostByContention).Should().Be((true, false));

        [Test]
        public void It_grants_the_contender_once_the_holder_is_released()
        {
            _second.Should().BeOfType<DmsTargetLockResult.Acquired>();
            _secondGranted.Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_a_contender_whose_budget_ends_while_the_holder_lives
    {
        private PostgresqlTargetLockSession? _holder;
        private DmsTargetLockResult _result = null!;
        private NpgsqlConnection? _contenderConnection;
        private ConnectionState? _contenderConnectionStateOnReturn;
        private bool _holderGranted;
        private bool _holderLost;
        private int _holderResult;
        private bool _contenderSessionGone;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            DmsTargetDatabaseName name = TargetName("budget");
            DmsOperationId contender = DmsOperationId.New();
            _holder = await AcquireAsync(Engine(), name, DmsOperationId.New());

            // The seam hands over the contender's own connection; the fixture keeps the reference so that
            // finalization cannot stand in for the disposal under test.
            _result = await PostgresqlTargetLockSession.AcquireAsync(
                AdminConnection(),
                name,
                contender,
                acquireTimeout: TimeSpan.FromSeconds(1),
                heartbeatInterval: TimeSpan.FromSeconds(5),
                TimeProvider.System,
                NullLogger.Instance,
                connectionOpened: connection => _contenderConnection = connection,
                default
            );
            _contenderConnectionStateOnReturn = _contenderConnection?.FullState;
            _holderGranted = await AdvisoryLockGrantedAsync(_holder.Session.Id, _holder.Key);
            _holderLost = _holder.IsLost;
            _holderResult = await SelectOneAsync(_holder);
            _contenderSessionGone = await WaitUntilAsync(
                async () => await SessionCountNamedAsync(contender.HolderApplicationName) == 0,
                _bound
            );
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_contenderConnection is not null)
            {
                await _contenderConnection.DisposeAsync();
            }

            if (_holder is not null)
            {
                await _holder.DisposeAsync();
            }
        }

        [Test]
        public void It_returns_timeout() => _result.Should().BeOfType<DmsTargetLockResult.Timeout>();

        [Test]
        public void It_closes_the_contender_connection_before_returning() =>
            _contenderConnectionStateOnReturn.Should().Be(ConnectionState.Closed);

        [Test]
        public void It_never_terminates_the_holder() =>
            (_holderGranted, _holderLost, _holderResult).Should().Be((true, false, 1));

        [Test]
        public void It_leaves_no_contender_session_behind() => _contenderSessionGone.Should().BeTrue();
    }

    [TestFixture]
    public class Given_an_acquisition_is_cancelled_while_it_waits
    {
        private PostgresqlTargetLockSession? _holder;
        private bool _contenderObservedWaiting;
        private Exception? _outcome;
        private bool _contenderSessionGone;
        private bool _holderGranted;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            DmsTargetDatabaseName name = TargetName("cancel");
            DmsOperationId contender = DmsOperationId.New();
            _holder = await AcquireAsync(Engine(), name, DmsOperationId.New());

            using CancellationTokenSource cancellation = new();
            Task<DmsTargetLockResult> waiting = Engine(lockAcquireTimeout: TimeSpan.FromSeconds(30))
                .AcquireTargetLockAsync(name, contender, cancellation.Token);
            _contenderObservedWaiting = await WaitUntilAsync(
                async () =>
                {
                    int pid = await PidNamedAsync(contender.HolderApplicationName);
                    return pid != 0 && await WaitEventOfAsync(pid) == AdvisoryLockWait;
                },
                _bound
            );
            await cancellation.CancelAsync();
            _outcome = await Record.ExceptionAsync(() => waiting);
            _contenderSessionGone = await WaitUntilAsync(
                async () => await SessionCountNamedAsync(contender.HolderApplicationName) == 0,
                _bound
            );
            _holderGranted = await AdvisoryLockGrantedAsync(_holder.Session.Id, _holder.Key);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_holder is not null)
            {
                await _holder.DisposeAsync();
            }
        }

        [Test]
        public void It_sees_the_contender_waiting_before_cancellation() =>
            _contenderObservedWaiting.Should().BeTrue();

        [Test]
        public void It_propagates_the_cancellation() =>
            _outcome.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_closes_the_cancelled_contender_session() => _contenderSessionGone.Should().BeTrue();

        [Test]
        public void It_leaves_the_holder_lock_in_place() => _holderGranted.Should().BeTrue();
    }

    [TestFixture]
    public class Given_locks_for_different_names
    {
        private PostgresqlTargetLockSession? _first;
        private PostgresqlTargetLockSession? _second;
        private bool _bothGranted;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            PostgresqlDmsTargetEngine engine = Engine(lockAcquireTimeout: TimeSpan.FromSeconds(1));
            Task<PostgresqlTargetLockSession> first = AcquireAsync(
                engine,
                TargetName("one"),
                DmsOperationId.New()
            );
            Task<PostgresqlTargetLockSession> second = AcquireAsync(
                engine,
                TargetName("two"),
                DmsOperationId.New()
            );
            await Task.WhenAll(first, second);
            _first = await first;
            _second = await second;
            _bothGranted =
                await AdvisoryLockGrantedAsync(_first.Session.Id, _first.Key)
                && await AdvisoryLockGrantedAsync(_second.Session.Id, _second.Key);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_second is not null)
            {
                await _second.DisposeAsync();
            }

            if (_first is not null)
            {
                await _first.DisposeAsync();
            }
        }

        [Test]
        public void It_acquires_both_without_contention() => _bothGranted.Should().BeTrue();

        [Test]
        public void It_derives_different_keys() => _first!.Key.Should().NotBe(_second!.Key);
    }

    [TestFixture]
    public class Given_the_holder_session_is_terminated
    {
        private static readonly TimeSpan _heartbeat = TimeSpan.FromSeconds(1);

        private PostgresqlTargetLockSession? _holder;
        private DmsTargetLockResult? _reacquired;
        private bool _terminated;
        private bool _lossObserved;
        private TimeSpan _lossLatency;
        private Exception? _commandAfterLoss;
        private bool _noHolderSessionRemains;
        private Exception? _disposalAfterLoss;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            DmsTargetDatabaseName name = TargetName("terminate");
            DmsOperationId operation = DmsOperationId.New();
            PostgresqlDmsTargetEngine engine = Engine(lockHeartbeatInterval: _heartbeat);
            _holder = await AcquireAsync(engine, name, operation);
            TaskCompletionSource lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = _holder.LostToken.Register(() =>
                lost.TrySetResult()
            );

            long started = Stopwatch.GetTimestamp();
            _terminated = await TerminateBackendAsync(_holder.Session.Id);
            _lossObserved = await Task.WhenAny(lost.Task, Task.Delay(_bound)) == lost.Task;
            _lossLatency = Stopwatch.GetElapsedTime(started);

            _commandAfterLoss = await Record.ExceptionAsync(() => SelectOneAsync(_holder));
            _noHolderSessionRemains = await WaitUntilAsync(
                async () => await SessionCountNamedAsync(operation.HolderApplicationName) == 0,
                _bound
            );
            _reacquired = await engine.AcquireTargetLockAsync(name, DmsOperationId.New(), default);
            _disposalAfterLoss = await Record.ExceptionAsync(() => _holder.DisposeAsync().AsTask());
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_reacquired is DmsTargetLockResult.Acquired acquired)
            {
                await acquired.Holder.DisposeAsync();
            }

            if (_holder is not null)
            {
                await _holder.DisposeAsync();
            }
        }

        [Test]
        public void It_terminated_the_holder_backend() => _terminated.Should().BeTrue();

        [Test]
        public void It_declares_loss_through_the_heartbeat()
        {
            TestContext.Out.WriteLine(
                $"Loss observed {_lossLatency.TotalMilliseconds:F0} ms after termination."
            );
            _lossObserved.Should().BeTrue();
        }

        [Test]
        public void It_refuses_commands_after_loss() =>
            _commandAfterLoss.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_never_reopens_the_holder_session() => _noHolderSessionRemains.Should().BeTrue();

        [Test]
        public void It_lets_the_lock_be_acquired_again() =>
            _reacquired.Should().BeOfType<DmsTargetLockResult.Acquired>();

        [Test]
        public void It_disposes_safely_after_loss() => _disposalAfterLoss.Should().BeNull();
    }

    [TestFixture]
    public class Given_a_heartbeat_becomes_due_while_a_holder_command_blocks_on_the_server
    {
        private static readonly TimeSpan _interval = TimeSpan.FromSeconds(5);

        private FakeTimeProvider _clock = null!;
        private PostgresqlTargetLockSession? _holder;
        private Blocker? _blocker;
        private bool _holderObservedWaiting;
        private int _skippedWhileBlocked;
        private int _completedWhileBlocked;
        private bool _lostWhileBlocked;
        private bool _completedBeforeRelease;
        private Exception? _blockedFailure;
        private int _result;
        private bool _probedAfterRelease;
        private bool _lostAfterRelease;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _clock = new FakeTimeProvider();
            _holder = await AcquireAsync(
                Engine(lockHeartbeatInterval: _interval, timeProvider: _clock),
                TargetName("gate"),
                DmsOperationId.New()
            );
            long otherKey = PostgresqlTargetLockSession.LockKeyFor(TargetName("blocker"));
            _blocker = await Blocker.HoldAsync(otherKey);

            Task<int> blocked = LockOnHolderAsync(_holder, otherKey, _commandDeadline);
            _holderObservedWaiting = await WaitingOnAdvisoryLockAsync(_holder.Session.Id);
            _clock.Advance(_interval);
            _clock.Advance(_interval);
            _skippedWhileBlocked = _holder.Heartbeat.SkippedProbes;
            _completedWhileBlocked = _holder.Heartbeat.CompletedProbes;
            _lostWhileBlocked = _holder.IsLost;
            _completedBeforeRelease = blocked.IsCompleted;

            await _blocker.ReleaseAsync();
            _blockedFailure = await Record.ExceptionAsync(async () => _result = await blocked);

            _clock.Advance(_interval);
            _probedAfterRelease = await WaitUntilAsync(
                () => Task.FromResult(_holder.Heartbeat.CompletedProbes == 1),
                _bound
            );
            _lostAfterRelease = _holder.IsLost;
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_blocker is not null)
            {
                await _blocker.DisposeAsync();
            }

            if (_holder is not null)
            {
                await _holder.DisposeAsync();
            }
        }

        [Test]
        public void It_sees_the_command_blocked_on_the_server() => _holderObservedWaiting.Should().BeTrue();

        [Test]
        public void It_skips_both_due_probes_instead_of_waiting() =>
            (_skippedWhileBlocked, _completedWhileBlocked).Should().Be((2, 0));

        [Test]
        public void It_declares_no_loss_while_the_command_blocks() => _lostWhileBlocked.Should().BeFalse();

        [Test]
        public void It_completes_the_blocked_command_only_after_release() =>
            (_completedBeforeRelease, _blockedFailure, _result).Should().Be((false, null, 1));

        [Test]
        public void It_probes_once_the_gate_is_free() =>
            (_probedAfterRelease, _lostAfterRelease).Should().Be((true, false));
    }

    [TestFixture]
    public class Given_a_holder_command_deadline_expires_while_gated
    {
        private static readonly TimeSpan _interval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan _deadline = TimeSpan.FromSeconds(3);

        private FakeTimeProvider _clock = null!;
        private PostgresqlTargetLockSession? _holder;
        private Blocker? _blocker;
        private long _otherKey;
        private bool _holderObservedWaiting;
        private Exception? _failure;
        private bool _lost;
        private int _laterResult;
        private bool _blockerStillHolds;
        private bool _holderHoldsTheOtherKey;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _clock = new FakeTimeProvider();
            _holder = await AcquireAsync(
                Engine(lockHeartbeatInterval: _interval, timeProvider: _clock),
                TargetName("deadline"),
                DmsOperationId.New()
            );
            _otherKey = PostgresqlTargetLockSession.LockKeyFor(TargetName("blocker"));
            _blocker = await Blocker.HoldAsync(_otherKey);

            Task<int> blocked = LockOnHolderAsync(_holder, _otherKey, _deadline);
            _holderObservedWaiting = await WaitingOnAdvisoryLockAsync(_holder.Session.Id);
            _clock.Advance(_deadline);
            _failure = await Record.ExceptionAsync(() => blocked);
            _lost = _holder.IsLost;
            _laterResult = await SelectOneAsync(_holder);
            _blockerStillHolds = await AdvisoryLockGrantedAsync(_blocker.Pid, _otherKey);
            _holderHoldsTheOtherKey = await AdvisoryLockGrantedAsync(_holder.Session.Id, _otherKey);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_blocker is not null)
            {
                await _blocker.DisposeAsync();
            }

            if (_holder is not null)
            {
                await _holder.DisposeAsync();
            }
        }

        [Test]
        public void It_sees_the_command_blocked_on_the_server() => _holderObservedWaiting.Should().BeTrue();

        [Test]
        public void It_cancels_that_command() =>
            _failure.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_declares_no_loss() => _lost.Should().BeFalse();

        [Test]
        public void It_leaves_the_holder_usable() => _laterResult.Should().Be(1);

        [Test]
        public void It_leaves_the_blocked_lock_with_the_other_session() =>
            (_blockerStillHolds, _holderHoldsTheOtherKey).Should().Be((true, false));
    }
}
