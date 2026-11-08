// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using static EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning.MssqlProvisioningTestServer;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning;

/// <summary>A session on a test database, idle or waiting inside a transaction; disposal ends it whatever happened.</summary>
internal sealed class CompetingSession : IAsyncDisposable
{
    private static readonly TimeSpan _bound = TimeSpan.FromSeconds(60);

    private readonly SqlConnection _connection;
    private readonly Task? _waiting;

    private CompetingSession(SqlConnection connection, Task? waiting, int sessionId)
    {
        _connection = connection;
        _waiting = waiting;
        SessionId = sessionId;
    }

    public int SessionId { get; }

    /// <summary>
    /// Opens a session on <paramref name="database"/>; <paramref name="busy"/> leaves it inside a transaction that has
    /// written to the database and waits (<c>WAITFOR</c>), observed waiting before this returns.
    /// </summary>
    public static async Task<CompetingSession> OpenAsync(string database, bool busy = false)
    {
        SqlConnection connection = await AdminConnection().OpenAsync(database, "dms1438-competing", default);
        await using (SqlCommand id = new("SELECT CONVERT(int, @@SPID);", connection))
        {
            int sessionId = (int)(await id.ExecuteScalarAsync())!;
            Task? waiting = null;
            if (busy)
            {
                SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync();
                SqlCommand wait = new(
                    "CREATE TABLE [dbo].[dms1438_competing] ([value] int NOT NULL); WAITFOR DELAY '00:10:00';",
                    connection,
                    transaction
                )
                {
                    CommandTimeout = 0,
                };
                waiting = wait.ExecuteNonQueryAsync();
                bool observed = await WaitUntilAsync(
                    async () => await WaitTypeOfAsync(sessionId) == "WAITFOR",
                    _bound
                );
                observed.Should().BeTrue("the competing session must be observed waiting");
            }

            return new CompetingSession(connection, waiting, sessionId);
        }
    }

    /// <summary>Whether the server lists the session now.</summary>
    public async Task<bool> IsListedAsync() =>
        await ScalarOnServerAsync<int>(
            "SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE session_id = @id AND program_name = N'dms1438-competing';",
            ("@id", SessionId)
        ) > 0;

    /// <summary>Whether the server no longer lists the session, within the bound.</summary>
    public Task<bool> GoneAsync() => WaitUntilAsync(async () => !await IsListedAsync(), _bound);

    /// <summary>What ended the wait within the bound: the exception, or a timeout when it is still waiting.</summary>
    public async Task<Exception?> WaitOutcomeAsync() =>
        _waiting is null || await Task.WhenAny(_waiting, Task.Delay(_bound)) != _waiting
            ? new TimeoutException("The wait did not end within the bound.")
            : await Record.ExceptionAsync(() => _waiting);

    public async ValueTask DisposeAsync()
    {
        await Record.ExceptionAsync(async () =>
        {
            if (await IsListedAsync())
            {
                await KillAsync(SessionId);
            }
        });
        if (_waiting is not null)
        {
            await Record.ExceptionAsync(() => _waiting.WaitAsync(_bound));
        }

        await _connection.DisposeAsync();
    }
}

/// <summary>
/// The SQL Server engine's owned-delete commands (6.6, Step 4.8) against a live server: the fence refuses another
/// instance and touches nothing, sets single-user mode and ends every session, busy or idle; the unfence restores
/// multi-user mode; the owned drop refuses an unfenced target and another instance, is refused by a session holding the
/// single-user slot (<c>3702</c>, and the unfence then <c>5064</c>), and drops our fenced instance with its files; a
/// name found empty is absent. A client that takes the slot right after the fence leaves exclusivity not established,
/// and an admin that cannot see other sessions cannot prove it; both leave the fence standing for the caller to reverse.
/// </summary>
[Category(Category)]
public class MssqlDeletionTests
{
    private static readonly ProtectedDatabaseNames _protectedNames = ProtectedDatabaseNames.Create(
        "edfi_configurationservice",
        []
    );

    internal static DmsTargetDatabaseName TargetNameOf(string name) =>
        DmsTargetDatabaseName.TryCreate(name, _protectedNames, out DmsTargetDatabaseName target)
        == DmsTargetNameValidation.Valid
            ? target
            : throw new InvalidOperationException("The target name does not satisfy the rules.");

    internal static Task<string> UserAccessOfAsync(string database) =>
        ScalarOnServerAsync<string>(
            "SELECT ISNULL(MAX(user_access_desc), N'') FROM sys.databases WHERE name = @name;",
            ("@name", database)
        );

