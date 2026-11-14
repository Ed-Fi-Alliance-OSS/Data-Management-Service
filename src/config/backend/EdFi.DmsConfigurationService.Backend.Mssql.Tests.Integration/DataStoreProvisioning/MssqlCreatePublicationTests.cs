// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using static EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning.MssqlProvisioningTestServer;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning;

/// <summary>
/// The orchestrator's SQL Server publication end to end (6.4 steps 9 to 12, 6.9b, 6.7, 6.5 collision tests, Step 4.6)
/// over the real catalog and engine, with a package whose artifact is a server-made backup of the committed DMS-shaped
/// fixture. A published target keeps the staging database's <c>database_id</c> and primary file, its
/// <c>create_date</c> changes, and the marker session and every later guard use the re-captured snapshot. A foreign
/// database injected under the target name at <c>BeforeRename</c>, after the last inspection, is discovered only by
/// the rename and is read but never touched: same family with another identity, unrelated without one, unrelated with a
/// hand-built one. A rename whose result is lost after the server ran it is resolved by the holder's termination and
/// the instance found under the target name. The marker in doubt resolves both ways; a marker the server refuses is
/// compensated on exactly the renamed instance; a target replaced before the marker session opens is an instance change
/// and is never dropped. Needs a staging directory shared with the server.
/// </summary>
[Category(Category)]
public class MssqlCreatePublicationTests
{
    private static readonly Guid _foreignIdentity = Guid.Parse("4c3b2a19-0f8e-4d7c-9b6a-5f4e3d2c1b0a");
    private static readonly Guid _sentinelDocument = Guid.Parse("7a6b5c4d-3e2f-4a1b-8c9d-0e1f2a3b4c5d");

    private const string SentinelTable =
        "CREATE TABLE [dbo].[collision_sentinel] ([marker] nvarchar(50) NOT NULL); INSERT INTO [dbo].[collision_sentinel] ([marker]) VALUES (N'foreign');";

    private static DmsProvisioningFailure FailureOf(DmsDataStoreCreateResult result) =>
        result.Should().BeOfType<DmsDataStoreCreateResult.Failure>().Subject.Detail;

    private static DmsCleanupReport CleanupOf(DmsDataStoreCreateResult result) =>
        result.Should().BeOfType<DmsDataStoreCreateResult.Failure>().Subject.Cleanup;

    /// <summary>The instance under the staging name now, read by inspection only (no session on it).</summary>
    private static async Task<DmsInstanceIdentity.Mssql> StagedInstanceAsync(DmsOperationId operation) =>
        (DmsInstanceIdentity.Mssql)
            (await Engine().InspectAsync(operation.StagingDatabaseName, default))
                .Should()
                .BeOfType<DmsLiveTargetState.Present>()
                .Subject.Instance;

    private static Task<DmsLiveTargetState> InspectAsync(DmsDatabaseName name) =>
        Engine().InspectAsync(name, default);

    /// <summary>Everything the collision tests require unchanged about the foreign database (6.5).</summary>
    private sealed record ForeignEvidence(
        DmsInstanceIdentity.Mssql Instance,
        Guid? Identity,
        string Sentinel,
        bool SentinelDocument,
        int ObjectCount,
        string Inventory
    );

    /// <summary>One collision variant: the foreign database built at <c>BeforeRename</c> by a script, with every piece of evidence taken before and after.</summary>
    private sealed class Collision : IAsyncDisposable
    {
        private Collision(Lane lane) => Lane = lane;

        private static async Task<ForeignEvidence> ForeignEvidenceAsync(DmsTargetDatabaseName target)
        {
            TestDatabase foreign = new(target.Value);
            DmsInstanceIdentity.Mssql instance = await foreign.CatalogInstanceAsync();
            Guid? identity = await OptionalAsync(
                foreign,
                "SELECT CONVERT(nvarchar(36), [SourceIdentity]) FROM [dms].[DataStoreIdentity];"
            )
                is string text
                ? Guid.Parse(text)
                : null;
            string sentinel = await foreign.ScalarAsync<string>(
                "SELECT STRING_AGG([marker], N',') WITHIN GROUP (ORDER BY [marker]) FROM [dbo].[collision_sentinel];"
            );
            bool sentinelDocument =
                await OptionalAsync(
                    foreign,
                    $"SELECT CONVERT(nvarchar(5), COUNT(*)) FROM [dms].[Document] WHERE [DocumentUuid] = '{_sentinelDocument:D}';"
                )
                is "1";
            int objectCount = await foreign.ScalarAsync<int>(TargetEvidence.ObjectCountQuery);
            string inventory = await TargetEvidence.InventoryFingerprintAsync(target, instance);
            await WaitForNoSessionsOnAsync(target.Value);
            return new ForeignEvidence(
                instance,
                identity,
                sentinel,
                sentinelDocument,
                objectCount,
                inventory
            );
        }

