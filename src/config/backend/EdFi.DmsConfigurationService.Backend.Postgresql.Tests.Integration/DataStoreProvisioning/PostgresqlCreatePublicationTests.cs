// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using FluentAssertions;
using Npgsql;
using static EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning.DmsProvisioningTestServer;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning;

/// <summary>
/// The orchestrator's publication end to end (6.3 steps 8 to 10, 6.9b, 6.7, 6.5 collision tests, Step 3.6)
/// over the real catalog and engine with a real <c>psql</c>. A foreign database injected under the target
/// name at <c>BeforeRename</c>, after the last inspection, is discovered only by the rename itself, and it
/// is read but never touched: same family with another identity, unrelated without one, unrelated with a
/// hand-built one. A rename whose result is lost after the server ran it is resolved by the holder's
/// termination and the instance found under the target name. The marker in doubt resolves both ways: a lost
/// result after the commit is <c>Created</c>; a marker that never reached the server is compensated. A
/// marker the server refuses is compensated; a target replaced before the marker session opens is an
/// instance change and is never dropped.
/// </summary>
[Category(Category)]
public class PostgresqlCreatePublicationTests
{
    private static readonly Guid _foreignIdentity = Guid.Parse("4c3b2a19-0f8e-4d7c-9b6a-5f4e3d2c1b0a");
    private static readonly Guid _sentinelDocument = Guid.Parse("7a6b5c4d-3e2f-4a1b-8c9d-0e1f2a3b4c5d");

    private const string SentinelTable =
        "CREATE TABLE public.collision_sentinel (marker text NOT NULL); INSERT INTO public.collision_sentinel (marker) VALUES ('foreign');";

    private const string ObjectCountQuery =
        "SELECT (SELECT COUNT(*) FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg\\_toast%') + (SELECT COUNT(*) FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname NOT IN ('pg_catalog', 'information_schema'));";

    private static void RequireLane()
    {
        RequireConfiguredOrSkip();
        RequirePsqlOrSkip();
    }

    private static DmsProvisioningFailure FailureOf(DmsDataStoreCreateResult result) =>
        result.Should().BeOfType<DmsDataStoreCreateResult.Failure>().Subject.Detail;

    private static DmsCleanupReport CleanupOf(DmsDataStoreCreateResult result) =>
        result.Should().BeOfType<DmsDataStoreCreateResult.Failure>().Subject.Cleanup;

    /// <summary>The <c>oid</c> under the staging name now, read by inspection only (no session on it).</summary>
    private static async Task<DmsInstanceIdentity.Postgresql> StagedInstanceAsync(DmsOperationId operation) =>
        (DmsInstanceIdentity.Postgresql)
            (await DmsProvisioningTestServer.Engine().InspectAsync(operation.StagingDatabaseName, default))
                .Should()
                .BeOfType<DmsLiveTargetState.Present>()
                .Subject.Instance;

    /// <summary>Everything the collision tests require unchanged about the foreign database (6.5).</summary>
    private sealed record ForeignEvidence(
        DmsInstanceIdentity.Postgresql Instance,
        Guid? Identity,
        string Sentinel,
        bool SentinelDocument,
        long ObjectCount,
        string Inventory
    );

    /// <summary>One collision variant: the foreign database built at <c>BeforeRename</c> by <paramref name="build"/>, with every piece of evidence taken before and after.</summary>
    private sealed class Collision : IAsyncDisposable
    {
        private Collision(Lane lane) => Lane = lane;

        private static async Task<ForeignEvidence> ForeignEvidenceAsync(DmsTargetDatabaseName target)
        {
            TestDatabase foreign = new(target.Value);
            DmsInstanceIdentity.Postgresql instance = await foreign.CatalogInstanceAsync();
            Guid? identity = await OptionalAsync<Guid>(
                foreign,
                "SELECT \"SourceIdentity\" FROM \"dms\".\"DataStoreIdentity\";"
            );
            string sentinel = await foreign.ScalarAsync<string>(
                "SELECT string_agg(marker, ',' ORDER BY marker) FROM public.collision_sentinel;"
            );
            bool sentinelDocument =
                await OptionalAsync<bool>(
                    foreign,
                    $"SELECT EXISTS (SELECT 1 FROM \"dms\".\"Document\" WHERE \"DocumentUuid\" = '{_sentinelDocument:D}');"
                ) ?? false;
            long objectCount = await foreign.ScalarAsync<long>(ObjectCountQuery);
            ForeignEvidence evidence = new(
                instance,
                identity,
                sentinel,
                sentinelDocument,
                objectCount,
                await InventoryFingerprintAsync(target, instance)
            );
            await WaitForNoSessionsOnAsync(target.Value);
            return evidence;
        }

        /// <summary>The value, or null when the relation it reads does not exist.</summary>
        private static async Task<T?> OptionalAsync<T>(TestDatabase database, string sql)
            where T : struct
        {
            try
            {
                return await database.ScalarAsync<T>(sql);
            }
            catch (PostgresException exception)
                when (exception.SqlState
                        is PostgresErrorCodes.UndefinedTable
                            or PostgresErrorCodes.InvalidSchemaName
                )
            {
                return null;
            }
        }