    /// <summary>The catalog rows and the on-disk primary file left for <paramref name="instance"/> after a drop.</summary>
    internal static async Task<(int FileRows, bool PrimaryFileExists)> FilesLeftAsync(
        DmsInstanceIdentity.Mssql instance
    ) =>
        (
            await ScalarOnServerAsync<int>(
                "SELECT COUNT(*) FROM sys.master_files WHERE database_id = @id;",
                ("@id", instance.DatabaseId)
            ),
            await ScalarOnServerAsync<int>(
                "SELECT CONVERT(int, file_exists) FROM sys.dm_os_file_exists(@path);",
                ("@path", instance.PrimaryFileTag)
            ) == 1
        );

    private static async Task<IDmsTargetLockHolder> AcquireAsync(
        MssqlDmsTargetEngine engine,
        DmsTargetDatabaseName target
    ) =>
        await engine.AcquireTargetLockAsync(target, DmsOperationId.New(), default)
            is DmsTargetLockResult.Acquired acquired
            ? acquired.Holder
            : throw new InvalidOperationException("The target lock was not acquired.");

    [TestFixture]
    public class Given_the_engine_fences_and_drops_an_instance
    {
        private static DmsCommandOutcome Failed(DmsCommandFailureClass failureClass, string operation) =>
            new DmsCommandOutcome.Failed(
                new DmsCommandFailure(failureClass, DmsProvisioningDiagnostic.ForOperation(operation))
            );

        private TestDatabase? _database;
        private DmsInstanceIdentity.Mssql _instance = null!;
        private Exception? _keepRefused;
        private DmsCommandOutcome _wrongInstanceFence = null!;
        private DmsCommandOutcome _otherCreateDateFence = null!;
        private string _accessBeforeFence = "";
        private DmsCommandOutcome _fence = null!;
        private string _accessAfterFence = "";
        private bool _busyGone;
        private bool _idleGone;
        private Exception? _busyOutcome;
        private DmsCommandOutcome _unfence = null!;
        private string _accessAfterUnfence = "";
        private DmsCommandOutcome _dropUnfenced = null!;
        private DmsCommandOutcome _secondFence = null!;
        private Exception? _competingOpen;
        private Exception? _foreignDrop;
        private DmsIdentityRead _holderIdentity = null!;
        private DmsCommandOutcome _dropOtherInstance = null!;
        private DmsCommandOutcome _drop = null!;
        private bool _existsAfterDrop;
        private (int FileRows, bool PrimaryFileExists) _filesAfterDrop;
        private DmsCommandOutcome _dropAgain = null!;
        private DmsCommandOutcome _fenceAbsent = null!;
        private DmsCommandOutcome _unfenceAbsent = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _database = await CreateDmsShapedDatabaseAsync("fence");
            DmsTargetDatabaseName target = TargetNameOf(_database.Name);
            _instance = await _database.CatalogInstanceAsync();
            DmsInstanceIdentity.Mssql otherFile = _instance with
            {
                PrimaryFileTag = _instance.PrimaryFileTag + ".other",
            };
            MssqlDmsTargetEngine engine = Engine();
            await using IDmsTargetLockHolder holder = await AcquireAsync(engine, target);

            _keepRefused = await Record.ExceptionAsync(() =>
                engine.FenceAsync(
                    holder,
                    target,
                    _instance,
                    new DmsServerSessionId(60, DateTimeOffset.UtcNow),
                    default
                )
            );
            _wrongInstanceFence = await engine.FenceAsync(holder, target, otherFile, null, default);

            // The same database_id and file under another create_date: what a database recreated outside the
            // provisioner under the same name looks like (the id is reused, the default file name repeats).
            _otherCreateDateFence = await engine.FenceAsync(
                holder,
                target,
                _instance.WithCreateDateSnapshot(_instance.CreateDateSnapshot.AddSeconds(1)),
                null,
                default
            );
            _accessBeforeFence = await UserAccessOfAsync(_database.Name);

            await using (CompetingSession busy = await CompetingSession.OpenAsync(_database.Name, busy: true))
            await using (CompetingSession idle = await CompetingSession.OpenAsync(_database.Name))
            {
                _fence = await engine.FenceAsync(holder, target, _instance, null, default);
                _accessAfterFence = await UserAccessOfAsync(_database.Name);
                _busyGone = await busy.GoneAsync();
                _idleGone = await idle.GoneAsync();
                _busyOutcome = await busy.WaitOutcomeAsync();
            }

            _unfence = await engine.UnfenceAsync(holder, target, _instance, default);
            _accessAfterUnfence = await UserAccessOfAsync(_database.Name);
            _dropUnfenced = await engine.DropInstanceAsync(
                holder,
                target,
                _instance,
                DmsDropReason.Delete,
                default
            );

