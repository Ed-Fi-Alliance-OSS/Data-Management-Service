// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning.DmsProvisioningTestServer;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning;

/// <summary>
/// The dispatch boundary of the selected-target mutations (fence, drop, rename) against a live server. A
/// cancellation that arrives once a mutation's checks have returned, before the mutation is handed to the driver,
/// and a check that the caller or the holder's deadline cancels while it runs on the server (<c>57014</c>), both
/// end as <see cref="DmsCommandNotDispatchedException"/> with the target as it was. Through the orchestrator, the
/// drop's case leaves the delete's ledger fenced, so the fence is reversed under the same lock.
/// </summary>
[Category(Category)]
public class PostgresqlDispatchBoundaryTests
{
    private static readonly TimeSpan _bound = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan _quietHeartbeat = TimeSpan.FromMinutes(30);

    /// <summary>Past the fence's deadline and every command deadline of the lane, and well short of its heartbeat.</summary>
    private static readonly TimeSpan _pastEveryDeadline = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Cancels <paramref name="source"/> once, the moment the driver's trace reports that a command starting with
    /// <paramref name="statement"/> (up to its first parameter) has completed: after that read has returned its
    /// result, before the next statement is handed to the driver.
    /// </summary>
    private sealed class CancelAfterRead : IDisposable
    {
        private readonly ActivityListener _listener;
        private int _fired;

