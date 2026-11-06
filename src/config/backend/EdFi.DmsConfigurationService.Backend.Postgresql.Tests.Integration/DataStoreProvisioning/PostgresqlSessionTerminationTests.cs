// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using FluentAssertions;
using Npgsql;
using static EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning.DmsProvisioningTestServer;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning;

/// <summary>
/// Session termination and exclusivity verification against a live server (D-22, Step 3.4): a busy session
/// is terminated with the timeout form and proven absent; a session that is already gone, or a live pid whose
/// start time differs, is never signalled; both privilege refusals (a superuser-owned session under a
/// non-superuser admin, an admin without <c>pg_signal_backend</c>) leave the session alive and carry
/// <c>42501</c>; an admin that may not read another role's start time (<c>pg_read_all_stats</c>) cannot
/// identify the session and is refused rather than told it is gone; a backend that cannot act on the
/// signal (stopped with <c>SIGSTOP</c>) is reported as a
/// timeout with the session still listed; cancellation while the server waits propagates without a verdict;
/// the holder session and a <c>psql</c>-style session located by <c>application_name</c> go through the
/// same protocol. Every wait is on a server-observed condition under a bound, never a fixed delay.
/// </summary>
[Category(Category)]
public class PostgresqlSessionTerminationTests
{
    private const string SleepWait = "Timeout:PgSleep";
    private const string SignalRole = "pg_signal_backend";
    private const string ReadStatsRole = "pg_read_all_stats";

    private static readonly TimeSpan _bound = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan _deadline = TimeSpan.FromSeconds(30);

    private static Task<bool> SessionGoneAsync(DmsServerSessionId session) =>
        WaitUntilAsync(async () => !await SessionPresentAsync(session), _bound);

    /// <summary>
    /// A session as the role given to <see cref="StartAsync"/> (the lane admin by default) running
    /// <c>pg_sleep</c> inside a transaction, handed over only once the server shows it sleeping. Disposal
    /// ends it whatever happened.
    /// </summary>
    private sealed class BusySession : IAsyncDisposable
    {
        private const string ApplicationName = "dms1438-busy";

        private readonly NpgsqlConnection _connection;
        private readonly NpgsqlTransaction _transaction;
        private readonly NpgsqlCommand _sleep;
        private readonly Task _sleeping;

        private BusySession(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            NpgsqlCommand sleep,
            Task sleeping,
            DmsServerSessionId session
        )
        {
            _connection = connection;
            _transaction = transaction;
            _sleep = sleep;
            _sleeping = sleeping;
            Session = session;
        }

        public DmsServerSessionId Session { get; }

