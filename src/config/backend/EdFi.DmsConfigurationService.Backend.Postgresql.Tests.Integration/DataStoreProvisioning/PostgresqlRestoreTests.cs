// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using static EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning.DmsProvisioningTestServer;
using FakePsql = EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Postgresql.FakePsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning;

/// <summary>
/// The engine's replay into a staging database (6.3 step 5, D-10, Step 3.5): the shaped artifact commits
/// and the staging instance comes back; a rejected statement rolls the single transaction back with its
/// SQLSTATE; a replay the restore bound ends, and one the caller cancels, are terminated with proof and
/// found rolled back, with nothing left of the replay session; and the D-16 role check refuses a role
/// that is not locked down before any process starts. The replay fixtures need a real <c>psql</c>
/// (<see cref="PsqlEnvironmentVariableName"/>) and skip without one; the fixture whose process outlives a
/// failed kill runs a fake executable that never connects, on every platform.
/// </summary>
[Category(Category)]
public class PostgresqlRestoreTests
{
    private const string SleepStatement = "SELECT pg_catalog.pg_sleep(60);\n";

    private static readonly TimeSpan _bound = TimeSpan.FromSeconds(20);
    private static readonly ProtectedDatabaseNames _protectedNames = ProtectedDatabaseNames.Create(
        "edfi_configurationservice",
        []
    );

    private static byte[] ShapedSql(string prefix = "", string suffix = "") =>
        Encoding.UTF8.GetBytes(prefix + File.ReadAllText(FixturePath("dms-shaped-database.sql")) + suffix);

    /// <summary>A replay artifact in the private directory of its own operation, as the catalog would stage it.</summary>
    private sealed class ReplayArtifact : IDisposable
    {
        private readonly string _root;
        private readonly PrivateOperationDirectory _directory;

        private ReplayArtifact(string root, PrivateOperationDirectory directory, StagedArtifact artifact)
        {
            _root = root;
            _directory = directory;
            Artifact = artifact;
        }

        public StagedArtifact Artifact { get; }

        public static ReplayArtifact Write(DmsOperationId operation, byte[] sql)
        {
            string root = Path.Combine(Path.GetTempPath(), "dms1438-replay-" + operation.Value);
            Directory.CreateDirectory(root);
            PrivateOperationDirectory directory = PrivateOperationDirectory.Create(root, operation);
            string path = directory.PathOf("artifact.sql");
            File.WriteAllBytes(path, sql);
            return new ReplayArtifact(
                root,
                directory,
                new StagedArtifact(
                    path,
                    Convert.ToHexStringLower(SHA256.HashData(sql)),
                    DmsArtifactKind.PostgresqlSqlDump
                )
            );
        }

