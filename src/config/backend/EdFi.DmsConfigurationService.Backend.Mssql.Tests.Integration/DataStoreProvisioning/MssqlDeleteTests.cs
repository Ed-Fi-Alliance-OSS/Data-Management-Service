// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using static EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning.MssqlDeletionTests;
using static EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning.MssqlProvisioningTestServer;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning;

/// <summary>
/// The SQL Server owned delete through the orchestrator (6.6, Step 4.8) against a live server. An owned target is
/// dropped with its files, also with a busy session on it; an absent, unowned or unreadable target is left untouched
/// and never fenced; a competing writer before the fence is refused under it and the fence reversed; an instance
/// recreated before the fence is refused by the fence's guard and left untouched; while the fence stands nobody else
/// can open or drop the target; deletes of one target, and a delete behind a create, are serialized by the target lock,
/// and a contender never takes it over from a live holder. The takeover fixtures (round-3 finding 2) kill a paused
/// worker's holder before its heartbeat can notice, let a second worker replace the target with a full create, and
/// prove the first can then mutate nothing; those that create need a staging directory shared with the server.
/// </summary>
[Category(Category)]
public class MssqlDeleteTests
{
    private static readonly TimeSpan _bound = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan _quietHeartbeat = TimeSpan.FromMinutes(30);

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

    private static IEnumerable<string> MembersOf(RecordingEngine engine) =>
        engine.Calls.Select(call => call.Member);

    private static int CountOf(RecordingEngine engine, string member) =>
        engine.Calls.Count(call => call.Member == member);

    /// <summary>Whether a holder session is waiting for its application lock, as a second worker queued behind the first does.</summary>
    private static Task<bool> HolderWaitingAsync() =>
        WaitUntilAsync(
            async () =>
                await ScalarOnServerAsync<int>(
                    "SELECT COUNT(*) FROM sys.dm_exec_requests r JOIN sys.dm_exec_sessions s ON s.session_id = r.session_id WHERE s.program_name LIKE @prefix AND r.wait_type LIKE N'LCK[_]M[_]%';",
                    ("@prefix", DmsOperationId.HolderApplicationNamePrefix + "%")
                ) > 0,
            _bound
        );

    private static async Task<Lane> DeleteOnlyLaneAsync(
        LaneHooks? hooks = null,
        MssqlDmsTargetEngine? engine = null
    ) => await Lane.BuildAsync(Lane.DeleteOnlyArtifact, hooks ?? new LaneHooks(), engine);

    [TestFixture]
    public class Given_an_owned_target_with_a_busy_session_is_deleted
    {
        private static Task<int> HolderSessionsAsync() =>
            ScalarOnServerAsync<int>(
                "SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE program_name LIKE @prefix;",
                ("@prefix", DmsOperationId.HolderApplicationNamePrefix + "%")
            );

        private Lane? _lane;
        private DmsInstanceIdentity.Mssql _instance = null!;
        private DmsDataStoreDeleteResult _result = null!;
        private bool _busyGone;
        private Exception? _busyOutcome;
        private (int FileRows, bool PrimaryFileExists) _filesAfter;
        private int _holderSessions;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _lane = await DeleteOnlyLaneAsync();
            _instance = await _lane.SeedTargetAsync();
            await using (
                CompetingSession busy = await CompetingSession.OpenAsync(_lane.Target.Value, busy: true)
            )
            {
                _result = await _lane.DeleteAsync();
                _busyGone = await busy.GoneAsync();
                _busyOutcome = await busy.WaitOutcomeAsync();
            }

            _filesAfter = await FilesLeftAsync(_instance);
            _holderSessions = await HolderSessionsAsync();
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
        public void It_drops_the_target_and_its_files() =>
            (_result, _filesAfter)
                .Should()
                .Be((new DmsDataStoreDeleteResult.Dropped(DmsCleanupReport.Clean), (0, false)));

        [Test]
        public void It_ended_the_busy_session_with_the_fence() =>
            (_busyGone, _busyOutcome is SqlException).Should().Be((true, true));

        [Test]
        public void It_read_the_identity_on_the_holder_under_the_fence_and_never_reversed_it()
        {
            MembersOf(_lane!.Engine)
                .SkipWhile(member => member != nameof(IDmsTargetEngine.FenceAsync))
                .Should()
                .Equal(
                    nameof(IDmsTargetEngine.FenceAsync),
                    nameof(IDmsTargetEngine.ReadIdentityOnHolderAsync),
                    nameof(IDmsTargetEngine.InspectAsync),
                    nameof(IDmsTargetEngine.DropInstanceAsync)
                );
            _lane.Engine.OpenedInstances.Should().Equal(_instance);
        }