        /// <summary>The text value, or null when the object it reads does not exist.</summary>
        private static async Task<string?> OptionalAsync(TestDatabase database, string sql)
        {
            try
            {
                return await database.ScalarAsync<string>(sql);
            }
            catch (SqlException exception) when (exception.Number == 208)
            {
                return null;
            }
        }

        /// <summary>Names of every staging database on the server other than <paramref name="own"/>.</summary>
        private static async Task<string[]> OtherStagingDatabasesAsync(DmsDatabaseName own)
        {
            List<string> names = [];
            await using SqlConnection connection = await AdminConnection().OpenMaintenanceAsync(default);
            await using SqlCommand command = new(
                "SELECT d.name FROM sys.databases d WHERE d.name LIKE N'edfi\\_dms\\_provision\\_stage\\_%' ESCAPE N'\\' AND d.name <> @own ORDER BY 1;",
                connection
            );
            command.Parameters.AddWithValue("@own", own.Value);
            await using SqlDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(0));
            }

            return [.. names];
        }

        public Lane Lane { get; }

        public DmsDataStoreCreateResult Result { get; private set; } = null!;

        public ForeignEvidence Before { get; private set; } = null!;

        public ForeignEvidence After { get; private set; } = null!;

        public string[] OtherStagingBefore { get; private set; } = [];

        public string[] OtherStagingAfter { get; private set; } = [];

        public DmsLiveTargetState OwnStagingAfter { get; private set; } = null!;

        public static async Task<Collision> RunAsync(byte[] artifact, string script)
        {
            LaneHooks hooks = new();
            Collision collision = new(await Lane.BuildAsync(artifact, hooks));
            hooks.Probe = async (point, operation) =>
            {
                if (point != DmsProvisioningHookPoint.BeforeRename)
                {
                    return;
                }

                await ExecuteOnServerAsync(
                    $"CREATE DATABASE {MssqlIdentifiers.Quote(collision.Lane.Target.Value)};"
                );
                await new TestDatabase(collision.Lane.Target.Value).ExecuteAsync(script);
                collision.Before = await ForeignEvidenceAsync(collision.Lane.Target);
                collision.OtherStagingBefore = await OtherStagingDatabasesAsync(
                    operation.StagingDatabaseName
                );
            };
            collision.Result = await collision.Lane.CreateAsync();
            collision.After = await ForeignEvidenceAsync(collision.Lane.Target);
            collision.OtherStagingAfter = await OtherStagingDatabasesAsync(
                collision.Lane.Operation.StagingDatabaseName
            );
            collision.OwnStagingAfter = await InspectAsync(collision.Lane.Operation.StagingDatabaseName);
            return collision;
        }

        public ValueTask DisposeAsync() => Lane.DisposeAsync();
    }

    [TestFixture]
    public class Given_a_valid_package_is_published
    {
        private Lane? _lane;
        private readonly LaneHooks _hooks = new();
        private DmsInstanceIdentity.Mssql _staged = null!;
        private DmsDataStoreCreateResult _result = null!;
        private DmsInstanceIdentity.Mssql _targetAfter = null!;
        private DmsLiveTargetState _stagingAfter = null!;
        private Guid _identityAfter;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireSharedStagingOrSkip();
            _lane = await Lane.BuildAsync(await ShapedBackup.CreateAsync(), _hooks);
            _hooks.Probe = async (point, operation) =>
            {
                if (point == DmsProvisioningHookPoint.BeforeRename)
                {
                    _staged = await StagedInstanceAsync(operation);

                    // datetime has a resolution of about 3 ms: the rename must fall in a later tick than the restore.
                    await WaitForServerClockPastAsync(_staged);
                }
            };
            _result = await _lane.CreateAsync();
            _targetAfter = await new TestDatabase(_lane.Target.Value).CatalogInstanceAsync();
            _stagingAfter = await InspectAsync(_lane.Operation.StagingDatabaseName);
            _identityAfter = await _lane.TargetIdentityAsync();
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
        public void It_creates_the_target_with_the_expected_identity() =>
            (_result is DmsDataStoreCreateResult.Created, _identityAfter)
                .Should()
                .Be((true, _lane!.Request.ExpectedSourceIdentity));

        [Test]
        public void It_keeps_the_database_id_and_the_primary_file_and_frees_the_staging_name() =>
            (_targetAfter.IsSameStableInstance(_staged), _stagingAfter)
                .Should()
                .Be((true, new DmsLiveTargetState.Absent()));

        [Test]
        public void It_recaptures_the_create_date_the_rename_changed() =>
            (
                _lane!
                    .Engine.RenameOutcomes.Should()
                    .ContainSingle()
                    .Which.Should()
                    .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Succeeded>()
                    .Subject.Value,
                _targetAfter.CreateDateSnapshot == _staged.CreateDateSnapshot
            )
                .Should()
                .Be(((DmsInstanceIdentity)_targetAfter, false));

        [Test]
        public void It_binds_the_marker_session_to_the_recaptured_snapshot() =>
            _lane!.Engine.OpenedInstances[^1].Should().Be(_targetAfter);
    }

    [TestFixture]
    public class Given_a_foreign_database_takes_the_target_name_before_the_rename
    {
        private Collision? _sameFamily;
        private Collision? _unrelatedWithoutIdentity;
        private Collision? _unrelatedWithIdentity;

        private IEnumerable<Collision> All =>
            [_sameFamily!, _unrelatedWithoutIdentity!, _unrelatedWithIdentity!];

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireSharedStagingOrSkip();
            byte[] artifact = await ShapedBackup.CreateAsync();
            string shaped = await File.ReadAllTextAsync(FixturePath("dms-shaped-database.sql"));

            // (a) the same family, with another identity and sentinels.
            _sameFamily = await Collision.RunAsync(
                artifact,
                $"{shaped}\nGO\nUPDATE [dms].[DataStoreIdentity] SET [SourceIdentity] = '{_foreignIdentity:D}'; INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId]) VALUES ('{_sentinelDocument:D}', 2); {SentinelTable}"
            );

            // (b1) unrelated, with the sentinel table only.
            _unrelatedWithoutIdentity = await Collision.RunAsync(artifact, SentinelTable);

            // (b2) unrelated, with a hand-built identity singleton holding another identity.
            _unrelatedWithIdentity = await Collision.RunAsync(
                artifact,
                $"{SentinelTable}\nGO\nCREATE SCHEMA [dms];\nGO\nCREATE TABLE [dms].[DataStoreIdentity] ([DataStoreIdentitySingletonId] smallint NOT NULL PRIMARY KEY, [SourceIdentity] uniqueidentifier NOT NULL); INSERT INTO [dms].[DataStoreIdentity] VALUES (1, '{_foreignIdentity:D}');"
            );
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            foreach (
                Collision? collision in new[]
                {
                    _sameFamily,
                    _unrelatedWithoutIdentity,
                    _unrelatedWithIdentity,
                }
            )
            {
                if (collision is not null)
                {
                    await collision.DisposeAsync();
                }
            }
        }

        [Test]
        public void It_classifies_each_variant_by_its_identity() =>
            All.Select(collision => FailureOf(collision.Result).Code)
                .Should()
                .Equal(
                    DmsProvisioningFailureCode.TargetUnowned,
                    DmsProvisioningFailureCode.TargetUnverifiable,
                    DmsProvisioningFailureCode.TargetUnowned
                );

        [Test]
        public void It_discovered_each_collision_by_exactly_one_rename_refused_as_a_duplicate_name() =>
            All.Should()
                .AllSatisfy(collision =>
                    collision
                        .Lane.Engine.RenameOutcomes.Should()
                        .ContainSingle()
                        .Which.Should()
                        .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Failed>()
                        .Which.Failure.Should()
                        .Match<DmsCommandFailure>(failure =>
                            failure.Class == DmsCommandFailureClass.DuplicateName
                            && failure.Diagnostic.ProviderErrorCode == "1801"
                        )
                );

        [Test]
        public void It_addressed_the_foreign_database_with_nothing_but_the_rename_and_the_reconcile_reads() =>
            All.Should()
                .AllSatisfy(collision =>
                {
                    collision
                        .Lane.TargetMutations.Select(call => call.Member)
                        .Should()
                        .Equal(
                            nameof(IDmsTargetEngine.RenameIntoTargetAsync),
                            nameof(IDmsTargetEngine.OpenSessionAsync)
                        );
                    (
                        collision.Lane.Engine.CountOf(nameof(IDmsTargetEngine.AssignMarkerAsync)),
                        collision.Lane.Engine.CountOf(nameof(IDmsTargetEngine.DropInstanceAsync)),
                        collision.Lane.Engine.CountOf(nameof(IDmsTargetEngine.FenceAsync))
                    )
                        .Should()
                        .Be((0, 0, 0));
                });

        [Test]
        public void It_left_the_foreign_database_exactly_as_it_was() =>
            All.Should().AllSatisfy(collision => collision.After.Should().Be(collision.Before));

        [Test]
        public void It_kept_the_sentinels_that_prove_it_read_the_foreign_database()
        {
            _sameFamily!
                .Before.Should()
                .Match<ForeignEvidence>(evidence =>
                    evidence.Identity == _foreignIdentity
                    && evidence.SentinelDocument
                    && evidence.Sentinel == "foreign"
                );
            (_unrelatedWithoutIdentity!.Before.Identity, _unrelatedWithIdentity!.Before.Identity)
                .Should()
                .Be(((Guid?)null, (Guid?)_foreignIdentity));
        }

        [Test]
        public void It_dropped_only_its_own_staging_database() =>
            All.Should()
                .AllSatisfy(collision =>
                {
                    collision.OwnStagingAfter.Should().Be(new DmsLiveTargetState.Absent());
                    collision.OtherStagingAfter.Should().Equal(collision.OtherStagingBefore);
                });
    }

    [TestFixture]
    public class Given_the_rename_result_is_lost_after_the_server_renamed
    {
        private Lane? _lane;
        private readonly LaneHooks _hooks = new();
        private DmsInstanceIdentity.Mssql _staged = null!;
        private DmsServerSessionId _holder;
        private DmsDataStoreCreateResult _result = null!;
        private DmsLiveTargetState _targetAfter = null!;
        private DmsLiveTargetState _stagingAfter = null!;
        private Guid _identityAfter;
        private bool _holderPresentAfter = true;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireSharedStagingOrSkip();
            _lane = await Lane.BuildAsync(await ShapedBackup.CreateAsync(), _hooks);
            _hooks.Probe = async (point, operation) =>
            {
                if (point == DmsProvisioningHookPoint.BeforeRename)
                {
                    _staged = await StagedInstanceAsync(operation);
                }
            };
            _lane.Engine.AfterRename = (outcome, holder) =>
            {
                _holder = holder.Session;
                return outcome is DmsCommandOutcome<DmsInstanceIdentity>.Succeeded
                    ? new DmsCommandOutcome<DmsInstanceIdentity>.Unknown(holder.Session)
                    : outcome;
            };
            _result = await _lane.CreateAsync();
            _targetAfter = await InspectAsync(_lane.Target);
            _stagingAfter = await InspectAsync(_lane.Operation.StagingDatabaseName);
            _identityAfter = await _lane.TargetIdentityAsync();
            _holderPresentAfter = await SessionExistsAsync(_holder);
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
        public void It_reports_a_rename_found_done_as_an_unassigned_identity_for_inspection()
        {
            DmsProvisioningFailure failure = FailureOf(_result);
            (failure.Code, failure.Category, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.IdentityAssignmentFailed,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.RenameIntoTargetStep
                    )
                );
        }

        [Test]
        public void It_proved_the_holder_session_gone() =>
            (
                _lane!
                    .Engine.Calls.SkipWhile(call =>
                        call.Member != nameof(IDmsTargetEngine.RenameIntoTargetAsync)
                    )
                    .Any(call => call.Member == nameof(IDmsTargetEngine.TerminateSessionAsync)),
                _holderPresentAfter
            )
                .Should()
                .Be((true, false));

        [Test]
        public void It_found_the_validated_instance_under_the_target_by_its_stable_pair_unmarked_and_reported_it()
        {
            _targetAfter
                .Should()
                .BeOfType<DmsLiveTargetState.Present>()
                .Which.Instance.IsSameStableInstance(_staged)
                .Should()
                .BeTrue();
            _identityAfter.Should().Be(MssqlDmsShapedManifest.SourceIdentity);
            CleanupOf(_result)
                .Residue.Should()
                .Equal(new DmsCleanupResidue(DmsResidueKind.UnmarkedTarget, _lane!.Target.Value));
        }

        [Test]
        public void It_issued_no_further_mutation_and_left_no_staging_database()
        {
            (
                _lane!.Engine.CountOf(nameof(IDmsTargetEngine.AssignMarkerAsync)),
                _lane.Engine.CountOf(nameof(IDmsTargetEngine.DropInstanceAsync))
            )
                .Should()
                .Be((0, 0));
            _stagingAfter.Should().Be(new DmsLiveTargetState.Absent());
        }
    }

    [TestFixture]
    public class Given_the_client_loses_the_marker_result_after_the_commit
    {
        private Lane? _lane;
        private readonly LaneHooks _hooks = new();
        private DmsDataStoreCreateResult _result = null!;
        private Guid _identityAfter;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireSharedStagingOrSkip();
            _hooks.Faults.Add(DmsProvisioningFaultPoint.FailAfterMarkerExecuted);
            _lane = await Lane.BuildAsync(await ShapedBackup.CreateAsync(), _hooks);
            _result = await _lane.CreateAsync();
            _identityAfter = await _lane.TargetIdentityAsync();
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
        public void It_resolves_the_marker_as_committed_and_returns_created() =>
            (_result is DmsDataStoreCreateResult.Created, _identityAfter)
                .Should()
                .Be((true, _lane!.Request.ExpectedSourceIdentity));

        [Test]
        public void It_terminated_the_marker_session_and_re_read_on_a_new_one_bound_to_the_recaptured_snapshot()
        {
            _lane!
                .Engine.Calls.SkipWhile(call => call.Member != nameof(IDmsTargetEngine.AssignMarkerAsync))
                .Select(call => call.Member)
                .Should()
                .Equal(
                    nameof(IDmsTargetEngine.AssignMarkerAsync),
                    nameof(IDmsTargetEngine.TerminateSessionAsync),
                    nameof(IDmsTargetEngine.OpenSessionAsync),
                    nameof(IDmsTargetEngine.ReadIdentityAsync)
                );
            DmsInstanceIdentity renamed = (
                (DmsCommandOutcome<DmsInstanceIdentity>.Succeeded)_lane.Engine.RenameOutcomes.Single()
            ).Value;
            _lane.Engine.OpenedInstances[^1].Should().Be(renamed);
        }
    }

    [TestFixture]
    public class Given_a_marker_in_doubt_never_reached_the_server
    {
        private Lane? _lane;
        private readonly LaneHooks _hooks = new();
        private DmsDataStoreCreateResult _result = null!;
        private DmsLiveTargetState _targetAfter = null!;
        private DmsLiveTargetState _stagingAfter = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireSharedStagingOrSkip();
            _lane = await Lane.BuildAsync(await ShapedBackup.CreateAsync(), _hooks);
            _lane.Engine.InsteadOfMarker = session => new DmsCommandOutcome.Unknown(session.ServerSession);
            _result = await _lane.CreateAsync();
            _targetAfter = await InspectAsync(_lane.Target);
            _stagingAfter = await InspectAsync(_lane.Operation.StagingDatabaseName);
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
        public void It_finds_the_package_identity_and_compensates()
        {
            DmsProvisioningFailure failure = FailureOf(_result);
            (failure.Code, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.IdentityAssignmentFailed,
                        DmsDataStoreProvisioningService.ResolveMarkerStep
                    )
                );
            _lane!.Engine.CountOf(nameof(IDmsTargetEngine.DropInstanceAsync)).Should().Be(1);
        }

        [Test]
        public void It_leaves_neither_the_target_nor_the_staging_database_nor_residue() =>
            (_targetAfter, _stagingAfter, CleanupOf(_result).HasResidue)
                .Should()
                .Be((new DmsLiveTargetState.Absent(), new DmsLiveTargetState.Absent(), false));
    }

    [TestFixture]
    public class Given_the_server_refuses_the_marker
    {
        private Lane? _lane;
        private readonly LaneHooks _hooks = new();
        private DmsDataStoreCreateResult _result = null!;
        private DmsLiveTargetState _targetAfter = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireSharedStagingOrSkip();
            _lane = await Lane.BuildAsync(await ShapedBackup.CreateAsync(), _hooks);
            _hooks.Probe = async (point, _) =>
            {
                if (point == DmsProvisioningHookPoint.BeforeMarker)
                {
                    // A constraint that only the package identity satisfies: the marker is refused by the server.
                    await new TestDatabase(_lane.Target.Value).ExecuteAsync(
                        $"ALTER TABLE [dms].[DataStoreIdentity] ADD CONSTRAINT [dms1438_package_only] CHECK ([SourceIdentity] = '{MssqlDmsShapedManifest.SourceIdentity:D}');"
                    );
                    await WaitForNoSessionsOnAsync(_lane.Target.Value);
                }
            };
            _result = await _lane.CreateAsync();
            _targetAfter = await InspectAsync(_lane.Target);
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
        public void It_reports_the_refused_marker_after_compensating()
        {
            DmsProvisioningFailure failure = FailureOf(_result);
            (failure.Code, failure.Diagnostic.ProviderErrorCode, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.IdentityAssignmentFailed,
                        "547",
                        MssqlPublicationQueries.AssignMarkerStep
                    )
                );
        }

        [Test]
        public void It_dropped_exactly_the_renamed_instance_and_left_no_residue()
        {
            DmsInstanceIdentity renamed = (
                (DmsCommandOutcome<DmsInstanceIdentity>.Succeeded)_lane!.Engine.RenameOutcomes.Single()
            ).Value;
            _lane.Engine.DroppedInstances.Should().Equal(renamed);
            (_targetAfter, CleanupOf(_result).HasResidue)
                .Should()
                .Be((new DmsLiveTargetState.Absent(), false));
        }
    }

    [TestFixture]
    public class Given_the_target_is_replaced_before_the_marker_session_opens
    {
        private Lane? _lane;
        private readonly LaneHooks _hooks = new();
        private DmsInstanceIdentity.Mssql _staged = null!;
        private DmsDataStoreCreateResult _result = null!;
        private DmsLiveTargetState _targetAfter = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireSharedStagingOrSkip();
            _lane = await Lane.BuildAsync(await ShapedBackup.CreateAsync(), _hooks);
            _hooks.Probe = async (point, operation) =>
            {
                if (point == DmsProvisioningHookPoint.BeforeRename)
                {
                    _staged = await StagedInstanceAsync(operation);
                }
                else if (point == DmsProvisioningHookPoint.BeforeMarker)
                {
                    await new TestDatabase(_lane.Target.Value).DisposeAsync();
                    await ExecuteOnServerAsync(
                        $"CREATE DATABASE {MssqlIdentifiers.Quote(_lane.Target.Value)};"
                    );
                }
            };
            _result = await _lane.CreateAsync();
            _targetAfter = await InspectAsync(_lane.Target);
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
        public void It_reports_the_instance_change_when_the_marker_session_is_refused()
        {
            DmsProvisioningFailure failure = FailureOf(_result);
            (failure.Code, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.InstanceChanged,
                        DmsDataStoreProvisioningService.OpenTargetSessionStep
                    )
                );
        }

        [Test]
        public void It_neither_marks_nor_drops_the_database_now_under_the_name()
        {
            (
                _lane!.Engine.CountOf(nameof(IDmsTargetEngine.AssignMarkerAsync)),
                _lane.Engine.CountOf(nameof(IDmsTargetEngine.DropInstanceAsync)),
                CleanupOf(_result).HasResidue
            )
                .Should()
                .Be((0, 0, false));
            _targetAfter
                .Should()
                .BeOfType<DmsLiveTargetState.Present>()
                .Which.Instance.IsSameStableInstance(_staged)
                .Should()
                .BeFalse();
        }
    }
}
