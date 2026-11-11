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
/// The staging database commands against a live server (6.3 step 4, 6.7, Step 3.5): a staging database is
/// created and claimed on the holder session and comes back as the instance the catalog shows; a name
/// already taken is a definite duplicate-name failure that touches the existing database; the drop
/// re-verifies the instance, succeeds once the name is free, and refuses a different database under the
/// operation's name; a lost holder refuses the create before anything is sent.
/// </summary>
[Category(Category)]
public class PostgresqlStagingTests
{
    private static readonly TimeSpan _bound = TimeSpan.FromSeconds(20);
    private static readonly ProtectedDatabaseNames _protectedNames = ProtectedDatabaseNames.Create(
        "edfi_configurationservice",
        []
    );

    private static DmsTargetDatabaseName TargetName(string purpose) =>
        DmsTargetDatabaseName.TryCreate(
            $"dms1438_{purpose}_{DmsOperationId.New().Value}",
            _protectedNames,
            out DmsTargetDatabaseName name
        ) == DmsTargetNameValidation.Valid
            ? name
            : throw new InvalidOperationException("The target name does not satisfy the rules.");

    private static async Task<IDmsTargetLockHolder> AcquireAsync(
        PostgresqlDmsTargetEngine engine,
        DmsTargetDatabaseName name,
        DmsOperationId operation
    ) =>
        await engine.AcquireTargetLockAsync(name, operation, default) is DmsTargetLockResult.Acquired acquired
            ? acquired.Holder
            : throw new InvalidOperationException("The target lock was not acquired.");

    private static Task DropByNameAsync(DmsDatabaseName name) =>
        ExecuteOnServerAsync(
            $"DROP DATABASE IF EXISTS {PostgresqlIdentifiers.Quote(name.Value)} WITH (FORCE);"
        );

    [TestFixture]
    public class Given_a_staging_database_is_created_and_dropped
    {
        private DmsOperationId _operation;
        private DmsProvisionClaim _claim = null!;
        private IDmsTargetLockHolder? _holder;
        private DmsCommandOutcome<DmsInstanceIdentity> _created = null!;
        private DmsLiveTargetState _inspectedAfterCreate = null!;
        private string _encoding = string.Empty;
        private DmsCommandOutcome _dropped = null!;
        private DmsLiveTargetState _inspectedAfterDrop = null!;
        private DmsCommandOutcome _droppedAgain = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _operation = DmsOperationId.New();
            _claim = new DmsProvisionClaim(_operation, Guid.NewGuid());
            PostgresqlDmsTargetEngine engine = Engine();
            _holder = await AcquireAsync(engine, TargetName("create"), _operation);
            DmsDatabaseName staging = _operation.StagingDatabaseName;

            _created = await engine.CreateStagingAsync(_holder, staging, _claim, default);
            _inspectedAfterCreate = await engine.InspectAsync(staging, default);
            _encoding = await ScalarOnServerAsync<string>(
                "SELECT pg_catalog.pg_encoding_to_char(d.encoding) FROM pg_catalog.pg_database d WHERE d.datname = @name;",
                ("name", staging.Value)
            );
            if (_created is DmsCommandOutcome<DmsInstanceIdentity>.Succeeded succeeded)
            {
                _dropped = await engine.DropStagingAsync(staging, succeeded.Value, default);
                _inspectedAfterDrop = await engine.InspectAsync(staging, default);
                _droppedAgain = await engine.DropStagingAsync(staging, succeeded.Value, default);
            }
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_holder is not null)
            {
                await _holder.DisposeAsync();
            }

