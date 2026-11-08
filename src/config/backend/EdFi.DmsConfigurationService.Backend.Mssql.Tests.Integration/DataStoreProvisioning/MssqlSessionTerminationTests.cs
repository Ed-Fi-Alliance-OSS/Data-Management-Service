// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Threading.Channels;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using static EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning.MssqlProvisioningTestServer;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning;

/// <summary>
/// Session termination and its verification against a live server (D-22, Step 4.4): a busy session
/// (<c>WAITFOR DELAY</c> inside a transaction that wrote a row) is killed, proven absent, and its write rolled
/// back; a session that is already gone, or a live <c>session_id</c> whose <c>login_time</c> differs, is never
/// killed; a login without <c>ALTER ANY CONNECTION</c> is refused with <c>6102</c> and the session survives; a
/// login that may kill but not see other sessions (no <c>VIEW SERVER STATE</c>) is refused before any kill
/// rather than told the session is gone; a session rolling back a large transaction is killed and proven gone
/// whatever the server's ordering; cancellation before the request or while its batch runs propagates
/// without a verdict; the holder session goes through the same protocol. The paths that need a killed session
/// still listed (the deadline passing, re-reading until gone, cancellation while verifying) do not rely on how
/// long a rollback runs: the kill is live, and the observation hook (test seam) reports the session listed,
/// with an explicit signal when the protocol acts on it and a fake clock where the deadline must pass. Every
/// other wait is on a server-observed condition or a completion signal under a bound, never a fixed delay.
/// </summary>
[Category(Category)]
public class MssqlSessionTerminationTests
{
    private const string WaitForWait = "WAITFOR";

    /// <summary>The size of the transaction the live rollback fixture kills.</summary>
    private const int RollbackRows = 300_000;

    private const string PayloadTable = """
        CREATE TABLE dbo.Payload (Id int IDENTITY PRIMARY KEY, Filler char(400) NOT NULL DEFAULT 'x');
        CREATE INDEX IX_Payload_Filler ON dbo.Payload (Filler);
        """;

    private static readonly TimeSpan _bound = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan _writeBound = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan _deadline = TimeSpan.FromSeconds(30);

    private static Task<bool> SessionGoneAsync(DmsServerSessionId session) =>
        WaitUntilAsync(async () => !await SessionExistsAsync(session), _writeBound);

    private static Task<int> PayloadRowsAsync(TestDatabase database) =>
        database.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Payload;");

    private static Task GrantAsync(string permission, string login) =>
        ExecuteOnServerAsync($"GRANT {permission} TO {MssqlIdentifiers.Quote(login)};");

    /// <summary>
    /// A session as the lane admin in a test database that writes <c>rows</c> rows inside a
    /// transaction and then runs <c>WAITFOR DELAY</c>, handed over only once the server shows it waiting.
    /// Disposal kills it only while the same session (id and <c>login_time</c>) is still listed.
    /// </summary>
    private sealed class BusySession : IAsyncDisposable
    {
        private const string ApplicationName = "dms1438-busy";

        private readonly SqlConnection _connection;
        private readonly SqlCommand _command;
        private readonly Task _running;

        private BusySession(
            SqlConnection connection,
            SqlCommand command,
            Task running,
            DmsServerSessionId session
        )
        {
            _connection = connection;
            _command = command;
            _running = running;
            Session = session;
        }

        public DmsServerSessionId Session { get; }

