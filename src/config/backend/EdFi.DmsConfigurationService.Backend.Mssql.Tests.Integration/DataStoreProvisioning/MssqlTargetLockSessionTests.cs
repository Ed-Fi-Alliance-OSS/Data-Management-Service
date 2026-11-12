// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using static EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning.MssqlProvisioningTestServer;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning;

/// <summary>
/// The target lock holder session against a live server (D-12, Step 4.2): the dedicated holder session in
/// <c>master</c> and its session-owned application lock, deterministic contention and timeout, release, loss
/// on <c>KILL</c> without any reopen, loss when the lock is gone while the session lives, a probe that
/// survives a changed database context, the execution gate under a command that blocks on the server while
/// the heartbeat becomes due, and the operator's lookup of a holder by its lock resource (7.6). Lock
/// ownership is read exactly, with <c>APPLOCK_MODE</c> on the owning session and <c>APPLOCK_TEST</c> from
/// another, because <c>sys.dm_tran_locks</c> shows only the first 32 characters of a resource, which every
/// test resource shares. Every wait is on a server-observed condition or a completion signal under a generous
/// bound, never a fixed delay; the gate fixtures drive the heartbeat and the command deadline from a fake clock.
/// </summary>
[Category(Category)]
public class MssqlTargetLockSessionTests
{
    private const string ExclusiveLockWait = "LCK_M_X";

    private static readonly TimeSpan _bound = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan _commandDeadline = TimeSpan.FromMinutes(1);
    private static readonly ProtectedDatabaseNames _protectedNames = ProtectedDatabaseNames.Create(
        "edfi_configurationservice",
        []
    );

    private static DmsTargetDatabaseName TargetName(string purpose) => ValidName(UniqueName(purpose));

    private static DmsTargetDatabaseName ValidName(string candidate) =>
        DmsTargetDatabaseName.TryCreate(candidate, _protectedNames, out DmsTargetDatabaseName name)
        == DmsTargetNameValidation.Valid
            ? name
            : throw new InvalidOperationException("The target name does not satisfy the rules.");

    private static async Task<MssqlTargetLockSession> AcquireAsync(
        MssqlDmsTargetEngine engine,
        DmsTargetDatabaseName name,
        DmsOperationId operation
    ) =>
        await engine.AcquireTargetLockAsync(name, operation, default) is DmsTargetLockResult.Acquired acquired
            ? (MssqlTargetLockSession)acquired.Holder
            : throw new InvalidOperationException("The target lock was not acquired.");

    /// <summary>A gated <c>SELECT 1</c> on the holder's own connection.</summary>
    private static Task<int> SelectOneAsync(MssqlTargetLockSession holder) =>
        ScalarOnHolderAsync<int>(holder, "SELECT 1;");

    private static Task<T> ScalarOnHolderAsync<T>(MssqlTargetLockSession holder, string sql) =>
        holder.RunAsync(
            async token =>
            {
                await using SqlCommand command = new(sql, holder.Connection);
                return (T)(await command.ExecuteScalarAsync(token))!;
            },
            _commandDeadline,
            CancellationToken.None
        );

    /// <summary>The mode in which the holder's session owns <paramref name="resource"/> in <c>master</c>, read on its own connection through the gate.</summary>
    private static Task<string> ModeOnHolderAsync(MssqlTargetLockSession holder, string resource) =>
        holder.RunAsync(
            token => ModeOnConnectionAsync(holder.Connection, resource, token),
            _commandDeadline,
            CancellationToken.None
        );