        public static async Task<BusySession> StartAsync(string? roleName = null)
        {
            PostgresqlAdminConnection admin = roleName is null
                ? AdminConnection()
                : AdminConnectionAs(roleName);
            NpgsqlConnection connection = await admin.OpenAsync(
                PostgresqlAdminConnection.MaintenanceDatabaseName,
                ApplicationName,
                default
            );
            try
            {
                DmsServerSessionId session = await OwnSessionAsync(connection);
                NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
                NpgsqlCommand sleep = new("SELECT pg_catalog.pg_sleep(600);", connection, transaction)
                {
                    CommandTimeout = 0,
                };
                Task sleeping = sleep.ExecuteNonQueryAsync();
                if (
                    !await WaitUntilAsync(async () => await WaitEventOfAsync(session.Id) == SleepWait, _bound)
                )
                {
                    throw new InvalidOperationException("The busy session was not observed sleeping.");
                }

                return new BusySession(connection, transaction, sleep, sleeping, session);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        /// <summary>What ended the sleep, once it ends within <see cref="_bound"/>: null for a normal end, the exception otherwise.</summary>
        public async Task<Exception?> SleepOutcomeAsync()
        {
            if (await Task.WhenAny(_sleeping, Task.Delay(_bound)) != _sleeping)
            {
                return new TimeoutException("The sleep did not end within the bound.");
            }

            return await Record.ExceptionAsync(() => _sleeping);
        }

        public async ValueTask DisposeAsync()
        {
            await Record.ExceptionAsync(() => TerminateBackendAsync(Session.Id));
            await SleepOutcomeAsync();
            await Record.ExceptionAsync(() => _sleep.DisposeAsync().AsTask());
            await Record.ExceptionAsync(() => _transaction.DisposeAsync().AsTask());
            await _connection.DisposeAsync();
        }
    }

    /// <summary>An idle session whose backend process is stopped with <c>SIGSTOP</c>; disposal continues it and waits for it to end.</summary>
    private sealed class StoppedSession : IAsyncDisposable
    {
        private const string ApplicationName = "dms1438-stopped";

        private readonly NpgsqlConnection _connection;

        private StoppedSession(NpgsqlConnection connection, DmsServerSessionId session)
        {
            _connection = connection;
            Session = session;
        }

        public DmsServerSessionId Session { get; }

        public static async Task<StoppedSession> StartAsync()
        {
            NpgsqlConnection connection = await AdminConnection()
                .OpenAsync(PostgresqlAdminConnection.MaintenanceDatabaseName, ApplicationName, default);
            try
            {
                DmsServerSessionId session = await OwnSessionAsync(connection);
                await StopBackendAsync(session.Id);
                return new StoppedSession(connection, session);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Record.ExceptionAsync(() => ContinueBackendAsync(Session.Id));
            await Record.ExceptionAsync(() => TerminateBackendAsync(Session.Id));
            await SessionGoneAsync(Session);
            await _connection.DisposeAsync();
        }
    }

    [TestFixture]
    public class Given_a_busy_session_is_terminated
    {
        private BusySession? _busy;
        private DmsSessionTermination _verdict = null!;
        private bool _presentAfterVerdict;
        private bool _backendGone;
        private Exception? _sleepOutcome;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _busy = await BusySession.StartAsync();
            _verdict = await Engine().TerminateSessionAsync(_busy.Session, _deadline, default);
            _presentAfterVerdict = await SessionPresentAsync(_busy.Session);
            _backendGone = await WaitUntilAsync(
                async () => !await SessionExistsAsync(_busy.Session.Id),
                _bound
            );
            _sleepOutcome = await _busy.SleepOutcomeAsync();
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_busy is not null)
            {
                await _busy.DisposeAsync();
            }
        }

        [Test]
        public void It_reports_terminated() => _verdict.Should().BeOfType<DmsSessionTermination.Terminated>();

        [Test]
        public void It_verified_absence_before_returning() => _presentAfterVerdict.Should().BeFalse();

        [Test]
        public void It_ended_the_backend() => _backendGone.Should().BeTrue();

        [Test]
        public void It_ended_the_sleep_with_an_administrator_termination() =>
            PostgresqlErrorClassifier
                .SqlStateOf(_sleepOutcome!)
                .Should()
                .Be(PostgresErrorCodes.AdminShutdown);
    }

    [TestFixture]
    public class Given_a_session_that_is_already_gone
    {
        private DmsSessionTermination _verdict = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            DmsServerSessionId session;
            await using (NpgsqlConnection connection = await AdminConnection().OpenMaintenanceAsync(default))
            {
                session = await OwnSessionAsync(connection);
            }

            if (!await SessionGoneAsync(session))
            {
                throw new InvalidOperationException("The closed session is still listed.");
            }

            _verdict = await Engine().TerminateSessionAsync(session, _deadline, default);
        }

        [Test]
        public void It_reports_already_absent() =>
            _verdict.Should().BeOfType<DmsSessionTermination.AlreadyAbsent>();
    }

