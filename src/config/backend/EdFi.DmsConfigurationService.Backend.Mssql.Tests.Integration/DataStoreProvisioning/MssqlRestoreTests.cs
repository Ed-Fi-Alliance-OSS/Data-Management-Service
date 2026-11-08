// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using static EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning.MssqlProvisioningTestServer;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning;

/// <summary>
/// Step 4.5 against a live server, engine level: the create reserves the staging name on a real holder and sends
/// nothing; a valid backup restores into an online staging database carrying the operation's primary file, the
/// staged file is gone afterwards, and the drop removes it; a restore the server rejects, one whose backup the planner
/// refuses, and one whose staged file the server cannot see are definite failures that create nothing; a restore
/// whose work session is killed, cancelled, or bounded while the database is restoring is resolved by killing and
/// proving the session gone and reading the name (<c>RESTORING</c> with the tag: not completed, named after what ended
/// it); a staged file replaced after the restore is a staging integrity violation; and the staging drop removes a
/// restoring or online database only when it is the operation's, refusing another database under the name. The
/// tests that restore need a staging directory shared with the server and skip otherwise; the deterministic
/// mid-restore fixtures replace the statement with the planned one plus <c>NORECOVERY</c> followed by a marked
/// <c>WAITFOR</c>, so the database is <c>RESTORING</c> whenever the test acts.
/// </summary>
[Category(Category)]
public class MssqlRestoreTests
{
    private static readonly ProtectedDatabaseNames _protectedNames = ProtectedDatabaseNames.Create(
        "edfi_configurationservice",
        []
    );

    private static DmsTargetDatabaseName TargetName(string purpose) =>
        DmsTargetDatabaseName.TryCreate(UniqueName(purpose), _protectedNames, out DmsTargetDatabaseName name)
        == DmsTargetNameValidation.Valid
            ? name
            : throw new InvalidOperationException("The candidate does not satisfy the target name rules.");

    /// <summary>The planned restore left <c>RESTORING</c>, then a long wait the test interrupts while the database is restoring.</summary>
    private static Func<MssqlRestorePlan, string, string> RestoringThenWaiting(string marker) =>
        (plan, serverPath) =>
            plan.RestoreStatement(serverPath)
                .Replace("STATS = 10;", "NORECOVERY, STATS = 10;", StringComparison.Ordinal)
            + $"\nWAITFOR DELAY '00:02:00'; -- {marker}";

    /// <summary>Waits until the operation's work session is in the <c>WAITFOR</c> that follows the restore; returns its id, or 0.</summary>
    private static async Task<int> WaitForWaitingWorkSessionAsync(DmsOperationId operation)
    {
        int sessionId = 0;
        bool waiting = await WaitUntilAsync(
            async () =>
            {
                sessionId = await SessionIdNamedAsync(operation.RestoreApplicationName);
                return sessionId != 0 && await WaitTypeOfAsync(sessionId) == "WAITFOR";
            },
            TimeSpan.FromSeconds(60)
        );
        return waiting ? sessionId : 0;
    }

    private static Task<string> ServerPropertyAsync(string property) =>
        ScalarOnServerAsync<string>($"SELECT CONVERT(nvarchar(4000), SERVERPROPERTY('{property}'));");

    /// <summary>One restore by the engine into a fresh operation's staging name, with the database dropped afterwards.</summary>
    private sealed class RestoreRun : IAsyncDisposable
    {
        private readonly string _directory;

        public RestoreRun()
        {
            Operation = DmsOperationId.New();
            _directory = Path.Combine(Path.GetTempPath(), "dms1438-restore-" + Operation.Value);
            Directory.CreateDirectory(_directory);
        }

        public DmsOperationId Operation { get; }

        public DmsDatabaseName Stage => Operation.StagingDatabaseName;

        public DmsTargetDatabaseName Target { get; } = TargetName("target");

        public DmsInstanceIdentity.MssqlReservation Reservation =>
            new(MssqlRestorePlanner.PrimaryFileNameFor(Operation));

        public Task<DmsCommandOutcome<DmsInstanceIdentity>> RestoreAsync(
            MssqlDmsTargetEngine engine,
            byte[] artifact,
            CancellationToken cancellationToken = default
        ) =>
            engine.RestoreIntoStagingAsync(
                Stage,
                Target,
                ShapedBackup.Extracted(_directory, artifact),
                Operation,
                cancellationToken
            );