    private static async Task<string> ModeOnConnectionAsync(
        SqlConnection connection,
        string resource,
        CancellationToken cancellationToken
    )
    {
        await using SqlCommand command = new(
            "EXEC master.sys.sp_executesql N'SELECT APPLOCK_MODE(N''public'', @r, N''Session'');', N'@r nvarchar(255)', @r = @resource;",
            connection
        );
        command.Parameters.AddWithValue("@resource", resource);
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>Whether a fresh session could take <paramref name="resource"/> exclusively in <c>master</c> now, i.e. no session holds it.</summary>
    private static async Task<bool> GrantableAsync(string resource) =>
        await ScalarOnServerAsync<int>(
            "SELECT APPLOCK_TEST(N'public', @resource, N'Exclusive', N'Session');",
            ("@resource", resource)
        ) == 1;

    /// <summary>
    /// A gated <c>sp_getapplock</c> on <paramref name="resource"/> that waits without a server limit while
    /// another session holds it. No driver timeout, so only the gate's <paramref name="deadline"/> bounds it.
    /// Returns the procedure's status.
    /// </summary>
    private static Task<int> LockOnHolderAsync(
        MssqlTargetLockSession holder,
        string resource,
        TimeSpan deadline
    ) =>
        holder.RunAsync(
            token => GetAppLockAsync(holder.Connection, resource, lockTimeoutMilliseconds: -1, token),
            deadline,
            CancellationToken.None
        );

    private static async Task<int> GetAppLockAsync(
        SqlConnection connection,
        string resource,
        int lockTimeoutMilliseconds,
        CancellationToken cancellationToken
    )
    {
        await using SqlCommand command = new("master.sys.sp_getapplock", connection);
        command.CommandType = CommandType.StoredProcedure;
        command.CommandTimeout = 0;
        command.Parameters.AddWithValue("@Resource", resource);
        command.Parameters.AddWithValue("@LockMode", "Exclusive");
        command.Parameters.AddWithValue("@LockOwner", "Session");
        command.Parameters.AddWithValue("@LockTimeout", lockTimeoutMilliseconds);
        SqlParameter status = command.Parameters.Add(
            new SqlParameter("@RETURN_VALUE", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue }
        );
        await command.ExecuteNonQueryAsync(cancellationToken);
        return (int)status.Value;
    }

    private static async Task ReleaseAppLockAsync(
        SqlConnection connection,
        string resource,
        CancellationToken cancellationToken
    )
    {
        await using SqlCommand command = new("master.sys.sp_releaseapplock", connection);
        command.CommandType = CommandType.StoredProcedure;
        command.Parameters.AddWithValue("@Resource", resource);
        command.Parameters.AddWithValue("@LockOwner", "Session");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Task<bool> WaitingOnExclusiveLockAsync(int sessionId) =>
        WaitUntilAsync(async () => await WaitTypeOfAsync(sessionId) == ExclusiveLockWait, _bound);

    private static Task<bool> ContenderWaitingAsync(DmsOperationId contender) =>
        WaitUntilAsync(
            async () =>
            {
                int sessionId = await SessionIdNamedAsync(contender.HolderApplicationName);
                return sessionId != 0 && await WaitTypeOfAsync(sessionId) == ExclusiveLockWait;
            },
            _bound
        );

    /// <summary>A second ordinary session holding the application lock <paramref name="resource"/>; released explicitly or by disposal.</summary>
    private sealed class Blocker(SqlConnection connection, string resource) : IAsyncDisposable
    {
        public static async Task<Blocker> HoldAsync(string resource)
        {
            SqlConnection connection = await AdminConnection()
                .OpenAsync("master", "dms1438-blocker", default);
            try
            {
                int status = await GetAppLockAsync(connection, resource, lockTimeoutMilliseconds: 0, default);
                return status == 0
                    ? new Blocker(connection, resource)
                    : throw new InvalidOperationException("The blocker could not take its lock.");
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        public Task<string> ModeAsync() => ModeOnConnectionAsync(connection, resource, default);

        public Task ReleaseAsync() => ReleaseAppLockAsync(connection, resource, default);

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    /// <summary>
    /// F-02 against a real server: a login that took a lock is then refused, first with a wrong password
    /// (<c>18456</c>), then disabled (<c>18470</c>). For each refusal the acquisition is <c>AccessDenied</c> with the
    /// error number only, and create and delete through the orchestrator are <c>AdminPrivilegeMissing</c>, permanent,
    /// at the lock step with that number, with one <c>OperationFailed</c> warning each and nothing else read; neither
    /// a diagnostic nor a log line carries a password or a connection string. A port nobody listens on stays a lock
    /// infrastructure <c>Failure</c>, and a caller's cancellation still wins over a refusal raised as it cancels.
    /// </summary>
    [TestFixture]
    public class Given_the_admin_login_is_refused_after_it_was_accepted
    {
        private const string WrongPassword = "Dms1438-Wrong-Pa55word!";

        private readonly CapturingLogger _logs = new();
        private string? _login;
        private Lane? _lane;
        private DmsTargetLockResult _accepted = null!;
        private DmsTargetLockResult _wrongPassword = null!;
        private DmsTargetLockResult _disabled = null!;
        private DmsTargetLockResult _unreachable = null!;
        private Operations _wrongPasswordOperations = null!;
        private Operations _disabledOperations = null!;
        private SqlException? _realRefusal;
        private Exception? _refusedWhileCancelled;
        private DmsTargetLockResult? _refusedWhileLive;

        /// <summary>A create and a delete run through the orchestrator on one engine, with what they recorded and logged.</summary>
        private sealed record Operations(
            string ErrorNumber,
            DmsDataStoreCreateResult Create,
            DmsDataStoreDeleteResult Delete,
            string[] Members,
            CapturingLogger ServiceLogs
        );

        private async Task<Operations> CreateAndDeleteAsync(MssqlDmsTargetEngine engine, string errorNumber)
        {
            CapturingLogger serviceLogs = new();
            Lane.Deployment refusing = _lane!.WithEngine(engine, serviceLogs);
            DmsDataStoreCreateResult create = await refusing.Service.CreateAsync(_lane.Request, default);
            DmsDataStoreDeleteResult delete = await refusing.Service.DeleteAsync(
                _lane.DeleteRequest,
                default
            );
            return new Operations(
                errorNumber,
                create,
                delete,
                [.. refusing.Engine.Calls.Select(call => call.Member)],
                serviceLogs
            );
        }

        private MssqlDmsTargetEngine EngineOn(string connectionString) =>
            new(
                new MssqlAdminConnection(connectionString, TimeSpan.FromSeconds(30)),
                Settings(),
                TimeProvider.System,
                _logs
            );

        private static string ConnectionStringFor(string login, string password) =>
            new SqlConnectionStringBuilder(AdminConnectionString!)
            {
                IntegratedSecurity = false,
                UserID = login,
                Password = password,
            }.ConnectionString;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _login = await CreateLoginAsync("lockrefused");
            _lane = await Lane.BuildAsync(await ShapedBackup.CreateAsync(), new LaneHooks());
            MssqlDmsTargetEngine engine = EngineOn(ConnectionStringFor(_login, LoginPassword));

            _accepted = await engine.AcquireTargetLockAsync(
                TargetName("accepted"),
                DmsOperationId.New(),
                default
            );
            if (_accepted is DmsTargetLockResult.Acquired acquired)
            {
                await acquired.Holder.DisposeAsync();
            }

            MssqlDmsTargetEngine wrongPassword = EngineOn(ConnectionStringFor(_login, WrongPassword));
            _wrongPassword = await wrongPassword.AcquireTargetLockAsync(
                TargetName("wrongpassword"),
                DmsOperationId.New(),
                default
            );
            _wrongPasswordOperations = await CreateAndDeleteAsync(wrongPassword, "18456");

            await ExecuteOnServerAsync($"ALTER LOGIN {MssqlIdentifiers.Quote(_login)} DISABLE;");
            _disabled = await engine.AcquireTargetLockAsync(
                TargetName("disabled"),
                DmsOperationId.New(),
                default
            );
            _disabledOperations = await CreateAndDeleteAsync(engine, "18470");

            // The refusal a real server raised, handed to the classification with the caller's token already
            // cancelled, as when the caller cancels while the server's refusal is on its way.
            try
            {
                await using SqlConnection refused = new(ConnectionStringFor(_login, LoginPassword));
                await refused.OpenAsync();
            }
            catch (SqlException exception)
            {
                _realRefusal = exception;
            }

            using CancellationTokenSource cancelled = new();
            await cancelled.CancelAsync();
            _refusedWhileCancelled = await Record.ExceptionAsync(() =>
                Task.FromResult(MssqlTargetLockSession.FailureOrCancellation(_realRefusal!, cancelled.Token))
            );
            _refusedWhileLive = MssqlTargetLockSession.FailureOrCancellation(
                _realRefusal!,
                CancellationToken.None
            );

            using TcpListener vacated = new(IPAddress.Loopback, 0);
            vacated.Start();
            int port = ((IPEndPoint)vacated.LocalEndpoint).Port;
            vacated.Stop();
            _unreachable = await EngineOn(
                    new SqlConnectionStringBuilder(AdminConnectionString!)
                    {
                        DataSource = $"127.0.0.1,{port}",
                        ConnectTimeout = 5,
                    }.ConnectionString
                )
                .AcquireTargetLockAsync(TargetName("unreachable"), DmsOperationId.New(), default);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_lane is not null)
            {
                await _lane.DisposeAsync();
            }

            if (_login is not null)
            {
                await DropLoginAsync(_login);
            }
        }

        [Test]
        public void It_acquired_the_lock_while_the_login_was_accepted() =>
            _accepted.Should().BeOfType<DmsTargetLockResult.Acquired>();

        [Test]
        public void It_reports_a_wrong_password_as_access_denied_with_the_error_number_only() =>
            _wrongPassword.Should().Be(new DmsTargetLockResult.AccessDenied(Refusal("18456")));

        [Test]
        public void It_reports_a_disabled_login_as_access_denied_with_the_error_number_only() =>
            _disabled.Should().Be(new DmsTargetLockResult.AccessDenied(Refusal("18470")));

        private Operations[] BothRefusals => [_wrongPasswordOperations, _disabledOperations];

        [Test]
        public void It_reports_each_refused_create_and_delete_as_a_permanent_missing_privilege_with_its_number()
        {
            foreach (Operations refused in BothRefusals)
            {
                foreach (
                    DmsProvisioningFailure failure in new[]
                    {
                        Failure(refused.Create),
                        Failure(refused.Delete),
                    }
                )
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
                                refused.ErrorNumber
                            )
                        );
                }
            }
        }

        [Test]
        public void It_reads_nothing_after_the_refused_acquisitions() =>
            BothRefusals
                .Should()
                .AllSatisfy(refused =>
                    refused
                        .Members.Should()
                        .Equal(
                            nameof(IDmsTargetEngine.AcquireTargetLockAsync),
                            nameof(IDmsTargetEngine.AcquireTargetLockAsync)
                        )
                );

        [Test]
        public void It_logs_one_failure_warning_for_each_refused_operation_with_the_code_and_step_only()
        {
            string ending =
                $" failed at {DmsDataStoreProvisioningService.AcquireTargetLockStep} with AdminPrivilegeMissing (Permanent)";
            foreach (Operations refused in BothRefusals)
            {
                CapturingLogger.Entry[] warnings =
                [
                    .. refused.ServiceLogs.Entries.Where(entry =>
                        entry.Id == DmsDataStoreProvisioningService.OperationFailed
                    ),
                ];
                warnings.Select(entry => entry.Level).Should().Equal(LogLevel.Warning, LogLevel.Warning);
                warnings
                    .Select(entry => entry.Text)
                    .Should()
                    .SatisfyRespectively(
                        create =>
                            create
                                .Should()
                                .StartWith(
                                    $"DMS data store provisioning {DmsDataStoreProvisioningService.CreateOperationName} "
                                )
                                .And.EndWith(ending),
                        delete =>
                            delete
                                .Should()
                                .StartWith(
                                    $"DMS data store provisioning {DmsDataStoreProvisioningService.DeleteOperationName} "
                                )
                                .And.EndWith(ending)
                    );
            }
        }

        [Test]
        public void It_exposes_neither_a_password_nor_a_connection_string()
        {
            DmsProvisioningDiagnostic[] diagnostics =
            [
                ((DmsTargetLockResult.AccessDenied)_wrongPassword).Diagnostic,
                ((DmsTargetLockResult.AccessDenied)_disabled).Diagnostic,
                .. BothRefusals.SelectMany(refused =>
                    new[] { Failure(refused.Create).Diagnostic, Failure(refused.Delete).Diagnostic }
                ),
            ];
            diagnostics
                .SelectMany(diagnostic =>
                    new[]
                    {
                        diagnostic.ExceptionTypeChain,
                        diagnostic.ProviderErrorCode ?? string.Empty,
                        diagnostic.Operation,
                    }
                )
                .Concat(
                    _logs
                        .Entries.Concat(BothRefusals.SelectMany(refused => refused.ServiceLogs.Entries))
                        .Select(entry => entry.Text)
                )
                .Should()
                .AllSatisfy(text =>
                    text.Should()
                        .NotContain(LoginPassword)
                        .And.NotContain(WrongPassword)
                        .And.NotContain("Password=", "a connection string must not be quoted")
                );
        }

        [Test]
        public void It_keeps_an_unreachable_server_a_lock_infrastructure_failure() =>
            _unreachable.Should().BeOfType<DmsTargetLockResult.Failure>();

        [Test]
        public void It_lets_the_callers_cancellation_win_over_a_refusal_raised_as_it_cancels()
        {
            _realRefusal!.Number.Should().Be(MssqlErrorClassifier.LoginAccountDisabled);
            _refusedWhileCancelled
                .Should()
                .BeOfType<OperationCanceledException>()
                .Which.InnerException.Should()
                .BeSameAs(_realRefusal);
            _refusedWhileLive.Should().BeOfType<DmsTargetLockResult.AccessDenied>();
        }

        private static DmsProvisioningDiagnostic Refusal(string number) =>
            new(typeof(SqlException).FullName!, number, MssqlTargetLockSession.AcquireStep);

        private static DmsProvisioningFailure Failure(DmsDataStoreCreateResult result) =>
            result.Should().BeOfType<DmsDataStoreCreateResult.Failure>().Subject.Detail;

        private static DmsProvisioningFailure Failure(DmsDataStoreDeleteResult result) =>
            result.Should().BeOfType<DmsDataStoreDeleteResult.Failure>().Subject.Detail;

        /// <summary>Every log line written to it (the engine and its lock sessions, or the orchestrator), formatted, with any exception's full text.</summary>
        private sealed class CapturingLogger : ILogger<DmsDataStoreProvisioningService>
        {
            private readonly System.Collections.Concurrent.ConcurrentQueue<Entry> _entries = new();

            public sealed record Entry(EventId Id, LogLevel Level, string Text);

            public IEnumerable<Entry> Entries => _entries;

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            ) =>
                _entries.Enqueue(
                    new Entry(
                        eventId,
                        logLevel,
                        formatter(state, exception) + (exception?.ToString() ?? string.Empty)
                    )
                );
        }
    }

    [TestFixture]
    public class Given_a_lock_is_acquired_and_released
    {
        private DmsTargetDatabaseName _name;
        private DmsOperationId _operation;
        private MssqlTargetLockSession _holder = null!;
        private string _applicationName = string.Empty;
        private string _databaseName = string.Empty;
        private DateTime _loginTime;
        private int _sessionOwnedLocksInMaster;
        private string _modeWhileHeld = string.Empty;
        private bool _grantableWhileHeld;
        private bool _lostWhileHeld;
        private int _gatedResult;
        private int _lockTimeoutAfterAcquire;
        private bool _grantableAfterRelease;
        private bool _sessionGoneAfterRelease;
        private Exception? _secondDisposal;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _name = TargetName("lock");
            _operation = DmsOperationId.New();
            _holder = await AcquireAsync(Engine(), _name, _operation);
            int sessionId = (int)_holder.Session.Id;
            _applicationName = await ScalarOnServerAsync<string>(
                "SELECT program_name FROM sys.dm_exec_sessions WHERE session_id = @id;",
                ("@id", sessionId)
            );
            _databaseName = await ScalarOnServerAsync<string>(
                "SELECT DB_NAME(database_id) FROM sys.dm_exec_sessions WHERE session_id = @id;",
                ("@id", sessionId)
            );
            _loginTime = await ScalarOnServerAsync<DateTime>(
                "SELECT login_time FROM sys.dm_exec_sessions WHERE session_id = @id;",
                ("@id", sessionId)
            );
            _sessionOwnedLocksInMaster = await ScalarOnServerAsync<int>(
                "SELECT COUNT(*) FROM sys.dm_tran_locks WHERE request_session_id = @id AND resource_type = N'APPLICATION' AND resource_database_id = 1 AND request_owner_type = N'SESSION' AND request_mode = N'X' AND request_status = N'GRANT';",
                ("@id", sessionId)
            );
            _modeWhileHeld = await ModeOnHolderAsync(_holder, _holder.Resource);
            _grantableWhileHeld = await GrantableAsync(_holder.Resource);
            _lostWhileHeld = _holder.IsLost;
            _gatedResult = await SelectOneAsync(_holder);
            _lockTimeoutAfterAcquire = await ScalarOnHolderAsync<int>(_holder, "SELECT @@LOCK_TIMEOUT;");

            await _holder.DisposeAsync();
            _grantableAfterRelease = await GrantableAsync(_holder.Resource);
            _sessionGoneAfterRelease = await WaitUntilAsync(
                async () => !await SessionExistsAsync(_holder.Session),
                _bound
            );
            _secondDisposal = await Record.ExceptionAsync(() => _holder.DisposeAsync().AsTask());
        }

        [Test]
        public void It_names_the_holder_session_by_the_operation() =>
            _applicationName.Should().Be(_operation.HolderApplicationName).And.HaveLength(20);

        [Test]
        public void It_binds_the_holder_to_the_maintenance_database() =>
            _databaseName.Should().Be(MssqlAdminConnection.MaintenanceDatabaseName);

        [Test]
        public void It_captures_the_login_time() =>
            _holder.Session.StartedAt.Should().Be(new DateTimeOffset(_loginTime, TimeSpan.Zero));

        [Test]
        public void It_derives_the_resource_from_the_lowercased_name() =>
            _holder
                .Resource.Should()
                .Be(MssqlTargetLockSession.LockResourcePrefix + _name.Value.ToLowerInvariant());

        [Test]
        public void It_holds_one_session_owned_exclusive_application_lock_in_master() =>
            _sessionOwnedLocksInMaster.Should().Be(1);

        [Test]
        public void It_holds_the_resource_exclusively() =>
            (_modeWhileHeld, _grantableWhileHeld).Should().Be(("Exclusive", false));

        [Test]
        public void It_runs_commands_on_the_holder_connection_through_the_gate() =>
            _gatedResult.Should().Be(1);

        [Test]
        public void It_leaves_the_session_lock_timeout_unchanged() =>
            _lockTimeoutAfterAcquire.Should().Be(-1);

        [Test]
        public void It_declares_no_loss_while_healthy() => _lostWhileHeld.Should().BeFalse();

        [Test]
        public void It_releases_the_lock_on_disposal() => _grantableAfterRelease.Should().BeTrue();

        [Test]
        public void It_closes_the_holder_session_on_disposal() => _sessionGoneAfterRelease.Should().BeTrue();

        [Test]
        public void It_is_safe_to_dispose_again() => _secondDisposal.Should().BeNull();
    }

    [TestFixture]
    public class Given_two_acquisitions_for_one_name
    {
        private MssqlTargetLockSession? _first;
        private DmsTargetLockResult? _second;
        private MssqlTargetLockSession? _secondHolder;
        private bool _contenderObservedWaiting;
        private bool _secondCompletedWhileFirstHeld;
        private string _firstModeWhileContended = string.Empty;
        private bool _firstLostByContention;
        private string _secondMode = string.Empty;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            DmsTargetDatabaseName name = TargetName("contend");
            DmsOperationId contender = DmsOperationId.New();
            _first = await AcquireAsync(Engine(), name, DmsOperationId.New());

            Task<DmsTargetLockResult> second = Engine(lockAcquireTimeout: TimeSpan.FromSeconds(30))
                .AcquireTargetLockAsync(name, contender, default);
            _contenderObservedWaiting = await ContenderWaitingAsync(contender);
            _secondCompletedWhileFirstHeld = second.IsCompleted;
            _firstModeWhileContended = await ModeOnHolderAsync(_first, _first.Resource);
            _firstLostByContention = _first.IsLost;

            await _first.DisposeAsync();
            _second = await second;
            _secondHolder = (_second as DmsTargetLockResult.Acquired)?.Holder as MssqlTargetLockSession;
            if (_secondHolder is not null)
            {
                _secondMode = await ModeOnHolderAsync(_secondHolder, _first.Resource);
            }
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
            (_firstModeWhileContended, _firstLostByContention).Should().Be(("Exclusive", false));

        [Test]
        public void It_grants_the_contender_once_the_holder_is_released()
        {
            _second.Should().BeOfType<DmsTargetLockResult.Acquired>();
            _secondMode.Should().Be("Exclusive");
        }
    }

    [TestFixture]
    public class Given_names_that_differ_only_in_case
    {
        private MssqlTargetLockSession? _holder;
        private DmsTargetLockResult? _result;
        private string _upperName = string.Empty;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            DmsTargetDatabaseName lower = TargetName("case");
            DmsTargetDatabaseName upper = ValidName(lower.Value.ToUpperInvariant());
            _upperName = upper.Value;
            _holder = await AcquireAsync(Engine(), lower, DmsOperationId.New());
            _result = await Engine(lockAcquireTimeout: TimeSpan.FromSeconds(1))
                .AcquireTargetLockAsync(upper, DmsOperationId.New(), default);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_result is DmsTargetLockResult.Acquired acquired)
            {
                await acquired.Holder.DisposeAsync();
            }

            if (_holder is not null)
            {
                await _holder.DisposeAsync();
            }
        }

        [Test]
        public void It_uses_a_name_that_differs_from_the_held_one() =>
            _upperName.Should().NotBe(_upperName.ToLowerInvariant());

        [Test]
        public void It_makes_them_contend_for_one_resource() =>
            _result.Should().BeOfType<DmsTargetLockResult.Timeout>();
    }

    [TestFixture]
    public class Given_a_contender_whose_budget_ends_while_the_holder_lives
    {
        private MssqlTargetLockSession? _holder;
        private DmsTargetLockResult _result = null!;
        private SqlConnection? _contenderConnection;
        private ConnectionState? _contenderConnectionStateOnReturn;
        private string _holderMode = string.Empty;
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
            _result = await MssqlTargetLockSession.AcquireAsync(
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
            _contenderConnectionStateOnReturn = _contenderConnection?.State;
            _holderMode = await ModeOnHolderAsync(_holder, _holder.Resource);
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
            (_holderMode, _holderLost, _holderResult).Should().Be(("Exclusive", false, 1));

        [Test]
        public void It_leaves_no_contender_session_behind() => _contenderSessionGone.Should().BeTrue();
    }

    [TestFixture]
    public class Given_an_acquisition_is_cancelled_while_it_waits
    {
        private MssqlTargetLockSession? _holder;
        private bool _contenderObservedWaiting;
        private Exception? _outcome;
        private bool _contenderSessionGone;
        private string _holderMode = string.Empty;

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
            _contenderObservedWaiting = await ContenderWaitingAsync(contender);
            await cancellation.CancelAsync();
            _outcome = await Record.ExceptionAsync(() => waiting);
            _contenderSessionGone = await WaitUntilAsync(
                async () => await SessionCountNamedAsync(contender.HolderApplicationName) == 0,
                _bound
            );
            _holderMode = await ModeOnHolderAsync(_holder, _holder.Resource);
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
        public void It_leaves_the_holder_lock_in_place() => _holderMode.Should().Be("Exclusive");
    }

    [TestFixture]
    public class Given_locks_for_different_names
    {
        private MssqlTargetLockSession? _first;
        private MssqlTargetLockSession? _second;
        private string _firstMode = string.Empty;
        private string _secondMode = string.Empty;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            MssqlDmsTargetEngine engine = Engine(lockAcquireTimeout: TimeSpan.FromSeconds(1));
            Task<MssqlTargetLockSession> first = AcquireAsync(
                engine,
                TargetName("one"),
                DmsOperationId.New()
            );
            Task<MssqlTargetLockSession> second = AcquireAsync(
                engine,
                TargetName("two"),
                DmsOperationId.New()
            );
            await Task.WhenAll(first, second);
            _first = await first;
            _second = await second;
            _firstMode = await ModeOnHolderAsync(_first, _first.Resource);
            _secondMode = await ModeOnHolderAsync(_second, _second.Resource);
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
        public void It_acquires_both_without_contention() =>
            (_firstMode, _secondMode).Should().Be(("Exclusive", "Exclusive"));

        [Test]
        public void It_tells_apart_resources_whose_first_32_characters_match()
        {
            _first!.Resource[..32].Should().Be(_second!.Resource[..32]);
            _first.Resource.Should().NotBe(_second.Resource);
        }
    }

    [TestFixture]
    public class Given_the_holder_session_is_killed
    {
        private static readonly TimeSpan _heartbeat = TimeSpan.FromSeconds(1);

        private MssqlTargetLockSession? _holder;
        private DmsTargetLockResult? _reacquired;
        private bool _lossObserved;
        private TimeSpan _lossLatency;
        private Exception? _commandAfterLoss;
        private bool _noHolderSessionRemains;
        private Exception? _disposalAfterLoss;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            DmsTargetDatabaseName name = TargetName("kill");
            DmsOperationId operation = DmsOperationId.New();
            MssqlDmsTargetEngine engine = Engine(lockHeartbeatInterval: _heartbeat);
            _holder = await AcquireAsync(engine, name, operation);
            TaskCompletionSource lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await using CancellationTokenRegistration registration = _holder.LostToken.Register(() =>
                lost.TrySetResult()
            );

            long started = Stopwatch.GetTimestamp();
            await KillAsync((int)_holder.Session.Id);
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
        public void It_declares_loss_through_the_heartbeat()
        {
            TestContext.Out.WriteLine($"Loss observed {_lossLatency.TotalMilliseconds:F0} ms after KILL.");
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
    public class Given_the_lock_is_released_while_the_holder_session_lives
    {
        private static readonly TimeSpan _interval = TimeSpan.FromSeconds(5);

        private FakeTimeProvider _clock = null!;
        private MssqlTargetLockSession? _holder;
        private bool _lostBeforeProbe;
        private bool _lossObserved;
        private int _completedProbes;
        private bool _sessionStillExists;
        private Exception? _commandAfterLoss;
        private Exception? _disposal;
        private bool _grantableAfterDisposal;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _clock = new FakeTimeProvider();
            _holder = await AcquireAsync(
                Engine(lockHeartbeatInterval: _interval, timeProvider: _clock),
                TargetName("released"),
                DmsOperationId.New()
            );
            await _holder.RunAsync(
                async token =>
                {
                    await ReleaseAppLockAsync(_holder.Connection, _holder.Resource, token);
                    return 0;
                },
                _commandDeadline,
                CancellationToken.None
            );
            _lostBeforeProbe = _holder.IsLost;

            _clock.Advance(_interval);
            _lossObserved = await WaitUntilAsync(() => Task.FromResult(_holder.IsLost), _bound);
            _completedProbes = _holder.Heartbeat.CompletedProbes;
            _sessionStillExists = await SessionExistsAsync(_holder.Session);
            _commandAfterLoss = await Record.ExceptionAsync(() => SelectOneAsync(_holder));
            _disposal = await Record.ExceptionAsync(() => _holder.DisposeAsync().AsTask());
            _grantableAfterDisposal = await GrantableAsync(_holder.Resource);
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
        public void It_declares_nothing_before_the_probe() => _lostBeforeProbe.Should().BeFalse();

        [Test]
        public void It_declares_loss_when_the_probe_finds_the_lock_gone() =>
            (_lossObserved, _completedProbes).Should().Be((true, 1));

        [Test]
        public void It_declares_it_while_the_session_still_lives() => _sessionStillExists.Should().BeTrue();

        [Test]
        public void It_refuses_commands_after_loss() =>
            _commandAfterLoss.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_disposes_safely_and_leaves_the_resource_free() =>
            (_disposal, _grantableAfterDisposal).Should().Be((null, true));
    }

    [TestFixture]
    public class Given_a_holder_command_changes_the_database_context
    {
        private static readonly TimeSpan _interval = TimeSpan.FromSeconds(5);

        private FakeTimeProvider _clock = null!;
        private MssqlTargetLockSession? _holder;
        private string _contextAfterCommand = string.Empty;
        private bool _probed;
        private bool _lost;
        private bool _grantableAfterDisposal;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _clock = new FakeTimeProvider();
            _holder = await AcquireAsync(
                Engine(lockHeartbeatInterval: _interval, timeProvider: _clock),
                TargetName("context"),
                DmsOperationId.New()
            );
            _contextAfterCommand = await ScalarOnHolderAsync<string>(
                _holder,
                "USE tempdb; SELECT DB_NAME();"
            );

            _clock.Advance(_interval);
            _probed = await WaitUntilAsync(
                () => Task.FromResult(_holder.Heartbeat.CompletedProbes == 1),
                _bound
            );
            _lost = _holder.IsLost;
            await _holder.DisposeAsync();
            _grantableAfterDisposal = await GrantableAsync(_holder.Resource);
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
        public void It_moves_the_holder_session_out_of_master() => _contextAfterCommand.Should().Be("tempdb");

        [Test]
        public void It_still_finds_the_lock_held() => (_probed, _lost).Should().Be((true, false));

        [Test]
        public void It_still_releases_the_lock() => _grantableAfterDisposal.Should().BeTrue();
    }

    [TestFixture]
    public class Given_a_heartbeat_becomes_due_while_a_holder_command_blocks_on_the_server
    {
        private static readonly TimeSpan _interval = TimeSpan.FromSeconds(5);

        private FakeTimeProvider _clock = null!;
        private MssqlTargetLockSession? _holder;
        private Blocker? _blocker;
        private bool _holderObservedWaiting;
        private int _skippedWhileBlocked;
        private int _completedWhileBlocked;
        private bool _lostWhileBlocked;
        private bool _completedBeforeRelease;
        private Exception? _blockedFailure;
        private int _status = int.MinValue;
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
            string otherResource = MssqlTargetLockSession.LockResourceFor(TargetName("blocker"));
            _blocker = await Blocker.HoldAsync(otherResource);

            Task<int> blocked = LockOnHolderAsync(_holder, otherResource, _commandDeadline);
            _holderObservedWaiting = await WaitingOnExclusiveLockAsync((int)_holder.Session.Id);
            _clock.Advance(_interval);
            _clock.Advance(_interval);
            _skippedWhileBlocked = _holder.Heartbeat.SkippedProbes;
            _completedWhileBlocked = _holder.Heartbeat.CompletedProbes;
            _lostWhileBlocked = _holder.IsLost;
            _completedBeforeRelease = blocked.IsCompleted;

            await _blocker.ReleaseAsync();
            _blockedFailure = await Record.ExceptionAsync(async () => _status = await blocked);

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
            (_completedBeforeRelease, _blockedFailure, _status).Should().Be((false, null, 1));

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
        private MssqlTargetLockSession? _holder;
        private Blocker? _blocker;
        private string _otherResource = string.Empty;
        private bool _holderObservedWaiting;
        private Exception? _failure;
        private bool _lost;
        private int _laterResult;
        private string _blockerMode = string.Empty;
        private string _holderModeOfTheOtherResource = string.Empty;

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
            _otherResource = MssqlTargetLockSession.LockResourceFor(TargetName("blocker"));
            _blocker = await Blocker.HoldAsync(_otherResource);

            Task<int> blocked = LockOnHolderAsync(_holder, _otherResource, _deadline);
            _holderObservedWaiting = await WaitingOnExclusiveLockAsync((int)_holder.Session.Id);
            _clock.Advance(_deadline);
            _failure = await Record.ExceptionAsync(() => blocked);
            _lost = _holder.IsLost;
            _laterResult = await SelectOneAsync(_holder);
            _blockerMode = await _blocker.ModeAsync();
            _holderModeOfTheOtherResource = await ModeOnHolderAsync(_holder, _otherResource);
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

        /// <summary>
        /// SqlClient reports a request it cancelled after sending as error <c>0</c> (severity below the fatal
        /// range), not as <see cref="OperationCanceledException"/>; the classifier reads it as an outcome in
        /// doubt and never as connection loss.
        /// </summary>
        [Test]
        public void It_cancels_that_command_as_a_client_cancellation() =>
            (
                _failure is SqlException,
                MssqlErrorClassifier.ErrorNumberOf(_failure!),
                MssqlErrorClassifier.IsConnectionFailure(_failure!)
            )
                .Should()
                .Be((true, MssqlErrorClassifier.ClientCancelled, false));

        [Test]
        public void It_declares_no_loss() => _lost.Should().BeFalse();

        [Test]
        public void It_leaves_the_holder_usable() => _laterResult.Should().Be(1);

        [Test]
        public void It_leaves_the_blocked_lock_with_the_other_session() =>
            (_blockerMode, _holderModeOfTheOtherResource).Should().Be(("Exclusive", "NoLock"));
    }

    /// <summary>
    /// The operator's lookup of a stuck holder (7.6): <c>sys.dm_tran_locks</c> shows only the first 32
    /// characters of an application lock resource followed by a hash, so the holder cannot be found by the
    /// full resource name. An operator session requests the same resource with a bounded wait; its waiting
    /// row carries the same description, prefix and hash, as the granted row of the holder.
    /// </summary>
    [TestFixture]
    public class Given_an_operator_looks_up_the_holder_by_its_lock_resource
    {
        private const string LookupByWaitingRequest = """
            SELECT g.request_session_id, s.program_name
            FROM sys.dm_tran_locks w
            JOIN sys.dm_tran_locks g
              ON g.resource_type = w.resource_type
             AND g.resource_database_id = w.resource_database_id
             AND g.resource_description = w.resource_description
             AND g.request_status = N'GRANT'
            JOIN sys.dm_exec_sessions s ON s.session_id = g.request_session_id
            WHERE w.request_session_id = @operator
              AND w.resource_type = N'APPLICATION'
              AND w.request_status = N'WAIT';
            """;

        private DmsOperationId _operation;
        private MssqlTargetLockSession? _holder;
        private int _byFullNameMatches = -1;
        private bool _operatorWaiting;
        private readonly List<(int SessionId, string ProgramName)> _found = [];

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            DmsTargetDatabaseName name = TargetName("operator");
            _operation = DmsOperationId.New();
            _holder = await AcquireAsync(Engine(), name, _operation);

            _byFullNameMatches = await ScalarOnServerAsync<int>(
                "SELECT COUNT(*) FROM sys.dm_tran_locks WHERE resource_type = N'APPLICATION' AND request_status = N'GRANT' AND resource_description LIKE N'%' + @resource + N'%';",
                ("@resource", _holder.Resource)
            );

            await using SqlConnection operatorSession = await AdminConnection()
                .OpenAsync("master", "dms1438-operator", default);
            int operatorId = await ScalarOnConnectionAsync(operatorSession, "SELECT CONVERT(int, @@SPID);");
            using CancellationTokenSource stop = new();
            Task<int> request = GetAppLockAsync(
                operatorSession,
                _holder.Resource,
                lockTimeoutMilliseconds: 20_000,
                stop.Token
            );
            _operatorWaiting = await WaitingOnExclusiveLockAsync(operatorId);

            await using SqlConnection reader = await AdminConnection().OpenMaintenanceAsync(default);
            await using SqlCommand lookup = new(LookupByWaitingRequest, reader);
            lookup.Parameters.AddWithValue("@operator", operatorId);
            await using (SqlDataReader rows = await lookup.ExecuteReaderAsync())
            {
                while (await rows.ReadAsync())
                {
                    _found.Add((rows.GetInt32(0), rows.GetString(1)));
                }
            }

            await stop.CancelAsync();
            await Record.ExceptionAsync(() => request);
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
        public void It_cannot_find_the_holder_by_the_full_resource_name() =>
            _byFullNameMatches.Should().Be(0);

        [Test]
        public void It_sees_the_operator_request_waiting() => _operatorWaiting.Should().BeTrue();

        [Test]
        public void It_finds_exactly_the_holder_session_and_its_operation() =>
            _found.Should().Equal(((int)_holder!.Session.Id, _operation.HolderApplicationName));

        private static async Task<int> ScalarOnConnectionAsync(SqlConnection connection, string sql)
        {
            await using SqlCommand command = new(sql, connection);
            return (int)(await command.ExecuteScalarAsync())!;
        }
    }
}
