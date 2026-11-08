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
/// The SQL Server engine's publication members against a live server (6.4 steps 10 and 12, 6.9b, 6.7, Step 4.6),
/// without a restore: a database built from the committed DMS-shaped fixture (or an empty one) stands in for the
/// staging database, and a real holder is taken for a fresh target name. The rename keeps <c>database_id</c> and the
/// primary file, changes <c>create_date</c>, and returns the re-captured snapshot, which a session open then requires
/// (the pre-rename snapshot is refused); a taken target name, a changed staging instance and a connected session each
/// refuse it definitely and leave every database as it was. The marker commits only with its row and its instance
/// predicate, also after the delivered identity read turned <c>NOCOUNT</c> on, and a constraint or a missing table
/// refuses it definitely. The compensation drop removes only the instance it was given and, unlike PostgreSQL's
/// <c>WITH (FORCE)</c>, is refused while a session uses the target.
/// </summary>
[Category(Category)]
public class MssqlPublicationTests
{
    private static readonly ProtectedDatabaseNames _protectedNames = ProtectedDatabaseNames.Create(
        "edfi_configurationservice",
        []
    );

    /// <summary>
    /// A real holder for a fresh target name and a test database in the staging role; disposal releases the holder and
    /// drops whatever is under either name.
    /// </summary>
    private sealed class Staged : IAsyncDisposable
    {
        private Staged(
            MssqlDmsTargetEngine engine,
            IDmsTargetLockHolder holder,
            DmsDatabaseName stage,
            DmsTargetDatabaseName target,
            DmsInstanceIdentity.Mssql instance,
            Guid expected
        )
        {
            Engine = engine;
            Holder = holder;
            Stage = stage;
            Target = target;
            Instance = instance;
            Expected = expected;
        }

        public MssqlDmsTargetEngine Engine { get; }

        public IDmsTargetLockHolder Holder { get; }

        public DmsDatabaseName Stage { get; }

        public DmsTargetDatabaseName Target { get; }

        /// <summary>The staging instance with its pre-rename <c>create_date</c> snapshot.</summary>
        public DmsInstanceIdentity.Mssql Instance { get; }

        public Guid Expected { get; }

        private static DmsTargetDatabaseName TargetName(string purpose) =>
            DmsTargetDatabaseName.TryCreate(
                $"dms1438_{purpose}_{DmsOperationId.New().Value}",
                _protectedNames,
                out DmsTargetDatabaseName name
            ) == DmsTargetNameValidation.Valid
                ? name
                : throw new InvalidOperationException("The target name does not satisfy the rules.");

        public static async Task<Staged> CreateAsync(string purpose, bool dmsShaped = false)
        {
            TestDatabase database = dmsShaped
                ? await CreateDmsShapedDatabaseAsync(purpose)
                : await CreateDatabaseAsync(purpose);
            await WaitForNoSessionsOnAsync(database.Name);
            MssqlDmsTargetEngine engine = MssqlProvisioningTestServer.Engine();
            DmsTargetDatabaseName target = TargetName(purpose);
            IDmsTargetLockHolder holder = (
                await engine.AcquireTargetLockAsync(target, DmsOperationId.New(), default)
            )
                .Should()
                .BeOfType<DmsTargetLockResult.Acquired>()
                .Subject.Holder;
            return new Staged(
                engine,
                holder,
                database.DatabaseName,
                target,
                await database.CatalogInstanceAsync(),
                Guid.NewGuid()
            );
        }

        public Task<DmsCommandOutcome<DmsInstanceIdentity>> RenameAsync(
            DmsInstanceIdentity? instance = null
        ) => Engine.RenameIntoTargetAsync(Holder, Stage, instance ?? Instance, Target, default);

        /// <summary>The rename, which must succeed, returning the instance it reported.</summary>
        public async Task<DmsInstanceIdentity.Mssql> RenamedAsync() =>
            (DmsInstanceIdentity.Mssql)
                (await RenameAsync())
                    .Should()
                    .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Succeeded>()
                    .Subject.Value;

        public static async Task<DmsLiveTargetState> InspectAsync(DmsDatabaseName name) =>
            await MssqlProvisioningTestServer.Engine().InspectAsync(name, default);

        /// <summary>Runs <paramref name="sql"/> on the database under the target name, then waits for the connection to be gone.</summary>
        public async Task ExecuteOnTargetAsync(string sql)
        {
            await new TestDatabase(Target.Value).ExecuteAsync(sql);
            await WaitForNoSessionsOnAsync(Target.Value);
        }