        public void Dispose()
        {
            _directory.TryDelete();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Left for the operating system; nothing under test depends on it.
            }
        }
    }

    /// <summary>One operation's staging stage on the engine: lock, staging database, artifact; the drop and release in disposal.</summary>
    private sealed class Replay : IAsyncDisposable
    {
        private readonly IDmsTargetLockHolder _holder;
        private readonly ReplayArtifact _artifact;

        private Replay(
            PostgresqlDmsTargetEngine engine,
            DmsOperationId operation,
            IDmsTargetLockHolder holder,
            DmsInstanceIdentity instance,
            ReplayArtifact artifact
        )
        {
            Engine = engine;
            Operation = operation;
            _holder = holder;
            Instance = instance;
            _artifact = artifact;
        }

        public PostgresqlDmsTargetEngine Engine { get; }

        public DmsOperationId Operation { get; }

        public DmsDatabaseName Stage => Operation.StagingDatabaseName;

        public DmsInstanceIdentity Instance { get; }

        internal static DmsTargetDatabaseName TargetName(string purpose) =>
            DmsTargetDatabaseName.TryCreate(
                $"dms1438_{purpose}_{DmsOperationId.New().Value}",
                _protectedNames,
                out DmsTargetDatabaseName name
            ) == DmsTargetNameValidation.Valid
                ? name
                : throw new InvalidOperationException("The target name does not satisfy the rules.");

        public static async Task<Replay> StartAsync(byte[] sql, TimeSpan? restoreTimeout = null)
        {
            PostgresqlDmsTargetEngine engine = DmsProvisioningTestServer.Engine(
                restoreTimeout: restoreTimeout
            );
            DmsOperationId operation = DmsOperationId.New();
            IDmsTargetLockHolder holder = await engine.AcquireTargetLockAsync(
                TargetName("replay"),
                operation,
                default
            )
                is DmsTargetLockResult.Acquired acquired
                ? acquired.Holder
                : throw new InvalidOperationException("The target lock was not acquired.");
            DmsCommandOutcome<DmsInstanceIdentity> created = await engine.CreateStagingAsync(
                holder,
                operation.StagingDatabaseName,
                new DmsProvisionClaim(operation, Guid.NewGuid()),
                default
            );
            DmsInstanceIdentity instance = created
                .Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Succeeded>()
                .Subject.Value;
            return new Replay(engine, operation, holder, instance, ReplayArtifact.Write(operation, sql));
        }

        public Task<DmsCommandOutcome<DmsInstanceIdentity>> RestoreAsync(
            CancellationToken cancellationToken = default
        ) =>
            Engine.RestoreIntoStagingAsync(
                Stage,
                TargetName("replay"),
                _artifact.Artifact,
                Operation,
                cancellationToken
            );

        public Task<bool> SchemaExistsAsync(string schemaName) =>
            ScalarOnStageAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_namespace n WHERE n.nspname = @name);",
                ("name", schemaName)
            );

        public Task<Guid> IdentityAsync() =>
            ScalarOnStageAsync<Guid>("SELECT \"SourceIdentity\" FROM \"dms\".\"DataStoreIdentity\";");

        public async Task<int> ReplaySessionCountAsync() =>
            (await Engine.FindSessionsAsync(Stage, Operation.RestoreApplicationName, default)).Count;

        public async ValueTask DisposeAsync()
        {
            await Record.ExceptionAsync(() => Engine.DropStagingAsync(Stage, Instance, default));
            await _holder.DisposeAsync();
            _artifact.Dispose();
            await ExecuteOnServerAsync(
                $"DROP DATABASE IF EXISTS {PostgresqlIdentifiers.Quote(Stage.Value)} WITH (FORCE);"
            );
        }

        private async Task<T> ScalarOnStageAsync<T>(
            string sql,
            params (string Name, object Value)[] parameters
        )
        {
            await using NpgsqlConnection connection = await AdminConnection()
                .OpenAsync(Stage.Value, "dms1438-test", default);
            await using NpgsqlCommand command = new(sql, connection);
            foreach ((string name, object value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            return (T)(await command.ExecuteScalarAsync())!;
        }
    }

    [TestFixture]
    public class Given_the_shaped_artifact_is_replayed
    {
        private Replay? _replay;
        private DmsCommandOutcome<DmsInstanceIdentity> _outcome = null!;
        private Guid _identity;
        private int _replaySessions;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            RequirePsqlOrSkip();
            _replay = await Replay.StartAsync(ShapedSql());
            _outcome = await _replay.RestoreAsync();
            _identity = await _replay.IdentityAsync();
            _replaySessions = await _replay.ReplaySessionCountAsync();
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_replay is not null)
            {
                await _replay.DisposeAsync();
            }
        }

        [Test]
        public void It_commits_and_returns_the_staging_instance() =>
            _outcome.Should().Be(new DmsCommandOutcome<DmsInstanceIdentity>.Succeeded(_replay!.Instance));

        [Test]
        public void It_restored_the_package_identity() =>
            _identity.Should().Be(DmsShapedManifest.SourceIdentity);

        [Test]
        public void It_leaves_no_replay_session() => _replaySessions.Should().Be(0);
    }

    [TestFixture]
    public class Given_the_artifact_ends_in_a_rejected_statement
    {
        private Replay? _replay;
        private DmsCommandOutcome<DmsInstanceIdentity> _outcome = null!;
        private bool _dmsSchemaExists;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            RequirePsqlOrSkip();
            _replay = await Replay.StartAsync(ShapedSql(suffix: "\nSELECT 1 / 0;\n"));
            _outcome = await _replay.RestoreAsync();
            _dmsSchemaExists = await _replay.SchemaExistsAsync("dms");
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_replay is not null)
            {
                await _replay.DisposeAsync();
            }
        }

        [Test]
        public void It_reports_the_rejection_with_its_sqlstate() =>
            _outcome
                .Should()
                .Be(
                    new DmsCommandOutcome<DmsInstanceIdentity>.Failed(
                        new DmsCommandFailure(
                            DmsCommandFailureClass.Rejected,
                            new DmsProvisioningDiagnostic(
                                string.Empty,
                                "22012",
                                PsqlRestoreRunner.RestoreStep
                            )
                        )
                    )
                );

        [Test]
        public void It_rolled_the_single_transaction_back() => _dmsSchemaExists.Should().BeFalse();
    }

    [TestFixture]
    public class Given_the_replay_exceeds_the_restore_bound
    {
        private Replay? _replay;
        private DmsCommandOutcome<DmsInstanceIdentity> _outcome = null!;
        private bool _dmsSchemaExists;
        private bool _replaySessionGone;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            RequirePsqlOrSkip();
            _replay = await Replay.StartAsync(ShapedSql(suffix: SleepStatement), TimeSpan.FromSeconds(2));
            _outcome = await _replay.RestoreAsync();
            _dmsSchemaExists = await _replay.SchemaExistsAsync("dms");
            _replaySessionGone = await WaitUntilAsync(
                async () => await _replay.ReplaySessionCountAsync() == 0,
                _bound
            );
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_replay is not null)
            {
                await _replay.DisposeAsync();
            }
        }

        [Test]
        public void It_reports_the_timeout_as_a_definite_rolled_back_failure() =>
            _outcome
                .Should()
                .Be(
                    new DmsCommandOutcome<DmsInstanceIdentity>.Failed(
                        new DmsCommandFailure(
                            DmsCommandFailureClass.Rejected,
                            DmsProvisioningDiagnostic.ForOperation(DmsEngineOperations.RestoreTimedOut)
                        )
                    )
                );

        [Test]
        public void It_left_nothing_of_the_replay() =>
            (_dmsSchemaExists, _replaySessionGone).Should().Be((false, true));
    }

    [TestFixture]
    public class Given_the_replay_is_cancelled_by_the_caller
    {
        private Replay? _replay;
        private bool _replayObserved;
        private DmsCommandOutcome<DmsInstanceIdentity> _outcome = null!;
        private bool _dmsSchemaExists;
        private bool _replaySessionGone;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            RequirePsqlOrSkip();
            _replay = await Replay.StartAsync(ShapedSql(suffix: SleepStatement));
            using CancellationTokenSource cancellation = new();
            Task<DmsCommandOutcome<DmsInstanceIdentity>> restoring = _replay.RestoreAsync(cancellation.Token);
            _replayObserved = await WaitUntilAsync(
                async () => await _replay.ReplaySessionCountAsync() == 1,
                _bound
            );
            await cancellation.CancelAsync();
            _outcome = await restoring;
            _dmsSchemaExists = await _replay.SchemaExistsAsync("dms");
            _replaySessionGone = await WaitUntilAsync(
                async () => await _replay.ReplaySessionCountAsync() == 0,
                _bound
            );
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_replay is not null)
            {
                await _replay.DisposeAsync();
            }
        }

        [Test]
        public void It_observed_the_replay_session_by_application_name() => _replayObserved.Should().BeTrue();

        [Test]
        public void It_returns_the_cancellation_as_a_rolled_back_failure_instead_of_throwing() =>
            _outcome
                .Should()
                .Be(
                    new DmsCommandOutcome<DmsInstanceIdentity>.Failed(
                        new DmsCommandFailure(
                            DmsCommandFailureClass.Rejected,
                            DmsProvisioningDiagnostic.ForOperation(DmsEngineOperations.RestoreCancelled)
                        )
                    )
                );

        [Test]
        public void It_left_nothing_of_the_replay() =>
            (_dmsSchemaExists, _replaySessionGone).Should().Be((false, true));
    }

    [TestFixture]
    public class Given_a_replay_process_whose_end_cannot_be_confirmed
    {
        private DmsOperationId _operation;
        private string _root = string.Empty;
        private FakePsql? _fake;
        private DmsCommandOutcome<DmsInstanceIdentity> _outcome = null!;
        private int _sessionsAfterTheRun = -1;
        private DmsLiveTargetState _stagingAfterTheRun = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _operation = DmsOperationId.New();
            DmsDatabaseName stage = _operation.StagingDatabaseName;
            await ExecuteOnServerAsync($"CREATE DATABASE {PostgresqlIdentifiers.Quote(stage.Value)};");
            _root = Path.Combine(Path.GetTempPath(), "dms1438-unconfirmed-" + _operation.Value);
            Directory.CreateDirectory(_root);
            PrivateOperationDirectory directory = PrivateOperationDirectory.Create(_root, _operation);
            string artifactPath = directory.PathOf("artifact.sql");
            await File.WriteAllTextAsync(artifactPath, "SELECT 1;\n");

            // The fake never connects; the bound ends the run and the kill fails, so its end is unconfirmed.
            _fake = FakePsql.Blocking(Path.Combine(_root, "started"));
            PsqlRestoreRunner runner = new(
                _fake.ExecutablePath,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(1),
                TimeProvider.System,
                NullLogger.Instance,
                kill: _ => throw new System.ComponentModel.Win32Exception(5, "Access is denied.")
            );
            PostgresqlDmsTargetEngine engine = new(
                AdminConnection(),
                Settings(),
                TimeProvider.System,
                NullLogger.Instance,
                runner
            );
            _outcome = await engine.RestoreIntoStagingAsync(
                stage,
                Replay.TargetName("replay"),
                new StagedArtifact(artifactPath, new string('0', 64), DmsArtifactKind.PostgresqlSqlDump),
                _operation,
                default
            );
            _sessionsAfterTheRun = (
                await engine.FindSessionsAsync(stage, _operation.RestoreApplicationName, default)
            ).Count;
            _stagingAfterTheRun = await engine.InspectAsync(stage, default);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_fake is not null)
            {
                await _fake.DisposeAsync();
            }

            await ExecuteOnServerAsync(
                $"DROP DATABASE IF EXISTS {PostgresqlIdentifiers.Quote(_operation.StagingDatabaseName.Value)} WITH (FORCE);"
            );
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Left for the operating system; nothing under test depends on it.
            }
        }

        [Test]
        public void It_had_no_session_for_any_lookup_to_find() => _sessionsAfterTheRun.Should().Be(0);

        [Test]
        public void It_leaves_the_outcome_unknown_and_unresolvable() =>
            _outcome
                .Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Unknown>()
                .Which.Should()
                .BeEquivalentTo(new { Session = (DmsServerSessionId?)null, Quiescent = false });

        [Test]
        public void It_reports_the_process_as_residue() =>
            _outcome
                .Residue.Should()
                .ContainSingle()
                .Which.Should()
                .Match<DmsCleanupResidue>(residue =>
                    residue.Kind == DmsResidueKind.ChildProcess
                    && residue.ResourceId.StartsWith(
                        _operation.RestoreApplicationName + "/",
                        StringComparison.Ordinal
                    )
                );

        [Test]
        public void It_leaves_the_staging_database_in_place() =>
            _stagingAfterTheRun.Should().BeOfType<DmsLiveTargetState.Present>();
    }

    /// <summary>
    /// The caller cancels after the replay process exited 0 and before the engine reads the staging instance (6.10).
    /// The admin connection's source is read at every open, and it cancels the caller's token at the first open after
    /// the fake's exit signal, which is the instance read, so the timing is fixed without a delay. The exit already
    /// proved the outcome, so the engine still returns the staging instance instead of losing it to the cancellation.
    /// </summary>
    [TestFixture]
    public class Given_the_caller_cancels_after_the_replay_exited_successfully
    {
        private DmsOperationId _operation;
        private string _root = string.Empty;
        private FakePsql? _fake;
        private bool _cancelledBeforeTheRead;
        private DmsInstanceIdentity _created = null!;
        private DmsCommandOutcome<DmsInstanceIdentity> _outcome = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _operation = DmsOperationId.New();
            DmsDatabaseName stage = _operation.StagingDatabaseName;
            await ExecuteOnServerAsync($"CREATE DATABASE {PostgresqlIdentifiers.Quote(stage.Value)};");
            _root = Path.Combine(Path.GetTempPath(), "dms1438-exited-" + _operation.Value);
            Directory.CreateDirectory(_root);
            PrivateOperationDirectory directory = PrivateOperationDirectory.Create(_root, _operation);
            string artifactPath = directory.PathOf("artifact.sql");
            await File.WriteAllTextAsync(artifactPath, "SELECT 1;\n");

            string exited = Path.Combine(_root, "exited");
            _fake = FakePsql.ExitingSuccessfully(exited);
            using CancellationTokenSource caller = new();
            PostgresqlAdminConnection admin = new(
                () =>
                {
                    if (File.Exists(exited) && !caller.IsCancellationRequested)
                    {
                        caller.Cancel();
                        _cancelledBeforeTheRead = true;
                    }

                    return AdminConnectionString!;
                },
                TimeSpan.FromSeconds(30)
            );
            PostgresqlDmsTargetEngine engine = new(
                admin,
                Settings(),
                TimeProvider.System,
                NullLogger.Instance,
                new PsqlRestoreRunner(
                    _fake.ExecutablePath,
                    TimeSpan.FromMinutes(1),
                    TimeProvider.System,
                    NullLogger.Instance
                )
            );
            _created = (await engine.InspectAsync(stage, default))
                .Should()
                .BeOfType<DmsLiveTargetState.Present>()
                .Subject.Instance;
            _outcome = await engine.RestoreIntoStagingAsync(
                stage,
                Replay.TargetName("replay"),
                new StagedArtifact(artifactPath, new string('0', 64), DmsArtifactKind.PostgresqlSqlDump),
                _operation,
                caller.Token
            );
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_fake is not null)
            {
                await _fake.DisposeAsync();
            }

            await ExecuteOnServerAsync(
                $"DROP DATABASE IF EXISTS {PostgresqlIdentifiers.Quote(_operation.StagingDatabaseName.Value)} WITH (FORCE);"
            );
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Left for the operating system; nothing under test depends on it.
            }
        }

        [Test]
        public void It_cancels_the_caller_only_after_the_replay_exited() =>
            _cancelledBeforeTheRead.Should().BeTrue();

        [Test]
        public void It_returns_the_staging_instance_the_exit_proved() =>
            _outcome
                .Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Succeeded>()
                .Which.Value.IsSameStableInstance(_created)
                .Should()
                .BeTrue();
    }

    /// <summary>
    /// Review finding 1, through the engine's real path: a fake <c>psql</c> writes what the real one writes and
    /// exits as it does, against an empty staging database, so whatever reaches resolution is found rolled back. A
    /// German server's refusal (exit 3) is the definite privilege failure, not a rollback; a connection attempt that
    /// failed (exit 2, no SQLSTATE: sqlstate reporting starts only once connected) is the retryable rollback; a script
    /// error psql reports without a SQLSTATE (exit 3) is the replay's own, permanent, failure; a server rejection
    /// (exit 3) is definite either way, retryable for a deadlock and the replay's own for any other; and a process
    /// ended from outside (exit 137) after a notice of an error class is not read as that notice's definite
    /// rejection (the notice carries <c>42P04</c> because its definite class, <c>DuplicateName</c>, differs from
    /// the rollback's <c>Rejected</c>).
    /// </summary>
    [TestFixture]
    public class Given_replays_that_fail_in_ways_only_the_exit_code_and_last_message_distinguish
    {
        private readonly Dictionary<string, DmsCommandOutcome<DmsInstanceIdentity>> _outcomes = [];

        private static DmsCommandOutcome<DmsInstanceIdentity> Rejected(
            DmsCommandFailureClass failureClass,
            string? sqlState,
            string step
        ) =>
            new DmsCommandOutcome<DmsInstanceIdentity>.Failed(
                new DmsCommandFailure(
                    failureClass,
                    new DmsProvisioningDiagnostic(string.Empty, sqlState, step)
                )
            );

        private static async Task<DmsCommandOutcome<DmsInstanceIdentity>> ReplayAsync(
            IReadOnlyList<string> standardError,
            int exitCode
        )
        {
            DmsOperationId operation = DmsOperationId.New();
            DmsDatabaseName stage = operation.StagingDatabaseName;
            string root = Path.Combine(Path.GetTempPath(), "dms1438-exit-" + operation.Value);
            await ExecuteOnServerAsync($"CREATE DATABASE {PostgresqlIdentifiers.Quote(stage.Value)};");
            await using FakePsql fake = FakePsql.WritingStandardError(standardError, exitCode);
            try
            {
                Directory.CreateDirectory(root);
                PrivateOperationDirectory directory = PrivateOperationDirectory.Create(root, operation);
                string artifactPath = directory.PathOf("artifact.sql");
                await File.WriteAllTextAsync(artifactPath, "SELECT 1;\n");
                PostgresqlDmsTargetEngine engine = new(
                    AdminConnection(),
                    Settings(),
                    TimeProvider.System,
                    NullLogger.Instance,
                    new PsqlRestoreRunner(
                        fake.ExecutablePath,
                        TimeSpan.FromMinutes(1),
                        TimeProvider.System,
                        NullLogger.Instance
                    )
                );
                return await engine.RestoreIntoStagingAsync(
                    stage,
                    Replay.TargetName("replay"),
                    new StagedArtifact(artifactPath, new string('0', 64), DmsArtifactKind.PostgresqlSqlDump),
                    operation,
                    default
                );
            }
            finally
            {
                await ExecuteOnServerAsync(
                    $"DROP DATABASE IF EXISTS {PostgresqlIdentifiers.Quote(stage.Value)} WITH (FORCE);"
                );
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException)
                {
                    // Left for the operating system; nothing under test depends on it.
                }
            }
        }

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _outcomes["localized refusal"] = await ReplayAsync(
                ["psql:/tmp/artifact.sql:3: FEHLER:  42501"],
                3
            );
            _outcomes["failed connection attempt"] = await ReplayAsync(
                [
                    "psql: error: connection to server at \"db\" (10.0.0.5), port 5432 failed: FATAL:  password authentication failed for user \"dms\"",
                ],
                2
            );
            _outcomes["script error without a sqlstate"] = await ReplayAsync(
                ["psql:/tmp/artifact.sql:1: error: invalid command \\restrict"],
                3
            );
            _outcomes["ended from outside after a notice"] = await ReplayAsync(
                ["psql:/tmp/artifact.sql:2: NOTICE:  42P04"],
                137
            );
            _outcomes["deadlock"] = await ReplayAsync(["psql:/tmp/artifact.sql:4: ERROR:  40P01"], 3);
            _outcomes["duplicate object"] = await ReplayAsync(["psql:/tmp/artifact.sql:4: ERROR:  42710"], 3);
        }

        [Test]
        public void It_reads_a_localized_refusal_as_the_definite_privilege_failure() =>
            _outcomes["localized refusal"]
                .Should()
                .Be(
                    Rejected(DmsCommandFailureClass.PrivilegeMissing, "42501", PsqlRestoreRunner.RestoreStep)
                );

        [Test]
        public void It_reports_a_failed_connection_attempt_as_a_retryable_rollback() =>
            _outcomes["failed connection attempt"]
                .Should()
                .Be(Rejected(DmsCommandFailureClass.Rejected, null, DmsEngineOperations.RestoreRolledBack));

        [Test]
        public void It_reports_a_script_error_without_a_sqlstate_as_the_replays_own_failure() =>
            _outcomes["script error without a sqlstate"]
                .Should()
                .Be(Rejected(DmsCommandFailureClass.Rejected, null, PsqlRestoreRunner.RestoreStep));

        [Test]
        public void It_reports_a_deadlock_as_a_definite_but_retryable_rollback() =>
            _outcomes["deadlock"]
                .Should()
                .Be(
                    Rejected(DmsCommandFailureClass.Rejected, "40P01", DmsEngineOperations.RestoreRolledBack)
                );

        [Test]
        public void It_reports_any_other_server_rejection_as_the_replays_own_failure() =>
            _outcomes["duplicate object"]
                .Should()
                .Be(Rejected(DmsCommandFailureClass.Rejected, "42710", PsqlRestoreRunner.RestoreStep));

        [Test]
        public void It_does_not_read_an_earlier_notice_as_the_definite_rejection_after_another_exit() =>
            _outcomes["ended from outside after a notice"]
                .Should()
                .Be(Rejected(DmsCommandFailureClass.Rejected, "42P04", PsqlRestoreRunner.RestoreStep));
    }

    [TestFixture]
    public class Given_the_enqueue_owner_role_is_not_locked_down
    {
        private Replay? _replay;
        private DmsCommandOutcome<DmsInstanceIdentity> _outcome = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            await RequireSuperuserOrSkipAsync();
            await EnsureEnqueueOwnerRoleAsync();
            await ExecuteOnServerAsync(
                $"ALTER ROLE {PostgresqlIdentifiers.Quote(DmsTargetValidator.EnqueueOwnerRoleName)} INHERIT;"
            );
            _replay = await Replay.StartAsync(ShapedSql());
            _outcome = await _replay.RestoreAsync();
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            await ExecuteOnServerAsync(
                $"ALTER ROLE {PostgresqlIdentifiers.Quote(DmsTargetValidator.EnqueueOwnerRoleName)} NOINHERIT;"
            );
            if (_replay is not null)
            {
                await _replay.DisposeAsync();
            }
        }

        [Test]
        public void It_refuses_before_the_replay_with_the_role_state_operation() =>
            _outcome
                .Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Failed>()
                .Which.Failure.Should()
                .BeEquivalentTo(
                    new
                    {
                        Class = DmsCommandFailureClass.Rejected,
                        Diagnostic = new
                        {
                            ProviderErrorCode = PostgresqlStagingQueries.RoleStateUnsafeSqlState,
                            Operation = DmsEngineOperations.RoleStateUnsafe,
                        },
                    }
                );
    }

    /// <summary>
    /// D-16 on a fresh cluster, two operations at once: the role is cluster-wide but target locks are per name,
    /// so both can find it missing. The first runs the D-16 block in an open transaction, so its role is not yet
    /// visible; the second runs it through the engine's query, finds the role missing, and waits in
    /// <c>CREATE ROLE</c> on the first's uncommitted row, which the server reports before the first commits.
    /// A locked-down role committed by the first is then accepted, and one the first leaves unsafe is still
    /// refused as <c>RoleStateUnsafe</c>. The lane's existing role is renamed aside for the fixture and restored.
    /// </summary>
    [TestFixture]
    public class Given_two_operations_initialize_the_enqueue_owner_role_at_once
    {
        private static readonly string _role = PostgresqlIdentifiers.Quote(
            DmsTargetValidator.EnqueueOwnerRoleName
        );

        private string? _parked;
        private DmsCommandOutcome<DmsInstanceIdentity>? _lockedDownOutcome;
        private DmsCommandOutcome<DmsInstanceIdentity>? _unsafeOutcome;

        private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
        {
            await using NpgsqlCommand command = new(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// The first session creates the role in an open transaction and runs <paramref name="beforeCommit"/>
        /// there; once the second session waits on it, the first commits, and the second's outcome is returned.
        /// The role is dropped afterwards; it owns nothing.
        /// </summary>
        private static async Task<DmsCommandOutcome<DmsInstanceIdentity>?> RaceAsync(string? beforeCommit)
        {
            try
            {
                await using NpgsqlConnection first = await AdminConnection().OpenMaintenanceAsync(default);
                await using NpgsqlConnection second = await AdminConnection().OpenMaintenanceAsync(default);
                await using NpgsqlTransaction transaction = await first.BeginTransactionAsync();
                await ExecuteAsync(first, PostgresqlStagingQueries.EnsureEnqueueOwner);
                if (beforeCommit is not null)
                {
                    await ExecuteAsync(first, beforeCommit);
                }

                Task<DmsCommandOutcome<DmsInstanceIdentity>?> racing =
                    PostgresqlStagingQueries.EnsureEnqueueOwnerAsync(
                        second,
                        await OwnSessionAsync(second),
                        CancellationToken.None
                    );
                await WaitUntilBlockedByAsync(first.ProcessID, second.ProcessID, racing);
                await transaction.CommitAsync();
                return await racing.WaitAsync(_bound);
            }
            finally
            {
                await ExecuteOnServerAsync($"DROP ROLE IF EXISTS {_role};");
            }
        }

        /// <summary>Returns once <paramref name="waiting"/> waits on <paramref name="blocking"/>; the bound only turns a call that never waits into a failure instead of a hang.</summary>
        private static async Task WaitUntilBlockedByAsync(int blocking, int waiting, Task call)
        {
            using CancellationTokenSource bound = new(_bound);
            while (
                !await ScalarOnServerAsync<bool>(
                    "SELECT @blocking = ANY(pg_catalog.pg_blocking_pids(@waiting));",
                    ("blocking", blocking),
                    ("waiting", waiting)
                )
            )
            {
                call.IsCompleted.Should().BeFalse("the second session must wait on the uncommitted role");
                await Task.Delay(TimeSpan.FromMilliseconds(20), bound.Token);
            }
        }

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            await RequireSuperuserOrSkipAsync();
            if (
                await ScalarOnServerAsync<bool>(
                    "SELECT pg_catalog.to_regrole(@role) IS NOT NULL;",
                    ("role", DmsTargetValidator.EnqueueOwnerRoleName)
                )
            )
            {
                _parked =
                    "dms1438_parked_owner_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
                await ExecuteOnServerAsync(
                    $"ALTER ROLE {_role} RENAME TO {PostgresqlIdentifiers.Quote(_parked)};"
                );
            }

            _lockedDownOutcome = await RaceAsync(beforeCommit: null);
            _unsafeOutcome = await RaceAsync($"ALTER ROLE {_role} INHERIT;");
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_parked is not null)
            {
                await ExecuteOnServerAsync($"DROP ROLE IF EXISTS {_role};");
                await ExecuteOnServerAsync(
                    $"ALTER ROLE {PostgresqlIdentifiers.Quote(_parked)} RENAME TO {_role};"
                );
            }
        }

        [Test]
        public void It_accepts_the_locked_down_role_the_other_operation_created() =>
            _lockedDownOutcome.Should().BeNull();

        [Test]
        public void It_still_refuses_a_role_the_other_operation_left_unsafe() =>
            _unsafeOutcome
                .Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Failed>()
                .Which.Failure.Should()
                .BeEquivalentTo(
                    new
                    {
                        Class = DmsCommandFailureClass.Rejected,
                        Diagnostic = new
                        {
                            ProviderErrorCode = PostgresqlStagingQueries.RoleStateUnsafeSqlState,
                            Operation = DmsEngineOperations.RoleStateUnsafe,
                        },
                    }
                );
    }
}