            _secondFence = await engine.FenceAsync(holder, target, _instance, null, default);

            // The holder that set single-user mode holds the one slot: nobody else may open or drop the target.
            string name = _database.Name;
            _competingOpen = await Record.ExceptionAsync(async () =>
                await (await CompetingSession.OpenAsync(name)).DisposeAsync()
            );
            _foreignDrop = await Record.ExceptionAsync(() =>
                ExecuteOnServerAsync($"DROP DATABASE {MssqlIdentifiers.Quote(name)};")
            );
            _holderIdentity = await engine.ReadIdentityOnHolderAsync(holder, target, default);

            _dropOtherInstance = await engine.DropInstanceAsync(
                holder,
                target,
                otherFile,
                DmsDropReason.Delete,
                default
            );
            _drop = await engine.DropInstanceAsync(holder, target, _instance, DmsDropReason.Delete, default);
            _existsAfterDrop = await UserAccessOfAsync(_database.Name) != "";
            _filesAfterDrop = await FilesLeftAsync(_instance);
            _dropAgain = await engine.DropInstanceAsync(
                holder,
                target,
                _instance,
                DmsDropReason.Delete,
                default
            );
            _fenceAbsent = await engine.FenceAsync(holder, target, _instance, null, default);
            _unfenceAbsent = await engine.UnfenceAsync(holder, target, _instance, default);
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
        public void It_refuses_to_keep_a_session_through_the_fence() =>
            _keepRefused.Should().BeOfType<ArgumentException>();

        [Test]
        public void It_refuses_to_fence_another_instance_and_touches_nothing() =>
            (_wrongInstanceFence, _otherCreateDateFence, _accessBeforeFence)
                .Should()
                .Be(
                    (
                        Failed(DmsCommandFailureClass.DoesNotExist, MssqlDeletionQueries.FenceInstanceStep),
                        Failed(DmsCommandFailureClass.DoesNotExist, MssqlDeletionQueries.FenceInstanceStep),
                        "MULTI_USER"
                    )
                );

        [Test]
        public void It_fences_in_single_user_mode_and_ends_every_session() =>
            (_fence, _accessAfterFence, _busyGone, _idleGone)
                .Should()
                .Be((new DmsCommandOutcome.Succeeded(), "SINGLE_USER", true, true));

        [Test]
        public void It_ended_the_busy_session_inside_its_transaction() =>
            _busyOutcome.Should().BeOfType<SqlException>();

        [Test]
        public void It_reverses_the_fence() =>
            (_unfence, _accessAfterUnfence).Should().Be((new DmsCommandOutcome.Succeeded(), "MULTI_USER"));

        [Test]
        public void It_refuses_to_drop_an_unfenced_target() =>
            _dropUnfenced
                .Should()
                .Be(Failed(DmsCommandFailureClass.ObjectInUse, MssqlDeletionQueries.DropTargetNotFencedStep));

        [Test]
        public void It_keeps_the_single_user_slot_on_the_holder_so_nobody_else_may_open_or_drop_the_target() =>
            (_secondFence, (_competingOpen as SqlException)?.Number, (_foreignDrop as SqlException)?.Number)
                .Should()
                .Be((new DmsCommandOutcome.Succeeded(), 4060, 3702));

        [Test]
        public void It_reads_the_identity_on_the_holder_under_the_fence() =>
            _holderIdentity.Should().Be(new DmsIdentityRead.Found(MssqlDmsShapedManifest.SourceIdentity));

        [Test]
        public void It_refuses_to_drop_another_instance() =>
            _dropOtherInstance
                .Should()
                .Be(Failed(DmsCommandFailureClass.DoesNotExist, MssqlDeletionQueries.DropTargetInstanceStep));

        [Test]
        public void It_drops_our_fenced_instance_with_its_files() =>
            (_drop, _existsAfterDrop, _filesAfterDrop)
                .Should()
                .Be((new DmsCommandOutcome.Succeeded(), false, (0, false)));

        [Test]
        public void It_reports_a_name_found_empty_as_absent() =>
            _dropAgain
                .Should()
                .Be(Failed(DmsCommandFailureClass.DoesNotExist, DmsEngineOperations.DropTargetAbsent));

        [Test]
        public void It_refuses_to_fence_or_unfence_a_name_found_empty() =>
            (_fenceAbsent, _unfenceAbsent)
                .Should()
                .Be(
                    (
                        Failed(DmsCommandFailureClass.DoesNotExist, MssqlDeletionQueries.FenceInstanceStep),
                        Failed(DmsCommandFailureClass.DoesNotExist, MssqlDeletionQueries.UnfenceInstanceStep)
                    )
                );
    }

