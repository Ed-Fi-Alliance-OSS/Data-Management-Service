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
/// The orchestrator's staging stage end to end (6.3 steps 2 to 7, Step 3.5) over the real catalog and the
/// real engine, with a package built from the committed DMS-shaped fixture: a valid package reaches the
/// rename with a validated staging database and the selected target never addressed by anything but an
/// inspection (polled by a side session at every hook), and is then published as the marked target; a
/// privilege drift and an inventory drift in the artifact end with the staging database dropped and the
/// target absent; a client that loses the replay's result after the server committed continues; and a
/// replay ended by the restore bound is a transient timeout with the staging database dropped. Needs a
/// real <c>psql</c>.
/// </summary>
[Category(Category)]
public class PostgresqlCreateStagingStageTests
{
    /// <summary>What a side session sees of the staging and target names at a hook point.</summary>
    private sealed record HookObservation(
        DmsProvisioningHookPoint Point,
        DmsLiveTargetState Staging,
        Guid? StagingIdentity,
        DmsLiveTargetState Target
    );

    private static async Task<HookObservation> ObserveAsync(
        DmsProvisioningHookPoint point,
        DmsOperationId operation,
        DmsTargetDatabaseName target
    )
    {
        PostgresqlDmsTargetEngine side = DmsProvisioningTestServer.Engine();
        DmsLiveTargetState staging = await side.InspectAsync(operation.StagingDatabaseName, default);
        Guid? identity = null;
        if (staging is DmsLiveTargetState.Present)
        {
            await using (
                NpgsqlConnection connection = await AdminConnection()
                    .OpenAsync(operation.StagingDatabaseName.Value, "dms1438-test", default)
            )
            {
                await using NpgsqlCommand command = new(
                    "SELECT \"SourceIdentity\" FROM \"dms\".\"DataStoreIdentity\";",
                    connection
                );
                identity = await command.ExecuteScalarAsync() as Guid?;
            }

            // The rename that follows the BeforeRename hook refuses a database still in use.
            await WaitForNoSessionsOnAsync(operation.StagingDatabaseName.Value);
        }

        return new HookObservation(point, staging, identity, await side.InspectAsync(target, default));
    }

    [TestFixture]
    public class Given_a_valid_package_is_staged_and_published
    {
        private Lane? _lane;
        private readonly LaneHooks _hooks = new();
        private readonly List<HookObservation> _observations = [];
        private DmsDataStoreCreateResult _result = null!;
        private DmsLiveTargetState _stagingAfter = null!;
        private DmsLiveTargetState _targetAfter = null!;
        private Guid _markerAfter;
        private string[] _entriesAfter = [];

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            RequirePsqlOrSkip();
            _lane = await Lane.BuildAsync(Lane.ShapedSql(), _hooks);
            _hooks.Probe = async (point, operation) =>
                _observations.Add(await ObserveAsync(point, operation, _lane.Target));
            _result = await _lane.CreateAsync();
            PostgresqlDmsTargetEngine side = DmsProvisioningTestServer.Engine();
            _stagingAfter = await side.InspectAsync(_lane.Operation.StagingDatabaseName, default);
            _targetAfter = await side.InspectAsync(_lane.Target, default);
            _markerAfter = await _lane.TargetIdentityAsync();
            _entriesAfter = _lane.OperationEntries;
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
        public void It_creates_the_target_without_residue() =>
            _result
                .Should()
                .Be(
                    new DmsDataStoreCreateResult.Created(
                        _lane!.Request.PinnedArtifact!,
                        DmsCleanupReport.Clean
                    )
                );

        [Test]
        public void It_had_a_validated_staging_database_and_no_target_at_the_rename_hook() =>
            _observations
                .Should()
                .ContainSingle(observation => observation.Point == DmsProvisioningHookPoint.BeforeRename)
                .Which.Should()
                .BeEquivalentTo(
                    new
                    {
                        StagingIdentity = DmsShapedManifest.SourceIdentity,
                        Target = new DmsLiveTargetState.Absent(),
                    }
                );