        public CancelAfterRead(string statement, CancellationTokenSource source)
        {
            string prefix = statement[..statement.IndexOf('@', StringComparison.Ordinal)];
            _listener = new ActivityListener
            {
                ShouldListenTo = activitySource => activitySource.Name == "Npgsql",
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                    ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    if (
                        activity.GetTagItem("db.statement") is string text
                        && text.StartsWith(prefix, StringComparison.Ordinal)
                        && Interlocked.Exchange(ref _fired, 1) == 0
                    )
                    {
                        source.Cancel();
                    }
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public bool Fired => Volatile.Read(ref _fired) == 1;

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>
    /// Holds <c>pg_shdescription</c> exclusively in an open transaction: every instance check reads the database
    /// comment, so one started meanwhile waits on the server until it is cancelled. Disposal releases it.
    /// </summary>
    private sealed class CommentCatalogLock : IAsyncDisposable
    {
        private readonly NpgsqlConnection _connection;
        private readonly NpgsqlTransaction _transaction;

        private CommentCatalogLock(NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            _connection = connection;
            _transaction = transaction;
        }

        public static async Task<CommentCatalogLock> TakeAsync()
        {
            NpgsqlConnection connection = await AdminConnection().OpenMaintenanceAsync(default);
            try
            {
                NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
                await using NpgsqlCommand command = new(
                    "LOCK TABLE pg_catalog.pg_shdescription IN ACCESS EXCLUSIVE MODE;",
                    connection,
                    transaction
                );
                await command.ExecuteNonQueryAsync();
                return new CommentCatalogLock(connection, transaction);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _transaction.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    /// <summary>A fresh target name under a holder, and a staging database created and claimed on it; disposal drops both names.</summary>
    private sealed class Staging(
        IDmsTargetLockHolder holder,
        DmsDatabaseName stage,
        DmsInstanceIdentity instance,
        DmsTargetDatabaseName target
    ) : IAsyncDisposable
    {
        public IDmsTargetLockHolder Holder => holder;

        public DmsDatabaseName Stage => stage;

        public DmsInstanceIdentity Instance => instance;

        public DmsTargetDatabaseName Target => target;

        public static async Task<Staging> CreateAsync(PostgresqlDmsTargetEngine engine, string purpose)
        {
            DmsOperationId operation = DmsOperationId.New();
            DmsTargetDatabaseName target = PostgresqlDeleteTests.TargetNameOf(
                $"dms1438_{purpose}_{operation.Value}"
            );
            IDmsTargetLockHolder holder = await AcquireAsync(engine, target, operation);
            DmsInstanceIdentity instance = (
                await engine.CreateStagingAsync(
                    holder,
                    operation.StagingDatabaseName,
                    new DmsProvisionClaim(operation, Guid.NewGuid()),
                    default
                )
            )
                .Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Succeeded>()
                .Subject.Value;
            return new Staging(holder, operation.StagingDatabaseName, instance, target);
        }

        public async ValueTask DisposeAsync()
        {
            await holder.DisposeAsync();
            await DropDatabaseAsync(stage.Value);
            await DropDatabaseAsync(target.Value);
        }

        private static Task DropDatabaseAsync(string name) =>
            ExecuteOnServerAsync(
                $"DROP DATABASE IF EXISTS {PostgresqlIdentifiers.Quote(name)} WITH (FORCE);"
            );
    }

    private static async Task<IDmsTargetLockHolder> AcquireAsync(
        PostgresqlDmsTargetEngine engine,
        DmsTargetDatabaseName target,
        DmsOperationId operation
    ) =>
        (await engine.AcquireTargetLockAsync(target, operation, default))
            .Should()
            .BeOfType<DmsTargetLockResult.Acquired>()
            .Subject.Holder;

    /// <summary>Whether a database holds <paramref name="name"/>, and whether it accepts connections.</summary>
    private static async Task<(bool Exists, bool AllowsConnections)> CatalogOfAsync(string name) =>
        (
            await ScalarOnServerAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_database d WHERE d.datname = @name);",
                ("name", name)
            ),
            await ScalarOnServerAsync<bool>(
                "SELECT COALESCE((SELECT d.datallowconn FROM pg_catalog.pg_database d WHERE d.datname = @name), false);",
                ("name", name)
            )
        );

    /// <summary>
    /// The caller cancels the moment the drop's session check has returned. The drop is never sent: the ledger
    /// keeps the fence, which is reversed on the same holder before the cancellation propagates.
    /// </summary>
    [TestFixture]
    public class Given_a_delete_is_cancelled_after_the_checks_before_its_drop
    {
        private Lane? _lane;
        private uint _seededOid;
        private bool _signalled;
        private Exception? _ended;
        private (bool Exists, uint Oid, bool AllowsConnections) _after;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _lane = await Lane.BuildAsync(Lane.ShapedSql(), new LaneHooks());
            _seededOid = (await _lane.SeedTargetAsync()).Oid;
            using CancellationTokenSource caller = new();
            using (CancelAfterRead signal = new(PostgresqlDeletionQueries.SessionCount, caller))
            {
                _ended = await Record.ExceptionAsync(() =>
                    _lane.Service.DeleteAsync(_lane.DeleteRequest, caller.Token)
                );
                _signalled = signal.Fired;
            }

            _after = await _lane.TargetCatalogAsync();
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_lane is not null)
            {
                await _lane.DisposeAsync();
            }
        }

        [Test]
        public void It_ends_with_the_callers_cancellation_at_the_drops_session_check()
        {
            _signalled.Should().BeTrue();
            _ended.Should().BeAssignableTo<OperationCanceledException>();
        }

        [Test]
        public void It_leaves_the_target_in_place_and_connectable() =>
            _after.Should().Be((true, _seededOid, true));

        [Test]
        public void It_reverses_the_fence_after_the_drop_that_was_never_sent() =>
            _lane!
                .TargetMutations.Select(call => call.Member)
                .Should()
                .Equal(
                    nameof(IDmsTargetEngine.OpenSessionAsync),
                    nameof(IDmsTargetEngine.FenceAsync),
                    nameof(IDmsTargetEngine.DropInstanceAsync),
                    nameof(IDmsTargetEngine.UnfenceAsync)
                );
    }

    /// <summary>The caller cancels the moment the instance check of a fence, and of a rename, has returned: neither is sent.</summary>
    [TestFixture]
    public class Given_a_fence_or_rename_is_cancelled_after_its_instance_check
    {
        private TestDatabase? _database;
        private IDmsTargetLockHolder? _fenceHolder;
        private Staging? _staging;
        private bool _fenceSignalled;
        private bool _renameSignalled;
        private Exception? _fence;
        private Exception? _rename;
        private (bool Exists, bool AllowsConnections) _fencedTargetAfter;
        private (bool StagingExists, bool TargetExists) _renameNamesAfter;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            PostgresqlDmsTargetEngine engine = Engine();

            _database = await CreateDatabaseAsync("cutfence");
            DmsTargetDatabaseName target = PostgresqlDeleteTests.TargetNameOf(_database.Name);
            DmsInstanceIdentity instance = await _database.CatalogInstanceAsync();
            _fenceHolder = await AcquireAsync(engine, target, DmsOperationId.New());
            using (CancellationTokenSource caller = new())
            using (CancelAfterRead signal = new(PostgresqlDeletionQueries.TargetByName, caller))
            {
                _fence = await Record.ExceptionAsync(() =>
                    engine.FenceAsync(_fenceHolder, target, instance, null, caller.Token)
                );
                _fenceSignalled = signal.Fired;
            }

            _fencedTargetAfter = await CatalogOfAsync(_database.Name);

            _staging = await Staging.CreateAsync(engine, "cutrename");
            using (CancellationTokenSource caller = new())
            using (CancelAfterRead signal = new(PostgresqlStagingQueries.InstanceByName, caller))
            {
                _rename = await Record.ExceptionAsync(() =>
                    engine.RenameIntoTargetAsync(
                        _staging.Holder,
                        _staging.Stage,
                        _staging.Instance,
                        _staging.Target,
                        caller.Token
                    )
                );
                _renameSignalled = signal.Fired;
            }

            _renameNamesAfter = (
                (await CatalogOfAsync(_staging.Stage.Value)).Exists,
                (await CatalogOfAsync(_staging.Target.Value)).Exists
            );
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_fenceHolder is not null)
            {
                await _fenceHolder.DisposeAsync();
            }

            if (_database is not null)
            {
                await _database.DisposeAsync();
            }

            if (_staging is not null)
            {
                await _staging.DisposeAsync();
            }
        }

