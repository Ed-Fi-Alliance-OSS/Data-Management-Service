// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using FluentAssertions;
using static EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.DmsDataStoreProvisioningServiceMssqlStagingTests;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

/// <summary>
/// The orchestrator's publication on the SQL Server branch (6.4 steps 10 to 12, 6.7, Step 4.6) over the spy engine: the
/// stale-snapshot regression. The rename returns the instance with the <c>create_date</c> it changed (snapshot₁), and
/// the spy refuses, as the real engine does, any session whose expected instance is not exactly what is under the name.
/// Every guard after the rename (the marker session, the marker's resolution, the compensation drop) is handed
/// snapshot₁ and never the pre-rename snapshot₀; a guard that still compared snapshot₀ would be refused and the create
/// would end <c>InstanceChanged</c> instead of the outcome asserted here.
/// </summary>
public class DmsDataStoreProvisioningServiceMssqlPublicationTests
{
    private static readonly DateTimeOffset _renamedAt = new(2026, 11, 7, 9, 30, 0, TimeSpan.Zero);

    private static readonly DmsServerSessionId _session = new(
        62,
        new DateTimeOffset(2026, 11, 7, 8, 0, 0, TimeSpan.Zero)
    );

    private sealed class FakeSession(DmsInstanceIdentity instance, DmsServerSessionId session)
        : DmsSession(instance, session)
    {
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// A SQL Server lane whose rename re-captures <c>create_date</c>, whose session opens are refused unless the
    /// expected instance is exactly the one under the name now, and which records every instance handed to a session
    /// open or a target drop after the rename.
    /// </summary>
    private sealed class SnapshotLane
    {
        public SnapshotLane(IDmsProvisioningTestHooks? hooks = null)
        {
            Lane = new MssqlLane(hooks);
            SpyDmsTargetEngine engine = Lane.Engine;
            engine.OnRenameIntoTarget = (_, instance, _, _) =>
            {
                Staged = (DmsInstanceIdentity.Mssql)instance;
                Renamed = Staged.WithCreateDateSnapshot(_renamedAt);
                return Task.FromResult<DmsCommandOutcome<DmsInstanceIdentity>>(
                    new DmsCommandOutcome<DmsInstanceIdentity>.Succeeded(Renamed)
                );
            };
            engine.OnOpenSession = (name, expected, _) =>
            {
                DmsInstanceIdentity current =
                    name.Value == Lane.Request.TargetName.Value ? Renamed! : RestoredFor(Lane.Staging);
                if (Renamed is not null)
                {
                    AfterRename.Add(expected);
                }

                return Task.FromResult<DmsSessionOpenResult>(
                    expected == current
                        ? new DmsSessionOpenResult.Opened(new FakeSession(expected, _session))
                        : new DmsSessionOpenResult.InstanceChanged()
                );
            };
            engine.OnDropInstance = (_, instance, _, _) =>
            {
                AfterRename.Add(instance);
                return Task.FromResult<DmsCommandOutcome>(new DmsCommandOutcome.Succeeded());
            };
        }

        public MssqlLane Lane { get; }

        public SpyDmsTargetEngine Engine => Lane.Engine;

        public DmsInstanceIdentity.Mssql? Staged { get; private set; }

        public DmsInstanceIdentity.Mssql? Renamed { get; private set; }

        /// <summary>Every instance a session open or a target drop was handed once the rename had returned.</summary>
        public List<DmsInstanceIdentity> AfterRename { get; } = [];

        public Task<DmsDataStoreCreateResult> CreateAsync() => Lane.CreateAsync();
    }

    [TestFixture]
    public class Given_the_rename_recaptures_the_create_date_and_the_marker_commits
    {
        private SnapshotLane _lane = null!;
        private DmsDataStoreCreateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _lane = new SnapshotLane();
            _result = await _lane.CreateAsync();
        }

        [Test]
        public void It_creates_the_target() => _result.Should().BeOfType<DmsDataStoreCreateResult.Created>();

        [Test]
        public void It_renamed_an_instance_whose_snapshot_the_rename_changed() =>
            (_lane.Staged!.IsSameStableInstance(_lane.Renamed!), _lane.Staged == _lane.Renamed)
                .Should()
                .Be((true, false));

        [Test]
        public void It_binds_the_marker_session_to_the_recaptured_snapshot_only() =>
            _lane.AfterRename.Should().Equal(_lane.Renamed!);
    }

    [TestFixture]
    public class Given_the_marker_is_in_doubt_and_proven_uncommitted
    {
        private SnapshotLane _lane = null!;
        private DmsDataStoreCreateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            FaultHooks hooks = new();
            hooks.Faults.Add(DmsProvisioningFaultPoint.FailAfterMarkerExecuted);
            _lane = new SnapshotLane(hooks);
            _result = await _lane.CreateAsync();
        }

        [Test]
        public void It_compensates_after_resolving_the_marker() =>
            (
                _result.Should().BeOfType<DmsDataStoreCreateResult.Failure>().Subject.Detail.Code,
                _lane.Lane.CountOf(SpyDmsTargetEngine.DropInstance)
            )
                .Should()
                .Be((DmsProvisioningFailureCode.IdentityAssignmentFailed, 1));

        [Test]
        public void It_hands_the_marker_session_the_resolution_session_and_the_drop_the_recaptured_snapshot_only() =>
            _lane.AfterRename.Should().Equal(_lane.Renamed!, _lane.Renamed!, _lane.Renamed!);
    }

    [TestFixture]
    public class Given_the_server_refuses_the_marker
    {
        private SnapshotLane _lane = null!;
        private DmsDataStoreCreateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _lane = new SnapshotLane();
            _lane.Engine.OnAssignMarker = (_, _, _) =>
                Task.FromResult<DmsCommandOutcome>(
                    new DmsCommandOutcome.Failed(
                        new DmsCommandFailure(
                            DmsCommandFailureClass.Rejected,
                            DmsProvisioningDiagnostic.ForOperation("AssignMarker")
                        )
                    )
                );
            _result = await _lane.CreateAsync();
        }

        [Test]
        public void It_compensates_the_renamed_instance() =>
            _result
                .Should()
                .BeOfType<DmsDataStoreCreateResult.Failure>()
                .Which.Detail.Code.Should()
                .Be(DmsProvisioningFailureCode.IdentityAssignmentFailed);

        [Test]
        public void It_hands_the_marker_session_and_the_drop_the_recaptured_snapshot_only() =>
            _lane.AfterRename.Should().Equal(_lane.Renamed!, _lane.Renamed!);
    }
}