        public static async Task<BusySession> StartAsync(TestDatabase database, int rows = 1)
        {
            SqlConnection connection = await AdminConnection()
                .OpenAsync(database.Name, ApplicationName, default);
            try
            {
                DmsServerSessionId session = await OwnSessionAsync(connection);
                SqlCommand command = new(
                    $"BEGIN TRANSACTION; INSERT dbo.Payload (Filler) SELECT TOP ({rows:D}) 'x' FROM sys.all_columns a CROSS JOIN sys.all_columns b; WAITFOR DELAY '00:10:00'; COMMIT;",
                    connection
                )
                {
                    CommandTimeout = 0,
                };
                Task running = command.ExecuteNonQueryAsync();
                if (
                    !await WaitUntilAsync(
                        async () => await WaitTypeOfAsync((int)session.Id) == WaitForWait,
                        _writeBound
                    )
                )
                {
                    throw new InvalidOperationException("The busy session was not observed waiting.");
                }

                return new BusySession(connection, command, running, session);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        /// <summary>What ended the command, once it ends within <see cref="_bound"/>: null for a normal end, the exception otherwise.</summary>
        public async Task<Exception?> OutcomeAsync()
        {
            if (await Task.WhenAny(_running, Task.Delay(_bound)) != _running)
            {
                return new TimeoutException("The busy command did not end within the bound.");
            }

            return await Record.ExceptionAsync(() => _running);
        }

        public async ValueTask DisposeAsync()
        {
            if (await SessionExistsAsync(Session))
            {
                await Record.ExceptionAsync(() => KillAsync((int)Session.Id));
            }

            await OutcomeAsync();
            await SessionGoneAsync(Session);
            await _command.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private static async Task<DmsServerSessionId> OwnSessionAsync(SqlConnection connection)
    {
        await using SqlCommand command = new(
            "SELECT CONVERT(int, @@SPID), s.login_time FROM sys.dm_exec_sessions s WHERE s.session_id = @@SPID;",
            connection
        );
        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("The own-session query returned no row.");
        }

        DateTime loginTime = await reader.GetFieldValueAsync<DateTime>(1);
        return new DmsServerSessionId(
            reader.GetInt32(0),
            new DateTimeOffset(DateTime.SpecifyKind(loginTime, DateTimeKind.Utc), TimeSpan.Zero)
        );
    }

    /// <summary>
    /// A fake clock that signals each timer it creates once the timer is registered, so a test moves time only
    /// while the protocol is waiting on it.
    /// </summary>
    private sealed class SignallingClock : FakeTimeProvider
    {
        private readonly Channel<bool> _created = Channel.CreateUnbounded<bool>();

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        )
        {
            ITimer timer = base.CreateTimer(callback, state, dueTime, period);
            _created.Writer.TryWrite(true);
            return timer;
        }

        /// <summary>Waits for the next timer creation not yet consumed; false when none comes within <paramref name="bound"/>.</summary>
        public async Task<bool> TimerCreatedAsync(TimeSpan bound)
        {
            using CancellationTokenSource timeout = new(bound);
            try
            {
                return await _created.Reader.ReadAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }

    /// <summary>An engine on the lane admin with <paramref name="clock"/> and the observation hook (test seam).</summary>
    private static MssqlDmsTargetEngine EngineWith(
        TimeProvider clock,
        Func<int, MssqlSessionObservation, MssqlSessionObservation> observationHook
    ) =>
        new(
            AdminConnection(),
            new MssqlDmsEngineSettings(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)),
            clock,
            NullLogger.Instance
        )
        {
            ObservationHook = observationHook,
        };

    [TestFixture]
    public class Given_a_busy_session_is_killed
    {
        private TestDatabase? _database;
        private BusySession? _busy;
        private DmsSessionTermination _verdict = null!;
        private bool _presentAfterVerdict;
        private Exception? _commandOutcome;
        private int _rowsAfter;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _database = await CreateDatabaseAsync("terminate", PayloadTable);
            _busy = await BusySession.StartAsync(_database);
            _verdict = await Engine().TerminateSessionAsync(_busy.Session, _deadline, default);
            _presentAfterVerdict = await SessionExistsAsync(_busy.Session);
            _commandOutcome = await _busy.OutcomeAsync();
            _rowsAfter = await PayloadRowsAsync(_database);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_busy is not null)
            {
                await _busy.DisposeAsync();
            }

            if (_database is not null)
            {
                await _database.DisposeAsync();
            }
        }

        [Test]
        public void It_reports_terminated() => _verdict.Should().BeOfType<DmsSessionTermination.Terminated>();

        [Test]
        public void It_verified_absence_before_returning() => _presentAfterVerdict.Should().BeFalse();

        [Test]
        public void It_ended_the_command_with_a_broken_connection() =>
            MssqlErrorClassifier.IsConnectionFailure(_commandOutcome!).Should().BeTrue();

        [Test]
        public void It_rolled_back_the_session_transaction() => _rowsAfter.Should().Be(0);
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
            await using (SqlConnection connection = await AdminConnection().OpenMaintenanceAsync(default))
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
    public class Given_a_live_session_id_whose_login_time_differs
    {
        private TestDatabase? _database;
        private BusySession? _busy;
        private DmsSessionTermination _verdict = null!;
        private bool _presentAfterVerdict;
        private string _waitTypeAfterVerdict = string.Empty;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _database = await CreateDatabaseAsync("reused", PayloadTable);
            _busy = await BusySession.StartAsync(_database);
            DmsServerSessionId reusedId = _busy.Session with
            {
                StartedAt = _busy.Session.StartedAt - TimeSpan.FromSeconds(1),
            };
            _verdict = await Engine().TerminateSessionAsync(reusedId, _deadline, default);
            _presentAfterVerdict = await SessionExistsAsync(_busy.Session);
            _waitTypeAfterVerdict = await WaitTypeOfAsync((int)_busy.Session.Id);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_busy is not null)
            {
                await _busy.DisposeAsync();
            }

            if (_database is not null)
            {
                await _database.DisposeAsync();
            }
        }