        [Test]
        public void It_reports_both_as_not_dispatched()
        {
            (_fenceSignalled, _renameSignalled).Should().Be((true, true));
            new[] { _fence, _rename }.Should().AllBeOfType<DmsCommandNotDispatchedException>();
        }

        [Test]
        public void It_leaves_the_target_connectable_and_the_staging_database_under_its_name()
        {
            _fencedTargetAfter.Should().Be((true, true));
            _renameNamesAfter.Should().Be((true, false));
        }
    }

    /// <summary>
    /// The instance check of a fence, a drop and a rename waits on the server and is cancelled there (<c>57014</c>),
    /// by the caller or by the holder's deadline: each mutation is reported as not dispatched, never as a server
    /// rejection, and nothing changed, so the holder can still reverse the fence.
    /// </summary>
    [TestFixture]
    public class Given_the_check_before_a_mutation_is_cancelled_while_it_waits_on_the_server
    {
        private readonly List<TestDatabase> _databases = [];
        private readonly List<IDmsTargetLockHolder> _holders = [];
        private readonly List<bool> _waited = [];
        private Staging? _staging;
        private object? _fenceByDeadline;
        private object? _dropByCaller;
        private object? _renameByCaller;
        private object? _renameByDeadline;
        private (bool Exists, bool AllowsConnections) _fenceTargetAfter;
        private (bool Exists, bool AllowsConnections) _dropTargetAfter;
        private (bool StagingExists, bool TargetExists) _renameNamesAfter;
        private DmsCommandOutcome _unfence = null!;

        /// <summary>
        /// Runs <paramref name="command"/> on <paramref name="holder"/> while the comment catalog is held, waits
        /// until its instance check is waiting on the server, then stops it with the caller's token or, when
        /// <paramref name="clock"/> is given, by moving the holder's clock past its deadline. Returns whether the
        /// check was seen waiting, and what ended the command: the exception, or the outcome it returned.
        /// </summary>
        private static async Task<(bool Waited, object? Ended)> StopWaitingCheckAsync(
            IDmsTargetLockHolder holder,
            FakeTimeProvider? clock,
            Func<CancellationToken, Task<object>> command
        )
        {
            await using CommentCatalogLock held = await CommentCatalogLock.TakeAsync();
            using CancellationTokenSource caller = new();
            Task<object> running = command(caller.Token);
            bool waited = await WaitUntilAsync(
                async () =>
                    (await WaitEventOfAsync(holder.Session.Id)).StartsWith("Lock:", StringComparison.Ordinal),
                _bound
            );
            if (clock is null)
            {
                await caller.CancelAsync();
            }
            else
            {
                clock.Advance(_pastEveryDeadline);
            }

            try
            {
                return (waited, await running.WaitAsync(_bound));
            }
            catch (Exception exception) when (exception is not TimeoutException)
            {
                return (waited, exception);
            }
        }

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            await RequireSuperuserOrSkipAsync();
            FakeTimeProvider clock = new();
            PostgresqlDmsTargetEngine engine = Engine(
                lockHeartbeatInterval: _quietHeartbeat,
                timeProvider: clock
            );

