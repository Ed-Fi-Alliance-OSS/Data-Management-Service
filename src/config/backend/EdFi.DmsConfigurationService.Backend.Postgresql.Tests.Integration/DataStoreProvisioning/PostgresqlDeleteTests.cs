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
/// The owned delete (6.6, Step 3.8) against a live server. The engine's fence keeps exactly the session it is
/// given and ends every other one, and its drop refuses an unfenced target, a target with a session on it,
/// and another instance. Through the orchestrator: an owned target is dropped, also with a busy session on it;
/// an absent, unowned, or unreadable target is left untouched; a session the admin may not end (a superuser's)
/// or a missing <c>pg_signal_backend</c> leaves the target unfenced and connectable; a competing writer or a
/// recreated instance is refused; deletes of one target are serialized by the target lock, and a contender
/// never takes it over from a live holder. The takeover fixtures (round-3 finding 2) kill a paused worker's
/// holder before its heartbeat can notice, let a second worker replace the target, and prove the first can
/// then mutate nothing; those that recreate the target with a full create need <c>psql</c>.
/// </summary>
[Category(Category)]
public class PostgresqlDeleteTests
{
    private static readonly TimeSpan _bound = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan _quietHeartbeat = TimeSpan.FromMinutes(30);

    private static readonly ProtectedDatabaseNames _protectedNames = ProtectedDatabaseNames.Create(
        "edfi_configurationservice",
        []
    );

    internal static DmsTargetDatabaseName TargetNameOf(string name) =>
        DmsTargetDatabaseName.TryCreate(name, _protectedNames, out DmsTargetDatabaseName target)
        == DmsTargetNameValidation.Valid
            ? target
            : throw new InvalidOperationException("The target name does not satisfy the rules.");

    /// <summary>Holds the first operation that reaches <paramref name="point"/> until released; every other passes.</summary>
    private sealed class Pause(DmsProvisioningHookPoint point)
    {
        private int _taken;

        public TaskCompletionSource<DmsOperationId> Reached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ProbeAsync(DmsProvisioningHookPoint at, DmsOperationId operation)
        {
            if (at != point || Interlocked.Exchange(ref _taken, 1) != 0)
            {
                return;
            }

            Reached.SetResult(operation);
            await Release.Task.WaitAsync(_bound);
        }

        /// <summary>The paused operation, or a failure naming why <paramref name="running"/> never reached the hook.</summary>
        public async Task<DmsOperationId> ReachedAsync(Task running)
        {
            await Task.WhenAny(Reached.Task, running, Task.Delay(_bound));
            Reached
                .Task.IsCompleted.Should()
                .BeTrue("the operation must reach the hook instead of ending earlier");
            return await Reached.Task;
        }
    }

    /// <summary>A session on the target, idle or sleeping inside a transaction; disposal ends it whatever happened.</summary>
    private sealed class TargetSession : IAsyncDisposable
    {
        private readonly NpgsqlConnection _connection;
        private readonly Task? _sleeping;

        private TargetSession(NpgsqlConnection connection, Task? sleeping, DmsServerSessionId session)
        {
            _connection = connection;
            _sleeping = sleeping;
            Session = session;
        }

        public DmsServerSessionId Session { get; }

        public static async Task<TargetSession> OpenAsync(
            DmsTargetDatabaseName target,
            string? roleName = null,
            bool busy = false
        )
        {
            PostgresqlAdminConnection admin = roleName is null
                ? AdminConnection()
                : AdminConnectionAs(roleName);
            NpgsqlConnection connection = await admin.OpenAsync(target.Value, "dms1438-competing", default);
            DmsServerSessionId session = await OwnSessionAsync(connection);
            Task? sleeping = null;
            if (busy)
            {
                NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
                NpgsqlCommand sleep = new("SELECT pg_catalog.pg_sleep(600);", connection, transaction)
                {
                    CommandTimeout = 0,
                };
                sleeping = sleep.ExecuteNonQueryAsync();
                bool observed = await WaitUntilAsync(
                    async () => await WaitEventOfAsync(session.Id) == "Timeout:PgSleep",
                    _bound
                );
                observed.Should().BeTrue("the competing session must be observed sleeping");
            }

            return new TargetSession(connection, sleeping, session);
        }

        /// <summary>What ended the sleep within the bound: the exception, or null for a normal end.</summary>
        public async Task<Exception?> SleepOutcomeAsync() =>
            _sleeping is null || await Task.WhenAny(_sleeping, Task.Delay(_bound)) != _sleeping
                ? new TimeoutException("The sleep did not end within the bound.")
                : await Record.ExceptionAsync(() => _sleeping);

        public async ValueTask DisposeAsync()
        {
            await Record.ExceptionAsync(() => TerminateBackendAsync(Session.Id));
            if (_sleeping is not null)
            {
                await Record.ExceptionAsync(() => _sleeping.WaitAsync(_bound));
            }

            await _connection.DisposeAsync();
        }
    }

    private static Task<bool> SessionGoneAsync(DmsServerSessionId session) =>
        WaitUntilAsync(async () => !await SessionPresentAsync(session), _bound);

    /// <summary>Kills the paused operation's holder session and waits until the server no longer lists it.</summary>
    internal static async Task KillHolderAsync(DmsOperationId operation)
    {
        int pid = await PidNamedAsync(operation.HolderApplicationName);
        pid.Should().NotBe(0, "the paused operation holds the target lock");
        await TerminateBackendAsync(pid);
        (
            await WaitUntilAsync(
                async () => await SessionCountNamedAsync(operation.HolderApplicationName) == 0,
                _bound
            )
        )
            .Should()
            .BeTrue();
    }

    /// <summary>Whether a holder session is waiting for an advisory lock, as a second worker queued behind the first does.</summary>
    private static Task<bool> HolderWaitingAsync() =>
        WaitUntilAsync(
            async () =>
                await ScalarOnServerAsync<int>(
                    "SELECT COUNT(*)::int FROM pg_catalog.pg_stat_activity a WHERE a.application_name LIKE @prefix AND a.wait_event_type = 'Lock' AND a.wait_event = 'advisory';",
                    ("prefix", DmsOperationId.HolderApplicationNamePrefix + "%")
                ) > 0,
            _bound
        );

    /// <summary>Adds a sentinel row to the target that only the worker that created it could have written.</summary>
    internal static async Task WriteSentinelAsync(DmsTargetDatabaseName target)
    {
        TestDatabase database = new(target.Value);
        await database.ExecuteAsync(
            "CREATE TABLE public.dms1438_sentinel (value integer NOT NULL); INSERT INTO public.dms1438_sentinel VALUES (1438);"
        );
    }

    internal static Task<int> SentinelAsync(DmsTargetDatabaseName target) =>
        new TestDatabase(target.Value).ScalarAsync<int>("SELECT value FROM public.dms1438_sentinel;");

    private static IEnumerable<string> MembersOf(RecordingEngine engine) =>
        engine.Calls.Select(call => call.Member);

    [TestFixture]
    public class Given_the_engine_fences_and_drops_an_instance
    {
        private TestDatabase? _database;
        private DmsCommandOutcome _wrongInstanceFence = null!;
        private bool _allowedAfterWrongInstance;
        private DmsCommandOutcome _fence = null!;
        private bool _allowedAfterFence;
        private bool _keptPresent;
        private bool _otherGone;
        private DmsCommandOutcome _dropWithSession = null!;
        private DmsCommandOutcome _unfence = null!;
        private bool _allowedAfterUnfence;
        private DmsCommandOutcome _dropUnfenced = null!;
        private DmsCommandOutcome _secondFence = null!;
        private DmsCommandOutcome _drop = null!;
        private bool _presentAfterDrop;
        private DmsCommandOutcome _dropAgain = null!;

