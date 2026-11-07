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
/// The PostgreSQL engine's publication members against a live server (6.3 steps 8 and 10, 6.9b, 6.7, Step
/// 3.6), without a replay: a staging database created and claimed on a real holder, filled from the
/// committed DMS-shaped fixture where the marker needs it. The rename keeps <c>oid</c> and claim and frees
/// the staging name; a taken target name, a changed staging instance, and a connected session each refuse it
/// definitely and leave every database as it was; the marker commits only with its row and its predicate,
/// and a constraint or a missing table refuses it definitely; the compensation drop removes only the
/// instance it was given; the owned delete is not part of this step.
/// </summary>
[Category(Category)]
public class PostgresqlPublicationTests
{
    /// <summary>A real holder for a fresh target name and a staging database created and claimed on it; disposal releases the holder and drops both names.</summary>
    private sealed class Staged : IAsyncDisposable
    {
        private static readonly ProtectedDatabaseNames _protectedNames = ProtectedDatabaseNames.Create(
            "edfi_configurationservice",
            []
        );

        private Staged(
            PostgresqlDmsTargetEngine engine,
            IDmsTargetLockHolder holder,
            DmsOperationId operation,
            DmsTargetDatabaseName target,
            DmsInstanceIdentity instance,
            Guid expected
        )
        {
            Engine = engine;
            Holder = holder;
            Operation = operation;
            Target = target;
            Instance = instance;
            Expected = expected;
        }

        public PostgresqlDmsTargetEngine Engine { get; }

        public IDmsTargetLockHolder Holder { get; }

        public DmsOperationId Operation { get; }

        public DmsDatabaseName Stage => Operation.StagingDatabaseName;

        public DmsTargetDatabaseName Target { get; }

        public DmsInstanceIdentity Instance { get; }

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
            PostgresqlDmsTargetEngine engine = DmsProvisioningTestServer.Engine();
            DmsTargetDatabaseName target = TargetName(purpose);
            DmsOperationId operation = DmsOperationId.New();
            Guid expected = Guid.NewGuid();
            IDmsTargetLockHolder holder = (await engine.AcquireTargetLockAsync(target, operation, default))
                .Should()
                .BeOfType<DmsTargetLockResult.Acquired>()
                .Subject.Holder;
            DmsInstanceIdentity instance = (
                await engine.CreateStagingAsync(
                    holder,
                    operation.StagingDatabaseName,
                    new DmsProvisionClaim(operation, expected),
                    default
                )
            )
                .Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Succeeded>()
                .Subject.Value;
            if (dmsShaped)
            {
                await EnsureEnqueueOwnerRoleAsync();
                await new TestDatabase(operation.StagingDatabaseName.Value).ExecuteAsync(
                    await File.ReadAllTextAsync(FixturePath("dms-shaped-database.sql"))
                );
                await WaitForNoSessionsOnAsync(operation.StagingDatabaseName.Value);
            }

            return new Staged(engine, holder, operation, target, instance, expected);
        }

        public Task<DmsCommandOutcome<DmsInstanceIdentity>> RenameAsync(
            DmsInstanceIdentity? instance = null
        ) => Engine.RenameIntoTargetAsync(Holder, Stage, instance ?? Instance, Target, default);

        public static async Task<DmsLiveTargetState> InspectAsync(DmsDatabaseName name) =>
            await DmsProvisioningTestServer.Engine().InspectAsync(name, default);

        /// <summary>Runs <paramref name="sql"/> on the database under the target name, then waits for the connection to be gone.</summary>
        public async Task ExecuteOnTargetAsync(string sql)
        {
            await new TestDatabase(Target.Value).ExecuteAsync(sql);
            await WaitForNoSessionsOnAsync(Target.Value);
        }

        public async Task<Guid?> TargetIdentityAsync()
        {
            await using NpgsqlConnection connection = await AdminConnection()
                .OpenAsync(Target.Value, "dms1438-test", default);
            await using NpgsqlCommand command = new(
                "SELECT \"SourceIdentity\" FROM \"dms\".\"DataStoreIdentity\";",
                connection
            );
            return await command.ExecuteScalarAsync() as Guid?;
        }

