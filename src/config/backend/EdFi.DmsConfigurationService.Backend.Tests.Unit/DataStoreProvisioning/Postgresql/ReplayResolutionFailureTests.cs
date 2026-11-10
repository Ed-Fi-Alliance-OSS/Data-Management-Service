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
        private int _restoreStarts;

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
                    default,
                    () => _restoreStarts++
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
                    cancellation.Token,
                    () => _restoreStarts++
                );
            }
        }

        [Test]
        public void It_reports_no_restore_start() => _restoreStarts.Should().Be(0);

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
    /// Review finding 4: the operation directory is gone when the replay is about to start (a temporary-file
    /// reaper, an operator). Nothing was launched and nothing connected, so the engine returns the definite
    /// <c>RestoreNotStarted</c>, or <c>RestoreCancelled</c> for a cancelled token, which lets cleanup drop the
    /// staging database, instead of throwing past the orchestrator into an unresolved outcome.
    /// </summary>
    [TestFixture]
    public class Given_the_operation_directory_is_gone_before_the_replay
    {
        private DmsCommandOutcome<DmsInstanceIdentity> _missing = null!;
        private DmsCommandOutcome<DmsInstanceIdentity> _cancelled = null!;

        private static DmsProvisioningDiagnostic RejectedDiagnosticOf(
            DmsCommandOutcome<DmsInstanceIdentity> outcome
        )
        {
            DmsCommandFailure failure = outcome
                .Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Failed>()
                .Subject.Failure;
            failure.Class.Should().Be(DmsCommandFailureClass.Rejected);
            return failure.Diagnostic;
        }

        private static async Task<DmsCommandOutcome<DmsInstanceIdentity>> RestoreWithoutDirectoryAsync(
            CancellationToken cancellationToken
        )
        {
            PostgresqlDmsTargetEngine engine = new(
                new PostgresqlAdminConnection(UnreachableAdmin, TimeSpan.FromSeconds(30)),
                new PostgresqlDmsEngineSettings(
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromMinutes(5),
                    Path.Combine(AppContext.BaseDirectory, "psql-not-available")
                ),
                TimeProvider.System,
                NullLogger.Instance
            );
            using StagedReplay replay = new();
            Directory.Delete(replay.OperationDirectory.Path, recursive: true);
            return await engine.RestoreIntoStagingAsync(
                replay.Operation.StagingDatabaseName,
                ProvisioningTestValues.CreateRequest().TargetName,
                replay.Artifact,
                replay.Operation,
                cancellationToken
            );
        }

        [OneTimeSetUp]
        public async Task Setup()
        {
            _missing = await RestoreWithoutDirectoryAsync(CancellationToken.None);
            using CancellationTokenSource cancellation = new();
            await cancellation.CancelAsync();
            _cancelled = await RestoreWithoutDirectoryAsync(cancellation.Token);
        }

        [Test]
        public void It_returns_a_definite_not_started_failure_naming_the_missing_directory()
        {
            DmsProvisioningDiagnostic diagnostic = RejectedDiagnosticOf(_missing);
            diagnostic.Operation.Should().Be(DmsEngineOperations.RestoreNotStarted);
            diagnostic.ExceptionTypeChain.Should().Be("System.IO.DirectoryNotFoundException");
        }

        [Test]
        public void It_names_a_cancelled_token_as_a_cancellation() =>
            RejectedDiagnosticOf(_cancelled).Operation.Should().Be(DmsEngineOperations.RestoreCancelled);
    }

    /// <summary>
    /// Review finding 5: a <c>psql</c> process the system could not create. A temporary shortage of processes,
    /// memory, or file descriptors can clear with no change, so it is the definite and retryable
    /// <c>RestoreNotStarted</c> (<c>RestoreCancelled</c> for a cancelled token); a missing or non-executable file
    /// or a refused permission keeps the deliberate <c>RestoreIntoStaging.Start</c>, which needs the operator.
    /// The codes are the ones .NET reports for process creation on each platform.
    /// </summary>
    [TestFixture]
    [Platform("Win,Linux")]
    public class Given_a_psql_process_the_system_could_not_create
    {
        // Windows: ERROR_NO_SYSTEM_RESOURCES, ERROR_NOT_ENOUGH_MEMORY. Linux: EAGAIN (process limit), ENOMEM.
        private static readonly int[] _shortages = OperatingSystem.IsWindows() ? [1450, 8] : [11, 12];

        // Windows: ERROR_FILE_NOT_FOUND, ERROR_ACCESS_DENIED. Linux: ENOENT, EACCES.
        private static readonly int[] _needsOperator = OperatingSystem.IsWindows() ? [2, 5] : [2, 13];

        private static DmsProvisioningDiagnostic RejectedDiagnosticOf(int code, CancellationToken token)
        {
            DmsCommandFailure failure = PostgresqlDmsTargetEngine
                .StartFailed(new System.ComponentModel.Win32Exception(code), token)
                .Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Failed>()
                .Subject.Failure;
            failure.Class.Should().Be(DmsCommandFailureClass.Rejected);
            return failure.Diagnostic;
        }

        [Test]
        public void It_reports_a_resource_shortage_as_a_retryable_not_started_replay() =>
            _shortages
                .Select(code => RejectedDiagnosticOf(code, CancellationToken.None))
                .Should()
                .AllSatisfy(diagnostic =>
                {
                    diagnostic.Operation.Should().Be(DmsEngineOperations.RestoreNotStarted);
                    diagnostic.ExceptionTypeChain.Should().Be("System.ComponentModel.Win32Exception");
                });

        [Test]
        public void It_names_a_resource_shortage_under_a_cancelled_token_as_a_cancellation()
        {
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();
            RejectedDiagnosticOf(_shortages[0], cancellation.Token)
                .Operation.Should()
                .Be(DmsEngineOperations.RestoreCancelled);
        }

        [Test]
        public void It_keeps_a_missing_or_refused_executable_for_the_operator() =>
            _needsOperator
                .Select(code => RejectedDiagnosticOf(code, CancellationToken.None).Operation)
                .Should()
                .AllBe(PostgresqlDmsTargetEngine.RestoreStartStep);
    }

    /// <summary>
    /// AC 11: the server refuses a session the replay needs before <c>psql</c> starts, such as a <c>pg_hba.conf</c>
    /// rule that admits the maintenance database but not the staging database (class <c>28</c>), or a refused
    /// privilege (<c>42501</c>). No retry outlives that without an operator, so the refusal keeps its SQLSTATE as
    /// <c>PrivilegeMissing</c> under <c>RestoreIntoStaging.AccessRefused</c>; a server shortage or a lost connection
    /// stays the retryable <c>RestoreNotStarted</c>, and a cancelled token is named as a cancellation.
    /// </summary>
    [TestFixture]
    public class Given_the_server_refuses_a_session_before_the_replay
    {
        private static PostgresException ServerError(string sqlState) =>
            new("SENTINEL-MESSAGE-4be1", "FATAL", "FATAL", sqlState);

        private static DmsCommandFailure FailureOf(Exception exception, CancellationToken token) =>
            PostgresqlDmsTargetEngine
                .PreReplayFailed(exception, token)
                .Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Failed>()
                .Subject.Failure;

        [TestCase("28000")]
        [TestCase("28P01")]
        [TestCase("42501")]
        public void It_reports_an_access_refusal_as_a_missing_privilege_with_its_sqlstate(string sqlState) =>
            FailureOf(ServerError(sqlState), CancellationToken.None)
                .Should()
                .Be(
                    new DmsCommandFailure(
                        DmsCommandFailureClass.PrivilegeMissing,
                        new DmsProvisioningDiagnostic(
                            "Npgsql.PostgresException",
                            sqlState,
                            PostgresqlDmsTargetEngine.RestoreAccessRefusedStep
                        )
                    )
                );

        [Test]
        public void It_keeps_a_server_shortage_or_a_lost_connection_retryable_as_not_started() =>
            new Exception[] { ServerError("53300"), new NpgsqlException("reset", new IOException()) }
                .Select(exception => FailureOf(exception, CancellationToken.None))
                .Should()
                .AllSatisfy(failure =>
                    (failure.Class, failure.Diagnostic.Operation)
                        .Should()
                        .Be((DmsCommandFailureClass.Rejected, DmsEngineOperations.RestoreNotStarted))
                );

        [Test]
        public void It_names_a_refusal_under_a_cancelled_token_as_a_cancellation()
        {
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();
            FailureOf(ServerError("28000"), cancellation.Token)
                .Diagnostic.Operation.Should()
                .Be(DmsEngineOperations.RestoreCancelled);
        }
    }

    /// <summary>
    /// The runner cannot prepare the password file (a password with a line break, rotated in after the admin
    /// connection was built and refused by it): no process is started, so the run returns
    /// <see cref="PsqlRunResult.NotStarted"/> with the cause instead of throwing, and no password file is left.
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
                    () =>
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
                .BeOfType<InvalidOperationException>();

        [Test]
        public void It_leaves_no_password_file() => _entriesAfterTheRun.Should().Equal("artifact.sql");
    }
}