        private Task<bool> AllowsConnectionsAsync() =>
            ScalarOnServerAsync<bool>(
                "SELECT d.datallowconn FROM pg_catalog.pg_database d WHERE d.datname = @name;",
                ("name", _database!.Name)
            );

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _database = await CreateDmsShapedDatabaseAsync("fence");
            DmsTargetDatabaseName target = TargetNameOf(_database.Name);
            DmsInstanceIdentity.Postgresql instance = await _database.CatalogInstanceAsync();
            PostgresqlDmsTargetEngine engine = Engine();
            await using IDmsTargetLockHolder holder = await engine.AcquireTargetLockAsync(
                target,
                DmsOperationId.New(),
                default
            )
                is DmsTargetLockResult.Acquired acquired
                ? acquired.Holder
                : throw new InvalidOperationException("The target lock was not acquired.");

            _wrongInstanceFence = await engine.FenceAsync(
                holder,
                target,
                new DmsInstanceIdentity.Postgresql(instance.Oid + 1, instance.Claim),
                null,
                default
            );
            _allowedAfterWrongInstance = await AllowsConnectionsAsync();

            DmsSession kept = (await engine.OpenSessionAsync(target, instance, default))
                .Should()
                .BeOfType<DmsSessionOpenResult.Opened>()
                .Subject.Session;
            await using (TargetSession other = await TargetSession.OpenAsync(target))
            {
                _fence = await engine.FenceAsync(holder, target, instance, kept.ServerSession, default);
                _allowedAfterFence = await AllowsConnectionsAsync();
                _keptPresent = await SessionPresentAsync(kept.ServerSession);
                _otherGone = await SessionGoneAsync(other.Session);
            }

            _dropWithSession = await engine.DropInstanceAsync(
                holder,
                target,
                instance,
                DmsDropReason.Delete,
                default
            );
            _unfence = await engine.UnfenceAsync(holder, target, instance, default);
            _allowedAfterUnfence = await AllowsConnectionsAsync();
            await kept.DisposeAsync();
            await WaitForNoSessionsOnAsync(_database.Name);
            _dropUnfenced = await engine.DropInstanceAsync(
                holder,
                target,
                instance,
                DmsDropReason.Delete,
                default
            );