        [Test]
        public void It_leaves_no_holder_session() => _holderSessions.Should().Be(0);
    }

    [TestFixture]
    public class Given_a_target_the_delete_must_not_touch
    {
        private readonly Guid _otherIdentity = Guid.Parse("1f2e3d4c-5b6a-4978-8695-a4b3c2d1e0f9");
        private Lane? _absent;
        private Lane? _unowned;
        private Lane? _unreadable;
        private DmsDataStoreDeleteResult _absentResult = null!;
        private DmsDataStoreDeleteResult _unownedResult = null!;
        private DmsDataStoreDeleteResult _unreadableResult = null!;
        private (bool Exists, int DatabaseId, string UserAccess) _unownedBefore;
        private (bool Exists, int DatabaseId, string UserAccess) _unownedAfter;
        private (bool Exists, int DatabaseId, string UserAccess) _unreadableBefore;
        private (bool Exists, int DatabaseId, string UserAccess) _unreadableAfter;
        private Guid _unownedIdentityAfter;
        private Lane? _columnMissing;
        private DmsDataStoreDeleteResult _columnMissingResult = null!;
        private (bool Exists, int DatabaseId, string UserAccess) _columnMissingBefore;
        private (bool Exists, int DatabaseId, string UserAccess) _columnMissingAfter;
        private Lane? _fencedColumnMissing;
        private DmsDataStoreDeleteResult _fencedColumnMissingResult = null!;
        private (bool Exists, int DatabaseId, string UserAccess) _fencedColumnMissingBefore;
        private (bool Exists, int DatabaseId, string UserAccess) _fencedColumnMissingAfter;
        private Lane? _overlength;
        private DmsDataStoreDeleteResult _overlengthResult = null!;
        private (bool Exists, int DatabaseId, string UserAccess) _overlengthBefore;
        private (bool Exists, int DatabaseId, string UserAccess) _overlengthAfter;
        private Lane? _fencedOverlength;
        private DmsDataStoreDeleteResult _fencedOverlengthResult = null!;
        private (bool Exists, int DatabaseId, string UserAccess) _fencedOverlengthBefore;
        private (bool Exists, int DatabaseId, string UserAccess) _fencedOverlengthAfter;

        /// <summary>
        /// F-001: the record's own identity followed by a space, which a read truncated to 36 characters without the
        /// overlength verdict would take for the record's identity.
        /// </summary>
        private const string PadIdentity =
            "UPDATE [dms].[DataStoreIdentity] SET [SourceIdentity] = [SourceIdentity] + N' ';";

        private const string IdentityAsText =
            "ALTER TABLE [dms].[DataStoreIdentity] ALTER COLUMN [SourceIdentity] nvarchar(max) NOT NULL;";

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _absent = await DeleteOnlyLaneAsync();
            _absentResult = await _absent.DeleteAsync();

            _unowned = await DeleteOnlyLaneAsync();
            await _unowned.SeedTargetAsync(_otherIdentity);
            _unownedBefore = await _unowned.TargetCatalogAsync();
            _unownedResult = await _unowned.DeleteAsync();
            _unownedAfter = await _unowned.TargetCatalogAsync();
            _unownedIdentityAfter = await _unowned.TargetIdentityAsync();

            _unreadable = await DeleteOnlyLaneAsync();
            await ExecuteOnServerAsync(
                $"CREATE DATABASE {MssqlIdentifiers.Quote(_unreadable.Target.Value)};"
            );
            _unreadableBefore = await _unreadable.TargetCatalogAsync();
            _unreadableResult = await _unreadable.DeleteAsync();
            _unreadableAfter = await _unreadable.TargetCatalogAsync();

            // F-01: the record's own identity once seeded, then the identity column dropped before the first read.
            _columnMissing = await DeleteOnlyLaneAsync();
            await _columnMissing.SeedTargetAsync();
            await new TestDatabase(_columnMissing.Target.Value).ExecuteAsync(
                "ALTER TABLE [dms].[DataStoreIdentity] DROP COLUMN [SourceIdentity];"
            );
            await WaitForNoSessionsOnAsync(_columnMissing.Target.Value);
            _columnMissingBefore = await _columnMissing.TargetCatalogAsync();
            _columnMissingResult = await _columnMissing.DeleteAsync();
            _columnMissingAfter = await _columnMissing.TargetCatalogAsync();