        public async ValueTask DisposeAsync()
        {
            await new TestDatabase(Stage.Value).DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }

    [TestFixture]
    public class Given_the_staging_name_is_reserved_on_a_real_holder
    {
        private DmsCommandOutcome<DmsInstanceIdentity> _outcome = null!;
        private Exception? _otherStaging;
        private DmsOperationId _operation;
        private DmsLiveTargetState _stagingAfter = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _operation = DmsOperationId.New();
            MssqlDmsTargetEngine engine = Engine();
            DmsTargetLockResult acquired = await engine.AcquireTargetLockAsync(
                TargetName("reserve"),
                _operation,
                default
            );
            await using IDmsTargetLockHolder holder = ((DmsTargetLockResult.Acquired)acquired).Holder;
            DmsProvisionClaim claim = new(_operation, Guid.NewGuid());
            _outcome = await engine.CreateStagingAsync(
                holder,
                _operation.StagingDatabaseName,
                claim,
                default
            );
            _otherStaging = await Record.ExceptionAsync(() =>
                engine.CreateStagingAsync(holder, DmsOperationId.New().StagingDatabaseName, claim, default)
            );
            _stagingAfter = await engine.InspectAsync(_operation.StagingDatabaseName, default);
        }

        [Test]
        public void It_returns_the_reservation_of_the_operations_primary_file() =>
            _outcome
                .Should()
                .Be(
                    new DmsCommandOutcome<DmsInstanceIdentity>.Succeeded(
                        new DmsInstanceIdentity.MssqlReservation(
                            MssqlRestorePlanner.PrimaryFileNameFor(_operation)
                        )
                    )
                );

        [Test]
        public void It_creates_nothing_on_the_server() =>
            _stagingAfter.Should().Be(new DmsLiveTargetState.Absent());

        [Test]
        public void It_refuses_a_staging_name_that_is_not_the_claims_operations() =>
            _otherStaging.Should().BeOfType<ArgumentException>().Which.ParamName.Should().Be("staging");
    }

    [TestFixture]
    public class Given_a_valid_backup_is_restored
    {
        private RestoreRun? _run;
        private DmsCommandOutcome<DmsInstanceIdentity> _outcome = null!;
        private DmsLiveTargetState _stagingAfterRestore = null!;
        private string _dataPath = string.Empty;
        private string[] _stagedFilesAfter = [];
        private bool _workSessionGone;
        private DmsCommandOutcome _dropped = null!;
        private DmsLiveTargetState _stagingAfterDrop = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireSharedStagingOrSkip();
            _run = new RestoreRun();
            _dataPath = await ServerPropertyAsync("InstanceDefaultDataPath");
            MssqlDmsTargetEngine engine = Engine();
            _outcome = await _run.RestoreAsync(engine, await ShapedBackup.CreateAsync());
            _stagingAfterRestore = await engine.InspectAsync(_run.Stage, default);
            _stagedFilesAfter = ShapedBackup.StagedFilesOf(_run.Operation);
            _workSessionGone = await WaitUntilAsync(
                async () => await SessionCountNamedAsync(_run.Operation.RestoreApplicationName) == 0,
                TimeSpan.FromSeconds(20)
            );
            DmsInstanceIdentity restored = ((DmsCommandOutcome<DmsInstanceIdentity>.Succeeded)_outcome).Value;
            _dropped = await engine.DropStagingAsync(_run.Stage, restored, default);
            _stagingAfterDrop = await engine.InspectAsync(_run.Stage, default);
        }

        [OneTimeTearDown]
        public async Task TearDown()
        {
            if (_run is not null)
            {
                await _run.DisposeAsync();
            }
        }

        [Test]
        public void It_returns_the_instance_carrying_the_operations_primary_file() =>
            _outcome
                .Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Succeeded>()
                .Which.Value.Should()
                .BeOfType<DmsInstanceIdentity.Mssql>()
                .Which.PrimaryFileTag.Should()
                .Be(MssqlServerPaths.Join(_dataPath, _run!.Reservation.PrimaryFileName));

        [Test]
        public void It_leaves_the_staging_database_online_as_that_instance() =>
            _stagingAfterRestore
                .Should()
                .Be(
                    new DmsLiveTargetState.Present(
                        ((DmsCommandOutcome<DmsInstanceIdentity>.Succeeded)_outcome).Value,
                        DmsDatabaseState.Online
                    )
                );

