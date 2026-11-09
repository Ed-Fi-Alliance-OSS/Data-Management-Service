// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Postgresql;

/// <summary>
/// The engine's replay resolution when it cannot finish (D-7b, Step 3.5 review round 2): a session lookup
/// that fails, here against a server that cannot be reached, proves nothing, so the outcome stays unknown,
/// names no session, is not quiescent, and is logged with only its type chain; it never escapes as an
/// exception the orchestrator would classify while a replay session might still live. A replay that fails
/// before any process starts is the opposite case: nothing ran, so the outcome is definite and cleanup may
/// drop the staging database.
/// </summary>
public class ReplayResolutionFailureTests
{
    private const string UnreachableAdmin =
        "Host=127.0.0.1;Port=1;Username=dms;Password=SENTINEL-PASSWORD-91c2;Timeout=1";

    /// <summary>An operation's private directory with an artifact in it, as the catalog stages one; deleted on disposal.</summary>
    private sealed class StagedReplay : IDisposable
    {
        private readonly string _root;

        public StagedReplay()
        {
            Operation = DmsOperationId.New();
            _root = Path.Combine(Path.GetTempPath(), "dms1438-not-started-" + Operation.Value);
            Directory.CreateDirectory(_root);
            OperationDirectory = PrivateOperationDirectory.Create(_root, Operation);
            string path = OperationDirectory.PathOf("artifact.sql");
            File.WriteAllText(path, "SELECT 1;\n");
            Artifact = new StagedArtifact(path, new string('0', 64), DmsArtifactKind.PostgresqlSqlDump);
        }

        public DmsOperationId Operation { get; }

        public PrivateOperationDirectory OperationDirectory { get; }

        public StagedArtifact Artifact { get; }

        public void Dispose()
        {
            OperationDirectory.TryDelete();
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestFixture]
    public class Given_a_resolution_whose_session_lookup_fails
    {
        private readonly CapturingLogger<PostgresqlDmsTargetEngine> _logger = new();
        private DmsCommandOutcome<DmsInstanceIdentity> _outcome = null!;
        private DmsOperationId _operation;

        [OneTimeSetUp]
        public async Task Setup()
        {
            _operation = DmsOperationId.New();
            PostgresqlDmsTargetEngine engine = new(
                new PostgresqlAdminConnection(
                    "Host=127.0.0.1;Port=1;Username=dms;Password=SENTINEL-PASSWORD-91c2;Timeout=1",
                    TimeSpan.FromSeconds(30)
                ),
                new PostgresqlDmsEngineSettings(
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromMinutes(5),
                    Path.Combine(AppContext.BaseDirectory, "psql-not-available")
                ),
                TimeProvider.System,
                _logger
            );
            _outcome = await engine.ResolveReplayAsync(
                _operation.StagingDatabaseName,
                _operation,
                new PsqlRunResult.Killed(4321, PsqlKillReason.Cancelled)
            );
        }

        [Test]
        public void It_leaves_the_outcome_unknown_and_unresolvable() =>
            _outcome
                .Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Unknown>()
                .Which.Should()
                .BeEquivalentTo(new { Session = (DmsServerSessionId?)null, Quiescent = false });

        [Test]
        public void It_logs_the_failure_with_only_the_operation_and_type_chain()
        {
            LogEntry entry = _logger
                .Entries.Should()
                .ContainSingle(entry => entry.EventId == PostgresqlDmsTargetEngine.ReplayResolutionFailed)
                .Subject;
            entry.Field("Operation").Should().Be(_operation.Value);
            _logger
                .Entries.SelectMany(logged =>
                    logged
                        .Fields.Select(field => field.Value?.ToString() ?? string.Empty)
                        .Prepend(logged.Message)
                )
                .Should()
                .NotContain(text => text.Contains("SENTINEL-PASSWORD-91c2") || text.Contains("127.0.0.1"));
        }
    }

    /// <summary>
    /// The D-16 role check runs before any process and cannot reach the server: the replay never started, so the
    /// engine returns a definite <c>RestoreNotStarted</c> with only the type chain, or <c>RestoreCancelled</c> when the
    /// token was cancelled, instead of throwing past the orchestrator's withheld drop.
    /// </summary>
    [TestFixture]
    public class Given_the_role_check_before_the_replay_cannot_reach_the_server
    {
        private static PostgresqlDmsEngineSettings EngineSettings() =>
            new(
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMinutes(5),
                Path.Combine(AppContext.BaseDirectory, "psql-not-available")
            );

