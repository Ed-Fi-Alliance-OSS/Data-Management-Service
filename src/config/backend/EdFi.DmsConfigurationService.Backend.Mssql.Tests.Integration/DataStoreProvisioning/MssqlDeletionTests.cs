// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
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

    /// <summary>
    /// F-01 on the holder session: under the fence, the identity read through the three-part name reports a missing
    /// identity column and an identity column that cannot be converted to text as typed reads. The error leaves the
    /// holder's single-user slot in place, and the fence is then reversed on the same holder.
    /// </summary>
    [TestFixture]
    public class Given_the_holder_reads_an_identity_column_that_is_missing_or_unconvertible
    {
        private TestDatabase? _database;
        private DmsCommandOutcome _firstFence = null!;
        private DmsIdentityRead _columnMissing = null!;
        private string _accessAfterMissingRead = "";
        private DmsCommandOutcome _firstUnfence = null!;
        private DmsCommandOutcome _secondFence = null!;
        private DmsIdentityRead _imageColumn = null!;
        private string _accessAfterImageRead = "";
        private DmsCommandOutcome _secondUnfence = null!;
        private string _accessAfterUnfence = "";

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _database = await CreateDmsShapedDatabaseAsync("holderid");
            await _database.ExecuteAsync(
                "ALTER TABLE [dms].[DataStoreIdentity] DROP COLUMN [SourceIdentity];"
            );
            DmsTargetDatabaseName target = TargetNameOf(_database.Name);
            DmsInstanceIdentity.Mssql instance = await _database.CatalogInstanceAsync();
            MssqlDmsTargetEngine engine = Engine();
            await using IDmsTargetLockHolder holder = await AcquireAsync(engine, target);

            _firstFence = await engine.FenceAsync(holder, target, instance, null, default);
            _columnMissing = await engine.ReadIdentityOnHolderAsync(holder, target, default);
            _accessAfterMissingRead = await UserAccessOfAsync(_database.Name);
            _firstUnfence = await engine.UnfenceAsync(holder, target, instance, default);

            await _database.ExecuteAsync(
                "ALTER TABLE [dms].[DataStoreIdentity] ADD [SourceIdentity] image NULL;"
            );
            await _database.ExecuteAsync("UPDATE [dms].[DataStoreIdentity] SET [SourceIdentity] = 0x01;");
            await WaitForNoSessionsOnAsync(_database.Name);
            _secondFence = await engine.FenceAsync(holder, target, instance, null, default);
            _imageColumn = await engine.ReadIdentityOnHolderAsync(holder, target, default);
            _accessAfterImageRead = await UserAccessOfAsync(_database.Name);
            _secondUnfence = await engine.UnfenceAsync(holder, target, instance, default);
            _accessAfterUnfence = await UserAccessOfAsync(_database.Name);
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
        public void It_fenced_the_target_for_each_read() =>
            (_firstFence, _secondFence)
                .Should()
                .Be((new DmsCommandOutcome.Succeeded(), new DmsCommandOutcome.Succeeded()));

        [Test]
        public void It_reports_a_missing_identity_column_as_column_missing() =>
            _columnMissing.Should().Be(new DmsIdentityRead.Invalid(DmsIdentityReadProblem.ColumnMissing));

        [Test]
        public void It_reports_an_unconvertible_identity_column_as_column_type_invalid() =>
            _imageColumn.Should().Be(new DmsIdentityRead.Invalid(DmsIdentityReadProblem.ColumnTypeInvalid));

        [Test]
        public void It_keeps_the_fence_standing_after_each_read() =>
            (_accessAfterMissingRead, _accessAfterImageRead).Should().Be(("SINGLE_USER", "SINGLE_USER"));

        [Test]
        public void It_reverses_the_fence_on_the_same_holder() =>
            (_firstUnfence, _secondUnfence, _accessAfterUnfence)
                .Should()
                .Be((new DmsCommandOutcome.Succeeded(), new DmsCommandOutcome.Succeeded(), "MULTI_USER"));
    }

    /// <summary>
    /// F-001 on the holder session: under the fence, the three-part identity read returns at most a GUID's 36
    /// characters with the server's overlength verdict, as the work session's read does. The target's own identity
    /// followed by a trailing space, and a 1 MiB value, are refused as unparsable rather than read as that identity;
    /// the native <c>uniqueidentifier</c> is still found, and every fence is reversed on the same holder.
    /// </summary>
    [TestFixture]
    public class Given_the_holder_reads_identity_text_longer_than_a_guid
    {
        private static readonly Guid _identity = MssqlDmsShapedManifest.SourceIdentity;

        private static readonly (string Name, string Value)[] _longer =
        [
            ("oversized", $"N'{_identity:D}' + REPLICATE(CONVERT(nvarchar(max), N'x'), 1048576)"),
            ("trailing space", $"N'{_identity:D} '"),
        ];

        private TestDatabase? _database;
        private DmsIdentityRead _native = null!;
        private readonly Dictionary<
            string,
            (List<(string? Text, bool Overlength)> Rows, DmsIdentityRead Read)
        > _observed = [];
        private readonly List<DmsCommandOutcome> _fences = [];
        private string _accessAfterUnfence = "";

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _database = await CreateDmsShapedDatabaseAsync("holderlong");
            DmsTargetDatabaseName target = TargetNameOf(_database.Name);
            DmsInstanceIdentity.Mssql instance = await _database.CatalogInstanceAsync();
            MssqlDmsTargetEngine engine = Engine();
            await using IDmsTargetLockHolder holder = await AcquireAsync(engine, target);

            async Task<DmsIdentityRead> FencedReadAsync()
            {
                await WaitForNoSessionsOnAsync(_database.Name);
                _fences.Add(await engine.FenceAsync(holder, target, instance, null, default));
                DmsIdentityRead read = await engine.ReadIdentityOnHolderAsync(holder, target, default);
                _fences.Add(await engine.UnfenceAsync(holder, target, instance, default));
                return read;
            }

            _native = await FencedReadAsync();
            await _database.ExecuteAsync(
                "ALTER TABLE [dms].[DataStoreIdentity] ALTER COLUMN [SourceIdentity] nvarchar(max) NOT NULL;"
            );
            foreach ((string name, string value) in _longer)
            {
                await _database.ExecuteAsync(
                    $"UPDATE [dms].[DataStoreIdentity] SET [SourceIdentity] = {value};"
                );
                DmsIdentityRead read = await FencedReadAsync();
                _observed[name] = (await HolderRowsAsync(target), read);
            }

            _accessAfterUnfence = await UserAccessOfAsync(_database.Name);
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
        public void It_finds_the_native_uniqueidentifier_on_the_holder() =>
            _native.Should().Be(new DmsIdentityRead.Found(_identity));

        [Test]
        public void It_returns_at_most_a_guid_of_text_with_the_overlength_verdict_through_the_three_part_name()
        {
            foreach ((string name, _) in _longer)
            {
                _observed[name].Rows.Should().Equal([($"{_identity:D}", true)], name);
            }
        }

        [Test]
        public void It_refuses_every_longer_value_on_the_holder_as_unparsable()
        {
            foreach ((string name, _) in _longer)
            {
                _observed[name]
                    .Read.Should()
                    .Be(new DmsIdentityRead.Invalid(DmsIdentityReadProblem.Unparsable), name);
            }
        }

        [Test]
        public void It_fenced_and_reversed_the_fence_for_each_read()
        {
            _fences.Should().HaveCount(6).And.AllBeEquivalentTo(new DmsCommandOutcome.Succeeded());
            _accessAfterUnfence.Should().Be("MULTI_USER");
        }

        /// <summary>The holder's three-part identity query, run on a side connection to <c>master</c> once the fence is reversed.</summary>
        private static async Task<List<(string? Text, bool Overlength)>> HolderRowsAsync(
            DmsTargetDatabaseName target
        )
        {
            List<(string? Text, bool Overlength)> rows = [];
            await using (SqlConnection connection = await AdminConnection().OpenMaintenanceAsync(default))
            {
                await using SqlCommand command = new(
                    MssqlInventoryQueries.SourceIdentityRowsIn(target),
                    connection
                );
                await using SqlDataReader reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    rows.Add(MssqlInventoryQueries.ReadSourceIdentityRow(reader));
                }
            }

            await WaitForNoSessionsOnAsync(target.Value);
            return rows;
        }
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

    /// <summary>
    /// The fence's holder deadline, through the engine on a held target lock, with no other session on the target and
    /// a <c>CommandTimeout</c> longer than <c>SessionTerminationTimeout</c>. The guarded <c>SET SINGLE_USER</c> batch
    /// runs before the termination bound starts, and the observation that proves exclusivity (D-22) starts inside it,
    /// so each has its own allowance within the holder's deadline: the review counterexample (a 59-second batch and a
    /// 40-second observation) and a batch and observation each one second short of their allowance both establish
    /// exclusivity. An observation that outlasts its own allowance is still ended by the deadline, and the fence
    /// stands for the delete to reverse. The durations are simulated: a fake clock advances through SqlClient's
    /// diagnostic events as each statement's result arrives, so a deadline passed meanwhile cancels the read of that
    /// result. This pins the holder budget against the fence's real control flow, not the driver's own timeouts.
    /// </summary>
    [TestFixture]
    public class Given_the_fence_statements_outlast_the_termination_bound_under_the_holder_deadline
    {
        private static readonly TimeSpan _command = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan _termination = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan _nearlyCommand = _command - TimeSpan.FromSeconds(1);
        private static readonly TimeSpan _counterexampleObservation = TimeSpan.FromSeconds(40);

        private FenceRun _counterexample = null!;
        private FenceRun _wholeAllowances = null!;
        private FenceRun _observationOverruns = null!;

        private sealed record FenceRun(
            string Database,
            DmsCommandOutcome Outcome,
            TimeSpan Elapsed,
            string Access
        );

        /// <summary>
        /// Advances the clock as the fence batch's and the first observation's results arrive on the holder session,
        /// through SqlClient's diagnostic events.
        /// </summary>
        private sealed class StatementClock
            : IObserver<DiagnosticListener>,
                IObserver<KeyValuePair<string, object?>>,
                IDisposable
        {
            private const string CommandCompleted = "Microsoft.Data.SqlClient.WriteCommandAfter";

            private readonly FakeTimeProvider _time;
            private readonly TimeSpan _batch;
            private readonly TimeSpan _observation;
            private readonly List<IDisposable> _subscriptions = [];
            private int _observations;

            public StatementClock(FakeTimeProvider time, TimeSpan batch, TimeSpan observation)
            {
                _time = time;
                _batch = batch;
                _observation = observation;
                IDisposable all = DiagnosticListener.AllListeners.Subscribe(this);
                lock (_subscriptions)
                {
                    _subscriptions.Add(all);
                }
            }

            void IObserver<DiagnosticListener>.OnNext(DiagnosticListener listener)
            {
                if (listener.Name == "SqlClientDiagnosticListener")
                {
                    IDisposable subscription = listener.Subscribe(this);
                    lock (_subscriptions)
                    {
                        _subscriptions.Add(subscription);
                    }
                }
            }

            void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> value)
            {
                if (
                    value.Key != CommandCompleted
                    || value.Value?.GetType().GetProperty("Command")?.GetValue(value.Value)
                        is not SqlCommand { CommandText: { } text }
                )
                {
                    return;
                }

                if (text.Contains("SET SINGLE_USER WITH ROLLBACK IMMEDIATE", StringComparison.Ordinal))
                {
                    _time.Advance(_batch);
                }
                else if (
                    text.Contains("sys.dm_tran_locks", StringComparison.Ordinal)
                    && Interlocked.Increment(ref _observations) == 1
                )
                {
                    _time.Advance(_observation);
                }
            }

            void IObserver<DiagnosticListener>.OnCompleted() { }

            void IObserver<DiagnosticListener>.OnError(Exception error) { }

            void IObserver<KeyValuePair<string, object?>>.OnCompleted() { }

            void IObserver<KeyValuePair<string, object?>>.OnError(Exception error) { }

            public void Dispose()
            {
                lock (_subscriptions)
                {
                    foreach (IDisposable subscription in _subscriptions)
                    {
                        subscription.Dispose();
                    }

                    _subscriptions.Clear();
                }
            }
        }

        /// <summary>Fences a target no other session uses, the batch taking nearly its allowance and the first observation <paramref name="observation"/>.</summary>
        private static async Task<FenceRun> FenceAsync(TimeSpan observation)
        {
            await using TestDatabase database = await CreateDmsShapedDatabaseAsync("budget");
            DmsTargetDatabaseName target = TargetNameOf(database.Name);
            DmsInstanceIdentity.Mssql instance = await database.CatalogInstanceAsync();
            FakeTimeProvider time = new();
            MssqlDmsTargetEngine engine = new(
                new MssqlAdminConnection(AdminConnectionString!, _command),
                Settings(
                    lockHeartbeatInterval: TimeSpan.FromMinutes(30),
                    sessionTerminationTimeout: _termination
                ),
                time,
                NullLogger.Instance
            );
            DmsCommandOutcome outcome;
            TimeSpan elapsed;
            await using (IDmsTargetLockHolder holder = await AcquireAsync(engine, target))
            {
                long started = time.GetTimestamp();
                using (new StatementClock(time, _nearlyCommand, observation))
                {
                    outcome = await engine.FenceAsync(holder, target, instance, null, default);
                }

                elapsed = time.GetElapsedTime(started);
            }

            return new FenceRun(database.Name, outcome, elapsed, await UserAccessOfAsync(database.Name));
        }

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _counterexample = await FenceAsync(_counterexampleObservation);
            _wholeAllowances = await FenceAsync(_nearlyCommand);
            _observationOverruns = await FenceAsync(_command + _command);
        }

        [Test]
        public void It_establishes_exclusivity_in_the_review_counterexample() =>
            (_counterexample.Outcome, _counterexample.Elapsed, _counterexample.Access)
                .Should()
                .Be(
                    (
                        new DmsCommandOutcome.Succeeded(),
                        _nearlyCommand + _counterexampleObservation,
                        "SINGLE_USER"
                    )
                );

        [Test]
        public void It_establishes_exclusivity_when_the_batch_and_the_observation_use_nearly_their_allowances() =>
            (_wholeAllowances.Outcome, _wholeAllowances.Elapsed, _wholeAllowances.Access)
                .Should()
                .Be((new DmsCommandOutcome.Succeeded(), _nearlyCommand + _nearlyCommand, "SINGLE_USER"));

        [Test]
        public void It_still_ends_an_observation_that_outlasts_its_allowance_and_keeps_the_fence()
        {
            DmsCommandOutcome.Failed failed = _observationOverruns
                .Outcome.Should()
                .BeOfType<DmsCommandOutcome.Failed>()
                .Subject;
            (failed.Failure.Class, failed.Failure.Diagnostic.Operation)
                .Should()
                .Be((DmsCommandFailureClass.Rejected, MssqlDeletionQueries.FenceExclusivityStep));
            failed
                .Residue.Should()
                .Equal(new DmsCleanupResidue(DmsResidueKind.FencedTarget, _observationOverruns.Database));
            _observationOverruns.Access.Should().Be("SINGLE_USER");
        }
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