        [Test]
        public void It_deletes_the_staged_file_and_reports_no_residue()
        {
            _stagedFilesAfter.Should().BeEmpty();
            _outcome.Residue.Should().BeEmpty();
        }

        [Test]
        public void It_closes_the_work_session() => _workSessionGone.Should().BeTrue();

        [Test]
        public void It_drops_the_restored_instance() =>
            (_dropped, _stagingAfterDrop)
                .Should()
                .Be((new DmsCommandOutcome.Succeeded(), new DmsLiveTargetState.Absent()));
    }

    [TestFixture]
    public class Given_the_server_rejects_the_restore
    {
        private RestoreRun? _run;
        private DmsCommandOutcome<DmsInstanceIdentity> _outcome = null!;
        private DmsLiveTargetState _stagingAfter = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireSharedStagingOrSkip();
            _run = new RestoreRun();
            MssqlDmsTargetEngine engine = new(
                AdminConnection(),
                Settings(),
                TimeProvider.System,
                NullLogger.Instance
            )
            {
                // The planned statement against a device that does not exist: refused in the restore's own response.
                RestoreStatement = (plan, serverPath) => plan.RestoreStatement(serverPath + ".missing"),
            };
            _outcome = await _run.RestoreAsync(engine, await ShapedBackup.CreateAsync());
            _stagingAfter = await engine.InspectAsync(_run.Stage, default);
        }

        [OneTimeTearDown]
        public async Task TearDown()
        {
            if (_run is not null)
            {
                await _run.DisposeAsync();
            }
        }

        [Test]
        public void It_reports_a_definite_rejection_with_the_servers_number() =>
            _outcome
                .Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Failed>()
                .Which.Failure.Should()
                .BeEquivalentTo(
                    new
                    {
                        Class = DmsCommandFailureClass.Rejected,
                        Diagnostic = new
                        {
                            Operation = MssqlDmsTargetEngine.RestoreStep,
                            ProviderErrorCode = "3201",
                        },
                    }
                );

        [Test]
        public void It_creates_nothing_and_deletes_the_staged_file()
        {
            _stagingAfter.Should().Be(new DmsLiveTargetState.Absent());
            ShapedBackup.StagedFilesOf(_run!.Operation).Should().BeEmpty();
        }
    }

    /// <summary>
    /// A statement that completes but leaves under the staging name a database the plan did not place (out-of-band
    /// interference, stood in for by a plain <c>CREATE DATABASE</c>): nothing proves what happened, so the outcome is
    /// unknown without a session and the database is left alone.
    /// </summary>
    [TestFixture]
    public class Given_the_name_holds_a_database_the_restore_did_not_plan
    {
        private RestoreRun? _run;
        private DmsCommandOutcome<DmsInstanceIdentity> _outcome = null!;
        private DmsLiveTargetState _stagingAfter = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireSharedStagingOrSkip();
            _run = new RestoreRun();
            MssqlDmsTargetEngine engine = new(
                AdminConnection(),
                Settings(),
                TimeProvider.System,
                NullLogger.Instance
            )
            {
                RestoreStatement = (plan, _) =>
                    $"CREATE DATABASE {MssqlIdentifiers.Quote(plan.Staging.Value)};",
            };
            _outcome = await _run.RestoreAsync(engine, await ShapedBackup.CreateAsync());
            _stagingAfter = await engine.InspectAsync(_run.Stage, default);
        }

        [OneTimeTearDown]
        public async Task TearDown()
        {
            if (_run is not null)
            {
                await _run.DisposeAsync();
            }
        }

        [Test]
        public void It_reports_an_unknown_outcome_that_names_nothing_to_terminate() =>
            _outcome
                .Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Unknown>()
                .Which.Should()
                .BeEquivalentTo(new { Session = (DmsServerSessionId?)null, Quiescent = false });

        [Test]
        public void It_leaves_the_database_under_the_name_untouched() =>
            _stagingAfter
                .Should()
                .BeOfType<DmsLiveTargetState.Present>()
                .Which.Instance.Should()
                .Match<DmsInstanceIdentity>(instance => !_run!.Reservation.IsRealizedBy(instance));
    }

    [TestFixture]
    public class Given_the_planner_refuses_the_backup
    {
        private RestoreRun? _run;
        private DmsCommandOutcome<DmsInstanceIdentity> _outcome = null!;
        private DmsLiveTargetState _stagingAfter = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireSharedStagingOrSkip();
            _run = new RestoreRun();
            MssqlDmsTargetEngine engine = Engine();
            _outcome = await _run.RestoreAsync(engine, await ShapedBackup.CreateAsync(appendSecondSet: true));
            _stagingAfter = await engine.InspectAsync(_run.Stage, default);
        }

        [OneTimeTearDown]
        public async Task TearDown()
        {
            if (_run is not null)
            {
                await _run.DisposeAsync();
            }
        }

        [Test]
        public void It_reports_the_broken_rule() =>
            _outcome
                .Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Failed>()
                .Which.Failure.Diagnostic.Operation.Should()
                .Be(DmsEngineOperations.PlanRestorePrefix + nameof(MssqlBackupDefect.BackupSetCount));

        [Test]
        public void It_creates_nothing_and_deletes_the_staged_file()
        {
            _stagingAfter.Should().Be(new DmsLiveTargetState.Absent());
            ShapedBackup.StagedFilesOf(_run!.Operation).Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_the_server_cannot_see_the_staged_file
    {
        private string _stagingPath = string.Empty;
        private DmsOperationId _operation;
        private DmsCommandOutcome<DmsInstanceIdentity> _outcome = null!;
        private DmsLiveTargetState _stagingAfter = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _operation = DmsOperationId.New();
            _stagingPath = Path.Combine(Path.GetTempPath(), "dms1438-unseen-" + _operation.Value);
            Directory.CreateDirectory(Path.Combine(_stagingPath, "artifact"));
            string dataPath = await ServerPropertyAsync("InstanceDefaultDataPath");
            string serverPath = dataPath.StartsWith('/')
                ? "/var/opt/dms1438-missing-" + _operation.Value
                : @"C:\dms1438-missing-" + _operation.Value;
            MssqlDmsTargetEngine engine = new(
                AdminConnection(),
                new MssqlDmsEngineSettings(
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromMinutes(5),
                    new MssqlStagingSettings(
                        _stagingPath,
                        serverPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
                        3,
                        TimeSpan.FromSeconds(30)
                    )
                ),
                TimeProvider.System,
                NullLogger.Instance
            );
            _outcome = await engine.RestoreIntoStagingAsync(
                _operation.StagingDatabaseName,
                TargetName("unseen"),
                ShapedBackup.Extracted(
                    Path.Combine(_stagingPath, "artifact"),
                    RandomNumberGenerator.GetBytes(4096)
                ),
                _operation,
                default
            );
            _stagingAfter = await engine.InspectAsync(_operation.StagingDatabaseName, default);
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            if (Directory.Exists(_stagingPath))
            {
                Directory.Delete(_stagingPath, recursive: true);
            }
        }

        [Test]
        public void It_reports_a_staging_failure_with_the_servers_device_error() =>
            _outcome
                .Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Failed>()
                .Which.Failure.Diagnostic.Should()
                .BeEquivalentTo(
                    new { Operation = MssqlBackupStager.NotVisibleStep, ProviderErrorCode = "3201" }
                );

        [Test]
        public void It_creates_nothing_and_deletes_the_staged_file()
        {
            _stagingAfter.Should().Be(new DmsLiveTargetState.Absent());
            Directory.GetFiles(_stagingPath, "dms-provision-*.bak").Should().BeEmpty();
        }
    }

    /// <summary>
    /// The three ways a restore ends in doubt while the database is restoring: the work session killed from outside
    /// (connection loss), the caller's cancellation, and the restore bound. Each is resolved to "not completed" by
    /// the engine itself, named after what ended it, with the session gone and the restoring database left for the
    /// drop, which then removes it by the reservation.
    /// </summary>
    [TestFixture]
    public class Given_the_restore_ends_in_doubt_while_restoring
    {
        private sealed record Ending(
            RestoreRun Run,
            bool Interrupted,
            DmsCommandOutcome<DmsInstanceIdentity> Outcome,
            TimeSpan Elapsed,
            bool SessionGone,
            DmsLiveTargetState StagingBeforeDrop,
            DmsCommandOutcome Dropped,
            DmsLiveTargetState StagingAfterDrop
        );

        private readonly List<Ending> _endings = [];

        private static async Task<Ending> EndAsync(
            Func<RestoreRun, Task<DmsCommandOutcome<DmsInstanceIdentity>>> restore,
            Func<RestoreRun, Task<bool>> interrupt
        )
        {
            RestoreRun run = new();
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            Task<DmsCommandOutcome<DmsInstanceIdentity>> restoring = restore(run);
            bool interrupted = await interrupt(run);
            DmsCommandOutcome<DmsInstanceIdentity> outcome = await restoring;
            TimeSpan elapsed = clock.Elapsed;
            bool sessionGone = await SessionCountNamedAsync(run.Operation.RestoreApplicationName) == 0;
            MssqlDmsTargetEngine side = Engine();
            DmsLiveTargetState before = await side.InspectAsync(run.Stage, default);
            DmsCommandOutcome dropped = await side.DropStagingAsync(run.Stage, run.Reservation, default);
            DmsLiveTargetState after = await side.InspectAsync(run.Stage, default);
            return new Ending(run, interrupted, outcome, elapsed, sessionGone, before, dropped, after);
        }

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireSharedStagingOrSkip();
            byte[] backup = await ShapedBackup.CreateAsync();

            // Connection loss: the work session is killed from outside while it waits.
            string killMarker =
                "dms1438_restore_kill_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
            MssqlDmsTargetEngine killed = new(
                AdminConnection(),
                Settings(),
                TimeProvider.System,
                NullLogger.Instance
            )
            {
                RestoreStatement = RestoringThenWaiting(killMarker),
            };
            _endings.Add(
                await EndAsync(
                    run => run.RestoreAsync(killed, backup),
                    async run =>
                    {
                        int sessionId = await WaitForWaitingWorkSessionAsync(run.Operation);
                        if (sessionId == 0)
                        {
                            return false;
                        }

                        await KillAsync(sessionId);
                        return true;
                    }
                )
            );

            // The caller's cancellation while it waits.
            using CancellationTokenSource cancellation = new();
            string cancelMarker =
                "dms1438_restore_cancel_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
            MssqlDmsTargetEngine cancelled = new(
                AdminConnection(),
                Settings(),
                TimeProvider.System,
                NullLogger.Instance
            )
            {
                RestoreStatement = RestoringThenWaiting(cancelMarker),
            };
            _endings.Add(
                await EndAsync(
                    run => run.RestoreAsync(cancelled, backup, cancellation.Token),
                    async run =>
                    {
                        bool waiting = await WaitForWaitingWorkSessionAsync(run.Operation) != 0;
                        await cancellation.CancelAsync();
                        return waiting;
                    }
                )
            );

            // The restore bound passes while it waits; nothing outside acts.
            string boundMarker =
                "dms1438_restore_bound_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
            MssqlDmsTargetEngine bounded = new(
                AdminConnection(),
                Settings(restoreTimeout: TimeSpan.FromSeconds(10)),
                TimeProvider.System,
                NullLogger.Instance
            )
            {
                RestoreStatement = RestoringThenWaiting(boundMarker),
            };
            _endings.Add(
                await EndAsync(
                    run => run.RestoreAsync(bounded, backup),
                    async run => await WaitForWaitingWorkSessionAsync(run.Operation) != 0
                )
            );
        }

        [OneTimeTearDown]
        public async Task TearDown()
        {
            foreach (Ending ending in _endings)
            {
                await ending.Run.DisposeAsync();
            }
        }

        [Test]
        public void It_acted_while_the_database_was_restoring() =>
            _endings.Select(ending => ending.Interrupted).Should().Equal(true, true, true);

        [Test]
        public void It_names_what_ended_each_restore() =>
            _endings
                .Select(ending =>
                    ending.Outcome is DmsCommandOutcome<DmsInstanceIdentity>.Failed failed
                        ? (failed.Failure.Class, failed.Failure.Diagnostic.Operation)
                        : (DmsCommandFailureClass.DoesNotExist, "not a failure")
                )
                .Should()
                .Equal(
                    (DmsCommandFailureClass.Rejected, DmsEngineOperations.RestoreRolledBack),
                    (DmsCommandFailureClass.Rejected, DmsEngineOperations.RestoreCancelled),
                    (DmsCommandFailureClass.Rejected, DmsEngineOperations.RestoreTimedOut)
                );

        [Test]
        public void It_ends_well_before_the_two_minute_wait() =>
            _endings.Should().OnlyContain(ending => ending.Elapsed < TimeSpan.FromSeconds(90));

        [Test]
        public void It_leaves_no_work_session() =>
            _endings.Should().OnlyContain(ending => ending.SessionGone);

        [Test]
        public void It_leaves_the_operations_database_restoring_for_the_drop() =>
            _endings
                .Should()
                .OnlyContain(ending =>
                    ending.StagingBeforeDrop is DmsLiveTargetState.Fenced
                    && ending.Run.Reservation.IsRealizedBy(
                        ((DmsLiveTargetState.Fenced)ending.StagingBeforeDrop).Instance
                    )
                );

        [Test]
        public void It_is_dropped_by_the_reservation() =>
            _endings
                .Select(ending =>
                    (
                        ending.Dropped is DmsCommandOutcome.Succeeded,
                        ending.StagingAfterDrop is DmsLiveTargetState.Absent
                    )
                )
                .Should()
                .Equal((true, true), (true, true), (true, true));

        [Test]
        public void It_deletes_every_staged_file() =>
            _endings
                .Should()
                .OnlyContain(ending => ShapedBackup.StagedFilesOf(ending.Run.Operation).Length == 0);
    }

    /// <summary>
    /// The caller cancels while the database is restoring and the kill that should end the work session fails (the
    /// guarded batch's <c>KILL</c> replaced by <c>PRINT</c>, so the session stays listed until the termination bound):
    /// the restore returns within that bound instead of waiting for the statement, with an unknown outcome that names
    /// the work session and is not quiescent, and nothing is dropped. Teardown kills the surviving session.
    /// </summary>
    [TestFixture]
    public class Given_the_kill_fails_after_the_caller_cancels
    {
        private static readonly TimeSpan _terminationBound = TimeSpan.FromSeconds(3);

        private RestoreRun? _run;
        private int _sessionId;
        private bool _completed;
        private TimeSpan _elapsed;
        private DmsCommandOutcome<DmsInstanceIdentity>? _outcome;
        private DmsLiveTargetState _stagingAfter = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireSharedStagingOrSkip();
            _run = new RestoreRun();
            string marker =
                "dms1438_restore_unkillable_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
            MssqlDmsTargetEngine engine = new(
                AdminConnection(),
                Settings(sessionTerminationTimeout: _terminationBound),
                TimeProvider.System,
                NullLogger.Instance
            )
            {
                RestoreStatement = RestoringThenWaiting(marker),
                GuardedKillStatement = sessionId =>
                {
                    string batch = MssqlSessionQueries.GuardedKill(sessionId);
                    string kill = $"KILL {sessionId};";
                    return batch.Contains(kill, StringComparison.Ordinal)
                        ? batch.Replace(kill, $"PRINT {sessionId};", StringComparison.Ordinal)
                        : throw new InvalidOperationException(
                            "The guarded batch no longer carries the KILL to disable."
                        );
                },
            };

            using CancellationTokenSource cancellation = new();
            Task<DmsCommandOutcome<DmsInstanceIdentity>> restoring = _run.RestoreAsync(
                engine,
                await ShapedBackup.CreateAsync(),
                cancellation.Token
            );
            _sessionId = await WaitForWaitingWorkSessionAsync(_run.Operation);
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            await cancellation.CancelAsync();

            // Never waits for the two-minute statement: a protocol that does fails here rather than hanging.
            _completed = await Task.WhenAny(restoring, Task.Delay(TimeSpan.FromSeconds(90))) == restoring;
            _elapsed = clock.Elapsed;
            if (_completed)
            {
                _outcome = await restoring;
            }

            _stagingAfter = await Engine().InspectAsync(_run.Stage, default);
        }

        [OneTimeTearDown]
        public async Task TearDown()
        {
            if (_sessionId != 0)
            {
                await KillAsync(_sessionId);
                await WaitUntilAsync(
                    async () => await SessionCountNamedAsync(_run!.Operation.RestoreApplicationName) == 0,
                    TimeSpan.FromSeconds(30)
                );
            }

            if (_run is not null)
            {
                await _run.DisposeAsync();
            }
        }

        [Test]
        public void It_cancelled_while_the_database_was_restoring() => _sessionId.Should().NotBe(0);

        [Test]
        public void It_returns_within_the_termination_budget_not_the_statements_wait()
        {
            _completed.Should().BeTrue();
            _elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
        }

        [Test]
        public void It_reports_an_unknown_outcome_naming_the_work_session_not_quiescent() =>
            _outcome
                .Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Unknown>()
                .Which.Should()
                .Match<DmsCommandOutcome<DmsInstanceIdentity>.Unknown>(unknown =>
                    unknown.Session != null && unknown.Session.Value.Id == _sessionId && !unknown.Quiescent
                );

        [Test]
        public void It_drops_nothing() =>
            _stagingAfter
                .Should()
                .BeOfType<DmsLiveTargetState.Fenced>()
                .Which.Instance.Should()
                .Match<DmsInstanceIdentity>(instance => _run!.Reservation.IsRealizedBy(instance));
    }

    [TestFixture]
    public class Given_the_staged_file_is_replaced_after_the_restore
    {
        private RestoreRun? _run;
        private DmsCommandOutcome<DmsInstanceIdentity> _outcome = null!;
        private DmsLiveTargetState _stagingBeforeDrop = null!;
        private DmsCommandOutcome _dropped = null!;
        private DmsLiveTargetState _stagingAfterDrop = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireSharedStagingOrSkip();
            _run = new RestoreRun();
            MssqlDmsTargetEngine engine = new(
                AdminConnection(),
                Settings(),
                TimeProvider.System,
                NullLogger.Instance
            )
            {
                // A writer (the simulated administrator D-13 leaves out of scope) replaces the bytes after the restore.
                AfterRestore = backup =>
                    File.WriteAllBytesAsync(backup.LocalPath, RandomNumberGenerator.GetBytes(4096)),
            };
            _outcome = await _run.RestoreAsync(engine, await ShapedBackup.CreateAsync());
            _stagingBeforeDrop = await engine.InspectAsync(_run.Stage, default);
            _dropped = await engine.DropStagingAsync(_run.Stage, _run.Reservation, default);
            _stagingAfterDrop = await engine.InspectAsync(_run.Stage, default);
        }

        [OneTimeTearDown]
        public async Task TearDown()
        {
            if (_run is not null)
            {
                await _run.DisposeAsync();
            }
        }

        [Test]
        public void It_reports_the_staged_backup_changed() =>
            _outcome
                .Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Failed>()
                .Which.Failure.Diagnostic.Operation.Should()
                .Be(DmsEngineOperations.StagedBackupChanged);

        [Test]
        public void It_leaves_the_restored_database_for_the_drop_which_removes_it()
        {
            _stagingBeforeDrop.Should().BeOfType<DmsLiveTargetState.Present>();
            (_dropped, _stagingAfterDrop)
                .Should()
                .Be((new DmsCommandOutcome.Succeeded(), new DmsLiveTargetState.Absent()));
        }

        [Test]
        public void It_deletes_the_replaced_file() =>
            ShapedBackup.StagedFilesOf(_run!.Operation).Should().BeEmpty();
    }

    [TestFixture]
    public class Given_staging_drops_without_a_shared_directory
    {
        private DmsOperationId _restoring;
        private DmsOperationId _online;
        private DmsOperationId _foreign;
        private DmsCommandOutcome _absentDropped = null!;
        private DmsLiveTargetState _restoringBefore = null!;
        private DmsCommandOutcome _restoringDropped = null!;
        private DmsLiveTargetState _restoringAfter = null!;
        private DmsCommandOutcome _onlineDropped = null!;
        private DmsLiveTargetState _onlineAfter = null!;
        private DmsCommandOutcome _foreignByReservation = null!;
        private DmsCommandOutcome _foreignByOtherInstance = null!;
        private DmsInstanceIdentity.Mssql _foreignBefore = null!;
        private DmsInstanceIdentity.Mssql _foreignAfter = null!;
        private readonly List<TestDatabase> _databases = [];

        /// <summary>
        /// The staging database of <paramref name="operation"/> restored by the test itself from a server-side backup,
        /// placed as the planner places it, left online or restoring; no shared directory is needed.
        /// </summary>
        private static async Task<TestDatabase> TaggedStagingAsync(DmsOperationId operation, bool recover)
        {
            string backupPath = await BackupPathAsync("dms1438-drop-source.bak");
            await using (TestDatabase source = await CreateDatabaseAsync("dropsource"))
            {
                await ExecuteOnServerAsync(
                    $"BACKUP DATABASE {MssqlIdentifiers.Quote(source.Name)} TO DISK = {MssqlIdentifiers.Literal(backupPath)} WITH INIT, FORMAT, COPY_ONLY;"
                );
            }

            MssqlBackupDescription description;
            await using (SqlConnection connection = await AdminConnection().OpenMaintenanceAsync(default))
            {
                description = await MssqlRestorePlanner.ReadAsync(connection, backupPath, 60, default);
            }

            MssqlRestorePlan plan = (
                (MssqlRestorePlanResult.Planned)
                    MssqlRestorePlanner.Plan(
                        description,
                        operation,
                        TargetName("unrelated"),
                        await ServerPropertyAsync("InstanceDefaultDataPath"),
                        await ServerPropertyAsync("InstanceDefaultLogPath")
                    )
            ).Plan;
            string statement = plan.RestoreStatement(backupPath);
            await ExecuteOnServerAsync(
                recover
                    ? statement
                    : statement.Replace("STATS = 10;", "NORECOVERY, STATS = 10;", StringComparison.Ordinal)
            );
            return new TestDatabase(operation.StagingDatabaseName.Value);
        }

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            MssqlDmsTargetEngine engine = Engine();
            _absentDropped = await engine.DropStagingAsync(
                DmsOperationId.New().StagingDatabaseName,
                new DmsInstanceIdentity.MssqlReservation("unused.mdf"),
                default
            );

            _restoring = DmsOperationId.New();
            _databases.Add(await TaggedStagingAsync(_restoring, recover: false));
            _restoringBefore = await engine.InspectAsync(_restoring.StagingDatabaseName, default);
            _restoringDropped = await engine.DropStagingAsync(
                _restoring.StagingDatabaseName,
                new DmsInstanceIdentity.MssqlReservation(MssqlRestorePlanner.PrimaryFileNameFor(_restoring)),
                default
            );
            _restoringAfter = await engine.InspectAsync(_restoring.StagingDatabaseName, default);

            _online = DmsOperationId.New();
            TestDatabase online = await TaggedStagingAsync(_online, recover: true);
            _databases.Add(online);
            _onlineDropped = await engine.DropStagingAsync(
                _online.StagingDatabaseName,
                await online.CatalogInstanceAsync(),
                default
            );
            _onlineAfter = await engine.InspectAsync(_online.StagingDatabaseName, default);

            // A database under the staging name whose files carry no tag of the operation.
            _foreign = DmsOperationId.New();
            await ExecuteOnServerAsync(
                $"CREATE DATABASE {MssqlIdentifiers.Quote(_foreign.StagingDatabaseName.Value)};"
            );
            TestDatabase foreign = new(_foreign.StagingDatabaseName.Value);
            _databases.Add(foreign);
            _foreignBefore = await foreign.CatalogInstanceAsync();
            _foreignByReservation = await engine.DropStagingAsync(
                _foreign.StagingDatabaseName,
                new DmsInstanceIdentity.MssqlReservation(MssqlRestorePlanner.PrimaryFileNameFor(_foreign)),
                default
            );
            _foreignByOtherInstance = await engine.DropStagingAsync(
                _foreign.StagingDatabaseName,
                _foreignBefore with
                {
                    DatabaseId = _foreignBefore.DatabaseId + 1000,
                },
                default
            );
            _foreignAfter = await foreign.CatalogInstanceAsync();
        }

        [OneTimeTearDown]
        public async Task TearDown()
        {
            foreach (TestDatabase database in _databases)
            {
                await database.DisposeAsync();
            }
        }

        [Test]
        public void It_counts_an_absent_name_as_dropped() =>
            _absentDropped.Should().Be(new DmsCommandOutcome.Succeeded());

        [Test]
        public void It_drops_a_restoring_database_carrying_the_reserved_file()
        {
            _restoringBefore.Should().BeOfType<DmsLiveTargetState.Fenced>();
            (_restoringDropped, _restoringAfter)
                .Should()
                .Be((new DmsCommandOutcome.Succeeded(), new DmsLiveTargetState.Absent()));
        }

        [Test]
        public void It_drops_an_online_database_by_its_instance() =>
            (_onlineDropped, _onlineAfter)
                .Should()
                .Be((new DmsCommandOutcome.Succeeded(), new DmsLiveTargetState.Absent()));

        [Test]
        public void It_refuses_another_database_under_the_name_either_way() =>
            new[] { _foreignByReservation, _foreignByOtherInstance }
                .Should()
                .AllBeEquivalentTo(
                    new DmsCommandOutcome.Failed(
                        new DmsCommandFailure(
                            DmsCommandFailureClass.DoesNotExist,
                            DmsProvisioningDiagnostic.ForOperation(
                                MssqlDmsTargetEngine.DropStagingInstanceStep
                            )
                        )
                    )
                );

        [Test]
        public void It_leaves_the_other_database_untouched() => _foreignAfter.Should().Be(_foreignBefore);
    }
}