            _secondFence = await engine.FenceAsync(holder, target, instance, null, default);
            _drop = await engine.DropInstanceAsync(holder, target, instance, DmsDropReason.Delete, default);
            _presentAfterDrop = await ScalarOnServerAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_database d WHERE d.datname = @name);",
                ("name", _database.Name)
            );
            _dropAgain = await engine.DropInstanceAsync(
                holder,
                target,
                instance,
                DmsDropReason.Delete,
                default
            );
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_database is not null)
            {
                await _database.DisposeAsync();
            }
        }

        [Test]
        public void It_refuses_to_fence_another_instance_and_touches_nothing()
        {
            _wrongInstanceFence
                .Should()
                .Be(
                    new DmsCommandOutcome.Failed(
                        new DmsCommandFailure(
                            DmsCommandFailureClass.DoesNotExist,
                            DmsProvisioningDiagnostic.ForOperation(
                                PostgresqlDeletionQueries.FenceInstanceStep
                            )
                        )
                    )
                );
            _allowedAfterWrongInstance.Should().BeTrue();
        }

        [Test]
        public void It_fences_keeping_exactly_the_given_session() =>
            (_fence, _allowedAfterFence, _keptPresent, _otherGone)
                .Should()
                .Be((new DmsCommandOutcome.Succeeded(), false, true, true));

        [Test]
        public void It_refuses_to_drop_while_a_session_remains() =>
            _dropWithSession
                .Should()
                .Be(
                    new DmsCommandOutcome.Failed(
                        new DmsCommandFailure(
                            DmsCommandFailureClass.ObjectInUse,
                            DmsProvisioningDiagnostic.ForOperation(
                                PostgresqlDeletionQueries.DropTargetSessionsStep
                            )
                        )
                    )
                );

        [Test]
        public void It_reverses_the_fence() =>
            (_unfence, _allowedAfterUnfence).Should().Be((new DmsCommandOutcome.Succeeded(), true));

        [Test]
        public void It_refuses_to_drop_an_unfenced_target() =>
            _dropUnfenced
                .Should()
                .Be(
                    new DmsCommandOutcome.Failed(
                        new DmsCommandFailure(
                            DmsCommandFailureClass.ObjectInUse,
                            DmsProvisioningDiagnostic.ForOperation(
                                PostgresqlDeletionQueries.DropTargetNotFencedStep
                            )
                        )
                    )
                );

        [Test]
        public void It_drops_a_fenced_target_without_sessions() =>
            (_secondFence, _drop, _presentAfterDrop)
                .Should()
                .Be((new DmsCommandOutcome.Succeeded(), new DmsCommandOutcome.Succeeded(), false));

        [Test]
        public void It_reports_a_name_found_empty_as_absent() =>
            _dropAgain
                .Should()
                .Be(
                    new DmsCommandOutcome.Failed(
                        new DmsCommandFailure(
                            DmsCommandFailureClass.DoesNotExist,
                            DmsProvisioningDiagnostic.ForOperation(DmsEngineOperations.DropTargetAbsent)
                        )
                    )
                );
    }

    /// <summary>
    /// The termination bound (D-22) at its edge: the clock reaches the whole <c>SessionTerminationTimeout</c> the
    /// moment the driver reports the first termination complete, so the session it ended is proven gone as the
    /// bound passes. The fence lists the sessions again before it decides, also when a second session is still
    /// waiting in the list read before the bound passed: with no other session left, including when that second
    /// session disconnected on its own during the first termination, it has established exclusivity; with the
    /// second session still connected it starts no termination past the bound, and that session keeps the
    /// target in use.
    /// </summary>
    [TestFixture]
    public class Given_the_last_termination_is_proven_as_the_termination_bound_passes
    {
        private static readonly TimeSpan _termination = TimeSpan.FromSeconds(30);

        private FenceRun _alone = null!;
        private FenceRun _secondLeft = null!;
        private FenceRun _withSecond = null!;

        private sealed record FenceRun(
            DmsTargetDatabaseName Target,
            DmsCommandOutcome Outcome,
            int Terminations,
            IReadOnlyList<bool> Present,
            bool AllowsConnections,
            bool SecondLeft
        );

        /// <summary>
        /// Counts the fence's guarded terminations the driver completes; at the first it runs
        /// <paramref name="atFirstTermination"/>, then advances the clock by the bound.
        /// </summary>
        private sealed class BoundPassesAtFirstTermination : IDisposable
        {
            private readonly ActivityListener _listener;
            private int _terminations;

            public BoundPassesAtFirstTermination(FakeTimeProvider time, Action atFirstTermination)
            {
                _listener = new ActivityListener
                {
                    ShouldListenTo = activitySource => activitySource.Name == "Npgsql",
                    Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                        ActivitySamplingResult.AllDataAndRecorded,
                    ActivityStopped = activity =>
                    {
                        if (
                            activity.GetTagItem("db.statement") is string text
                            && text.Contains("process_ended", StringComparison.Ordinal)
                            && Interlocked.Increment(ref _terminations) == 1
                        )
                        {
                            atFirstTermination();
                            time.Advance(_termination);
                        }
                    },
                };
                ActivitySource.AddActivityListener(_listener);
            }

            public int Terminations => Volatile.Read(ref _terminations);

            public void Dispose() => _listener.Dispose();
        }

        /// <param name="sessions">The other sessions on the target; the fence ends them in pid order.</param>
        /// <param name="secondLeaves">Whether the second session disconnects on its own during the first termination.</param>
        private static async Task<FenceRun> FenceAsync(int sessions, bool secondLeaves = false)
        {
            await using TestDatabase database = await CreateDmsShapedDatabaseAsync("bound");
            DmsTargetDatabaseName target = TargetNameOf(database.Name);
            DmsInstanceIdentity.Postgresql instance = await database.CatalogInstanceAsync();
            List<TargetSession> others = [];
            try
            {
                for (int opened = 0; opened < sessions; opened++)
                {
                    others.Add(await TargetSession.OpenAsync(target));
                }

                // The listing is ordered by pid, so the second session is the one listed after the first.
                others.Sort((left, right) => left.Session.Id.CompareTo(right.Session.Id));
                bool secondLeft = false;

                // Blocks the fence until the server no longer lists the second session.
                void AtFirstTermination()
                {
                    if (secondLeaves)
                    {
                        others[1].DisposeAsync().AsTask().GetAwaiter().GetResult();
                        secondLeft = SessionGoneAsync(others[1].Session).GetAwaiter().GetResult();
                    }
                }

                FakeTimeProvider time = new();
                DmsCommandOutcome outcome;
                int terminations;
                await using (
                    NpgsqlConnection connection = await AdminConnection().OpenMaintenanceAsync(default)
                )
                {
                    DmsServerSessionId holder = await OwnSessionAsync(connection);
                    using BoundPassesAtFirstTermination clock = new(time, AtFirstTermination);
                    outcome = await PostgresqlDeletionQueries.FenceAsync(
                        connection,
                        target,
                        instance,
                        null,
                        holder,
                        _termination,
                        _bound,
                        time,
                        default
                    );
                    terminations = clock.Terminations;
                }

                List<bool> present = [];
                foreach (TargetSession other in others)
                {
                    present.Add(await SessionPresentAsync(other.Session));
                }

                bool allowsConnections = await ScalarOnServerAsync<bool>(
                    "SELECT d.datallowconn FROM pg_catalog.pg_database d WHERE d.datname = @name;",
                    ("name", database.Name)
                );
                return new FenceRun(target, outcome, terminations, present, allowsConnections, secondLeft);
            }
            finally
            {
                foreach (TargetSession other in others)
                {
                    await other.DisposeAsync();
                }
            }
        }

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _alone = await FenceAsync(1);
            _secondLeft = await FenceAsync(2, secondLeaves: true);
            _withSecond = await FenceAsync(2);
        }

        [Test]
        public void It_establishes_exclusivity_when_no_session_is_listed_after_the_bound() =>
            (_alone.Outcome, _alone.Terminations, _alone.Present.Single(), _alone.AllowsConnections)
                .Should()
                .Be((new DmsCommandOutcome.Succeeded(), 1, false, false));

        [Test]
        public void It_establishes_exclusivity_when_a_session_listed_before_the_bound_has_left_since() =>
            (
                _secondLeft.SecondLeft,
                _secondLeft.Outcome,
                _secondLeft.Terminations,
                _secondLeft.Present.Count(present => present),
                _secondLeft.AllowsConnections
            )
                .Should()
                .Be((true, new DmsCommandOutcome.Succeeded(), 1, 0, false));

        [Test]
        public void It_refuses_a_session_still_listed_after_the_bound_without_ending_it()
        {
            DmsCommandOutcome.Failed failed = _withSecond
                .Outcome.Should()
                .BeOfType<DmsCommandOutcome.Failed>()
                .Subject;
            failed
                .Failure.Should()
                .Be(
                    new DmsCommandFailure(
                        DmsCommandFailureClass.ObjectInUse,
                        DmsProvisioningDiagnostic.ForOperation(PostgresqlDeletionQueries.FenceExclusivityStep)
                    )
                );
            failed
                .Residue.Should()
                .Equal(new DmsCleanupResidue(DmsResidueKind.FencedTarget, _withSecond.Target.Value));
            (
                _withSecond.Terminations,
                _withSecond.Present.Count(present => present),
                _withSecond.AllowsConnections
            )
                .Should()
                .Be((1, 1, false));
        }
    }

    /// <summary>
    /// The fence's holder deadline (<see cref="PostgresqlDmsTargetEngine.FenceDeadline"/>), through the engine on a
    /// held target lock. The clock advances as each fence statement completes, as though it had taken that long;
    /// the time the final listing needs passes as the termination's verification read completes, so a deadline
    /// that leaves that listing no room has passed before it is sent. The listing read after the termination
    /// bound is the proof of exclusivity (D-22), and the deadline leaves it room even when every statement before
    /// it used nearly its whole allowance: the instance check and the <c>ALTER DATABASE</c> nearly a
    /// <c>CommandTimeout</c> each, the session proven gone as the bound passes, and a short final listing; or the
    /// termination, its verification read and the final listing each nearly a <c>CommandTimeout</c> past the
    /// bound. A final listing that outlasts its own allowance is still ended by the deadline, and the target stays
    /// fenced for the delete to reverse.
    /// </summary>
    [TestFixture]
    public class Given_the_proof_after_the_termination_bound_runs_under_the_holder_deadline
    {
        private FenceRun _counterexample = null!;
        private FenceRun _wholeAllowances = null!;
        private FenceRun _listingOverruns = null!;
        private Durations _counterexampleDurations = null!;
        private Durations _wholeAllowanceDurations = null!;

        /// <summary>How long each fence statement takes on the fake clock.</summary>
        private sealed record Durations(
            TimeSpan Verify,
            TimeSpan Alter,
            TimeSpan Terminate,
            TimeSpan Observe,
            TimeSpan FinalListing
        )
        {
            public TimeSpan Total => Verify + Alter + Terminate + Observe + FinalListing;
        }

        private sealed record FenceRun(
            DmsTargetDatabaseName Target,
            DmsCommandOutcome Outcome,
            TimeSpan Elapsed,
            bool SessionGone,
            bool AllowsConnections
        );

        /// <summary>Advances the clock as each of the fence's statements completes on the holder session.</summary>
        private sealed class StatementClock : IDisposable
        {
            private readonly ActivityListener _listener;

            public StatementClock(FakeTimeProvider time, Durations durations)
            {
                _listener = new ActivityListener
                {
                    ShouldListenTo = activitySource => activitySource.Name == "Npgsql",
                    Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                        ActivitySamplingResult.AllDataAndRecorded,
                    ActivityStopped = activity =>
                    {
                        if (activity.GetTagItem("db.statement") is not string text)
                        {
                            return;
                        }

                        if (Has(text, "pg_catalog.shobj_description(d.oid, 'pg_database'), d.datallowconn"))
                        {
                            time.Advance(durations.Verify);
                        }
                        else if (Has(text, "WITH ALLOW_CONNECTIONS false"))
                        {
                            time.Advance(durations.Alter);
                        }
                        else if (Has(text, "process_ended"))
                        {
                            time.Advance(durations.Terminate);
                        }
                        else if (Has(text, "AS matched FROM pg_catalog.pg_stat_activity"))
                        {
                            time.Advance(durations.Observe + durations.FinalListing);
                        }
                    },
                };
                ActivitySource.AddActivityListener(_listener);
            }

            private static bool Has(string text, string part) =>
                text.Contains(part, StringComparison.Ordinal);

            public void Dispose() => _listener.Dispose();
        }

        /// <summary>Fences a target with one other session on it, the fence's statements taking <paramref name="durations"/>.</summary>
        private static async Task<FenceRun> FenceAsync(Durations durations)
        {
            await using TestDatabase database = await CreateDmsShapedDatabaseAsync("deadline");
            DmsTargetDatabaseName target = TargetNameOf(database.Name);
            DmsInstanceIdentity.Postgresql instance = await database.CatalogInstanceAsync();
            FakeTimeProvider time = new();
            PostgresqlDmsTargetEngine engine = Engine(
                lockHeartbeatInterval: _quietHeartbeat,
                timeProvider: time
            );
            await using TargetSession other = await TargetSession.OpenAsync(target);
            DmsCommandOutcome outcome;
            TimeSpan elapsed;
            await using (
                IDmsTargetLockHolder holder = await engine.AcquireTargetLockAsync(
                    target,
                    DmsOperationId.New(),
                    default
                )
                    is DmsTargetLockResult.Acquired acquired
                    ? acquired.Holder
                    : throw new InvalidOperationException("The target lock was not acquired.")
            )
            {
                long started = time.GetTimestamp();
                using (new StatementClock(time, durations))
                {
                    outcome = await engine.FenceAsync(holder, target, instance, null, default);
                }

                elapsed = time.GetElapsedTime(started);
            }

            bool sessionGone = await SessionGoneAsync(other.Session);
            bool allowsConnections = await ScalarOnServerAsync<bool>(
                "SELECT d.datallowconn FROM pg_catalog.pg_database d WHERE d.datname = @name;",
                ("name", database.Name)
            );
            return new FenceRun(target, outcome, elapsed, sessionGone, allowsConnections);
        }

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            TimeSpan command = AdminConnection().CommandTimeout;
            TimeSpan termination = Settings().SessionTerminationTimeout;
            TimeSpan nearlyCommand = command - TimeSpan.FromSeconds(1);

            // The review counterexample: the session is proven gone as the termination bound passes, and the final
            // listing needs three seconds more.
            _counterexampleDurations = new Durations(
                nearlyCommand,
                nearlyCommand,
                termination,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(3)
            );

            // A termination started at the loop's start may run one command past the bound (its driver allowance).
            _wholeAllowanceDurations = new Durations(
                nearlyCommand,
                nearlyCommand,
                termination + nearlyCommand,
                nearlyCommand,
                nearlyCommand
            );
            _counterexample = await FenceAsync(_counterexampleDurations);
            _wholeAllowances = await FenceAsync(_wholeAllowanceDurations);
            _listingOverruns = await FenceAsync(
                _wholeAllowanceDurations with
                {
                    FinalListing = command + command,
                }
            );
        }

        [Test]
        public void It_establishes_exclusivity_when_the_final_listing_follows_slow_statements_before_the_loop() =>
            (
                _counterexample.Outcome,
                _counterexample.Elapsed,
                _counterexample.SessionGone,
                _counterexample.AllowsConnections
            )
                .Should()
                .Be((new DmsCommandOutcome.Succeeded(), _counterexampleDurations.Total, true, false));

        [Test]
        public void It_establishes_exclusivity_when_every_statement_uses_nearly_its_whole_allowance() =>
            (
                _wholeAllowances.Outcome,
                _wholeAllowances.Elapsed,
                _wholeAllowances.SessionGone,
                _wholeAllowances.AllowsConnections
            )
                .Should()
                .Be((new DmsCommandOutcome.Succeeded(), _wholeAllowanceDurations.Total, true, false));

        [Test]
        public void It_still_ends_a_final_listing_that_outlasts_its_allowance_and_keeps_the_target_fenced()
        {
            DmsCommandOutcome.Failed failed = _listingOverruns
                .Outcome.Should()
                .BeOfType<DmsCommandOutcome.Failed>()
                .Subject;
            (failed.Failure.Class, failed.Failure.Diagnostic.Operation)
                .Should()
                .Be((DmsCommandFailureClass.Rejected, PostgresqlDeletionQueries.FenceExclusivityStep));
            failed
                .Residue.Should()
                .Equal(new DmsCleanupResidue(DmsResidueKind.FencedTarget, _listingOverruns.Target.Value));
            (_listingOverruns.SessionGone, _listingOverruns.AllowsConnections).Should().Be((true, false));
        }
    }

    /// <summary>
    /// The dispatch boundary against a live server, on our fenced instance so that a drop that ran would remove it:
    /// a drop the holder's gate refuses (another command holds the gate, as a heartbeat probe would, when the caller
    /// cancels) and a drop whose instance read is cancelled before anything is sent both end as not dispatched, and
    /// the instance is still there and still fenced, ready for the fence to be reversed under the same lock.
    /// </summary>
    [TestFixture]
    public class Given_a_drop_is_stopped_before_it_reaches_the_driver
    {
        private TestDatabase? _database;
        private Exception? _refusedAtTheGate;
        private Exception? _stoppedBeforeSending;
        private (bool Exists, bool AllowsConnections) _afterBoth;
        private DmsCommandOutcome _unfence = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _database = await CreateDmsShapedDatabaseAsync("dispatch");
            DmsTargetDatabaseName target = TargetNameOf(_database.Name);
            DmsInstanceIdentity.Postgresql instance = await _database.CatalogInstanceAsync();
            PostgresqlDmsTargetEngine engine = Engine();
            await using IDmsTargetLockHolder holder = await engine.AcquireTargetLockAsync(
                target,
                DmsOperationId.New(),
                default
            )
                is DmsTargetLockResult.Acquired acquired
                ? acquired.Holder
                : throw new InvalidOperationException("The target lock was not acquired.");
            (await engine.FenceAsync(holder, target, instance, null, default))
                .Should()
                .BeOfType<DmsCommandOutcome.Succeeded>();

            TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<int> blocker = holder.RunAsync(
                async _ =>
                {
                    held.SetResult();
                    await release.Task;
                    return 0;
                },
                _bound,
                default
            );
            await held.Task;
            using (CancellationTokenSource caller = new())
            {
                Task<DmsCommandOutcome> drop = engine.DropInstanceAsync(
                    holder,
                    target,
                    instance,
                    DmsDropReason.Delete,
                    caller.Token
                );
                await caller.CancelAsync();
                _refusedAtTheGate = await Record.ExceptionAsync(() => drop);
            }

            release.SetResult();
            await blocker;

            await using (
                NpgsqlConnection connection = await AdminConnection()
                    .OpenAsync("postgres", "dms1438-dispatch", default)
            )
            {
                using CancellationTokenSource cancelled = new();
                await cancelled.CancelAsync();
                _stoppedBeforeSending = await Record.ExceptionAsync(() =>
                    PostgresqlDeletionQueries.DropTargetAsync(
                        connection,
                        target,
                        instance,
                        holder.Session,
                        cancelled.Token
                    )
                );
            }

            _afterBoth = (
                await ScalarOnServerAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_database d WHERE d.datname = @name);",
                    ("name", _database.Name)
                ),
                await ScalarOnServerAsync<bool>(
                    "SELECT COALESCE((SELECT d.datallowconn FROM pg_catalog.pg_database d WHERE d.datname = @name), true);",
                    ("name", _database.Name)
                )
            );
            _unfence = await engine.UnfenceAsync(holder, target, instance, default);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_database is not null)
            {
                await _database.DisposeAsync();
            }
        }

        [Test]
        public void It_reports_both_drops_as_not_dispatched() =>
            new[] { _refusedAtTheGate, _stoppedBeforeSending }
                .Should()
                .AllBeOfType<DmsCommandNotDispatchedException>();

        [Test]
        public void It_leaves_our_instance_in_place_and_fenced() => _afterBoth.Should().Be((true, false));

        [Test]
        public void It_lets_the_same_holder_reverse_the_fence() =>
            _unfence.Should().BeOfType<DmsCommandOutcome.Succeeded>();
    }

    [TestFixture]
    public class Given_an_owned_target_with_a_busy_session_is_deleted
    {
        private Lane? _lane;
        private DmsDataStoreDeleteResult _result = null!;
        private (bool Exists, uint Oid, bool AllowsConnections) _after;
        private bool _competingGone;
        private Exception? _sleepOutcome;
        private int _holderSessions;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _lane = await Lane.BuildAsync(Lane.ShapedSql(), new LaneHooks());
            await _lane.SeedTargetAsync();
            await using TargetSession competing = await TargetSession.OpenAsync(_lane.Target, busy: true);
            _result = await _lane.DeleteAsync();
            _after = await _lane.TargetCatalogAsync();
            _competingGone = await SessionGoneAsync(competing.Session);
            _sleepOutcome = await competing.SleepOutcomeAsync();
            _holderSessions = await ScalarOnServerAsync<int>(
                "SELECT COUNT(*)::int FROM pg_catalog.pg_stat_activity a WHERE a.application_name LIKE @prefix;",
                ("prefix", DmsOperationId.HolderApplicationNamePrefix + "%")
            );
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
        public void It_drops_the_target() =>
            (_result, _after.Exists)
                .Should()
                .Be((new DmsDataStoreDeleteResult.Dropped(DmsCleanupReport.Clean), false));

        [Test]
        public void It_terminated_the_competing_session_with_proof()
        {
            _competingGone.Should().BeTrue();
            PostgresqlErrorClassifier
                .SqlStateOf(_sleepOutcome!)
                .Should()
                .Be(PostgresErrorCodes.AdminShutdown);
        }

        [Test]
        public void It_fenced_before_it_dropped_and_never_reversed_the_fence() =>
            _lane!
                .TargetMutations.Select(call => call.Member)
                .Should()
                .Equal(
                    nameof(IDmsTargetEngine.OpenSessionAsync),
                    nameof(IDmsTargetEngine.FenceAsync),
                    nameof(IDmsTargetEngine.DropInstanceAsync)
                );

        [Test]
        public void It_leaves_no_holder_session() => _holderSessions.Should().Be(0);
    }

    [TestFixture]
    public class Given_a_target_the_delete_must_not_touch
    {
        private Lane? _absent;
        private Lane? _unowned;
        private Lane? _unreadable;
        private DmsDataStoreDeleteResult _absentResult = null!;
        private DmsDataStoreDeleteResult _unownedResult = null!;
        private DmsDataStoreDeleteResult _unreadableResult = null!;
        private DmsInstanceIdentity.Postgresql _unownedBefore = null!;
        private (bool Exists, uint Oid, bool AllowsConnections) _unownedAfter;
        private (bool Exists, uint Oid, bool AllowsConnections) _unreadableBefore;
        private (bool Exists, uint Oid, bool AllowsConnections) _unreadableAfter;
        private Guid _foreignIdentity;
        private Guid _identityAfter;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _absent = await Lane.BuildAsync(Lane.ShapedSql(), new LaneHooks());
            _absentResult = await _absent.DeleteAsync();

            _unowned = await Lane.BuildAsync(Lane.ShapedSql(), new LaneHooks());
            _foreignIdentity = Guid.NewGuid();
            _unownedBefore = await _unowned.SeedTargetAsync(_foreignIdentity);
            _unownedResult = await _unowned.DeleteAsync();
            _unownedAfter = await _unowned.TargetCatalogAsync();
            _identityAfter = await _unowned.TargetIdentityAsync();

            _unreadable = await Lane.BuildAsync(Lane.ShapedSql(), new LaneHooks());
            await ExecuteOnServerAsync(
                $"CREATE DATABASE {PostgresqlIdentifiers.Quote(_unreadable.Target.Value)};"
            );
            _unreadableBefore = await _unreadable.TargetCatalogAsync();
            _unreadableResult = await _unreadable.DeleteAsync();
            _unreadableAfter = await _unreadable.TargetCatalogAsync();
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            foreach (Lane? lane in new[] { _absent, _unowned, _unreadable })
            {
                if (lane is not null)
                {
                    await lane.DisposeAsync();
                }
            }
        }

        [Test]
        public void It_reports_an_absent_target_as_already_absent() =>
            _absentResult.Should().BeOfType<DmsDataStoreDeleteResult.AlreadyAbsent>();

        [Test]
        public void It_reports_another_identity_as_unowned_and_leaves_it_untouched()
        {
            FailureOf(_unownedResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnowned,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.DeleteIdentityStep
                    )
                );
            (_unownedAfter, _identityAfter).Should().Be(((true, _unownedBefore.Oid, true), _foreignIdentity));
        }

        [Test]
        public void It_reports_a_target_without_an_identity_as_unverifiable_and_leaves_it_untouched()
        {
            FailureOf(_unreadableResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnverifiable,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.DeleteIdentityStep
                    )
                );
            (_unreadableAfter, _unreadableBefore.Exists).Should().Be((_unreadableBefore, true));
        }

        [Test]
        public void It_never_fences_or_drops()
        {
            foreach (Lane lane in new[] { _absent!, _unowned!, _unreadable! })
            {
                MembersOf(lane.Engine)
                    .Should()
                    .NotContain(nameof(IDmsTargetEngine.FenceAsync))
                    .And.NotContain(nameof(IDmsTargetEngine.DropInstanceAsync));
            }
        }

        private static (DmsProvisioningFailureCode, DmsProvisioningFailureCategory, string) FailureOf(
            DmsDataStoreDeleteResult result
        )
        {
            DmsProvisioningFailure failure = result
                .Should()
                .BeOfType<DmsDataStoreDeleteResult.Failure>()
                .Subject.Detail;
            return (failure.Code, failure.Category, failure.Diagnostic.Operation);
        }
    }

    /// <summary>
    /// A non-superuser admin that owns the target and may read it: with <c>pg_signal_backend</c> it still may not
    /// end a superuser's session (exclusivity not established); without it, it may not end another role's
    /// (a missing privilege). Either way the fence is reversed and the session survives.
    /// </summary>
    [TestFixture]
    public class Given_a_session_the_admin_may_not_end
    {
        private const string SignalRole = "pg_signal_backend";
        private const string ReadStatsRole = "pg_read_all_stats";

        private readonly List<string> _roles = [];
        private Lane? _superuserLane;
        private Lane? _unprivilegedLane;
        private TargetSession? _superuserSession;
        private TargetSession? _victimSession;
        private DmsDataStoreDeleteResult _superuserResult = null!;
        private DmsDataStoreDeleteResult _unprivilegedResult = null!;
        private Lane.Deployment _superuserDeployment = null!;
        private Lane.Deployment _unprivilegedDeployment = null!;
        private (bool Exists, uint Oid, bool AllowsConnections) _superuserBefore;
        private (bool Exists, uint Oid, bool AllowsConnections) _superuserAfter;
        private (bool Exists, uint Oid, bool AllowsConnections) _unprivilegedBefore;
        private (bool Exists, uint Oid, bool AllowsConnections) _unprivilegedAfter;
        private bool _superuserSessionAlive;
        private bool _victimSessionAlive;

        private async Task<string> AdminOwningAsync(Lane lane, params string[] memberOf)
        {
            string admin = await CreateLoginRoleAsync("deleter", memberOf);
            _roles.Add(admin);
            string quoted = PostgresqlIdentifiers.Quote(admin);
            await ExecuteOnServerAsync(
                $"ALTER DATABASE {PostgresqlIdentifiers.Quote(lane.Target.Value)} OWNER TO {quoted};"
            );
            await new TestDatabase(lane.Target.Value).ExecuteAsync(
                $"GRANT USAGE ON SCHEMA \"dms\" TO {quoted}; GRANT SELECT ON \"dms\".\"DataStoreIdentity\" TO {quoted};"
            );
            return admin;
        }

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            await RequireSuperuserOrSkipAsync();

            _superuserLane = await Lane.BuildAsync(Lane.ShapedSql(), new LaneHooks());
            await _superuserLane.SeedTargetAsync();
            string signaller = await AdminOwningAsync(_superuserLane, SignalRole, ReadStatsRole);
            _superuserSession = await TargetSession.OpenAsync(_superuserLane.Target);
            _superuserDeployment = _superuserLane.WithEngine(EngineAs(signaller));
            _superuserBefore = await _superuserLane.TargetCatalogAsync();
            _superuserResult = await _superuserDeployment.Service.DeleteAsync(
                _superuserLane.DeleteRequest,
                default
            );
            _superuserAfter = await _superuserLane.TargetCatalogAsync();
            _superuserSessionAlive = await SessionPresentAsync(_superuserSession.Session);

            _unprivilegedLane = await Lane.BuildAsync(Lane.ShapedSql(), new LaneHooks());
            await _unprivilegedLane.SeedTargetAsync();
            string reader = await AdminOwningAsync(_unprivilegedLane, ReadStatsRole);
            string victim = await CreateLoginRoleAsync("victim");
            _roles.Add(victim);
            _victimSession = await TargetSession.OpenAsync(_unprivilegedLane.Target, victim);
            _unprivilegedDeployment = _unprivilegedLane.WithEngine(EngineAs(reader));
            _unprivilegedBefore = await _unprivilegedLane.TargetCatalogAsync();
            _unprivilegedResult = await _unprivilegedDeployment.Service.DeleteAsync(
                _unprivilegedLane.DeleteRequest,
                default
            );
            _unprivilegedAfter = await _unprivilegedLane.TargetCatalogAsync();
            _victimSessionAlive = await SessionPresentAsync(_victimSession.Session);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            foreach (TargetSession? session in new[] { _superuserSession, _victimSession })
            {
                if (session is not null)
                {
                    await session.DisposeAsync();
                }
            }

            foreach (Lane? lane in new[] { _superuserLane, _unprivilegedLane })
            {
                if (lane is not null)
                {
                    await lane.DisposeAsync();
                }
            }

            foreach (string role in _roles)
            {
                await DropRoleAsync(role);
            }
        }

        [Test]
        public void It_reports_a_superuser_session_as_transient_not_established_exclusivity()
        {
            DmsProvisioningFailure failure = _superuserResult
                .Should()
                .BeOfType<DmsDataStoreDeleteResult.Failure>()
                .Subject.Detail;
            (
                failure.Code,
                failure.Category,
                failure.Diagnostic.ProviderErrorCode,
                failure.Diagnostic.Operation
            )
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.ExclusiveAccessNotEstablished,
                        DmsProvisioningFailureCategory.Transient,
                        "42501",
                        PostgresqlDeletionQueries.FenceExclusivityStep
                    )
                );
        }

        [Test]
        public void It_reports_a_missing_pg_signal_backend_as_admin_privilege_missing()
        {
            DmsProvisioningFailure failure = _unprivilegedResult
                .Should()
                .BeOfType<DmsDataStoreDeleteResult.Failure>()
                .Subject.Detail;
            (failure.Code, failure.Diagnostic.ProviderErrorCode, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.AdminPrivilegeMissing,
                        "42501",
                        PostgresqlSessionQueries.TerminateSessionStep
                    )
                );
        }

        [Test]
        public void It_leaves_both_targets_unfenced_connectable_and_in_place()
        {
            (_superuserAfter, _unprivilegedAfter).Should().Be((_superuserBefore, _unprivilegedBefore));
            (_superuserAfter.Exists, _superuserAfter.AllowsConnections, _unprivilegedAfter.AllowsConnections)
                .Should()
                .Be((true, true, true));
        }

        [Test]
        public void It_leaves_the_session_it_could_not_end_alive() =>
            (_superuserSessionAlive, _victimSessionAlive).Should().Be((true, true));

        [Test]
        public void It_reversed_the_fence_and_never_dropped()
        {
            foreach (Lane.Deployment deployment in new[] { _superuserDeployment, _unprivilegedDeployment })
            {
                MembersOf(deployment.Engine)
                    .Should()
                    .ContainInConsecutiveOrder(
                        nameof(IDmsTargetEngine.FenceAsync),
                        nameof(IDmsTargetEngine.UnfenceAsync)
                    )
                    .And.NotContain(nameof(IDmsTargetEngine.DropInstanceAsync));
            }
        }
    }

    [TestFixture]
    public class Given_the_target_changes_between_the_first_read_and_the_drop
    {
        private Lane? _rewritten;
        private Lane? _recreated;
        private DmsDataStoreDeleteResult _rewrittenResult = null!;
        private DmsDataStoreDeleteResult _recreatedResult = null!;
        private Guid _competitor;
        private Guid _rewrittenIdentity;
        private (bool Exists, uint Oid, bool AllowsConnections) _rewrittenAfter;
        private uint _recreatedOid;
        private (bool Exists, uint Oid, bool AllowsConnections) _recreatedAfter;
        private Guid _recreatedIdentity;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();

            LaneHooks writer = new();
            _rewritten = await Lane.BuildAsync(Lane.ShapedSql(), writer);
            await _rewritten.SeedTargetAsync();
            _competitor = Guid.NewGuid();
            writer.Probe = async (point, _) =>
            {
                if (point == DmsProvisioningHookPoint.BeforeFence)
                {
                    await new TestDatabase(_rewritten.Target.Value).ExecuteAsync(
                        $"UPDATE \"dms\".\"DataStoreIdentity\" SET \"SourceIdentity\" = '{_competitor:D}';"
                    );
                }
            };
            _rewrittenResult = await _rewritten.DeleteAsync();
            _rewrittenAfter = await _rewritten.TargetCatalogAsync();
            _rewrittenIdentity = await _rewritten.TargetIdentityAsync();

            LaneHooks recreator = new();
            _recreated = await Lane.BuildAsync(Lane.ShapedSql(), recreator);
            await _recreated.SeedTargetAsync();
            recreator.Probe = async (point, _) =>
            {
                if (point == DmsProvisioningHookPoint.BeforeDrop)
                {
                    await ExecuteOnServerAsync(
                        $"DROP DATABASE {PostgresqlIdentifiers.Quote(_recreated.Target.Value)} WITH (FORCE);"
                    );
                    _recreatedOid = (await _recreated.SeedTargetAsync()).Oid;
                }
            };
            _recreatedResult = await _recreated.DeleteAsync();
            _recreatedAfter = await _recreated.TargetCatalogAsync();
            _recreatedIdentity = await _recreated.TargetIdentityAsync();
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            foreach (Lane? lane in new[] { _rewritten, _recreated })
            {
                if (lane is not null)
                {
                    await lane.DisposeAsync();
                }
            }
        }

        [Test]
        public void It_refuses_a_target_a_competing_writer_claimed_and_reverses_the_fence()
        {
            DmsProvisioningFailure failure = _rewrittenResult
                .Should()
                .BeOfType<DmsDataStoreDeleteResult.Failure>()
                .Subject.Detail;
            (failure.Code, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnowned,
                        DmsDataStoreProvisioningService.VerifyFencedStep
                    )
                );
            (_rewrittenAfter.AllowsConnections, _rewrittenIdentity).Should().Be((true, _competitor));
            MembersOf(_rewritten!.Engine).Should().NotContain(nameof(IDmsTargetEngine.DropInstanceAsync));
        }

        [Test]
        public void It_refuses_an_instance_recreated_under_the_fence_and_leaves_it_untouched()
        {
            DmsProvisioningFailure failure = _recreatedResult
                .Should()
                .BeOfType<DmsDataStoreDeleteResult.Failure>()
                .Subject.Detail;
            (failure.Code, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.InstanceChanged,
                        PostgresqlDeletionQueries.DropTargetInstanceStep
                    )
                );
            (_recreatedAfter, _recreatedIdentity)
                .Should()
                .Be(((true, _recreatedOid, true), _recreated!.Request.ExpectedSourceIdentity));
            MembersOf(_recreated.Engine).Should().NotContain(nameof(IDmsTargetEngine.UnfenceAsync));
        }
    }

    /// <summary>
    /// Two deletes of one target: the second waits on the target lock while the first is paused before its drop,
    /// then observes the result. With a short acquisition budget instead, the contender gives up (no automatic
    /// takeover of a live holder) and sends nothing, and the paused worker completes normally.
    /// </summary>
    [TestFixture]
    public class Given_two_workers_delete_one_target
    {
        private Lane? _serialized;
        private Lane? _contended;
        private DmsDataStoreDeleteResult _first = null!;
        private DmsDataStoreDeleteResult _queued = null!;
        private bool _queuedWaited;
        private DmsDataStoreDeleteResult _pausedResult = null!;
        private DmsDataStoreDeleteResult _timedOut = null!;
        private DmsDataStoreDeleteResult _later = null!;
        private Lane.Deployment _queuedDeployment = null!;
        private Lane.Deployment _timedOutDeployment = null!;
        private (bool Exists, uint Oid, bool AllowsConnections) _whileContended;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();

            Pause serialize = new(DmsProvisioningHookPoint.BeforeDrop);
            LaneHooks serializeHooks = new() { Probe = serialize.ProbeAsync };
            _serialized = await Lane.BuildAsync(Lane.ShapedSql(), serializeHooks);
            await _serialized.SeedTargetAsync();
            _queuedDeployment = _serialized.WithEngine(Engine(lockAcquireTimeout: _bound));
            Task<DmsDataStoreDeleteResult> first = _serialized.DeleteAsync();
            await serialize.ReachedAsync(first);
            Task<DmsDataStoreDeleteResult> queued = _queuedDeployment.Service.DeleteAsync(
                _serialized.DeleteRequest,
                default
            );
            _queuedWaited = await HolderWaitingAsync();
            serialize.Release.SetResult();
            _first = await first;
            _queued = await queued;

            Pause contend = new(DmsProvisioningHookPoint.BeforeDrop);
            LaneHooks contendHooks = new() { Probe = contend.ProbeAsync };
            _contended = await Lane.BuildAsync(Lane.ShapedSql(), contendHooks);
            await _contended.SeedTargetAsync();
            _timedOutDeployment = _contended.WithEngine(Engine(lockAcquireTimeout: TimeSpan.FromSeconds(2)));
            Task<DmsDataStoreDeleteResult> paused = _contended.DeleteAsync();
            await contend.ReachedAsync(paused);
            _timedOut = await _timedOutDeployment.Service.DeleteAsync(_contended.DeleteRequest, default);
            _whileContended = await _contended.TargetCatalogAsync();
            contend.Release.SetResult();
            _pausedResult = await paused;
            _later = await _timedOutDeployment.Service.DeleteAsync(_contended.DeleteRequest, default);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            foreach (Lane? lane in new[] { _serialized, _contended })
            {
                if (lane is not null)
                {
                    await lane.DisposeAsync();
                }
            }
        }

        [Test]
        public void It_queues_the_second_delete_behind_the_first() => _queuedWaited.Should().BeTrue();

        [Test]
        public void It_lets_the_second_observe_what_the_first_did()
        {
            _first.Should().BeOfType<DmsDataStoreDeleteResult.Dropped>();
            _queued.Should().BeOfType<DmsDataStoreDeleteResult.AlreadyAbsent>();
            MembersOf(_queuedDeployment.Engine)
                .Should()
                .Equal(
                    nameof(IDmsTargetEngine.AcquireTargetLockAsync),
                    nameof(IDmsTargetEngine.ReadServerFactsAsync),
                    nameof(IDmsTargetEngine.InspectAsync)
                );
        }

        [Test]
        public void It_never_takes_over_a_live_holder()
        {
            _timedOut
                .Should()
                .BeOfType<DmsDataStoreDeleteResult.Failure>()
                .Which.Detail.Code.Should()
                .Be(DmsProvisioningFailureCode.LockTimeout);
            (_whileContended.Exists, _whileContended.AllowsConnections)
                .Should()
                .Be((true, false), "the paused worker still holds its fence");
        }

        [Test]
        public void It_lets_the_paused_worker_complete_and_a_later_contender_find_nothing()
        {
            _pausedResult.Should().BeOfType<DmsDataStoreDeleteResult.Dropped>();
            _later.Should().BeOfType<DmsDataStoreDeleteResult.AlreadyAbsent>();
            MembersOf(_timedOutDeployment.Engine)
                .Should()
                .Equal(
                    nameof(IDmsTargetEngine.AcquireTargetLockAsync),
                    nameof(IDmsTargetEngine.AcquireTargetLockAsync),
                    nameof(IDmsTargetEngine.ReadServerFactsAsync),
                    nameof(IDmsTargetEngine.InspectAsync)
                );
        }
    }

    /// <summary>
    /// Takeover (round-3 finding 2), with a full create by the second worker (<c>psql</c>): worker A is paused at
    /// a hook with a heartbeat too slow to notice anything; A's holder session is killed; worker B acquires the
    /// lock, replaces the target with the same record and writes a sentinel; A resumes and its next mutation runs
    /// on the dead holder, so nothing it sends reaches the server and B's target is untouched.
    /// </summary>
    [TestFixture]
    public class Given_a_paused_worker_loses_its_holder_and_another_replaces_the_target
    {
        private readonly List<Lane> _lanes = [];

        private sealed record Takeover(
            Lane Lane,
            object? Result,
            uint ReplacedOid,
            (bool Exists, uint Oid, bool AllowsConnections) After,
            Guid Identity,
            int Sentinel
        );

        private Takeover _beforeFence = null!;
        private Takeover _beforeDrop = null!;
        private Takeover _beforeRename = null!;
        private Takeover _afterRestore = null!;

        /// <summary>
        /// Runs A until <paramref name="point"/>, kills its holder, lets <paramref name="replace"/> run as B (which
        /// returns the oid B left under the target), then resumes A and gathers the evidence.
        /// </summary>
        private async Task<Takeover> RunAsync(
            DmsProvisioningHookPoint point,
            Func<Lane, Task<object>> runA,
            Func<Lane, Lane.Deployment, Task> replace,
            bool seedTarget
        )
        {
            Pause pause = new(point);
            Lane lane = await Lane.BuildAsync(
                Lane.ShapedSql(),
                new LaneHooks { Probe = pause.ProbeAsync },
                lockHeartbeatInterval: _quietHeartbeat
            );
            _lanes.Add(lane);
            if (seedTarget)
            {
                await lane.SeedTargetAsync();
            }

            Task<object> a = runA(lane);
            DmsOperationId operation = await pause.ReachedAsync(a);
            await KillHolderAsync(operation);

            Lane.Deployment b = lane.WithEngine(Engine());
            await replace(lane, b);
            await WriteSentinelAsync(lane.Target);
            uint replacedOid = (await lane.TargetCatalogAsync()).Oid;

            pause.Release.SetResult();
            object result = await a;
            return new Takeover(
                lane,
                result,
                replacedOid,
                await lane.TargetCatalogAsync(),
                await lane.TargetIdentityAsync(),
                await SentinelAsync(lane.Target)
            );
        }

        private static async Task DeleteAndRecreateAsync(Lane lane, Lane.Deployment b)
        {
            (await b.Service.DeleteAsync(lane.DeleteRequest, default))
                .Should()
                .BeOfType<DmsDataStoreDeleteResult.Dropped>();
            (await b.Service.CreateAsync(lane.Request, default))
                .Should()
                .BeOfType<DmsDataStoreCreateResult.Created>();
        }

        private static async Task CreateAsync(Lane lane, Lane.Deployment b) =>
            (await b.Service.CreateAsync(lane.Request, default))
                .Should()
                .BeOfType<DmsDataStoreCreateResult.Created>();

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            RequirePsqlOrSkip();

            _beforeFence = await RunAsync(
                DmsProvisioningHookPoint.BeforeFence,
                async lane => await lane.DeleteAsync(),
                DeleteAndRecreateAsync,
                seedTarget: true
            );

            // A's fence stands when it pauses before the drop: B can act only after the operator procedure
            // (7.6) reverses it, as the takeover requires.
            _beforeDrop = await RunAsync(
                DmsProvisioningHookPoint.BeforeDrop,
                async lane => await lane.DeleteAsync(),
                async (lane, b) =>
                {
                    await ExecuteOnServerAsync(
                        $"ALTER DATABASE {PostgresqlIdentifiers.Quote(lane.Target.Value)} WITH ALLOW_CONNECTIONS true;"
                    );
                    await DeleteAndRecreateAsync(lane, b);
                },
                seedTarget: true
            );

            _beforeRename = await RunAsync(
                DmsProvisioningHookPoint.BeforeRename,
                async lane => await lane.CreateAsync(),
                CreateAsync,
                seedTarget: false
            );

            _afterRestore = await RunAsync(
                DmsProvisioningHookPoint.AfterRestoreStarted,
                async lane => await lane.CreateAsync(),
                CreateAsync,
                seedTarget: false
            );
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            foreach (Lane lane in _lanes)
            {
                await lane.DisposeAsync();
            }
        }

        [Test]
        public void It_leaves_the_replacement_untouched_in_every_case()
        {
            foreach (Takeover takeover in new[] { _beforeFence, _beforeDrop, _beforeRename, _afterRestore })
            {
                (takeover.After, takeover.Identity, takeover.Sentinel)
                    .Should()
                    .Be(
                        (
                            (true, takeover.ReplacedOid, true),
                            takeover.Lane.Request.ExpectedSourceIdentity,
                            1438
                        )
                    );
            }
        }

        [Test]
        public void It_ends_a_paused_delete_as_lock_lost_without_a_further_mutation()
        {
            foreach (Takeover takeover in new[] { _beforeFence, _beforeDrop })
            {
                takeover
                    .Result.Should()
                    .BeOfType<DmsDataStoreDeleteResult.Failure>()
                    .Which.Detail.Code.Should()
                    .Be(DmsProvisioningFailureCode.LockLost);
                MembersOf(takeover.Lane.Engine).Should().NotContain(nameof(IDmsTargetEngine.UnfenceAsync));
            }

            FenceAttemptsOf(_beforeFence).Should().Be(1);
            MembersOf(_beforeFence.Lane.Engine)
                .Should()
                .NotContain(nameof(IDmsTargetEngine.DropInstanceAsync));
            MembersOf(_beforeDrop.Lane.Engine)
                .Count(member => member == nameof(IDmsTargetEngine.DropInstanceAsync))
                .Should()
                .Be(1);
        }

        [Test]
        public void It_ends_a_create_paused_before_the_rename_as_lock_lost_and_drops_its_staging()
        {
            _beforeRename
                .Result.Should()
                .BeOfType<DmsDataStoreCreateResult.Failure>()
                .Which.Detail.Code.Should()
                .Be(DmsProvisioningFailureCode.LockLost);
            MembersOf(_beforeRename.Lane.Engine)
                .Should()
                .NotContain(nameof(IDmsTargetEngine.AssignMarkerAsync))
                .And.NotContain(nameof(IDmsTargetEngine.DropInstanceAsync));
        }

        [Test]
        public async Task It_drops_the_staging_database_of_every_create_and_never_publishes_it()
        {
            foreach (Takeover takeover in new[] { _beforeRename, _afterRestore })
            {
                takeover.Result.Should().NotBeOfType<DmsDataStoreCreateResult.Created>();
                (
                    await ScalarOnServerAsync<bool>(
                        "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_database d WHERE d.datname = @name);",
                        ("name", takeover.Lane.Operation.StagingDatabaseName.Value)
                    )
                )
                    .Should()
                    .BeFalse();
                takeover
                    .Lane.TargetMutations.Select(call => call.Member)
                    .Should()
                    .NotContain(nameof(IDmsTargetEngine.AssignMarkerAsync))
                    .And.NotContain(nameof(IDmsTargetEngine.DropInstanceAsync));
            }
        }

        private static int FenceAttemptsOf(Takeover takeover) =>
            MembersOf(takeover.Lane.Engine).Count(member => member == nameof(IDmsTargetEngine.FenceAsync));
    }

    /// <summary>A create paused before its rename holds the lock; a delete of the same target waits, then drops what the create published.</summary>
    [TestFixture]
    public class Given_a_delete_waits_for_a_create_of_the_same_target
    {
        private Lane? _lane;
        private DmsDataStoreCreateResult _created = null!;
        private DmsDataStoreDeleteResult _deleted = null!;
        private bool _deleteWaited;
        private (bool Exists, uint Oid, bool AllowsConnections) _after;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            RequirePsqlOrSkip();
            Pause pause = new(DmsProvisioningHookPoint.BeforeRename);
            _lane = await Lane.BuildAsync(Lane.ShapedSql(), new LaneHooks { Probe = pause.ProbeAsync });
            Lane.Deployment deleter = _lane.WithEngine(Engine(lockAcquireTimeout: _bound));
            Task<DmsDataStoreCreateResult> create = _lane.CreateAsync();
            await pause.ReachedAsync(create);
            Task<DmsDataStoreDeleteResult> delete = deleter.Service.DeleteAsync(_lane.DeleteRequest, default);
            _deleteWaited = await HolderWaitingAsync();
            pause.Release.SetResult();
            _created = await create;
            _deleted = await delete;
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
        public void It_serializes_the_delete_behind_the_create() => _deleteWaited.Should().BeTrue();

        [Test]
        public void It_drops_what_the_create_published() =>
            (_created, _deleted, _after.Exists)
                .Should()
                .Be(
                    (
                        new DmsDataStoreCreateResult.Created(
                            _lane!.Request.PinnedArtifact,
                            DmsCleanupReport.Clean
                        ),
                        new DmsDataStoreDeleteResult.Dropped(DmsCleanupReport.Clean),
                        false
                    )
                );
    }
}
