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
using Npgsql;
using static EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning.DmsProvisioningTestServer;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning;

/// <summary>
/// The engine's replay into a staging database (6.3 step 5, D-10, Step 3.5): the shaped artifact commits
/// and the staging instance comes back; a rejected statement rolls the single transaction back with its
/// SQLSTATE; a replay the restore bound ends, and one the caller cancels, are terminated with proof and
/// found rolled back, with nothing left of the replay session; and the D-16 role check refuses a role
/// that is not locked down before any process starts. The replay fixtures need a real <c>psql</c>
/// (<see cref="PsqlEnvironmentVariableName"/>) and skip without one.
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

        private static DmsTargetDatabaseName TargetName(string purpose) =>
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
        ) => Engine.RestoreIntoStagingAsync(Stage, _artifact.Artifact, Operation, cancellationToken);

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
}