            // F-01 on the holder: the first read finds the record's identity; the column is dropped just before
            // the fence, so only the fenced re-read on the holder session meets the missing column.
            LaneHooks fencedHooks = new();
            _fencedColumnMissing = await DeleteOnlyLaneAsync(fencedHooks);
            await _fencedColumnMissing.SeedTargetAsync();
            string fencedTarget = _fencedColumnMissing.Target.Value;
            fencedHooks.Probe = async (point, _) =>
            {
                if (point == DmsProvisioningHookPoint.BeforeFence)
                {
                    await new TestDatabase(fencedTarget).ExecuteAsync(
                        "ALTER TABLE [dms].[DataStoreIdentity] DROP COLUMN [SourceIdentity];"
                    );
                }
            };
            _fencedColumnMissingBefore = await _fencedColumnMissing.TargetCatalogAsync();
            _fencedColumnMissingResult = await _fencedColumnMissing.DeleteAsync();
            _fencedColumnMissingAfter = await _fencedColumnMissing.TargetCatalogAsync();

            // F-001 on the work session: the padded identity is there before the first read.
            _overlength = await DeleteOnlyLaneAsync();
            await _overlength.SeedTargetAsync();
            // Separate batches: SQL Server binds a batch's statements before the first one changes the column type.
            await new TestDatabase(_overlength.Target.Value).ExecuteAsync(IdentityAsText);
            await new TestDatabase(_overlength.Target.Value).ExecuteAsync(PadIdentity);
            await WaitForNoSessionsOnAsync(_overlength.Target.Value);
            _overlengthBefore = await _overlength.TargetCatalogAsync();
            _overlengthResult = await _overlength.DeleteAsync();
            _overlengthAfter = await _overlength.TargetCatalogAsync();

            // F-001 on the holder: the first read finds the record's identity as text; it is padded just before the
            // fence, so only the fenced re-read on the holder session meets the longer value.
            LaneHooks paddedHooks = new();
            _fencedOverlength = await DeleteOnlyLaneAsync(paddedHooks);
            await _fencedOverlength.SeedTargetAsync();
            string paddedTarget = _fencedOverlength.Target.Value;
            await new TestDatabase(paddedTarget).ExecuteAsync(IdentityAsText);
            await WaitForNoSessionsOnAsync(paddedTarget);
            paddedHooks.Probe = async (point, _) =>
            {
                if (point == DmsProvisioningHookPoint.BeforeFence)
                {
                    await new TestDatabase(paddedTarget).ExecuteAsync(PadIdentity);
                }
            };
            _fencedOverlengthBefore = await _fencedOverlength.TargetCatalogAsync();
            _fencedOverlengthResult = await _fencedOverlength.DeleteAsync();
            _fencedOverlengthAfter = await _fencedOverlength.TargetCatalogAsync();
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            foreach (
                Lane? lane in new[]
                {
                    _absent,
                    _unowned,
                    _unreadable,
                    _columnMissing,
                    _fencedColumnMissing,
                    _overlength,
                    _fencedOverlength,
                }
            )
            {
                if (lane is not null)
                {
                    await lane.DisposeAsync();
                }
            }
        }

        [Test]
        public void It_reports_an_absent_target_as_already_absent() =>
            _absentResult.Should().Be(new DmsDataStoreDeleteResult.AlreadyAbsent());

        [Test]
        public void It_reports_another_identity_as_unowned_and_leaves_it_untouched()
        {
            _unownedResult
                .Should()
                .BeOfType<DmsDataStoreDeleteResult.Failure>()
                .Which.Detail.Code.Should()
                .Be(DmsProvisioningFailureCode.TargetUnowned);
            (_unownedAfter, _unownedIdentityAfter).Should().Be((_unownedBefore, _otherIdentity));
            _unownedAfter.UserAccess.Should().Be("MULTI_USER");
        }