        [Test]
        public void It_treats_the_addressed_session_as_gone() =>
            _verdict.Should().BeOfType<DmsSessionTermination.AlreadyAbsent>();

        [Test]
        public void It_never_kills_the_session_now_holding_the_id() =>
            (_presentAfterVerdict, _waitTypeAfterVerdict).Should().Be((true, WaitForWait));
    }

    [TestFixture]
    public class Given_an_admin_without_alter_any_connection
    {
        private string? _login;
        private TestDatabase? _database;
        private BusySession? _busy;
        private DmsSessionTermination _verdict = null!;
        private bool _presentAfterRefusal;
        private string _waitTypeAfterRefusal = string.Empty;
        private DmsSessionTermination _afterGrant = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _login = await CreateLoginAsync("nokill");
            await GrantAsync("VIEW SERVER STATE", _login);
            _database = await CreateDatabaseAsync("nokill", PayloadTable);
            _busy = await BusySession.StartAsync(_database);
            _verdict = await EngineAs(_login).TerminateSessionAsync(_busy.Session, _deadline, default);
            _presentAfterRefusal = await SessionExistsAsync(_busy.Session);
            _waitTypeAfterRefusal = await WaitTypeOfAsync((int)_busy.Session.Id);
            await GrantAsync("ALTER ANY CONNECTION", _login);
            _afterGrant = await EngineAs(_login).TerminateSessionAsync(_busy.Session, _deadline, default);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_busy is not null)
            {
                await _busy.DisposeAsync();
            }

            if (_database is not null)
            {
                await _database.DisposeAsync();
            }