        public async Task<Guid?> TargetIdentityAsync()
        {
            string? value = await new TestDatabase(Target.Value).ScalarAsync<string?>(
                "SELECT CONVERT(nvarchar(36), [SourceIdentity]) FROM [dms].[DataStoreIdentity];"
            );
            await WaitForNoSessionsOnAsync(Target.Value);
            return value is null ? null : Guid.Parse(value);
        }

        public async ValueTask DisposeAsync()
        {
            await Holder.DisposeAsync();
            await new TestDatabase(Target.Value).DisposeAsync();
            await new TestDatabase(Stage.Value).DisposeAsync();
        }
    }

    [TestFixture]
    public class Given_a_staging_database_is_renamed_into_a_free_target
    {
        private Staged? _staged;
        private DmsCommandOutcome<DmsInstanceIdentity> _outcome = null!;
        private DmsInstanceIdentity.Mssql _catalogAfter = null!;
        private DmsLiveTargetState _target = null!;
        private DmsLiveTargetState _stage = null!;
        private DmsSessionOpenResult _openedWithStaleSnapshot = null!;
        private DmsSessionOpenResult _openedWithRecapturedSnapshot = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _staged = await Staged.CreateAsync("rename");

            // datetime has a resolution of about 3 ms: the rename must fall in a later tick than the create.
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            _outcome = await _staged.RenameAsync();
            _catalogAfter = await new TestDatabase(_staged.Target.Value).CatalogInstanceAsync();
            _target = await Staged.InspectAsync(_staged.Target);
            _stage = await Staged.InspectAsync(_staged.Stage);
            _openedWithStaleSnapshot = await _staged.Engine.OpenSessionAsync(
                _staged.Target,
                _staged.Instance,
                default
            );
            _openedWithRecapturedSnapshot = await _staged.Engine.OpenSessionAsync(
                _staged.Target,
                ((DmsCommandOutcome<DmsInstanceIdentity>.Succeeded)_outcome).Value,
                default
            );
            if (_openedWithRecapturedSnapshot is DmsSessionOpenResult.Opened { Session: { } session })
            {
                await session.DisposeAsync();
            }
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_staged is not null)
            {
                await _staged.DisposeAsync();
            }
        }

        [Test]
        public void It_keeps_the_database_id_and_the_primary_file() =>
            _outcome
                .Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Succeeded>()
                .Which.Value.IsSameStableInstance(_staged!.Instance)
                .Should()
                .BeTrue();

        [Test]
        public void It_returns_the_create_date_the_rename_changed_as_the_new_snapshot()
        {
            DmsInstanceIdentity.Mssql renamed = (DmsInstanceIdentity.Mssql)
                ((DmsCommandOutcome<DmsInstanceIdentity>.Succeeded)_outcome).Value;
            (renamed, renamed.CreateDateSnapshot == _staged!.Instance.CreateDateSnapshot)
                .Should()
                .Be((_catalogAfter, false));
        }

        [Test]
        public void It_moves_the_instance_from_the_staging_name_to_the_target_name() =>
            (_target, _stage)
                .Should()
                .Be(
                    (
                        new DmsLiveTargetState.Present(_catalogAfter, DmsDatabaseState.Online),
                        new DmsLiveTargetState.Absent()
                    )
                );

        [Test]
        public void It_refuses_a_session_against_the_pre_rename_snapshot_and_opens_one_against_the_new_one() =>
            (_openedWithStaleSnapshot, _openedWithRecapturedSnapshot is DmsSessionOpenResult.Opened)
                .Should()
                .Be((new DmsSessionOpenResult.InstanceChanged(), true));
    }

    [TestFixture]
    public class Given_the_target_name_is_already_taken
    {
        private Staged? _staged;
        private DmsInstanceIdentity.Mssql _foreignBefore = null!;
        private DmsCommandOutcome<DmsInstanceIdentity> _outcome = null!;
        private DmsLiveTargetState _target = null!;
        private DmsLiveTargetState _stage = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _staged = await Staged.CreateAsync("taken");
            await ExecuteOnServerAsync($"CREATE DATABASE {MssqlIdentifiers.Quote(_staged.Target.Value)};");
            _foreignBefore = await new TestDatabase(_staged.Target.Value).CatalogInstanceAsync();
            _outcome = await _staged.RenameAsync();
            _target = await Staged.InspectAsync(_staged.Target);
            _stage = await Staged.InspectAsync(_staged.Stage);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_staged is not null)
            {
                await _staged.DisposeAsync();
            }
        }

        [Test]
        public void It_refuses_with_a_duplicate_name_carrying_1801()
        {
            DmsCommandFailure failure = _outcome
                .Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Failed>()
                .Subject.Failure;
            (failure.Class, failure.Diagnostic.ProviderErrorCode, failure.Diagnostic.Operation)
                .Should()
                .Be((DmsCommandFailureClass.DuplicateName, "1801", MssqlPublicationQueries.RenameStep));
        }

        [Test]
        public void It_leaves_the_foreign_database_and_the_staging_database_where_they_were() =>
            (_target, _stage)
                .Should()
                .Be(
                    (
                        new DmsLiveTargetState.Present(_foreignBefore, DmsDatabaseState.Online),
                        new DmsLiveTargetState.Present(_staged!.Instance, DmsDatabaseState.Online)
                    )
                );
    }

    [TestFixture]
    public class Given_the_staging_name_no_longer_holds_the_instance
    {
        private Staged? _staged;
        private DmsCommandOutcome<DmsInstanceIdentity> _otherFile = null!;
        private DmsCommandOutcome<DmsInstanceIdentity> _otherId = null!;
        private DmsLiveTargetState _target = null!;
        private DmsLiveTargetState _stage = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _staged = await Staged.CreateAsync("moved");
            _otherFile = await _staged.RenameAsync(
                _staged.Instance with
                {
                    PrimaryFileTag = _staged.Instance.PrimaryFileTag + ".other",
                }
            );
            _otherId = await _staged.RenameAsync(
                _staged.Instance with
                {
                    DatabaseId = _staged.Instance.DatabaseId + 1,
                }
            );
            _target = await Staged.InspectAsync(_staged.Target);
            _stage = await Staged.InspectAsync(_staged.Stage);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_staged is not null)
            {
                await _staged.DisposeAsync();
            }
        }

        [Test]
        public void It_refuses_without_renaming_for_another_file_or_another_id()
        {
            DmsCommandOutcome<DmsInstanceIdentity> refused =
                new DmsCommandOutcome<DmsInstanceIdentity>.Failed(
                    new DmsCommandFailure(
                        DmsCommandFailureClass.DoesNotExist,
                        DmsProvisioningDiagnostic.ForOperation(MssqlPublicationQueries.RenameInstanceStep)
                    )
                );
            (_otherFile, _otherId).Should().Be((refused, refused));
        }

        [Test]
        public void It_leaves_the_staging_database_under_its_name() =>
            (_target, _stage)
                .Should()
                .Be(
                    (
                        new DmsLiveTargetState.Absent(),
                        new DmsLiveTargetState.Present(_staged!.Instance, DmsDatabaseState.Online)
                    )
                );
    }

    [TestFixture]
    public class Given_a_session_is_connected_to_the_staging_database
    {
        private Staged? _staged;
        private DmsCommandOutcome<DmsInstanceIdentity> _outcome = null!;
        private DmsLiveTargetState _target = null!;
        private DmsLiveTargetState _stage = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _staged = await Staged.CreateAsync("inuse");
            await using (
                SqlConnection connected = await AdminConnection()
                    .OpenAsync(_staged.Stage.Value, "dms1438-test", default)
            )
            {
                _outcome = await _staged.RenameAsync();
            }

            await WaitForNoSessionsOnAsync(_staged.Stage.Value);
            _target = await Staged.InspectAsync(_staged.Target);
            _stage = await Staged.InspectAsync(_staged.Stage);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_staged is not null)
            {
                await _staged.DisposeAsync();
            }
        }

        [Test]
        public void It_refuses_with_object_in_use_carrying_5030() =>
            _outcome
                .Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Failed>()
                .Which.Failure.Should()
                .Match<DmsCommandFailure>(failure =>
                    failure.Class == DmsCommandFailureClass.ObjectInUse
                    && failure.Diagnostic.ProviderErrorCode == "5030"
                );

        [Test]
        public void It_leaves_the_staging_database_under_its_name_and_the_target_name_free() =>
            (_target, _stage)
                .Should()
                .Be(
                    (
                        new DmsLiveTargetState.Absent(),
                        new DmsLiveTargetState.Present(_staged!.Instance, DmsDatabaseState.Online)
                    )
                );
    }

    [TestFixture]
    public class Given_the_marker_is_assigned_on_the_renamed_instance
    {
        private Staged? _assigned;
        private Staged? _noRow;
        private Staged? _constrained;
        private Staged? _plain;
        private Staged? _rewritten;
        private DmsCommandOutcome _assignedOutcome = null!;
        private DmsCommandOutcome _noRowOutcome = null!;
        private DmsCommandOutcome _constrainedOutcome = null!;
        private DmsCommandOutcome _plainOutcome = null!;
        private DmsCommandOutcome _rewrittenOutcome = null!;
        private Guid? _assignedIdentity;
        private Guid? _constrainedIdentity;

        /// <summary>
        /// Renames <paramref name="staged"/>, prepares the target with <paramref name="prepare"/>, then opens a session
        /// against the renamed instance, runs the delivered identity read on it (which turns <c>NOCOUNT</c> on, as the
        /// orchestrator's re-check does), and assigns the marker there.
        /// </summary>
        private static async Task<DmsCommandOutcome> AssignAsync(Staged staged, string? prepare = null)
        {
            DmsInstanceIdentity.Mssql renamed = await staged.RenamedAsync();
            if (prepare is not null)
            {
                await staged.ExecuteOnTargetAsync(prepare);
            }

            DmsSession session = (await staged.Engine.OpenSessionAsync(staged.Target, renamed, default))
                .Should()
                .BeOfType<DmsSessionOpenResult.Opened>()
                .Subject.Session;
            await using (session)
            {
                await staged.Engine.ReadIdentityAsync(session, default);
                return await staged.Engine.AssignMarkerAsync(session, staged.Expected, default);
            }
        }

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _assigned = await Staged.CreateAsync("marker", dmsShaped: true);
            _assignedOutcome = await AssignAsync(_assigned);
            _assignedIdentity = await _assigned.TargetIdentityAsync();

            _noRow = await Staged.CreateAsync("markernorow", dmsShaped: true);
            _noRowOutcome = await AssignAsync(_noRow, "DELETE FROM [dms].[DataStoreIdentity];");

            _constrained = await Staged.CreateAsync("markercheck", dmsShaped: true);
            _constrainedOutcome = await AssignAsync(
                _constrained,
                $"ALTER TABLE [dms].[DataStoreIdentity] ADD CONSTRAINT [dms1438_package_only] CHECK ([SourceIdentity] = '{MssqlDmsShapedManifest.SourceIdentity:D}');"
            );
            _constrainedIdentity = await _constrained.TargetIdentityAsync();

            _plain = await Staged.CreateAsync("markerplain");
            _plainOutcome = await AssignAsync(_plain);

            // A trigger restores the package value: the statement updates its row, but the read-back differs.
            _rewritten = await Staged.CreateAsync("markerrewrite", dmsShaped: true);
            _rewrittenOutcome = await AssignAsync(
                _rewritten,
                $"CREATE TRIGGER [dms].[dms1438_keep_package] ON [dms].[DataStoreIdentity] AFTER UPDATE AS BEGIN SET NOCOUNT ON; UPDATE [dms].[DataStoreIdentity] SET [SourceIdentity] = '{MssqlDmsShapedManifest.SourceIdentity:D}'; END;"
            );
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            foreach (Staged? staged in new[] { _assigned, _noRow, _constrained, _plain, _rewritten })
            {
                if (staged is not null)
                {
                    await staged.DisposeAsync();
                }
            }
        }

        [Test]
        public void It_commits_the_expected_identity_and_reads_it_back() =>
            (_assignedOutcome, _assignedIdentity)
                .Should()
                .Be(((DmsCommandOutcome)new DmsCommandOutcome.Succeeded(), (Guid?)_assigned!.Expected));

        [Test]
        public void It_refuses_definitely_when_no_singleton_row_is_updated() =>
            _noRowOutcome
                .Should()
                .Be(
                    new DmsCommandOutcome.Failed(
                        new DmsCommandFailure(
                            DmsCommandFailureClass.Rejected,
                            DmsProvisioningDiagnostic.ForOperation(
                                MssqlPublicationQueries.AssignMarkerNoRowStep
                            )
                        )
                    )
                );

        [Test]
        public void It_refuses_definitely_on_a_constraint_and_keeps_the_package_identity()
        {
            DmsCommandFailure failure = _constrainedOutcome
                .Should()
                .BeOfType<DmsCommandOutcome.Failed>()
                .Subject.Failure;
            (failure.Class, failure.Diagnostic.ProviderErrorCode, failure.Diagnostic.Operation)
                .Should()
                .Be((DmsCommandFailureClass.Rejected, "547", MssqlPublicationQueries.AssignMarkerStep));
            _constrainedIdentity.Should().Be(MssqlDmsShapedManifest.SourceIdentity);
        }

        [Test]
        public void It_refuses_definitely_when_the_identity_table_is_missing() =>
            _plainOutcome
                .Should()
                .BeOfType<DmsCommandOutcome.Failed>()
                .Which.Failure.Should()
                .Match<DmsCommandFailure>(failure =>
                    failure.Class == DmsCommandFailureClass.DoesNotExist
                    && failure.Diagnostic.ProviderErrorCode == "208"
                );

        [Test]
        public void It_leaves_an_updated_row_that_reads_back_another_value_unknown_for_its_session() =>
            _rewrittenOutcome
                .Should()
                .BeOfType<DmsCommandOutcome.Unknown>()
                .Which.Should()
                .Match<DmsCommandOutcome.Unknown>(unknown => unknown.Session.HasValue && !unknown.Quiescent);
    }

    [TestFixture]
    public class Given_the_marker_statement_names_another_instance
    {
        private Staged? _staged;
        private DmsCommandOutcome _wrongId = null!;
        private DmsCommandOutcome _wrongFile = null!;
        private DmsCommandOutcome _right = null!;
        private Guid? _afterWrong;
        private Guid? _afterRight;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _staged = await Staged.CreateAsync("markerid", dmsShaped: true);
            DmsInstanceIdentity.Mssql renamed = await _staged.RenamedAsync();

            // The statement itself, on a connection to the renamed database, with the predicate wrong and then right.
            await using (
                SqlConnection connection = await AdminConnection()
                    .OpenAsync(_staged.Target.Value, "dms1438-test", default)
            )
            {
                DmsServerSessionId session = await MssqlStagingQueries.ReadOwnSessionAsync(
                    connection,
                    30,
                    default
                );
                _wrongId = await MssqlPublicationQueries.AssignMarkerAsync(
                    connection,
                    renamed with
                    {
                        DatabaseId = renamed.DatabaseId + 1,
                    },
                    _staged.Expected,
                    session,
                    30,
                    default
                );
                _wrongFile = await MssqlPublicationQueries.AssignMarkerAsync(
                    connection,
                    renamed with
                    {
                        PrimaryFileTag = renamed.PrimaryFileTag + ".other",
                    },
                    _staged.Expected,
                    session,
                    30,
                    default
                );
                _afterWrong = await IdentityOnAsync(connection);
                _right = await MssqlPublicationQueries.AssignMarkerAsync(
                    connection,
                    renamed,
                    _staged.Expected,
                    session,
                    30,
                    default
                );
            }

            await WaitForNoSessionsOnAsync(_staged.Target.Value);
            _afterRight = await _staged.TargetIdentityAsync();
        }

        private static async Task<Guid?> IdentityOnAsync(SqlConnection connection)
        {
            await using SqlCommand command = new(
                "SELECT CONVERT(nvarchar(36), [SourceIdentity]) FROM [dms].[DataStoreIdentity];",
                connection
            );
            return await command.ExecuteScalarAsync() is string value ? Guid.Parse(value) : null;
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_staged is not null)
            {
                await _staged.DisposeAsync();
            }
        }

        [Test]
        public void It_updates_no_row_and_keeps_the_package_identity_for_another_id_or_file()
        {
            DmsCommandOutcome noRow = new DmsCommandOutcome.Failed(
                new DmsCommandFailure(
                    DmsCommandFailureClass.Rejected,
                    DmsProvisioningDiagnostic.ForOperation(MssqlPublicationQueries.AssignMarkerNoRowStep)
                )
            );
            (_wrongId, _wrongFile, _afterWrong)
                .Should()
                .Be((noRow, noRow, (Guid?)MssqlDmsShapedManifest.SourceIdentity));
        }

        [Test]
        public void It_commits_with_the_instance_predicate() =>
            (_right, _afterRight)
                .Should()
                .Be(((DmsCommandOutcome)new DmsCommandOutcome.Succeeded(), (Guid?)_staged!.Expected));
    }

    [TestFixture]
    public class Given_a_renamed_target_is_compensated
    {
        private Staged? _matching;
        private Staged? _mismatching;
        private Staged? _connected;
        private DmsCommandOutcome _matchingOutcome = null!;
        private DmsCommandOutcome _mismatchingOutcome = null!;
        private DmsCommandOutcome _connectedOutcome = null!;
        private DmsLiveTargetState _matchingAfter = null!;
        private DmsLiveTargetState _mismatchingAfter = null!;
        private DmsLiveTargetState _connectedAfter = null!;
        private DmsCommandOutcome _ownedDelete = null!;
        private DmsLiveTargetState _ownedDeleteAfter = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _matching = await Staged.CreateAsync("compensate");
            DmsInstanceIdentity.Mssql renamed = await _matching.RenamedAsync();
            _matchingOutcome = await _matching.Engine.DropInstanceAsync(
                _matching.Holder,
                _matching.Target,
                renamed,
                DmsDropReason.Compensation,
                default
            );
            _matchingAfter = await Staged.InspectAsync(_matching.Target);

            _mismatching = await Staged.CreateAsync("compensateother");
            DmsInstanceIdentity.Mssql other = await _mismatching.RenamedAsync();
            _mismatchingOutcome = await _mismatching.Engine.DropInstanceAsync(
                _mismatching.Holder,
                _mismatching.Target,
                other with
                {
                    PrimaryFileTag = other.PrimaryFileTag + ".other",
                },
                DmsDropReason.Compensation,
                default
            );
            _mismatchingAfter = await Staged.InspectAsync(_mismatching.Target);
            _ownedDelete = await _mismatching.Engine.DropInstanceAsync(
                _mismatching.Holder,
                _mismatching.Target,
                other,
                DmsDropReason.Delete,
                default
            );
            _ownedDeleteAfter = await Staged.InspectAsync(_mismatching.Target);

            _connected = await Staged.CreateAsync("compensateinuse");
            DmsInstanceIdentity.Mssql inUse = await _connected.RenamedAsync();
            await using (
                SqlConnection connection = await AdminConnection()
                    .OpenAsync(_connected.Target.Value, "dms1438-test", default)
            )
            {
                _connectedOutcome = await _connected.Engine.DropInstanceAsync(
                    _connected.Holder,
                    _connected.Target,
                    inUse,
                    DmsDropReason.Compensation,
                    default
                );
            }

            await WaitForNoSessionsOnAsync(_connected.Target.Value);
            _connectedAfter = await Staged.InspectAsync(_connected.Target);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            foreach (Staged? staged in new[] { _matching, _mismatching, _connected })
            {
                if (staged is not null)
                {
                    await staged.DisposeAsync();
                }
            }
        }

        [Test]
        public void It_drops_the_instance_it_was_given() =>
            (_matchingOutcome, _matchingAfter)
                .Should()
                .Be(
                    (
                        (DmsCommandOutcome)new DmsCommandOutcome.Succeeded(),
                        (DmsLiveTargetState)new DmsLiveTargetState.Absent()
                    )
                );

        [Test]
        public void It_refuses_another_instance_and_touches_nothing()
        {
            _mismatchingOutcome
                .Should()
                .Be(
                    new DmsCommandOutcome.Failed(
                        new DmsCommandFailure(
                            DmsCommandFailureClass.DoesNotExist,
                            DmsProvisioningDiagnostic.ForOperation(
                                MssqlPublicationQueries.DropInstanceInstanceStep
                            )
                        )
                    )
                );
            _mismatchingAfter.Should().BeOfType<DmsLiveTargetState.Present>();
        }

        [Test]
        public void It_is_refused_while_a_session_uses_the_target_and_ends_no_session()
        {
            _connectedOutcome
                .Should()
                .BeOfType<DmsCommandOutcome.Failed>()
                .Which.Failure.Should()
                .Match<DmsCommandFailure>(failure =>
                    failure.Class == DmsCommandFailureClass.ObjectInUse
                    && failure.Diagnostic.ProviderErrorCode == "3702"
                );
            _connectedAfter.Should().BeOfType<DmsLiveTargetState.Present>();
        }

        [Test]
        public void It_routes_an_owned_delete_to_the_fenced_drop_which_refuses_an_unfenced_target()
        {
            _ownedDelete
                .Should()
                .Be(
                    new DmsCommandOutcome.Failed(
                        new DmsCommandFailure(
                            DmsCommandFailureClass.ObjectInUse,
                            DmsProvisioningDiagnostic.ForOperation(
                                MssqlDeletionQueries.DropTargetNotFencedStep
                            )
                        )
                    )
                );
            _ownedDeleteAfter.Should().BeOfType<DmsLiveTargetState.Present>();
        }
    }
}