            await DropByNameAsync(_operation.StagingDatabaseName);
        }

        [Test]
        public void It_returns_the_claimed_instance() =>
            _created
                .Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Succeeded>()
                .Which.Value.Should()
                .BeOfType<DmsInstanceIdentity.Postgresql>()
                .Which.Claim.Should()
                .Be(_claim.Text);

        [Test]
        public void It_is_present_under_the_staging_name_with_the_same_instance() =>
            _inspectedAfterCreate
                .Should()
                .BeOfType<DmsLiveTargetState.Present>()
                .Which.Instance.Should()
                .Be(((DmsCommandOutcome<DmsInstanceIdentity>.Succeeded)_created).Value);

        [Test]
        public void It_creates_the_database_as_utf8() => _encoding.Should().Be("UTF8");

        [Test]
        public void It_drops_the_staging_database() =>
            (_dropped, _inspectedAfterDrop)
                .Should()
                .Be((new DmsCommandOutcome.Succeeded(), new DmsLiveTargetState.Absent()));

        [Test]
        public void It_treats_a_second_drop_as_success() =>
            _droppedAgain.Should().Be(new DmsCommandOutcome.Succeeded());
    }

    /// <summary>
    /// F-05 (D-5): <c>template1</c> holds local objects (a marker table and function) and an open session while a
    /// staging database is created. The staging database is a copy of <c>template0</c>, so the create succeeds
    /// (a copy of <c>template1</c> is refused with <c>55006</c> while another session is on it) and none of the
    /// markers reach it. Teardown closes the session and removes the markers from <c>template1</c>, and asserts
    /// they are gone; setup removes any left by an earlier run first.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class Given_template1_holds_local_objects_and_an_open_connection
    {
        private const string MarkerTable = "dms1438_template1_marker";
        private const string MarkerFunction = "dms1438_template1_marker_fn";

        private const string MarkerCount =
            $"SELECT (SELECT count(*) FROM pg_catalog.pg_class WHERE relname = '{MarkerTable}') + (SELECT count(*) FROM pg_catalog.pg_proc WHERE proname = '{MarkerFunction}');";

        private DmsOperationId _operation;
        private IDmsTargetLockHolder? _holder;
        private NpgsqlConnection? _template1Session;
        private long _markersInTemplate1;
        private long _template1SessionsDuringCreate;
        private DmsCommandOutcome<DmsInstanceIdentity> _created = null!;
        private long _markersInStaging = -1;
        private DmsCommandOutcome? _dropped;

        /// <summary>A session on <c>template1</c>, unpooled so that closing it ends the backend.</summary>
        private static async Task<NpgsqlConnection> OpenTemplate1Async()
        {
            NpgsqlConnectionStringBuilder builder = new(AdminConnectionString)
            {
                Database = "template1",
                Pooling = false,
            };
            NpgsqlConnection connection = new(builder.ConnectionString);
            await connection.OpenAsync();
            return connection;
        }

        private static async Task<long> ScalarInTemplate1Async(string sql)
        {
            await using NpgsqlConnection connection = await OpenTemplate1Async();
            await using NpgsqlCommand command = new(sql, connection);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        private static async Task ExecuteInTemplate1Async(string sql)
        {
            await using NpgsqlConnection connection = await OpenTemplate1Async();
            await using NpgsqlCommand command = new(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        private static Task RemoveMarkersAsync() =>
            ExecuteInTemplate1Async(
                $"DROP FUNCTION IF EXISTS public.{MarkerFunction}(); DROP TABLE IF EXISTS public.{MarkerTable};"
            );

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            await RemoveMarkersAsync();
            await ExecuteInTemplate1Async(
                $"CREATE TABLE public.{MarkerTable} (id int); CREATE FUNCTION public.{MarkerFunction}() RETURNS int LANGUAGE sql AS 'SELECT 1';"
            );
            _markersInTemplate1 = await ScalarInTemplate1Async(MarkerCount);
            _template1Session = await OpenTemplate1Async();

            _operation = DmsOperationId.New();
            PostgresqlDmsTargetEngine engine = Engine();
            _holder = await AcquireAsync(engine, TargetName("template0"), _operation);
            DmsDatabaseName staging = _operation.StagingDatabaseName;
            _template1SessionsDuringCreate = await ScalarOnServerAsync<long>(
                "SELECT count(*) FROM pg_catalog.pg_stat_activity WHERE datname = 'template1';"
            );
            _created = await engine.CreateStagingAsync(
                _holder,
                staging,
                new DmsProvisionClaim(_operation, Guid.NewGuid()),
                default
            );
            if (_created is DmsCommandOutcome<DmsInstanceIdentity>.Succeeded succeeded)
            {
                _markersInStaging = await new TestDatabase(staging.Value).ScalarAsync<long>(MarkerCount);
                await WaitForNoSessionsOnAsync(staging.Value);
                _dropped = await engine.DropStagingAsync(staging, succeeded.Value, default);
            }
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_holder is not null)
            {
                await _holder.DisposeAsync();
            }

            if (_template1Session is not null)
            {
                await _template1Session.DisposeAsync();
            }

            await DropByNameAsync(_operation.StagingDatabaseName);
            await RemoveMarkersAsync();
            (await ScalarInTemplate1Async(MarkerCount))
                .Should()
                .Be(0, "teardown removes the template1 markers");
        }

        [Test]
        public void It_created_both_markers_in_template1() => _markersInTemplate1.Should().Be(2);

        [Test]
        public void It_held_a_template1_session_during_the_create() =>
            _template1SessionsDuringCreate.Should().BeGreaterThanOrEqualTo(1);

        [Test]
        public void It_creates_the_staging_database() =>
            _created.Should().BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Succeeded>();

        [Test]
        public void It_copies_nothing_local_to_template1() => _markersInStaging.Should().Be(0);

        [Test]
        public void It_drops_the_staging_database() =>
            _dropped.Should().Be(new DmsCommandOutcome.Succeeded());
    }

    [TestFixture]
    public class Given_the_staging_name_is_already_taken
    {
        private DmsOperationId _operation;
        private TestDatabase? _existing;
        private IDmsTargetLockHolder? _holder;
        private DmsInstanceIdentity.Postgresql _existingInstance = null!;
        private DmsCommandOutcome<DmsInstanceIdentity> _created = null!;
        private DmsInstanceIdentity.Postgresql _instanceAfterwards = null!;
        private DmsCommandOutcome _dropOfForeign = null!;
        private DmsLiveTargetState _stateAfterDrop = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _operation = DmsOperationId.New();
            PostgresqlDmsTargetEngine engine = Engine();
            _holder = await AcquireAsync(engine, TargetName("taken"), _operation);
            DmsDatabaseName staging = _operation.StagingDatabaseName;

            // A foreign database already holds the generated name, with its own comment.
            await ExecuteOnServerAsync($"CREATE DATABASE {PostgresqlIdentifiers.Quote(staging.Value)};");
            await ExecuteOnServerAsync(
                $"COMMENT ON DATABASE {PostgresqlIdentifiers.Quote(staging.Value)} IS 'not ours';"
            );
            _existing = new TestDatabase(staging.Value);
            _existingInstance = await _existing.CatalogInstanceAsync();

            _created = await engine.CreateStagingAsync(
                _holder,
                staging,
                new DmsProvisionClaim(_operation, Guid.NewGuid()),
                default
            );
            _instanceAfterwards = await _existing.CatalogInstanceAsync();

            // The drop addresses the operation's instance only: a different oid under the name is refused.
            _dropOfForeign = await engine.DropStagingAsync(
                staging,
                new DmsInstanceIdentity.Postgresql(_existingInstance.Oid + 1, _existingInstance.Claim),
                default
            );
            _stateAfterDrop = await engine.InspectAsync(staging, default);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_holder is not null)
            {
                await _holder.DisposeAsync();
            }

            if (_existing is not null)
            {
                await _existing.DisposeAsync();
            }
        }

        [Test]
        public void It_reports_a_duplicate_name() =>
            _created
                .Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Failed>()
                .Which.Failure.Should()
                .BeEquivalentTo(
                    new
                    {
                        Class = DmsCommandFailureClass.DuplicateName,
                        Diagnostic = new
                        {
                            ProviderErrorCode = "42P04",
                            Operation = PostgresqlStagingQueries.CreateStagingStep,
                        },
                    }
                );

        [Test]
        public void It_leaves_the_existing_database_untouched() =>
            _instanceAfterwards.Should().Be(_existingInstance);

        [Test]
        public void It_refuses_to_drop_a_different_instance_under_the_name() =>
            _dropOfForeign
                .Should()
                .Be(
                    new DmsCommandOutcome.Failed(
                        new DmsCommandFailure(
                            DmsCommandFailureClass.DoesNotExist,
                            DmsProvisioningDiagnostic.ForOperation(
                                PostgresqlStagingQueries.DropStagingInstanceStep
                            )
                        )
                    )
                );

        [Test]
        public void It_keeps_the_foreign_database() =>
            _stateAfterDrop
                .Should()
                .BeOfType<DmsLiveTargetState.Present>()
                .Which.Instance.Should()
                .Be(_existingInstance);
    }

    [TestFixture]
    public class Given_a_create_interrupted_once_the_database_exists
    {
        private const string Rejected = "SELECT 1 / 0;";

        private readonly List<DmsDatabaseName> _names = [];
        private DmsProvisionClaim _cancelledClaim = null!;
        private DmsCommandOutcome<DmsInstanceIdentity> _cancelled = null!;
        private DmsLiveTargetState _cancelledAfter = null!;
        private DmsCommandOutcome<DmsInstanceIdentity> _claimRejected = null!;
        private DmsLiveTargetState _claimRejectedAfter = null!;
        private DmsDatabaseName _undroppable;
        private DmsCommandOutcome<DmsInstanceIdentity> _dropRejected = null!;
        private DmsLiveTargetState _dropRejectedAfter = null!;

        private async Task<DmsCommandOutcome<DmsInstanceIdentity>> CreateAsync(
            DmsProvisionClaim claim,
            PostgresqlStagingQueries.CreateSeams seams,
            CancellationToken cancellationToken
        )
        {
            DmsDatabaseName stage = claim.Operation.StagingDatabaseName;
            _names.Add(stage);
            await using NpgsqlConnection connection = await AdminConnection().OpenMaintenanceAsync(default);
            DmsServerSessionId session = await PostgresqlSessionQueries.ReadOwnSessionAsync(
                connection,
                default
            );
            return await PostgresqlStagingQueries.CreateAsync(
                connection,
                stage,
                claim,
                session,
                seams,
                cancellationToken
            );
        }

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            PostgresqlDmsTargetEngine side = Engine();

            // The caller cancels the moment CREATE DATABASE returns: the claim must still be written.
            _cancelledClaim = new DmsProvisionClaim(DmsOperationId.New(), Guid.NewGuid());
            using CancellationTokenSource cancellation = new();
            _cancelled = await CreateAsync(
                _cancelledClaim,
                new PostgresqlStagingQueries.CreateSeams(AfterCreate: () => cancellation.CancelAsync()),
                cancellation.Token
            );
            _cancelledAfter = await side.InspectAsync(_cancelledClaim.Operation.StagingDatabaseName, default);

            DmsProvisionClaim rejected = new(DmsOperationId.New(), Guid.NewGuid());
            _claimRejected = await CreateAsync(
                rejected,
                new PostgresqlStagingQueries.CreateSeams(ClaimStatementOverride: Rejected),
                default
            );
            _claimRejectedAfter = await side.InspectAsync(rejected.Operation.StagingDatabaseName, default);

            DmsProvisionClaim undroppable = new(DmsOperationId.New(), Guid.NewGuid());
            _undroppable = undroppable.Operation.StagingDatabaseName;
            _dropRejected = await CreateAsync(
                undroppable,
                new PostgresqlStagingQueries.CreateSeams(
                    ClaimStatementOverride: Rejected,
                    DropStatementOverride: Rejected
                ),
                default
            );
            _dropRejectedAfter = await side.InspectAsync(_undroppable, default);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            foreach (DmsDatabaseName name in _names)
            {
                await DropByNameAsync(name);
            }
        }

        [Test]
        public void It_completes_the_claim_although_the_caller_cancelled()
        {
            DmsInstanceIdentity instance = _cancelled
                .Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Succeeded>()
                .Subject.Value;
            ((DmsInstanceIdentity.Postgresql)instance).Claim.Should().Be(_cancelledClaim.Text);
            _cancelledAfter
                .Should()
                .BeOfType<DmsLiveTargetState.Present>()
                .Which.Instance.Should()
                .Be(instance);
        }

        [Test]
        public void It_drops_the_database_again_when_the_claim_is_rejected()
        {
            _claimRejected
                .Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Failed>()
                .Which.Failure.Should()
                .BeEquivalentTo(
                    new
                    {
                        Class = DmsCommandFailureClass.Rejected,
                        Diagnostic = new
                        {
                            ProviderErrorCode = "22012",
                            Operation = PostgresqlStagingQueries.ClaimStagingStep,
                        },
                    }
                );
            _claimRejected.Residue.Should().BeEmpty();
            _claimRejectedAfter.Should().Be(new DmsLiveTargetState.Absent());
        }

        [Test]
        public void It_reports_a_database_the_drop_could_not_remove()
        {
            _dropRejected.Should().BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Failed>();
            _dropRejected
                .Residue.Should()
                .Equal(new DmsCleanupResidue(DmsResidueKind.StagingDatabase, _undroppable.Value));
            _dropRejectedAfter.Should().BeOfType<DmsLiveTargetState.Present>();
        }
    }

    [TestFixture]
    public class Given_the_holder_was_lost_before_the_create
    {
        private DmsOperationId _operation;
        private IDmsTargetLockHolder? _holder;
        private Exception? _outcome;
        private DmsLiveTargetState _state = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _operation = DmsOperationId.New();
            PostgresqlDmsTargetEngine engine = Engine(lockHeartbeatInterval: TimeSpan.FromSeconds(1));
            _holder = await AcquireAsync(engine, TargetName("lost"), _operation);
            TaskCompletionSource lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = _holder.LostToken.Register(() =>
                lost.TrySetResult()
            );
            await TerminateBackendAsync(_holder.Session.Id);
            if (await Task.WhenAny(lost.Task, Task.Delay(_bound)) != lost.Task)
            {
                throw new InvalidOperationException("Loss was not declared within the bound.");
            }

            _outcome = await Record.ExceptionAsync(() =>
                engine.CreateStagingAsync(
                    _holder,
                    _operation.StagingDatabaseName,
                    new DmsProvisionClaim(_operation, Guid.NewGuid()),
                    default
                )
            );
            _state = await engine.InspectAsync(_operation.StagingDatabaseName, default);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_holder is not null)
            {
                await _holder.DisposeAsync();
            }

            await DropByNameAsync(_operation.StagingDatabaseName);
        }

        [Test]
        public void It_refuses_the_command() =>
            _outcome.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_created_nothing() => _state.Should().Be(new DmsLiveTargetState.Absent());
    }
}