    [TestFixture]
    public class Given_the_fence_still_observes_a_session_at_the_bound
    {
        private TestDatabase? _database;
        private int _observations;
        private DmsCommandOutcome _fence = null!;
        private string _accessAfterFence = "";
        private DmsCommandOutcome _unfence = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _database = await CreateDmsShapedDatabaseAsync("slot");
            DmsTargetDatabaseName target = TargetNameOf(_database.Name);
            DmsInstanceIdentity.Mssql instance = await _database.CatalogInstanceAsync();
            MssqlDmsTargetEngine engine = new(
                AdminConnection(),
                Settings(sessionTerminationTimeout: TimeSpan.FromSeconds(2)),
                TimeProvider.System,
                NullLogger.Instance
            )
            {
                // A session the server keeps listing; the real observation must have found none.
                FenceObservationHook = (observation, observed) =>
                {
                    _observations = observation + 1;
                    _realObservations.Add(observed);
                    return new MssqlFenceObservation(true, 1);
                },
            };
            await using IDmsTargetLockHolder holder = await AcquireAsync(engine, target);
            _fence = await engine.FenceAsync(holder, target, instance, null, default);
            _accessAfterFence = await UserAccessOfAsync(_database.Name);
            _unfence = await engine.UnfenceAsync(holder, target, instance, default);
        }

        private readonly List<MssqlFenceObservation> _realObservations = [];

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_database is not null)
            {
                await _database.DisposeAsync();
            }
        }

        [Test]
        public void It_reports_exclusivity_not_established_and_the_fence_as_residue()
        {
            DmsCommandOutcome.Failed failed = _fence.Should().BeOfType<DmsCommandOutcome.Failed>().Subject;
            (failed.Failure.Class, failed.Failure.Diagnostic.Operation)
                .Should()
                .Be((DmsCommandFailureClass.ObjectInUse, MssqlDeletionQueries.FenceExclusivityStep));
            failed
                .Residue.Should()
                .Equal(new DmsCleanupResidue(DmsResidueKind.FencedTarget, _database!.Name));
        }

        [Test]
        public void It_kept_observing_until_the_bound_with_the_fence_standing() =>
            (_observations > 1, _accessAfterFence, _realObservations.Distinct().Single())
                .Should()
                .Be((true, "SINGLE_USER", new MssqlFenceObservation(true, 0)));

        [Test]
        public void It_reverses_the_fence() => _unfence.Should().Be(new DmsCommandOutcome.Succeeded());
    }

    [TestFixture]
    public class Given_an_admin_that_cannot_see_other_sessions
    {
        private TestDatabase? _database;
        private string? _login;
        private DmsCommandOutcome _fence = null!;
        private string _accessAfterFence = "";
        private DmsCommandOutcome _unfence = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _database = await CreateDmsShapedDatabaseAsync("blind");
            DmsTargetDatabaseName target = TargetNameOf(_database.Name);
            DmsInstanceIdentity.Mssql instance = await _database.CatalogInstanceAsync();

            // dbcreator may alter any database and see the file catalog, but not other sessions.
            _login = await CreateLoginAsync("blind");
            await ExecuteOnServerAsync(
                $"ALTER SERVER ROLE [dbcreator] ADD MEMBER {MssqlIdentifiers.Quote(_login)};"
            );
            MssqlDmsTargetEngine engine = EngineAs(_login);
            await using IDmsTargetLockHolder holder = await AcquireAsync(engine, target);
            _fence = await engine.FenceAsync(holder, target, instance, null, default);
            _accessAfterFence = await UserAccessOfAsync(_database.Name);
            _unfence = await engine.UnfenceAsync(holder, target, instance, default);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_database is not null)
            {
                await _database.DisposeAsync();
            }

            if (_login is not null)
            {
                await DropLoginAsync(_login);
            }
        }

        [Test]
        public void It_reports_the_missing_visibility_with_the_fence_as_residue()
        {
            DmsCommandOutcome.Failed failed = _fence.Should().BeOfType<DmsCommandOutcome.Failed>().Subject;
            (failed.Failure.Class, failed.Failure.Diagnostic.Operation, _accessAfterFence)
                .Should()
                .Be(
                    (
                        DmsCommandFailureClass.PrivilegeMissing,
                        MssqlSessionQueries.IdentifySessionStep,
                        "SINGLE_USER"
                    )
                );
            failed
                .Residue.Should()
                .Equal(new DmsCleanupResidue(DmsResidueKind.FencedTarget, _database!.Name));
        }

        [Test]
        public void It_can_reverse_the_fence() => _unfence.Should().Be(new DmsCommandOutcome.Succeeded());
    }
}