            // The fence's check, stopped by the holder's deadline.
            TestDatabase fenceDatabase = await CreateDatabaseAsync("checkfence");
            _databases.Add(fenceDatabase);
            DmsTargetDatabaseName fenceTarget = PostgresqlDeleteTests.TargetNameOf(fenceDatabase.Name);
            DmsInstanceIdentity fenceInstance = await fenceDatabase.CatalogInstanceAsync();
            IDmsTargetLockHolder fenceHolder = await AcquireAsync(engine, fenceTarget, DmsOperationId.New());
            _holders.Add(fenceHolder);
            (bool waited, _fenceByDeadline) = await StopWaitingCheckAsync(
                fenceHolder,
                clock,
                async token => await engine.FenceAsync(fenceHolder, fenceTarget, fenceInstance, null, token)
            );
            _waited.Add(waited);
            _fenceTargetAfter = await CatalogOfAsync(fenceDatabase.Name);

            // The drop's check on a fenced target, stopped by the caller; the same holder then reverses the fence.
            TestDatabase dropDatabase = await CreateDatabaseAsync("checkdrop");
            _databases.Add(dropDatabase);
            DmsTargetDatabaseName dropTarget = PostgresqlDeleteTests.TargetNameOf(dropDatabase.Name);
            DmsInstanceIdentity dropInstance = await dropDatabase.CatalogInstanceAsync();
            IDmsTargetLockHolder dropHolder = await AcquireAsync(engine, dropTarget, DmsOperationId.New());
            _holders.Add(dropHolder);
            (await engine.FenceAsync(dropHolder, dropTarget, dropInstance, null, default))
                .Should()
                .BeOfType<DmsCommandOutcome.Succeeded>();
            (waited, _dropByCaller) = await StopWaitingCheckAsync(
                dropHolder,
                null,
                async token =>
                    await engine.DropInstanceAsync(
                        dropHolder,
                        dropTarget,
                        dropInstance,
                        DmsDropReason.Delete,
                        token
                    )
            );
            _waited.Add(waited);
            _dropTargetAfter = await CatalogOfAsync(dropDatabase.Name);
            _unfence = await engine.UnfenceAsync(dropHolder, dropTarget, dropInstance, default);

            // The rename's check, stopped by the caller and then by the holder's deadline.
            _staging = await Staging.CreateAsync(engine, "checkrename");
            Func<CancellationToken, Task<object>> rename = async token =>
                await engine.RenameIntoTargetAsync(
                    _staging.Holder,
                    _staging.Stage,
                    _staging.Instance,
                    _staging.Target,
                    token
                );
            (waited, _renameByCaller) = await StopWaitingCheckAsync(_staging.Holder, null, rename);
            _waited.Add(waited);
            (waited, _renameByDeadline) = await StopWaitingCheckAsync(_staging.Holder, clock, rename);
            _waited.Add(waited);
            _renameNamesAfter = (
                (await CatalogOfAsync(_staging.Stage.Value)).Exists,
                (await CatalogOfAsync(_staging.Target.Value)).Exists
            );
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            foreach (IDmsTargetLockHolder holder in _holders)
            {
                await holder.DisposeAsync();
            }

            foreach (TestDatabase database in _databases)
            {
                await database.DisposeAsync();
            }

            if (_staging is not null)
            {
                await _staging.DisposeAsync();
            }
        }

        [Test]
        public void It_cancels_each_check_while_it_waits_on_the_server() =>
            _waited.Should().Equal(true, true, true, true);

        [Test]
        public void It_reports_each_mutation_as_not_dispatched() =>
            new[] { _fenceByDeadline, _dropByCaller, _renameByCaller, _renameByDeadline }
                .Should()
                .AllBeOfType<DmsCommandNotDispatchedException>();

        [Test]
        public void It_changes_nothing()
        {
            _fenceTargetAfter.Should().Be((true, true));
            _dropTargetAfter.Should().Be((true, false));
            _renameNamesAfter.Should().Be((true, false));
        }

        [Test]
        public void It_lets_the_same_holder_reverse_the_fence() =>
            _unfence.Should().BeOfType<DmsCommandOutcome.Succeeded>();
    }
}