            if (_login is not null)
            {
                await DropLoginAsync(_login);
            }
        }

        [Test]
        public void It_reports_the_privilege_refusal_with_its_number_only() =>
            _verdict
                .Should()
                .Be(
                    new DmsSessionTermination.NotTerminated(
                        DmsSessionTerminationFailure.PrivilegeMissing,
                        new DmsProvisioningDiagnostic(
                            string.Empty,
                            "6102",
                            MssqlSessionQueries.TerminateSessionStep
                        )
                    )
                );

        [Test]
        public void It_leaves_the_session_alive() =>
            (_presentAfterRefusal, _waitTypeAfterRefusal).Should().Be((true, WaitForWait));

        [Test]
        public void It_terminates_once_the_permission_is_granted() =>
            _afterGrant.Should().BeOfType<DmsSessionTermination.Terminated>();
    }

    [TestFixture]
    public class Given_an_admin_that_may_kill_but_not_see_other_sessions
    {
        private string? _login;
        private TestDatabase? _database;
        private BusySession? _busy;
        private DmsSessionTermination _verdict = null!;
        private bool _presentAfterRefusal;
        private string _waitTypeAfterRefusal = string.Empty;
        private DmsSessionTermination _afterGrant = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _login = await CreateLoginAsync("blind");
            await GrantAsync("ALTER ANY CONNECTION", _login);
            _database = await CreateDatabaseAsync("blind", PayloadTable);
            _busy = await BusySession.StartAsync(_database);
            _verdict = await EngineAs(_login).TerminateSessionAsync(_busy.Session, _deadline, default);
            _presentAfterRefusal = await SessionExistsAsync(_busy.Session);
            _waitTypeAfterRefusal = await WaitTypeOfAsync((int)_busy.Session.Id);
            await GrantAsync("VIEW SERVER STATE", _login);
            _afterGrant = await EngineAs(_login).TerminateSessionAsync(_busy.Session, _deadline, default);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_busy is not null)
            {
                await _busy.DisposeAsync();
            }

            if (_database is not null)
            {
                await _database.DisposeAsync();
            }

            if (_login is not null)
            {
                await DropLoginAsync(_login);
            }
        }

        [Test]
        public void It_refuses_rather_than_reporting_the_session_gone() =>
            _verdict
                .Should()
                .Be(
                    new DmsSessionTermination.NotTerminated(
                        DmsSessionTerminationFailure.PrivilegeMissing,
                        DmsProvisioningDiagnostic.ForOperation(MssqlSessionQueries.IdentifySessionStep)
                    )
                );

        [Test]
        public void It_never_kills_a_session_it_cannot_identify() =>
            (_presentAfterRefusal, _waitTypeAfterRefusal).Should().Be((true, WaitForWait));

        [Test]
        public void It_terminates_once_sessions_are_visible() =>
            _afterGrant.Should().BeOfType<DmsSessionTermination.Terminated>();
    }

    /// <summary>
    /// The kill is live and real; the observation hook then reports the session still listed at every
    /// observation, as it is while a long rollback runs, and the fake clock passes the deadline once the
    /// protocol has acted on the batch's own observation. No timing of the server decides the path.
    /// </summary>
    [TestFixture]
    public class Given_the_deadline_passes_while_a_killed_session_is_listed
    {
        // Three verification intervals of fake time: the protocol re-reads the catalog while the session is
        // reported listed before the deadline passes.
        private static readonly TimeSpan _shortDeadline = MssqlSessionQueries.VerificationInterval * 3;

        private readonly SignallingClock _clock = new();
        private readonly TaskCompletionSource _listedAfterKill = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private TestDatabase? _database;
        private BusySession? _busy;
        private int _lastObservation;
        private bool _sawListedAfterKill;
        private bool _completedWithinBound;
        private DmsSessionTermination? _verdict;
        private bool _goneOnTheServer;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _database = await CreateDatabaseAsync("deadline", PayloadTable);
            _busy = await BusySession.StartAsync(_database);
            Task<DmsSessionTermination> terminating = EngineWith(
                    _clock,
                    (observation, _) =>
                    {
                        _lastObservation = observation;
                        _listedAfterKill.TrySetResult();
                        return MssqlSessionObservation.Present;
                    }
                )
                .TerminateSessionAsync(_busy.Session, _shortDeadline, CancellationToken.None);
            _sawListedAfterKill =
                await Task.WhenAny(_listedAfterKill.Task, terminating, Task.Delay(_bound))
                == _listedAfterKill.Task;

            // Time moves one interval only after the protocol has registered a timer, so every wait it starts
            // is released by the clock and never by the real time the test takes.
            while (true)
            {
                Task<bool> created = _clock.TimerCreatedAsync(_bound);
                if (await Task.WhenAny(created, terminating) != created || !await created)
                {
                    break;
                }

                _clock.Advance(MssqlSessionQueries.VerificationInterval);
            }

            _completedWithinBound = await Task.WhenAny(terminating, Task.Delay(_bound)) == terminating;
            _verdict = _completedWithinBound ? await terminating : null;
            _goneOnTheServer = await SessionGoneAsync(_busy.Session);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_busy is not null)
            {
                await _busy.DisposeAsync();
            }

            if (_database is not null)
            {
                await _database.DisposeAsync();
            }
        }

        [Test]
        public void It_acted_on_the_session_listed_after_the_kill() => _sawListedAfterKill.Should().BeTrue();

        [Test]
        public void It_read_the_catalog_again_while_the_session_was_listed() =>
            _lastObservation.Should().BeGreaterThan(0);

        [Test]
        public void It_returns_once_the_deadline_passes() => _completedWithinBound.Should().BeTrue();

        [Test]
        public void It_reports_a_timeout_without_a_diagnostic() =>
            _verdict
                .Should()
                .Be(new DmsSessionTermination.NotTerminated(DmsSessionTerminationFailure.TimedOut));

        [Test]
        public void It_had_issued_the_kill() => _goneOnTheServer.Should().BeTrue();
    }

    /// <summary>
    /// The hook reports the session listed only in the batch's own observation, so the protocol must wait and
    /// read the catalog again; every later observation is the server's.
    /// </summary>
    [TestFixture]
    public class Given_a_killed_session_is_observed_until_gone
    {
        private readonly List<MssqlSessionObservation> _serverObservations = [];
        private TestDatabase? _database;
        private BusySession? _busy;
        private DmsSessionTermination _verdict = null!;
        private int _lastObservation;
        private bool _presentAfterVerdict;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _database = await CreateDatabaseAsync("observed", PayloadTable);
            _busy = await BusySession.StartAsync(_database);
            _verdict = await EngineWith(
                    TimeProvider.System,
                    (observation, observed) =>
                    {
                        _lastObservation = observation;
                        _serverObservations.Add(observed);
                        return observation == 0 ? MssqlSessionObservation.Present : observed;
                    }
                )
                .TerminateSessionAsync(_busy.Session, _deadline, CancellationToken.None);
            _presentAfterVerdict = await SessionExistsAsync(_busy.Session);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_busy is not null)
            {
                await _busy.DisposeAsync();
            }

            if (_database is not null)
            {
                await _database.DisposeAsync();
            }
        }

        [Test]
        public void It_reports_terminated() => _verdict.Should().BeOfType<DmsSessionTermination.Terminated>();

        [Test]
        public void It_read_the_catalog_again_after_the_kill() => _lastObservation.Should().BeGreaterThan(0);

        [Test]
        public void It_ended_on_the_server_reporting_the_session_gone() =>
            _serverObservations[^1]
                .Should()
                .BeOneOf(MssqlSessionObservation.Absent, MssqlSessionObservation.SessionIdReused);

        [Test]
        public void It_returned_only_once_the_session_was_gone() => _presentAfterVerdict.Should().BeFalse();
    }

    /// <summary>
    /// The hook reports the session listed after the kill, and the fake clock never moves, so the protocol
    /// is waiting to read the catalog again until the caller cancels.
    /// </summary>
    [TestFixture]
    public class Given_termination_is_cancelled_while_it_verifies
    {
        private readonly FakeTimeProvider _clock = new();
        private readonly TaskCompletionSource _listedAfterKill = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private TestDatabase? _database;
        private BusySession? _busy;
        private bool _verifying;
        private bool _completedWithinBound;
        private Exception? _outcome;
        private bool _goneOnTheServer;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _database = await CreateDatabaseAsync("cancelverify", PayloadTable);
            _busy = await BusySession.StartAsync(_database);
            using CancellationTokenSource cancellation = new();
            Task<DmsSessionTermination> terminating = EngineWith(
                    _clock,
                    (_, _) =>
                    {
                        _listedAfterKill.TrySetResult();
                        return MssqlSessionObservation.Present;
                    }
                )
                .TerminateSessionAsync(_busy.Session, _deadline, cancellation.Token);
            _verifying =
                await Task.WhenAny(_listedAfterKill.Task, terminating, Task.Delay(_bound))
                == _listedAfterKill.Task;
            await cancellation.CancelAsync();
            _completedWithinBound = await Task.WhenAny(terminating, Task.Delay(_bound)) == terminating;
            _outcome = _completedWithinBound ? await Record.ExceptionAsync(() => terminating) : null;
            _goneOnTheServer = await SessionGoneAsync(_busy.Session);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_busy is not null)
            {
                await _busy.DisposeAsync();
            }

            if (_database is not null)
            {
                await _database.DisposeAsync();
            }
        }

        [Test]
        public void It_was_verifying_when_cancelled() => _verifying.Should().BeTrue();

        [Test]
        public void It_returns_promptly_once_cancelled() => _completedWithinBound.Should().BeTrue();

        [Test]
        public void It_propagates_cancellation_instead_of_a_verdict() =>
            _outcome.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_had_issued_the_kill() => _goneOnTheServer.Should().BeTrue();
    }

    /// <summary>
    /// The live kill of a session rolling back a large transaction, with nothing controlled: whichever way the
    /// server orders the rollback and the batch's last statement, the verdict is proven absence and the write
    /// is undone.
    /// </summary>
    [TestFixture]
    public class Given_a_session_rolling_back_a_large_transaction_is_killed
    {
        private TestDatabase? _database;
        private BusySession? _busy;
        private DmsSessionTermination _verdict = null!;
        private bool _presentAfterVerdict;
        private int _rowsAfter;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _database = await CreateDatabaseAsync("rollback", PayloadTable);
            _busy = await BusySession.StartAsync(_database, RollbackRows);
            _verdict = await Engine().TerminateSessionAsync(_busy.Session, _deadline, CancellationToken.None);
            _presentAfterVerdict = await SessionExistsAsync(_busy.Session);
            _rowsAfter = await PayloadRowsAsync(_database);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_busy is not null)
            {
                await _busy.DisposeAsync();
            }

            if (_database is not null)
            {
                await _database.DisposeAsync();
            }
        }

        [Test]
        public void It_reports_terminated() => _verdict.Should().BeOfType<DmsSessionTermination.Terminated>();

        [Test]
        public void It_returned_only_once_the_session_was_gone() =>
            (_presentAfterVerdict, _rowsAfter).Should().Be((false, 0));
    }

    [TestFixture]
    public class Given_a_termination_cancelled_before_it_starts
    {
        private TestDatabase? _database;
        private BusySession? _busy;
        private Exception? _outcome;
        private bool _presentAfter;
        private string _waitTypeAfter = string.Empty;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _database = await CreateDatabaseAsync("cancelfirst", PayloadTable);
            _busy = await BusySession.StartAsync(_database);
            using CancellationTokenSource cancellation = new();
            await cancellation.CancelAsync();
            _outcome = await Record.ExceptionAsync(() =>
                Engine().TerminateSessionAsync(_busy.Session, _deadline, cancellation.Token)
            );
            _presentAfter = await SessionExistsAsync(_busy.Session);
            _waitTypeAfter = await WaitTypeOfAsync((int)_busy.Session.Id);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_busy is not null)
            {
                await _busy.DisposeAsync();
            }

            if (_database is not null)
            {
                await _database.DisposeAsync();
            }
        }

        [Test]
        public void It_propagates_cancellation() =>
            _outcome.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_sends_no_kill() => (_presentAfter, _waitTypeAfter).Should().Be((true, WaitForWait));
    }

    /// <summary>
    /// Cancellation while the kill batch is on the server: the batch is replaced (test seam) by a marked
    /// <c>WAITFOR</c> ahead of the real guarded kill, and the caller cancels once the server shows it running.
    /// SqlClient reports that cancellation as a <see cref="SqlException"/>, which must surface as
    /// <see cref="OperationCanceledException"/>; the aborted batch never reached its <c>KILL</c>.
    /// </summary>
    [TestFixture]
    public class Given_termination_is_cancelled_while_its_batch_runs
    {
        private const string RunningRequestQuery =
            "SELECT COUNT(*) FROM sys.dm_exec_requests r CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t WHERE r.session_id <> @@SPID AND t.text LIKE @pattern;";

        private TestDatabase? _database;
        private BusySession? _busy;
        private bool _dispatched;
        private bool _completedWithinBound;
        private Exception? _outcome;
        private bool _presentAfter;
        private string _waitTypeAfter = string.Empty;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _database = await CreateDatabaseAsync("cancelbatch", PayloadTable);
            _busy = await BusySession.StartAsync(_database);
            string marker = UniqueName("kill_cancel");
            MssqlDmsTargetEngine engine = new(
                AdminConnection(),
                new MssqlDmsEngineSettings(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)),
                TimeProvider.System,
                NullLogger.Instance
            )
            {
                GuardedKillStatement = id =>
                    $"WAITFOR DELAY '00:02:00'; -- {marker}\n" + MssqlSessionQueries.GuardedKill(id),
            };
            using CancellationTokenSource cancellation = new();
            Task<DmsSessionTermination> terminating = engine.TerminateSessionAsync(
                _busy.Session,
                _deadline,
                cancellation.Token
            );
            _dispatched = await WaitUntilAsync(
                async () =>
                    await ScalarOnServerAsync<int>(RunningRequestQuery, ("@pattern", "%" + marker + "%"))
                    == 1,
                _bound
            );
            await cancellation.CancelAsync();
            _completedWithinBound = await Task.WhenAny(terminating, Task.Delay(_bound)) == terminating;
            _outcome = _completedWithinBound ? await Record.ExceptionAsync(() => terminating) : null;
            _presentAfter = await SessionExistsAsync(_busy.Session);
            _waitTypeAfter = await WaitTypeOfAsync((int)_busy.Session.Id);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_busy is not null)
            {
                await _busy.DisposeAsync();
            }

            if (_database is not null)
            {
                await _database.DisposeAsync();
            }
        }

        [Test]
        public void It_was_running_on_the_server() => _dispatched.Should().BeTrue();

        [Test]
        public void It_returns_promptly_once_cancelled() => _completedWithinBound.Should().BeTrue();

        [Test]
        public void It_propagates_cancellation_instead_of_the_driver_error() =>
            _outcome.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_sends_no_kill() => (_presentAfter, _waitTypeAfter).Should().Be((true, WaitForWait));
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
                    UniqueName("holder"),
                    _protectedNames,
                    out DmsTargetDatabaseName target
                ) == DmsTargetNameValidation.Valid
                    ? target
                    : throw new InvalidOperationException("The target name does not satisfy the rules.");
            MssqlDmsTargetEngine engine = Engine(lockHeartbeatInterval: _heartbeat);
            _holder = await engine.AcquireTargetLockAsync(name, DmsOperationId.New(), default)
                is DmsTargetLockResult.Acquired acquired
                ? acquired.Holder
                : throw new InvalidOperationException("The target lock was not acquired.");
            TaskCompletionSource lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await using CancellationTokenRegistration registration = _holder.LostToken.Register(() =>
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
}