        public async ValueTask DisposeAsync()
        {
            await Holder.DisposeAsync();
            await ExecuteOnServerAsync(
                $"DROP DATABASE IF EXISTS {PostgresqlIdentifiers.Quote(Target.Value)} WITH (FORCE);"
            );
            await ExecuteOnServerAsync(
                $"DROP DATABASE IF EXISTS {PostgresqlIdentifiers.Quote(Stage.Value)} WITH (FORCE);"
            );
        }
    }

    [TestFixture]
    public class Given_a_staging_database_is_renamed_into_a_free_target
    {
        private Staged? _staged;
        private DmsCommandOutcome<DmsInstanceIdentity> _outcome = null!;
        private DmsLiveTargetState _target = null!;
        private DmsLiveTargetState _stage = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _staged = await Staged.CreateAsync("rename");
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
        public void It_returns_the_same_instance_with_its_claim() =>
            _outcome.Should().Be(new DmsCommandOutcome<DmsInstanceIdentity>.Succeeded(_staged!.Instance));

        [Test]
        public void It_moves_the_instance_from_the_staging_name_to_the_target_name() =>
            (_target, _stage)
                .Should()
                .Be(
                    (
                        new DmsLiveTargetState.Present(_staged!.Instance, DmsDatabaseState.Online),
                        new DmsLiveTargetState.Absent()
                    )
                );
    }

    [TestFixture]
    public class Given_the_target_name_is_already_taken
    {
        private Staged? _staged;
        private DmsInstanceIdentity.Postgresql _foreignBefore = null!;
        private DmsCommandOutcome<DmsInstanceIdentity> _outcome = null!;
        private DmsLiveTargetState _target = null!;
        private DmsLiveTargetState _stage = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _staged = await Staged.CreateAsync("taken");
            await ExecuteOnServerAsync(
                $"CREATE DATABASE {PostgresqlIdentifiers.Quote(_staged.Target.Value)};"
            );
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
        public void It_refuses_with_a_duplicate_name_carrying_42P04()
        {
            DmsCommandFailure failure = _outcome
                .Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Failed>()
                .Subject.Failure;
            (failure.Class, failure.Diagnostic.ProviderErrorCode, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsCommandFailureClass.DuplicateName,
                        PostgresErrorCodes.DuplicateDatabase,
                        PostgresqlPublicationQueries.RenameStep
                    )
                );
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
        private DmsCommandOutcome<DmsInstanceIdentity> _outcome = null!;
        private DmsLiveTargetState _target = null!;
        private DmsLiveTargetState _stage = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _staged = await Staged.CreateAsync("moved");
            DmsInstanceIdentity.Postgresql actual = (DmsInstanceIdentity.Postgresql)_staged.Instance;
            _outcome = await _staged.RenameAsync(actual with { Claim = actual.Claim + "-other" });
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
        public void It_refuses_without_renaming() =>
            _outcome
                .Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Failed>()
                .Which.Failure.Should()
                .Be(
                    new DmsCommandFailure(
                        DmsCommandFailureClass.DoesNotExist,
                        DmsProvisioningDiagnostic.ForOperation(
                            PostgresqlPublicationQueries.RenameInstanceStep
                        )
                    )
                );

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

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _staged = await Staged.CreateAsync("inuse");
            await using (
                NpgsqlConnection connected = await AdminConnection()
                    .OpenAsync(_staged.Stage.Value, "dms1438-test", default)
            )
            {
                _outcome = await _staged.RenameAsync();
            }

            _target = await Staged.InspectAsync(_staged.Target);
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
        public void It_refuses_with_object_in_use_carrying_55006() =>
            _outcome
                .Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Failed>()
                .Which.Failure.Should()
                .Match<DmsCommandFailure>(failure =>
                    failure.Class == DmsCommandFailureClass.ObjectInUse
                    && failure.Diagnostic.ProviderErrorCode == PostgresErrorCodes.ObjectInUse
                );

        [Test]
        public void It_leaves_the_target_name_free() => _target.Should().Be(new DmsLiveTargetState.Absent());
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

        /// <summary>Renames <paramref name="staged"/>, prepares the target with <paramref name="prepare"/>, then assigns the marker on a session opened against the renamed instance.</summary>
        private static async Task<DmsCommandOutcome> AssignAsync(Staged staged, string? prepare = null)
        {
            DmsInstanceIdentity renamed = (await staged.RenameAsync())
                .Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Succeeded>()
                .Subject.Value;
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
            _noRowOutcome = await AssignAsync(_noRow, "DELETE FROM \"dms\".\"DataStoreIdentity\";");

            _constrained = await Staged.CreateAsync("markercheck", dmsShaped: true);
            _constrainedOutcome = await AssignAsync(
                _constrained,
                $"ALTER TABLE \"dms\".\"DataStoreIdentity\" ADD CONSTRAINT \"dms1438_package_only\" CHECK (\"SourceIdentity\" = '{DmsShapedManifest.SourceIdentity:D}');"
            );
            _constrainedIdentity = await _constrained.TargetIdentityAsync();

            _plain = await Staged.CreateAsync("markerplain");
            _plainOutcome = await AssignAsync(_plain);

            // A trigger keeps the package value: the statement updates its row, but the read-back differs.
            _rewritten = await Staged.CreateAsync("markerrewrite", dmsShaped: true);
            _rewrittenOutcome = await AssignAsync(
                _rewritten,
                $"CREATE FUNCTION public.dms1438_keep_package() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN NEW.\"SourceIdentity\" := '{DmsShapedManifest.SourceIdentity:D}'; RETURN NEW; END $$; CREATE TRIGGER dms1438_keep_package BEFORE UPDATE ON \"dms\".\"DataStoreIdentity\" FOR EACH ROW EXECUTE FUNCTION public.dms1438_keep_package();"
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
                                PostgresqlPublicationQueries.AssignMarkerNoRowStep
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
            (failure.Class, failure.Diagnostic.ProviderErrorCode)
                .Should()
                .Be((DmsCommandFailureClass.Rejected, PostgresErrorCodes.CheckViolation));
            _constrainedIdentity.Should().Be(DmsShapedManifest.SourceIdentity);
        }

        [Test]
        public void It_refuses_definitely_when_the_identity_table_is_missing() =>
            _plainOutcome
                .Should()
                .BeOfType<DmsCommandOutcome.Failed>()
                .Which.Failure.Class.Should()
                .Be(DmsCommandFailureClass.DoesNotExist);

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
        private DmsCommandOutcome _wrongOutcome = null!;
        private DmsCommandOutcome _rightOutcome = null!;
        private Guid? _afterWrong;
        private Guid? _afterRight;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _staged = await Staged.CreateAsync("markeroid", dmsShaped: true);
            DmsInstanceIdentity.Postgresql renamed = (DmsInstanceIdentity.Postgresql)
                ((DmsCommandOutcome<DmsInstanceIdentity>.Succeeded)await _staged.RenameAsync()).Value;

            // The statement itself, on a connection to the renamed database, with the predicate's oid wrong and then right.
            await using (
                NpgsqlConnection connection = await AdminConnection()
                    .OpenAsync(_staged.Target.Value, "dms1438-test", default)
            )
            {
                DmsServerSessionId session = await OwnSessionAsync(connection);
                _wrongOutcome = await PostgresqlPublicationQueries.AssignMarkerAsync(
                    connection,
                    renamed.Oid + 1,
                    _staged.Expected,
                    session,
                    default
                );
                _afterWrong = await _staged.TargetIdentityAsync();
                _rightOutcome = await PostgresqlPublicationQueries.AssignMarkerAsync(
                    connection,
                    renamed.Oid,
                    _staged.Expected,
                    session,
                    default
                );
            }

            _afterRight = await _staged.TargetIdentityAsync();
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
        public void It_updates_no_row_and_keeps_the_package_identity_for_another_oid()
        {
            _wrongOutcome
                .Should()
                .Be(
                    new DmsCommandOutcome.Failed(
                        new DmsCommandFailure(
                            DmsCommandFailureClass.Rejected,
                            DmsProvisioningDiagnostic.ForOperation(
                                PostgresqlPublicationQueries.AssignMarkerNoRowStep
                            )
                        )
                    )
                );
            _afterWrong.Should().Be(DmsShapedManifest.SourceIdentity);
        }

        [Test]
        public void It_commits_with_the_instance_oid() =>
            (_rightOutcome, _afterRight)
                .Should()
                .Be(((DmsCommandOutcome)new DmsCommandOutcome.Succeeded(), (Guid?)_staged!.Expected));
    }

    [TestFixture]
    public class Given_a_renamed_target_is_compensated
    {
        private Staged? _matching;
        private Staged? _mismatching;
        private DmsCommandOutcome _matchingOutcome = null!;
        private DmsCommandOutcome _mismatchingOutcome = null!;
        private DmsLiveTargetState _matchingAfter = null!;
        private DmsLiveTargetState _mismatchingAfter = null!;
        private DmsCommandOutcome _deleteOutcome = null!;
        private DmsLiveTargetState _afterDelete = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _matching = await Staged.CreateAsync("compensate");
            DmsInstanceIdentity renamed = (
                (DmsCommandOutcome<DmsInstanceIdentity>.Succeeded)await _matching.RenameAsync()
            ).Value;
            _matchingOutcome = await _matching.Engine.DropInstanceAsync(
                _matching.Holder,
                _matching.Target,
                renamed,
                DmsDropReason.Compensation,
                default
            );
            _matchingAfter = await Staged.InspectAsync(_matching.Target);

            _mismatching = await Staged.CreateAsync("compensateother");
            DmsInstanceIdentity.Postgresql other = (DmsInstanceIdentity.Postgresql)
                ((DmsCommandOutcome<DmsInstanceIdentity>.Succeeded)await _mismatching.RenameAsync()).Value;
            _mismatchingOutcome = await _mismatching.Engine.DropInstanceAsync(
                _mismatching.Holder,
                _mismatching.Target,
                other with
                {
                    Oid = other.Oid + 1,
                },
                DmsDropReason.Compensation,
                default
            );
            _mismatchingAfter = await Staged.InspectAsync(_mismatching.Target);

            _deleteOutcome = await _mismatching.Engine.DropInstanceAsync(
                _mismatching.Holder,
                _mismatching.Target,
                other,
                DmsDropReason.Delete,
                default
            );
            _afterDelete = await Staged.InspectAsync(_mismatching.Target);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            foreach (Staged? staged in new[] { _matching, _mismatching })
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
                                PostgresqlPublicationQueries.DropInstanceInstanceStep
                            )
                        )
                    )
                );
            _mismatchingAfter.Should().BeOfType<DmsLiveTargetState.Present>();
        }

        [Test]
        public void It_refuses_an_owned_delete_of_a_target_that_is_not_fenced()
        {
            _deleteOutcome
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
            _afterDelete.Should().BeOfType<DmsLiveTargetState.Present>();
        }
    }
}