        /// <summary>The canonical inventory hash the engine's facts describe, or the reason it cannot be built.</summary>
        private static async Task<string> InventoryFingerprintAsync(
            DmsTargetDatabaseName target,
            DmsInstanceIdentity instance
        )
        {
            PostgresqlDmsTargetEngine side = DmsProvisioningTestServer.Engine();
            DmsSession session = (await side.OpenSessionAsync(target, instance, default))
                .Should()
                .BeOfType<DmsSessionOpenResult.Opened>()
                .Subject.Session;
            await using (session)
            {
                DmsTargetFacts facts;
                try
                {
                    facts = await side.ReadTargetFactsAsync(session, default);
                }
                catch (DmsProvisioningFailedException exception)
                {
                    return "unreadable:" + exception.Failure.Diagnostic.Operation;
                }

                return DmsTargetValidator.TryBuildInventory(
                    facts,
                    out CanonicalInventory? inventory,
                    out string? defect
                )
                    ? CanonicalInventoryHash.Compute(inventory)
                    : "defect:" + defect;
            }
        }

        /// <summary>Names of every staging database on the server other than <paramref name="own"/>.</summary>
        private static async Task<string[]> OtherStagingDatabasesAsync(DmsDatabaseName own)
        {
            List<string> names = [];
            await using NpgsqlConnection connection = await AdminConnection().OpenMaintenanceAsync(default);
            await using NpgsqlCommand command = new(
                "SELECT d.datname FROM pg_catalog.pg_database d WHERE d.datname LIKE 'edfi_dms_provision_stage_%' AND d.datname <> @own ORDER BY 1;",
                connection
            );
            command.Parameters.AddWithValue("own", own.Value);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
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

        public static async Task<Collision> RunAsync(Func<TestDatabase, Task> build)
        {
            LaneHooks hooks = new();
            Collision collision = new(await Lane.BuildAsync(Lane.ShapedSql(), hooks));
            hooks.Probe = async (point, operation) =>
            {
                if (point != DmsProvisioningHookPoint.BeforeRename)
                {
                    return;
                }

                await ExecuteOnServerAsync(
                    $"CREATE DATABASE {PostgresqlIdentifiers.Quote(collision.Lane.Target.Value)};"
                );
                await build(new TestDatabase(collision.Lane.Target.Value));
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
            collision.OwnStagingAfter = await DmsProvisioningTestServer
                .Engine()
                .InspectAsync(collision.Lane.Operation.StagingDatabaseName, default);
            return collision;
        }

        public ValueTask DisposeAsync() => Lane.DisposeAsync();
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
            RequireLane();
            await EnsureEnqueueOwnerRoleAsync();
            string shaped = await File.ReadAllTextAsync(FixturePath("dms-shaped-database.sql"));

            // (a) restored out of band from the same artifact, with another identity and sentinels.
            _sameFamily = await Collision.RunAsync(async foreign =>
            {
                await foreign.ExecuteAsync(shaped);
                await foreign.ExecuteAsync(
                    $"UPDATE \"dms\".\"DataStoreIdentity\" SET \"SourceIdentity\" = '{_foreignIdentity:D}'; INSERT INTO \"dms\".\"Document\" (\"DocumentUuid\", \"ResourceKeyId\") VALUES ('{_sentinelDocument:D}', 1); {SentinelTable}"
                );
            });

            // (b1) unrelated, with the sentinel table only.
            _unrelatedWithoutIdentity = await Collision.RunAsync(foreign =>
                foreign.ExecuteAsync(SentinelTable)
            );

            // (b2) unrelated, with a hand-built identity singleton holding another identity.
            _unrelatedWithIdentity = await Collision.RunAsync(foreign =>
                foreign.ExecuteAsync(
                    $"{SentinelTable} CREATE SCHEMA \"dms\"; CREATE TABLE \"dms\".\"DataStoreIdentity\" (\"DataStoreIdentitySingletonId\" smallint PRIMARY KEY, \"SourceIdentity\" uuid NOT NULL); INSERT INTO \"dms\".\"DataStoreIdentity\" VALUES (1, '{_foreignIdentity:D}');"
                )
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
                        .Which.Failure.Class.Should()
                        .Be(DmsCommandFailureClass.DuplicateName)
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
            _unrelatedWithoutIdentity!.Before.Identity.Should().BeNull();
            _unrelatedWithIdentity!.Before.Identity.Should().Be(_foreignIdentity);
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
        private DmsInstanceIdentity.Postgresql _staged = null!;
        private DmsServerSessionId _holder;
        private DmsDataStoreCreateResult _result = null!;
        private DmsLiveTargetState _targetAfter = null!;
        private DmsLiveTargetState _stagingAfter = null!;
        private Guid _identityAfter;
        private bool _holderPresentAfter = true;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireLane();
            _lane = await Lane.BuildAsync(Lane.ShapedSql(), _hooks);
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
            PostgresqlDmsTargetEngine side = DmsProvisioningTestServer.Engine();
            _targetAfter = await side.InspectAsync(_lane.Target, default);
            _stagingAfter = await side.InspectAsync(_lane.Operation.StagingDatabaseName, default);
            _identityAfter = await _lane.TargetIdentityAsync();
            _holderPresentAfter = await SessionPresentAsync(_holder);
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
        public void It_ends_as_lock_lost_for_inspection_at_the_rename()
        {
            DmsProvisioningFailure failure = FailureOf(_result);
            (failure.Code, failure.Category, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.LockLost,
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
        public void It_found_the_validated_instance_under_the_target_unmarked_and_reported_it()
        {
            _targetAfter.Should().Be(new DmsLiveTargetState.Present(_staged, DmsDatabaseState.Online));
            _identityAfter.Should().Be(DmsShapedManifest.SourceIdentity);
            CleanupOf(_result)
                .Residue.Should()
                .Equal(new DmsCleanupResidue(DmsResidueKind.UnmarkedTarget, _lane!.Target.Value));
        }

        [Test]
        public void It_issued_no_further_mutation_and_left_no_staging_database()
        {
            _lane!.Engine.CountOf(nameof(IDmsTargetEngine.AssignMarkerAsync)).Should().Be(0);
            _lane.Engine.CountOf(nameof(IDmsTargetEngine.DropInstanceAsync)).Should().Be(0);
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
            RequireLane();
            _hooks.Faults.Add(DmsProvisioningFaultPoint.FailAfterMarkerExecuted);
            _lane = await Lane.BuildAsync(Lane.ShapedSql(), _hooks);
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
        public void It_terminated_the_marker_session_and_re_read_on_a_new_one() =>
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
            RequireLane();
            _lane = await Lane.BuildAsync(Lane.ShapedSql(), _hooks);
            _lane.Engine.InsteadOfMarker = session => new DmsCommandOutcome.Unknown(session.ServerSession);
            _result = await _lane.CreateAsync();
            PostgresqlDmsTargetEngine side = DmsProvisioningTestServer.Engine();
            _targetAfter = await side.InspectAsync(_lane.Target, default);
            _stagingAfter = await side.InspectAsync(_lane.Operation.StagingDatabaseName, default);
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
            RequireLane();
            _lane = await Lane.BuildAsync(Lane.ShapedSql(), _hooks);
            _hooks.Probe = async (point, _) =>
            {
                if (point == DmsProvisioningHookPoint.BeforeMarker)
                {
                    // A constraint that only the package identity satisfies: the marker is refused by the server.
                    await new TestDatabase(_lane.Target.Value).ExecuteAsync(
                        $"ALTER TABLE \"dms\".\"DataStoreIdentity\" ADD CONSTRAINT \"dms1438_package_only\" CHECK (\"SourceIdentity\" = '{DmsShapedManifest.SourceIdentity:D}');"
                    );
                    await WaitForNoSessionsOnAsync(_lane.Target.Value);
                }
            };
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
        public void It_reports_the_refused_marker_after_compensating()
        {
            DmsProvisioningFailure failure = FailureOf(_result);
            (failure.Code, failure.Diagnostic.ProviderErrorCode, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.IdentityAssignmentFailed,
                        PostgresErrorCodes.CheckViolation,
                        PostgresqlPublicationQueries.AssignMarkerStep
                    )
                );
        }

        [Test]
        public void It_dropped_the_renamed_target_and_left_no_residue() =>
            (
                _targetAfter,
                CleanupOf(_result).HasResidue,
                _lane!.Engine.CountOf(nameof(IDmsTargetEngine.DropInstanceAsync))
            )
                .Should()
                .Be((new DmsLiveTargetState.Absent(), false, 1));
    }

    [TestFixture]
    public class Given_the_target_is_replaced_before_the_marker_session_opens
    {
        private Lane? _lane;
        private readonly LaneHooks _hooks = new();
        private DmsInstanceIdentity.Postgresql _staged = null!;
        private DmsDataStoreCreateResult _result = null!;
        private DmsLiveTargetState _targetAfter = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireLane();
            _lane = await Lane.BuildAsync(Lane.ShapedSql(), _hooks);
            _hooks.Probe = async (point, operation) =>
            {
                if (point == DmsProvisioningHookPoint.BeforeRename)
                {
                    _staged = await StagedInstanceAsync(operation);
                }
                else if (point == DmsProvisioningHookPoint.BeforeMarker)
                {
                    string target = PostgresqlIdentifiers.Quote(_lane.Target.Value);
                    await ExecuteOnServerAsync($"DROP DATABASE {target} WITH (FORCE);");
                    await ExecuteOnServerAsync($"CREATE DATABASE {target};");
                }
            };
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
            ((DmsInstanceIdentity.Postgresql)((DmsLiveTargetState.Present)_targetAfter).Instance)
                .Oid.Should()
                .NotBe(_staged.Oid);
        }
    }
}