        [Test]
        public void It_addressed_the_target_only_by_the_rename_and_the_marker_session() =>
            _lane!
                .TargetMutations.Select(call => call.Member)
                .Should()
                .Equal(
                    nameof(IDmsTargetEngine.RenameIntoTargetAsync),
                    nameof(IDmsTargetEngine.OpenSessionAsync)
                );

        [Test]
        public void It_renamed_the_validated_instance_with_its_claim_into_the_target()
        {
            DmsInstanceIdentity staged = _observations
                .Single(observation => observation.Point == DmsProvisioningHookPoint.BeforeRename)
                .Staging.Should()
                .BeOfType<DmsLiveTargetState.Present>()
                .Subject.Instance;
            _targetAfter
                .Should()
                .Be(
                    new DmsLiveTargetState.Present(
                        new DmsInstanceIdentity.Postgresql(
                            ((DmsInstanceIdentity.Postgresql)staged).Oid,
                            new DmsProvisionClaim(_lane!.Operation, _lane.Request.ExpectedSourceIdentity).Text
                        ),
                        DmsDatabaseState.Online
                    )
                );
        }

        [Test]
        public void It_assigned_the_expected_identity_as_the_marker() =>
            _markerAfter.Should().Be(_lane!.Request.ExpectedSourceIdentity);

        [Test]
        public void It_left_no_staging_database_and_released_the_directory() =>
            (_stagingAfter, _entriesAfter.Length).Should().Be((new DmsLiveTargetState.Absent(), 0));

        [Test]
        public void It_passed_the_hooks_in_order() =>
            _hooks
                .Points.Should()
                .Equal(
                    DmsProvisioningHookPoint.AfterRestoreStarted,
                    DmsProvisioningHookPoint.BeforeRename,
                    DmsProvisioningHookPoint.BeforeMarker,
                    DmsProvisioningHookPoint.BeforeMarkerDispatch
                );
    }

    [TestFixture]
    public class Given_the_artifact_drifts_from_the_manifest
    {
        private Lane? _privilege;
        private Lane? _inventory;
        private readonly LaneHooks _privilegeHooks = new();
        private readonly LaneHooks _inventoryHooks = new();
        private readonly List<HookObservation> _observations = [];
        private DmsDataStoreCreateResult _privilegeResult = null!;
        private DmsDataStoreCreateResult _inventoryResult = null!;
        private DmsLiveTargetState _targetAfter = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            RequirePsqlOrSkip();
            _privilege = await Lane.BuildAsync(
                Lane.ShapedSql("\nGRANT SELECT ON TABLE \"edfi\".\"School\" TO PUBLIC;\n"),
                _privilegeHooks
            );
            _privilegeHooks.Probe = async (point, operation) =>
                _observations.Add(await ObserveAsync(point, operation, _privilege.Target));
            _privilegeResult = await _privilege.CreateAsync();

            _inventory = await Lane.BuildAsync(
                Lane.ShapedSql("\nCREATE SCHEMA \"dmscs\";\n"),
                _inventoryHooks
            );
            _inventoryResult = await _inventory.CreateAsync();
            _targetAfter = await DmsProvisioningTestServer.Engine().InspectAsync(_privilege.Target, default);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_privilege is not null)
            {
                await _privilege.DisposeAsync();
            }