        /// <summary>The diagnostic of a definite <c>Rejected</c> failure; anything else fails the test.</summary>
        private static DmsProvisioningDiagnostic DiagnosticOf(DmsCommandOutcome<DmsInstanceIdentity> outcome)
        {
            DmsCommandFailure failure = outcome
                .Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Failed>()
                .Subject.Failure;
            failure.Class.Should().Be(DmsCommandFailureClass.Rejected);
            return failure.Diagnostic;
        }

        private DmsCommandOutcome<DmsInstanceIdentity> _unreachable = null!;
        private DmsCommandOutcome<DmsInstanceIdentity> _cancelled = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            PostgresqlDmsTargetEngine engine = new(
                new PostgresqlAdminConnection(UnreachableAdmin, TimeSpan.FromSeconds(30)),
                EngineSettings(),
                TimeProvider.System,
                NullLogger.Instance
            );
            using (StagedReplay replay = new())
            {
                _unreachable = await engine.RestoreIntoStagingAsync(
                    replay.Operation.StagingDatabaseName,
                    ProvisioningTestValues.CreateRequest().TargetName,
                    replay.Artifact,
                    replay.Operation,
                    default
                );
            }

            using (StagedReplay replay = new())
            {
                using CancellationTokenSource cancellation = new();
                await cancellation.CancelAsync();
                _cancelled = await engine.RestoreIntoStagingAsync(
                    replay.Operation.StagingDatabaseName,
                    ProvisioningTestValues.CreateRequest().TargetName,
                    replay.Artifact,
                    replay.Operation,
                    cancellation.Token
                );
            }
        }

        [Test]
        public void It_returns_a_definite_not_started_failure_with_only_the_type_chain()
        {
            DmsProvisioningDiagnostic diagnostic = DiagnosticOf(_unreachable);
            diagnostic.Operation.Should().Be(DmsEngineOperations.RestoreNotStarted);
            diagnostic.ExceptionTypeChain.Should().StartWith("Npgsql.");
            diagnostic.ExceptionTypeChain.Should().NotContain("SENTINEL-PASSWORD-91c2");
        }

        [Test]
        public void It_names_a_cancelled_token_as_a_cancellation() =>
            DiagnosticOf(_cancelled).Operation.Should().Be(DmsEngineOperations.RestoreCancelled);
    }

    /// <summary>
    /// The runner cannot write the password file (a password with a line break): no process is started, so the run
    /// returns <see cref="PsqlRunResult.NotStarted"/> with the cause instead of throwing, and no password file is left.
    /// </summary>
    [TestFixture]
    public class Given_the_runner_cannot_write_the_password_file
    {
        private PsqlRunResult _result = null!;
        private string[] _entriesAfterTheRun = [];

        [OneTimeSetUp]
        public async Task Setup()
        {
            PsqlRestoreRunner runner = new(
                Path.Combine(AppContext.BaseDirectory, "psql-not-available"),
                TimeSpan.FromMinutes(1),
                TimeProvider.System,
                NullLogger.Instance
            );
            using StagedReplay replay = new();
            _result = await runner.RunAsync(
                new PostgresqlAdminConnection(
                    new NpgsqlConnectionStringBuilder
                    {
                        Host = "db",
                        Username = "dms",
                        Password = "line\nbreak",
                    }.ConnectionString,
                    TimeSpan.FromSeconds(30)
                ),
                replay.Operation.StagingDatabaseName,
                replay.Artifact,
                replay.OperationDirectory,
                replay.Operation,
                default
            );
            _entriesAfterTheRun =
            [
                .. Directory
                    .GetFileSystemEntries(replay.OperationDirectory.Path)
                    .Select(entry => Path.GetFileName(entry)),
            ];
        }

        [Test]
        public void It_reports_the_run_as_not_started_with_its_cause() =>
            _result
                .Should()
                .BeOfType<PsqlRunResult.NotStarted>()
                .Which.Cause.Should()
                .BeOfType<ArgumentException>();

        [Test]
        public void It_leaves_no_password_file() => _entriesAfterTheRun.Should().Equal("artifact.sql");
    }
}