        [Test]
        public void It_reports_the_identity_followed_by_a_space_as_unverifiable_and_leaves_it_untouched()
        {
            FailureOf(_overlengthResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnverifiable,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.DeleteIdentityStep
                    )
                );
            (_overlengthAfter, _overlengthBefore.UserAccess).Should().Be((_overlengthBefore, "MULTI_USER"));
            MembersOf(_overlength!.Engine)
                .Should()
                .NotContain(nameof(IDmsTargetEngine.FenceAsync))
                .And.NotContain(nameof(IDmsTargetEngine.DropInstanceAsync));
        }

        [Test]
        public void It_reports_the_identity_padded_under_the_fence_as_unverifiable_and_reverses_the_fence()
        {
            FailureOf(_fencedOverlengthResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnverifiable,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.VerifyFencedStep
                    )
                );
            (_fencedOverlengthAfter, _fencedOverlengthBefore.UserAccess)
                .Should()
                .Be((_fencedOverlengthBefore, "MULTI_USER"));
            MembersOf(_fencedOverlength!.Engine)
                .Should()
                .ContainInOrder(
                    nameof(IDmsTargetEngine.FenceAsync),
                    nameof(IDmsTargetEngine.ReadIdentityOnHolderAsync),
                    nameof(IDmsTargetEngine.UnfenceAsync)
                )
                .And.NotContain(nameof(IDmsTargetEngine.DropInstanceAsync));
        }

        [Test]
        public void It_reports_a_target_without_an_identity_as_unverifiable_and_leaves_it_untouched()
        {
            _unreadableResult
                .Should()
                .BeOfType<DmsDataStoreDeleteResult.Failure>()
                .Which.Detail.Code.Should()
                .Be(DmsProvisioningFailureCode.TargetUnverifiable);
            _unreadableAfter.Should().Be(_unreadableBefore);
        }

        [Test]
        public void It_reports_a_target_without_the_identity_column_as_unverifiable_and_leaves_it_untouched()
        {
            FailureOf(_columnMissingResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnverifiable,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.DeleteIdentityStep
                    )
                );
            (_columnMissingAfter, _columnMissingBefore.UserAccess)
                .Should()
                .Be((_columnMissingBefore, "MULTI_USER"));
        }

        [Test]
        public void It_reports_a_column_missing_under_the_fence_as_unverifiable_and_reverses_the_fence()
        {
            FailureOf(_fencedColumnMissingResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnverifiable,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.VerifyFencedStep
                    )
                );
            _fencedColumnMissingResult
                .Should()
                .BeOfType<DmsDataStoreDeleteResult.Failure>()
                .Which.Cleanup.HasResidue.Should()
                .BeFalse();
            (_fencedColumnMissingAfter, _fencedColumnMissingBefore.UserAccess)
                .Should()
                .Be((_fencedColumnMissingBefore, "MULTI_USER"));
        }

        [Test]
        public void It_read_the_fenced_identity_on_the_holder_and_reversed_the_fence() =>
            MembersOf(_fencedColumnMissing!.Engine)
                .Should()
                .ContainInOrder(
                    nameof(IDmsTargetEngine.FenceAsync),
                    nameof(IDmsTargetEngine.ReadIdentityOnHolderAsync),
                    nameof(IDmsTargetEngine.UnfenceAsync)
                );

        [Test]
        public void It_never_fences_or_drops()
        {
            foreach (Lane lane in new[] { _absent!, _unowned!, _unreadable!, _columnMissing! })
            {
                MembersOf(lane.Engine)
                    .Should()
                    .NotContain(nameof(IDmsTargetEngine.FenceAsync))
                    .And.NotContain(nameof(IDmsTargetEngine.DropInstanceAsync));
            }
        }

        [Test]
        public void It_never_drops_the_target_whose_column_went_missing_under_the_fence() =>
            MembersOf(_fencedColumnMissing!.Engine)
                .Should()
                .NotContain(nameof(IDmsTargetEngine.DropInstanceAsync));

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

    [TestFixture]
    public class Given_the_target_changes_or_is_contested_around_the_fence
    {
        private readonly Guid _competingIdentity = Guid.Parse("2a3b4c5d-6e7f-4809-9a1b-2c3d4e5f6a7b");
        private Lane? _claimed;
        private Lane? _recreated;
        private Lane? _contested;
        private DmsDataStoreDeleteResult _claimedResult = null!;
        private (bool Exists, int DatabaseId, string UserAccess) _claimedAfter;
        private Guid _claimedIdentityAfter;
        private DmsDataStoreDeleteResult _recreatedResult = null!;
        private DmsInstanceIdentity.Mssql _replacement = null!;
        private (bool Exists, int DatabaseId, string UserAccess) _recreatedAfter;
        private DmsDataStoreDeleteResult _contestedResult = null!;
        private Exception? _openWhileFenced;
        private Exception? _dropWhileFenced;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();

            // A competing writer claims the target between our identity read and the fence.
            LaneHooks claimHooks = new();
            _claimed = await DeleteOnlyLaneAsync(claimHooks);
            await _claimed.SeedTargetAsync();
            Lane claimed = _claimed;
            claimHooks.Probe = async (point, _) =>
            {
                if (point == DmsProvisioningHookPoint.BeforeFence)
                {
                    await new TestDatabase(claimed.Target.Value).ExecuteAsync(
                        $"UPDATE [dms].[DataStoreIdentity] SET [SourceIdentity] = '{_competingIdentity:D}';"
                    );
                    await WaitForNoSessionsOnAsync(claimed.Target.Value);
                }
            };
            _claimedResult = await _claimed.DeleteAsync();
            _claimedAfter = await _claimed.TargetCatalogAsync();
            _claimedIdentityAfter = await _claimed.TargetIdentityAsync();

            // The target is replaced, with the same identity, between our identity read and the fence.
            LaneHooks recreateHooks = new();
            _recreated = await DeleteOnlyLaneAsync(recreateHooks);
            await _recreated.SeedTargetAsync();
            Lane recreated = _recreated;
            recreateHooks.Probe = async (point, _) =>
            {
                if (point == DmsProvisioningHookPoint.BeforeFence)
                {
                    await new TestDatabase(recreated.Target.Value).DisposeAsync();
                    _replacement = await recreated.SeedTargetAsync();
                }
            };
            _recreatedResult = await _recreated.DeleteAsync();
            _recreatedAfter = await _recreated.TargetCatalogAsync();

            // While the fence stands nobody else can open or drop the target; the delete then completes.
            LaneHooks contestHooks = new();
            _contested = await DeleteOnlyLaneAsync(contestHooks);
            await _contested.SeedTargetAsync();
            string contested = _contested.Target.Value;
            contestHooks.Probe = async (point, _) =>
            {
                if (point == DmsProvisioningHookPoint.BeforeDrop)
                {
                    _openWhileFenced = await Record.ExceptionAsync(async () =>
                        await (await CompetingSession.OpenAsync(contested)).DisposeAsync()
                    );
                    _dropWhileFenced = await Record.ExceptionAsync(() =>
                        ExecuteOnServerAsync($"DROP DATABASE {MssqlIdentifiers.Quote(contested)};")
                    );
                }
            };
            _contestedResult = await _contested.DeleteAsync();
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            foreach (Lane? lane in new[] { _claimed, _recreated, _contested })
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
            DmsProvisioningFailure failure = _claimedResult
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
            (_claimedAfter.Exists, _claimedAfter.UserAccess, _claimedIdentityAfter)
                .Should()
                .Be((true, "MULTI_USER", _competingIdentity));
            CountOf(_claimed!.Engine, nameof(IDmsTargetEngine.UnfenceAsync)).Should().Be(1);
            CountOf(_claimed.Engine, nameof(IDmsTargetEngine.DropInstanceAsync)).Should().Be(0);
        }

        [Test]
        public void It_refuses_an_instance_recreated_before_the_fence_and_leaves_it_untouched()
        {
            DmsProvisioningFailure failure = _recreatedResult
                .Should()
                .BeOfType<DmsDataStoreDeleteResult.Failure>()
                .Subject.Detail;
            (failure.Code, failure.Diagnostic.Operation)
                .Should()
                .Be((DmsProvisioningFailureCode.InstanceChanged, MssqlDeletionQueries.FenceInstanceStep));
            _recreatedAfter.Should().Be((true, _replacement.DatabaseId, "MULTI_USER"));
            MembersOf(_recreated!.Engine)
                .Should()
                .NotContain(nameof(IDmsTargetEngine.UnfenceAsync))
                .And.NotContain(nameof(IDmsTargetEngine.DropInstanceAsync));
        }

        [Test]
        public void It_lets_nobody_else_open_or_drop_the_target_while_fenced() =>
            (
                (_openWhileFenced as SqlException)?.Number,
                (_dropWhileFenced as SqlException)?.Number,
                _contestedResult
            )
                .Should()
                .Be((4060, 3702, new DmsDataStoreDeleteResult.Dropped(DmsCleanupReport.Clean)));
    }

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
        private (bool Exists, int DatabaseId, string UserAccess) _whileContended;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();

            Pause serialize = new(DmsProvisioningHookPoint.BeforeDrop);
            _serialized = await DeleteOnlyLaneAsync(new LaneHooks { Probe = serialize.ProbeAsync });
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
            _contended = await DeleteOnlyLaneAsync(new LaneHooks { Probe = contend.ProbeAsync });
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
            (_whileContended.Exists, _whileContended.UserAccess)
                .Should()
                .Be((true, "SINGLE_USER"), "the paused worker still holds its fence");
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

    /// <summary>A create paused before its rename holds the lock; a delete of the same target waits, then drops what the create published.</summary>
    [TestFixture]
    public class Given_a_delete_waits_for_a_create_of_the_same_target
    {
        private Lane? _lane;
        private DmsDataStoreCreateResult _created = null!;
        private DmsDataStoreDeleteResult _deleted = null!;
        private bool _deleteWaited;
        private (bool Exists, int DatabaseId, string UserAccess) _after;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireSharedStagingOrSkip();
            Pause pause = new(DmsProvisioningHookPoint.BeforeRename);
            _lane = await Lane.BuildAsync(
                await ShapedBackup.CreateAsync(),
                new LaneHooks { Probe = pause.ProbeAsync }
            );
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

    /// <summary>
    /// Takeover (round-3 finding 2), with a full create by the second worker: worker A is paused at a hook with a
    /// heartbeat too slow to notice anything; A's holder session is killed; worker B acquires the lock, replaces the
    /// target with the same record and writes a sentinel; A resumes and its next mutation runs on the dead holder, so
    /// nothing it sends reaches the server and B's target is untouched.
    /// </summary>
    [TestFixture]
    public class Given_a_paused_worker_loses_its_holder_and_another_replaces_the_target
    {
        /// <summary>Kills the paused operation's holder session and waits until the server no longer lists it.</summary>
        private static async Task KillHolderAsync(DmsOperationId operation)
        {
            int sessionId = await SessionIdNamedAsync(operation.HolderApplicationName);
            sessionId.Should().NotBe(0, "the paused operation holds the target lock");
            await KillAsync(sessionId);
            (
                await WaitUntilAsync(
                    async () => await SessionCountNamedAsync(operation.HolderApplicationName) == 0,
                    _bound
                )
            )
                .Should()
                .BeTrue();
        }

        /// <summary>Adds a sentinel table to the target that only the worker that created it could have written.</summary>
        private static Task WriteSentinelAsync(DmsTargetDatabaseName target) =>
            new TestDatabase(target.Value).ExecuteAsync(
                "CREATE TABLE [dbo].[dms1438_sentinel] ([value] int NOT NULL); INSERT INTO [dbo].[dms1438_sentinel] ([value]) VALUES (1438);"
            );

        private static Task<int> SentinelAsync(DmsTargetDatabaseName target) =>
            new TestDatabase(target.Value).ScalarAsync<int>("SELECT [value] FROM [dbo].[dms1438_sentinel];");

        private readonly List<Lane> _lanes = [];

        private sealed record Takeover(
            Lane Lane,
            object? Result,
            int ReplacedDatabaseId,
            (bool Exists, int DatabaseId, string UserAccess) After,
            Guid Identity,
            int Sentinel
        );

        private Takeover _beforeFence = null!;
        private Takeover _beforeDrop = null!;
        private Takeover _beforeRename = null!;
        private Takeover _afterRestore = null!;

        /// <summary>
        /// Runs A until <paramref name="point"/>, kills its holder, lets <paramref name="replace"/> run as B, then
        /// resumes A and gathers the evidence.
        /// </summary>
        private async Task<Takeover> RunAsync(
            byte[] artifact,
            DmsProvisioningHookPoint point,
            Func<Lane, Task<object>> runA,
            Func<Lane, Lane.Deployment, Task> replace,
            bool seedTarget
        )
        {
            Pause pause = new(point);
            Lane lane = await Lane.BuildAsync(
                artifact,
                new LaneHooks { Probe = pause.ProbeAsync },
                Engine(lockHeartbeatInterval: _quietHeartbeat)
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
            await WaitForNoSessionsOnAsync(lane.Target.Value);
            int replaced = (await lane.TargetCatalogAsync()).DatabaseId;

            pause.Release.SetResult();
            object result = await a;
            return new Takeover(
                lane,
                result,
                replaced,
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
            RequireSharedStagingOrSkip();
            byte[] artifact = await ShapedBackup.CreateAsync();

            _beforeFence = await RunAsync(
                artifact,
                DmsProvisioningHookPoint.BeforeFence,
                async lane => await lane.DeleteAsync(),
                DeleteAndRecreateAsync,
                seedTarget: true
            );

            // A's fence stands when it pauses before the drop (its killed holder no longer holds the slot, but the
            // database stays in single-user mode): B can act only after the operator procedure (7.6) reverses it.
            _beforeDrop = await RunAsync(
                artifact,
                DmsProvisioningHookPoint.BeforeDrop,
                async lane => await lane.DeleteAsync(),
                async (lane, b) =>
                {
                    await ExecuteOnServerAsync(
                        $"ALTER DATABASE {MssqlIdentifiers.Quote(lane.Target.Value)} SET MULTI_USER;"
                    );
                    await DeleteAndRecreateAsync(lane, b);
                },
                seedTarget: true
            );

            _beforeRename = await RunAsync(
                artifact,
                DmsProvisioningHookPoint.BeforeRename,
                async lane => await lane.CreateAsync(),
                CreateAsync,
                seedTarget: false
            );

            _afterRestore = await RunAsync(
                artifact,
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
                            (true, takeover.ReplacedDatabaseId, "MULTI_USER"),
                            takeover.Lane.Request.ExpectedSourceIdentity,
                            1438
                        )
                    );
            }
        }

        [Test]
        public void It_ends_a_paused_delete_as_lock_lost_without_a_further_mutation()
        {
            (
                Detail(_beforeFence).Code,
                Detail(_beforeDrop).Code,
                Detail(_beforeDrop).Category,
                Detail(_beforeDrop).Diagnostic.Operation
            )
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.LockLost,
                        DmsProvisioningFailureCode.InstanceChanged,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.ResolveDeletionStep
                    )
                );
            foreach (Takeover takeover in new[] { _beforeFence, _beforeDrop })
            {
                MembersOf(takeover.Lane.Engine).Should().NotContain(nameof(IDmsTargetEngine.UnfenceAsync));
            }

            CountOf(_beforeFence.Lane.Engine, nameof(IDmsTargetEngine.FenceAsync)).Should().Be(1);
            CountOf(_beforeFence.Lane.Engine, nameof(IDmsTargetEngine.DropInstanceAsync)).Should().Be(0);
            CountOf(_beforeDrop.Lane.Engine, nameof(IDmsTargetEngine.DropInstanceAsync)).Should().Be(1);
        }

        /// <summary>
        /// The result of A's delete. Before the fence: <c>LockLost</c>. Before the drop: SqlClient learns of the killed
        /// connection only when it sends, so the drop was handed over and stands in doubt; settlement proves the holder gone
        /// and finds another instance under the name, which it reports for inspection rather than as a drop.
        /// </summary>
        private static DmsProvisioningFailure Detail(Takeover takeover) =>
            takeover.Result.Should().BeOfType<DmsDataStoreDeleteResult.Failure>().Subject.Detail;

        [Test]
        public void It_ends_a_create_paused_before_the_rename_as_lock_lost() =>
            _beforeRename
                .Result.Should()
                .BeOfType<DmsDataStoreCreateResult.Failure>()
                .Which.Detail.Code.Should()
                .Be(DmsProvisioningFailureCode.LockLost);

        [Test]
        public async Task It_drops_the_staging_database_of_every_create_and_never_marks_or_drops_the_target()
        {
            foreach (Takeover takeover in new[] { _beforeRename, _afterRestore })
            {
                takeover.Result.Should().NotBeOfType<DmsDataStoreCreateResult.Created>();
                (
                    await ScalarOnServerAsync<int>(
                        "SELECT COUNT(*) FROM sys.databases WHERE name = @name;",
                        ("@name", takeover.Lane.Operation.StagingDatabaseName.Value)
                    )
                )
                    .Should()
                    .Be(0);
                MembersOf(takeover.Lane.Engine)
                    .Should()
                    .NotContain(nameof(IDmsTargetEngine.AssignMarkerAsync))
                    .And.NotContain(nameof(IDmsTargetEngine.DropInstanceAsync));
            }
        }
    }
}