            if (_inventory is not null)
            {
                await _inventory.DisposeAsync();
            }
        }

        [Test]
        public void It_reports_the_extra_grant_as_a_security_state_violation()
        {
            DmsProvisioningFailure failure = _privilegeResult
                .Should()
                .BeOfType<DmsDataStoreCreateResult.Failure>()
                .Subject.Detail;
            (failure.Code, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.SecurityStateViolation,
                        DmsDataStoreProvisioningService.ValidateStagingStep
                            + "."
                            + DmsTargetValidator.PrivilegesCheck
                    )
                );
        }

        [Test]
        public void It_reports_the_extra_schema_as_incompatible()
        {
            DmsProvisioningFailure failure = _inventoryResult
                .Should()
                .BeOfType<DmsDataStoreCreateResult.Failure>()
                .Subject.Detail;
            (failure.Code, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetIncompatible,
                        DmsDataStoreProvisioningService.ValidateStagingStep
                            + "."
                            + DmsTargetValidator.InventoryHashCheck
                    )
                );
        }

        [Test]
        public void It_never_reached_the_rename_hook_and_kept_the_target_absent()
        {
            _privilegeHooks.Points.Should().Equal(DmsProvisioningHookPoint.AfterRestoreStarted);
            _observations
                .Should()
                .OnlyContain(observation => observation.Target is DmsLiveTargetState.Absent);
            _targetAfter.Should().Be(new DmsLiveTargetState.Absent());
        }

        [Test]
        public void It_dropped_both_staging_databases_and_reported_no_residue() =>
            (
                ((DmsDataStoreCreateResult.Failure)_privilegeResult).Cleanup.HasResidue,
                ((DmsDataStoreCreateResult.Failure)_inventoryResult).Cleanup.HasResidue,
                _privilege!.Engine.Calls.Count(call =>
                    call.Member == nameof(IDmsTargetEngine.DropStagingAsync)
                ),
                _inventory!.Engine.Calls.Count(call =>
                    call.Member == nameof(IDmsTargetEngine.DropStagingAsync)
                )
            )
                .Should()
                .Be((false, false, 1, 1));
    }

    [TestFixture]
    public class Given_the_client_loses_the_restore_result_after_the_commit
    {
        private Lane? _lane;
        private readonly LaneHooks _hooks = new();
        private DmsDataStoreCreateResult _result = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            RequirePsqlOrSkip();
            _hooks.Faults.Add(DmsProvisioningFaultPoint.DropConnectionDuringRestore);
            _lane = await Lane.BuildAsync(Lane.ShapedSql(), _hooks);
            _result = await _lane.CreateAsync();
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
        public void It_reads_the_committed_state_and_goes_on_to_create_the_target()
        {
            _result.Should().BeOfType<DmsDataStoreCreateResult.Created>();
            _hooks.Points.Should().Contain(DmsProvisioningHookPoint.BeforeRename);
        }
    }

    [TestFixture]
    public class Given_the_replay_is_ended_by_the_restore_bound
    {
        private Lane? _lane;
        private readonly LaneHooks _hooks = new();
        private DmsDataStoreCreateResult _result = null!;
        private DmsLiveTargetState _targetAfter = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            RequirePsqlOrSkip();
            _lane = await Lane.BuildAsync(
                Lane.ShapedSql("\nSELECT pg_catalog.pg_sleep(60);\n"),
                _hooks,
                TimeSpan.FromSeconds(2)
            );
            _result = await _lane.CreateAsync();
            _targetAfter = await DmsProvisioningTestServer.Engine().InspectAsync(_lane.Target, default);
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
        public void It_reports_a_transient_timeout()
        {
            DmsProvisioningFailure failure = _result
                .Should()
                .BeOfType<DmsDataStoreCreateResult.Failure>()
                .Subject.Detail;
            (failure.Code, failure.Category, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TimedOut,
                        DmsProvisioningFailureCategory.Transient,
                        DmsEngineOperations.RestoreTimedOut
                    )
                );
        }

        [Test]
        public void It_dropped_the_staging_database_and_left_the_target_absent() =>
            (
                ((DmsDataStoreCreateResult.Failure)_result).Cleanup.HasResidue,
                _lane!.Engine.Calls.Count(call => call.Member == nameof(IDmsTargetEngine.DropStagingAsync)),
                _targetAfter
            )
                .Should()
                .Be((false, 1, new DmsLiveTargetState.Absent()));
    }
}