    [TestFixture]
    public class Given_a_live_pid_whose_start_time_differs
    {
        private BusySession? _busy;
        private DmsSessionTermination _verdict = null!;
        private bool _presentAfterVerdict;
        private string _waitEventAfterVerdict = string.Empty;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _busy = await BusySession.StartAsync();
            DmsServerSessionId reusedPid = _busy.Session with
            {
                StartedAt = _busy.Session.StartedAt - TimeSpan.FromSeconds(1),
            };
            _verdict = await Engine().TerminateSessionAsync(reusedPid, _deadline, default);
            _presentAfterVerdict = await SessionPresentAsync(_busy.Session);
            _waitEventAfterVerdict = await WaitEventOfAsync(_busy.Session.Id);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_busy is not null)
            {
                await _busy.DisposeAsync();
            }
        }

        [Test]
        public void It_treats_the_session_as_gone() =>
            _verdict.Should().BeOfType<DmsSessionTermination.AlreadyAbsent>();

        [Test]
        public void It_never_signals_the_backend_now_holding_the_pid() =>
            (_presentAfterVerdict, _waitEventAfterVerdict).Should().Be((true, SleepWait));
    }

    [TestFixture]
    public class Given_a_superuser_session_and_an_admin_that_is_not_one
    {
        private string? _signaller;
        private BusySession? _busy;
        private DmsSessionTermination _verdict = null!;
        private bool _presentAfterRefusal;
        private DmsSessionTermination _bySuperuser = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            await RequireSuperuserOrSkipAsync();
            _signaller = await CreateLoginRoleAsync("signaller", SignalRole, ReadStatsRole);
            _busy = await BusySession.StartAsync();
            _verdict = await EngineAs(_signaller).TerminateSessionAsync(_busy.Session, _deadline, default);
            _presentAfterRefusal = await SessionPresentAsync(_busy.Session);
            _bySuperuser = await Engine().TerminateSessionAsync(_busy.Session, _deadline, default);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_busy is not null)
            {
                await _busy.DisposeAsync();
            }

            if (_signaller is not null)
            {
                await DropRoleAsync(_signaller);
            }
        }

        [Test]
        public void It_reports_the_privilege_refusal_with_its_state() =>
            _verdict
                .Should()
                .BeOfType<DmsSessionTermination.NotTerminated>()
                .Which.Should()
                .BeEquivalentTo(
                    new
                    {
                        Reason = DmsSessionTerminationFailure.PrivilegeMissing,
                        Diagnostic = new
                        {
                            ProviderErrorCode = PostgresErrorCodes.InsufficientPrivilege,
                            Operation = PostgresqlSessionQueries.TerminateSessionStep,
                        },
                    }
                );

        [Test]
        public void It_names_only_exception_types() =>
            ((DmsSessionTermination.NotTerminated)_verdict)
                .Diagnostic!.ExceptionTypeChain.Should()
                .StartWith(typeof(PostgresException).FullName);

        [Test]
        public void It_leaves_the_session_alive() => _presentAfterRefusal.Should().BeTrue();

        [Test]
        public void It_was_terminable_by_a_superuser() =>
            _bySuperuser.Should().BeOfType<DmsSessionTermination.Terminated>();
    }

    [TestFixture]
    public class Given_an_admin_without_pg_signal_backend
    {
        private string? _admin;
        private string? _victim;
        private BusySession? _busy;
        private DmsSessionTermination _withoutPrivilege = null!;
        private bool _presentAfterRefusal;
        private DmsSessionTermination _withPrivilege = null!;
        private bool _presentAfterGrant;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            await RequireSuperuserOrSkipAsync();
            _admin = await CreateLoginRoleAsync("admin", ReadStatsRole);
            _victim = await CreateLoginRoleAsync("victim");
            _busy = await BusySession.StartAsync(_victim);
            _withoutPrivilege = await EngineAs(_admin)
                .TerminateSessionAsync(_busy.Session, _deadline, default);
            _presentAfterRefusal = await SessionPresentAsync(_busy.Session);
            await GrantRoleAsync(SignalRole, _admin);
            _withPrivilege = await EngineAs(_admin).TerminateSessionAsync(_busy.Session, _deadline, default);
            _presentAfterGrant = await SessionPresentAsync(_busy.Session);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_busy is not null)
            {
                await _busy.DisposeAsync();
            }

            if (_victim is not null)
            {
                await DropRoleAsync(_victim);
            }

            if (_admin is not null)
            {
                await DropRoleAsync(_admin);
            }
        }

        [Test]
        public void It_reports_the_privilege_refusal_with_its_state() =>
            _withoutPrivilege
                .Should()
                .BeOfType<DmsSessionTermination.NotTerminated>()
                .Which.Should()
                .BeEquivalentTo(
                    new
                    {
                        Reason = DmsSessionTerminationFailure.PrivilegeMissing,
                        Diagnostic = new { ProviderErrorCode = PostgresErrorCodes.InsufficientPrivilege },
                    }
                );

        [Test]
        public void It_leaves_the_session_alive() => _presentAfterRefusal.Should().BeTrue();

        [Test]
        public void It_terminates_once_the_privilege_is_granted() =>
            (_withPrivilege, _presentAfterGrant).Should().Be((new DmsSessionTermination.Terminated(), false));
    }

    [TestFixture]
    public class Given_an_admin_that_cannot_read_the_session_identity
    {
        private string? _admin;
        private string? _victim;
        private BusySession? _busy;
        private DmsSessionTermination _withoutStats = null!;
        private bool _presentAfterRefusal;
        private string _waitEventAfterRefusal = string.Empty;
        private DmsSessionTermination _withStats = null!;
        private bool _presentAfterGrant;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            await RequireSuperuserOrSkipAsync();
            _admin = await CreateLoginRoleAsync("blind", SignalRole);
            _victim = await CreateLoginRoleAsync("victim");
            _busy = await BusySession.StartAsync(_victim);
            _withoutStats = await EngineAs(_admin).TerminateSessionAsync(_busy.Session, _deadline, default);
            _presentAfterRefusal = await SessionPresentAsync(_busy.Session);
            _waitEventAfterRefusal = await WaitEventOfAsync(_busy.Session.Id);
            await GrantRoleAsync(ReadStatsRole, _admin);
            _withStats = await EngineAs(_admin).TerminateSessionAsync(_busy.Session, _deadline, default);
            _presentAfterGrant = await SessionPresentAsync(_busy.Session);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_busy is not null)
            {
                await _busy.DisposeAsync();
            }

            if (_victim is not null)
            {
                await DropRoleAsync(_victim);
            }

            if (_admin is not null)
            {
                await DropRoleAsync(_admin);
            }
        }

        [Test]
        public void It_refuses_rather_than_reporting_the_session_gone() =>
            _withoutStats
                .Should()
                .Be(
                    new DmsSessionTermination.NotTerminated(
                        DmsSessionTerminationFailure.PrivilegeMissing,
                        DmsProvisioningDiagnostic.ForOperation(PostgresqlSessionQueries.IdentifySessionStep)
                    )
                );

        [Test]
        public void It_never_signals_a_session_it_cannot_identify() =>
            (_presentAfterRefusal, _waitEventAfterRefusal).Should().Be((true, SleepWait));

        [Test]
        public void It_terminates_once_the_start_time_is_readable() =>
            (_withStats, _presentAfterGrant).Should().Be((new DmsSessionTermination.Terminated(), false));
    }

    [TestFixture]
    public class Given_a_backend_that_cannot_act_on_the_signal
    {
        private static readonly TimeSpan _shortDeadline = TimeSpan.FromSeconds(2);

        private StoppedSession? _stopped;
        private DmsSessionTermination _verdict = null!;
        private bool _presentAfterVerdict;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            await RequireServerProgramsOrSkipAsync();
            _stopped = await StoppedSession.StartAsync();
            _verdict = await Engine().TerminateSessionAsync(_stopped.Session, _shortDeadline, default);
            _presentAfterVerdict = await SessionPresentAsync(_stopped.Session);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_stopped is not null)
            {
                await _stopped.DisposeAsync();
            }
        }

        [Test]
        public void It_reports_a_timeout_without_a_diagnostic() =>
            _verdict
                .Should()
                .Be(new DmsSessionTermination.NotTerminated(DmsSessionTerminationFailure.TimedOut));

        [Test]
        public void It_leaves_the_session_listed_for_the_operator() => _presentAfterVerdict.Should().BeTrue();
    }

    [TestFixture]
    public class Given_termination_is_cancelled_while_the_server_waits
    {
        // Far beyond the completion bound: a call that only notices the cancellation once the server's
        // wait ends would return a verdict long after the bound, not the prompt cancellation under test.
        private static readonly TimeSpan _longDeadline = TimeSpan.FromMinutes(2);

        private StoppedSession? _stopped;
        private bool _waitObserved;
        private bool _completedWithinBound;
        private Exception? _outcome;
        private bool _presentAfterCancellation;
        private bool _terminatorReleased;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            await RequireServerProgramsOrSkipAsync();
            _stopped = await StoppedSession.StartAsync();
            using CancellationTokenSource cancellation = new();
            Task<DmsSessionTermination> terminating = Engine()
                .TerminateSessionAsync(_stopped.Session, _longDeadline, cancellation.Token);
            _waitObserved = await WaitUntilAsync(async () => await TerminatorWaitingAsync() != 0, _bound);
            await cancellation.CancelAsync();
            _completedWithinBound = await Task.WhenAny(terminating, Task.Delay(_bound)) == terminating;
            _outcome = _completedWithinBound ? await Record.ExceptionAsync(() => terminating) : null;
            _presentAfterCancellation = await SessionPresentAsync(_stopped.Session);
            _terminatorReleased = await WaitUntilAsync(
                async () => await TerminatorWaitingAsync() == 0,
                _bound
            );
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_stopped is not null)
            {
                await _stopped.DisposeAsync();
            }
        }

        [Test]
        public void It_was_waiting_on_the_server() => _waitObserved.Should().BeTrue();

        [Test]
        public void It_returns_promptly_once_cancelled() => _completedWithinBound.Should().BeTrue();

        [Test]
        public void It_propagates_cancellation_instead_of_a_verdict() =>
            _outcome.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_leaves_the_session_listed() => _presentAfterCancellation.Should().BeTrue();

        [Test]
        public void It_releases_the_terminating_session() => _terminatorReleased.Should().BeTrue();
    }

    [TestFixture]
    public class Given_the_holder_session_is_terminated_through_the_engine
    {
        private static readonly TimeSpan _heartbeat = TimeSpan.FromSeconds(1);
        private static readonly ProtectedDatabaseNames _protectedNames = ProtectedDatabaseNames.Create(
            "edfi_configurationservice",
            []
        );

        private IDmsTargetLockHolder? _holder;
        private DmsSessionTermination _verdict = null!;
        private bool _lossObserved;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            DmsTargetDatabaseName name =
                DmsTargetDatabaseName.TryCreate(
                    "dms1438_terminate_holder",
                    _protectedNames,
                    out DmsTargetDatabaseName target
                ) == DmsTargetNameValidation.Valid
                    ? target
                    : throw new InvalidOperationException("The target name does not satisfy the rules.");
            PostgresqlDmsTargetEngine engine = Engine(lockHeartbeatInterval: _heartbeat);
            _holder = await engine.AcquireTargetLockAsync(name, DmsOperationId.New(), default)
                is DmsTargetLockResult.Acquired acquired
                ? acquired.Holder
                : throw new InvalidOperationException("The target lock was not acquired.");
            TaskCompletionSource lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = _holder.LostToken.Register(() =>
                lost.TrySetResult()
            );
            _verdict = await engine.TerminateSessionAsync(_holder.Session, _deadline, default);
            _lossObserved = await Task.WhenAny(lost.Task, Task.Delay(_bound)) == lost.Task;
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
        public void It_proves_the_holder_gone() =>
            _verdict.Should().BeOfType<DmsSessionTermination.Terminated>();

        [Test]
        public void It_declares_the_lock_lost() => _lossObserved.Should().BeTrue();
    }

    [TestFixture]
    public class Given_a_replay_session_addressed_by_application_name
    {
        private static readonly DmsDatabaseName _maintenance = DmsDatabaseName.TryCreate(
            PostgresqlAdminConnection.MaintenanceDatabaseName,
            out DmsDatabaseName name
        )
            ? name
            : throw new InvalidOperationException("The name does not satisfy the grammar.");

        private TestDatabase? _database;
        private NpgsqlConnection? _replay;
        private DmsServerSessionId _own;
        private IReadOnlyList<DmsServerSessionId> _found = [];
        private IReadOnlyList<DmsServerSessionId> _otherOperation = [];
        private IReadOnlyList<DmsServerSessionId> _otherDatabase = [];
        private DmsSessionTermination _verdict = null!;
        private IReadOnlyList<DmsServerSessionId> _foundAfter = [];

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _database = await CreateDatabaseAsync("replay");
            DmsOperationId operation = DmsOperationId.New();
            _replay = await AdminConnection()
                .OpenAsync(_database.Name, operation.RestoreApplicationName, default);
            _own = await OwnSessionAsync(_replay);

            PostgresqlDmsTargetEngine engine = Engine();
            _found = await engine.FindSessionsAsync(
                _database.DatabaseName,
                operation.RestoreApplicationName,
                default
            );
            _otherOperation = await engine.FindSessionsAsync(
                _database.DatabaseName,
                DmsOperationId.New().RestoreApplicationName,
                default
            );
            _otherDatabase = await engine.FindSessionsAsync(
                _maintenance,
                operation.RestoreApplicationName,
                default
            );
            _verdict = await engine.TerminateSessionAsync(_found.Single(), _deadline, default);
            _foundAfter = await engine.FindSessionsAsync(
                _database.DatabaseName,
                operation.RestoreApplicationName,
                default
            );
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_replay is not null)
            {
                await _replay.DisposeAsync();
            }

            if (_database is not null)
            {
                await _database.DisposeAsync();
            }
        }

        [Test]
        public void It_finds_exactly_the_replay_session() => _found.Should().Equal(_own);

        [Test]
        public void It_finds_nothing_under_another_operation() => _otherOperation.Should().BeEmpty();

        [Test]
        public void It_scopes_the_lookup_to_the_database() => _otherDatabase.Should().BeEmpty();

        [Test]
        public void It_terminates_the_located_session() =>
            (_verdict, _foundAfter.Count).Should().Be((new DmsSessionTermination.Terminated(), 0));
    }
}
