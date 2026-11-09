// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Postgresql;

/// <summary>
/// One replay's artifact, operation directory, administrative connection (two endpoints, a sentinel
/// password), and capturing logger, so each fixture asserts on the same secrets.
/// </summary>
internal sealed class ReplayWorkspace : IDisposable
{
    public const string Password = "SENTINEL-PASSWORD-7f3a";

    public const string ConnectionString =
        "Host=primary.example.test:5433,standby.example.test;Port=5432;Username=dms_admin;Password="
        + Password
        + ";Timeout=20";

    public ReplayWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "dms-1438-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Operation = DmsOperationId.New();
        OperationDirectory = PrivateOperationDirectory.Create(Root, Operation);
        string artifactPath = OperationDirectory.PathOf("artifact.sql");
        byte[] artifact = "SELECT 1;\n"u8.ToArray();
        File.WriteAllBytes(artifactPath, artifact);
        Artifact = new StagedArtifact(
            artifactPath,
            Convert.ToHexStringLower(SHA256.HashData(artifact)),
            DmsArtifactKind.PostgresqlSqlDump
        );
    }

    public string Root { get; }

    public DmsOperationId Operation { get; }

    public PrivateOperationDirectory OperationDirectory { get; }

    public StagedArtifact Artifact { get; }

    public PostgresqlAdminConnection AdminConnection { get; } =
        new(ConnectionString, TimeSpan.FromSeconds(30));

    public CapturingLogger<PsqlRestoreRunner> Logger { get; } = new();

    public DmsDatabaseName Database => Operation.StagingDatabaseName;

    public string PasswordFilePath => OperationDirectory.PathOf(PgPassFile.FileName);

    public PsqlRestoreRunner Runner(
        string psqlPath,
        TimeSpan? restoreTimeout = null,
        TimeProvider? timeProvider = null,
        TimeSpan? terminationBound = null,
        PsqlProcess.KillProcess? kill = null
    ) =>
        new(
            psqlPath,
            restoreTimeout ?? TimeSpan.FromMinutes(5),
            terminationBound ?? PsqlProcess.DefaultTerminationBound,
            timeProvider ?? TimeProvider.System,
            Logger,
            kill
        );

    public Task<PsqlRunResult> RunAsync(
        PsqlRestoreRunner runner,
        CancellationToken cancellationToken = default
    ) =>
        runner.RunAsync(
            AdminConnection,
            Database,
            Artifact,
            OperationDirectory,
            Operation,
            cancellationToken
        );

    /// <summary>Every string the logger received: messages and structured field values, at every level.</summary>
    public IReadOnlyList<string> LoggedText() =>
        [
            .. Logger.Entries.SelectMany(entry =>
                entry.Fields.Select(field => field.Value?.ToString() ?? string.Empty).Prepend(entry.Message)
            ),
        ];

    public static bool IsRunning(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        OperationDirectory.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // The temp path is reclaimed later.
        }
    }
}

/// <summary>
/// The <c>psql</c> runner of D-10/D-10b: the start info carries the fixed arguments and the connection
/// arguments (parallel host and port lists) but never the password or the connection string; the
/// environment is exactly the allowlist, at build time and as the started process sees it; stderr with a
/// sentinel password and connection string reaches no log entry at any level, no result, and no exception
/// while its SQLSTATE is still extracted through the artifact's real path; cancellation and the restore
/// timeout each kill the process and return once it is confirmed gone; a process that exits while a child
/// keeps its pipes is reported unconfirmed within the termination bound; and the password file is deleted
/// however the run ends.
/// </summary>
public class PsqlRestoreRunnerTests
{
    private static readonly TimeSpan _bound = TimeSpan.FromSeconds(60);

    [TestFixture]
    public class Given_the_start_info_of_a_replay
    {
        private ReplayWorkspace _workspace = null!;
        private ProcessStartInfo _startInfo = null!;
        private string _psqlPath = null!;

        [SetUp]
        public void Setup()
        {
            _workspace = new ReplayWorkspace();
            _psqlPath = Path.Combine(_workspace.Root, "psql");
            _startInfo = PsqlRestoreRunner.CreateStartInfo(
                _psqlPath,
                _workspace.AdminConnection.ToLibpqSettings(),
                _workspace.Database.Value,
                _workspace.Artifact.FilePath,
                _workspace.PasswordFilePath,
                _workspace.OperationDirectory.Path,
                _workspace.Operation
            );
        }

        [TearDown]
        public void TearDown() => _workspace.Dispose();

        [Test]
        public void It_starts_the_configured_executable_without_a_shell_or_a_window()
        {
            _startInfo.FileName.Should().Be(_psqlPath);
            _startInfo.UseShellExecute.Should().BeFalse();
            _startInfo.CreateNoWindow.Should().BeTrue();
            _startInfo.WorkingDirectory.Should().Be(_workspace.OperationDirectory.Path);
        }

        [Test]
        public void It_redirects_every_standard_stream()
        {
            _startInfo.RedirectStandardInput.Should().BeTrue();
            _startInfo.RedirectStandardOutput.Should().BeTrue();
            _startInfo.RedirectStandardError.Should().BeTrue();
        }

        [Test]
        public void It_passes_the_d10_arguments_then_the_file_then_the_endpoint_lists() =>
            _startInfo
                .ArgumentList.Should()
                .Equal(
                    "--no-password",
                    "--no-psqlrc",
                    "--set",
                    "ON_ERROR_STOP=1",
                    "--set",
                    "VERBOSITY=verbose",
                    "--single-transaction",
                    "--quiet",
                    "--file",
                    _workspace.Artifact.FilePath,
                    "--host",
                    "primary.example.test,standby.example.test",
                    "--port",
                    "5433,5432",
                    "--username",
                    "dms_admin",
                    "--dbname",
                    _workspace.Database.Value
                );

        [Test]
        public void It_never_puts_the_password_or_the_connection_string_in_an_argument()
        {
            _startInfo
                .ArgumentList.Should()
                .NotContain(argument => argument.Contains(ReplayWorkspace.Password));
            _startInfo.ArgumentList.Should().NotContain(argument => argument.Contains("Password="));
            _startInfo.Arguments.Should().BeEmpty();
        }

        [Test]
        public void It_sets_exactly_the_allowlisted_environment_without_a_root_certificate() =>
            _startInfo
                .Environment.Keys.Should()
                .BeEquivalentTo(LibpqEnvironment.AllowedVariables.Where(name => name != "PGSSLROOTCERT"));

        [Test]
        public void It_names_the_password_file_and_the_operation_in_the_environment()
        {
            _startInfo.Environment["PGPASSFILE"].Should().Be(_workspace.PasswordFilePath);
            _startInfo.Environment["PGAPPNAME"].Should().Be("dms-provision:" + _workspace.Operation.Value);
            _startInfo.Environment["PGCONNECT_TIMEOUT"].Should().Be("20");
        }

        [Test]
        public void It_never_puts_the_password_in_an_environment_value() =>
            _startInfo
                .Environment.Values.Should()
                .NotContain(value => value!.Contains(ReplayWorkspace.Password));
    }

    [TestFixture]
    public class Given_a_replay_that_stops_on_an_error_and_leaks_secrets_to_stderr
    {
        private ReplayWorkspace _workspace = null!;
        private FakePsql _fake = null!;
        private PsqlRunResult _result = null!;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            _workspace = new ReplayWorkspace();
            _fake = FakePsql.WritingStandardError(
                [
                    "psql:"
                        + _workspace.Artifact.FilePath
                        + ":3: ERROR:  42P01: relation \"edfi.student\" does not exist",
                    "DETAIL:  the leaked password is " + ReplayWorkspace.Password,
                    "HINT:  the leaked connection string is " + ReplayWorkspace.ConnectionString,
                    "LOCATION:  parserOpenTable, parse_relation.c:1449",
                    "psql:/tmp/dms-provision-0123456789ab/artifact.sql:9: ERROR:  22P02: a later error that is not kept",
                ],
                exitCode: 3
            );
            _exception = await Record.ExceptionAsync(async () =>
                _result = await _workspace.RunAsync(_workspace.Runner(_fake.ExecutablePath)).WaitAsync(_bound)
            );
        }

        [TearDown]
        public async Task TearDown()
        {
            await _fake.DisposeAsync();
            _workspace.Dispose();
        }

        [Test]
        public void It_does_not_throw() => _exception.Should().BeNull();

        [Test]
        public void It_reports_the_exit_code_and_the_first_sqlstate_through_the_artifact_path()
        {
            PsqlRunResult.Exited exited = _result.Should().BeOfType<PsqlRunResult.Exited>().Subject;
            exited.ExitCode.Should().Be(3);
            exited.SqlState.Should().Be("42P01");
        }

        [Test]
        public void It_logs_the_start_and_the_exit_with_value_free_fields()
        {
            _workspace
                .Logger.Entries.Select(entry => entry.EventId)
                .Should()
                .Equal(PsqlRestoreRunner.ReplayStarted, PsqlRestoreRunner.ReplayExited);
            LogEntry exited = _workspace.Logger.Entries.Last();
            exited.Field("Operation").Should().Be(_workspace.Operation.Value);
            exited.Field("ExitCode").Should().Be(3);
            exited.Field("SqlState").Should().Be("42P01");
        }

        [Test]
        public void It_never_logs_the_password_or_the_connection_string_at_any_level()
        {
            IReadOnlyList<string> logged = _workspace.LoggedText();
            logged.Should().NotBeEmpty();
            logged.Should().NotContain(text => text.Contains(ReplayWorkspace.Password));
            logged.Should().NotContain(text => text.Contains(ReplayWorkspace.ConnectionString));
            logged.Should().NotContain(text => text.Contains("relation"));
        }

        [Test]
        public void It_never_carries_stderr_in_the_result()
        {
            _result.ToString().Should().NotContain(ReplayWorkspace.Password);
            _result.ToString().Should().NotContain("relation");
        }

        [Test]
        public void It_deletes_the_password_file_and_keeps_the_operation_directory()
        {
            File.Exists(_workspace.PasswordFilePath).Should().BeFalse();
            Directory.Exists(_workspace.OperationDirectory.Path).Should().BeTrue();
            File.Exists(_workspace.Artifact.FilePath).Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_a_replay_that_exits_cleanly
    {
        private ReplayWorkspace _workspace = null!;
        private FakePsql _fake = null!;
        private PsqlRunResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _workspace = new ReplayWorkspace();
            _fake = FakePsql.WritingStandardError([], exitCode: 0);
            _result = await _workspace.RunAsync(_workspace.Runner(_fake.ExecutablePath)).WaitAsync(_bound);
        }

        [TearDown]
        public async Task TearDown()
        {
            await _fake.DisposeAsync();
            _workspace.Dispose();
        }

        [Test]
        public void It_reports_exit_code_zero_and_no_sqlstate()
        {
            PsqlRunResult.Exited exited = _result.Should().BeOfType<PsqlRunResult.Exited>().Subject;
            exited.ExitCode.Should().Be(0);
            exited.SqlState.Should().BeNull();
        }

        [Test]
        public void It_logs_no_sqlstate_as_none() =>
            _workspace.Logger.Entries.Last().Field("SqlState").Should().Be("none");
    }

    [TestFixture]
    public class Given_a_replay_cancelled_while_the_process_runs
    {
        private ReplayWorkspace _workspace = null!;
        private FakePsql _fake = null!;
        private string _signal = null!;
        private PsqlRunResult _result = null!;
        private Exception? _exception;
        private IReadOnlyDictionary<string, string> _environmentSeen = null!;
        private bool _passwordFileExistedWhileRunning;

        [SetUp]
        public async Task Setup()
        {
            _workspace = new ReplayWorkspace();
            _signal = Path.Combine(_workspace.Root, "started");
            _fake = FakePsql.Blocking(_signal);
            using CancellationTokenSource cancellation = new();
            Task<PsqlRunResult> run = _workspace.RunAsync(
                _workspace.Runner(_fake.ExecutablePath),
                cancellation.Token
            );

            await FakePsql.WaitForFileAsync(_signal, _bound);
            _passwordFileExistedWhileRunning = File.Exists(_workspace.PasswordFilePath);
            _environmentSeen = FakePsql.ParseEnvironmentDump(await FakePsql.ReadSignalAsync(_signal, _bound));
            run.IsCompleted.Should().BeFalse("the fake blocks until it is killed");

            await cancellation.CancelAsync();
            _exception = await Record.ExceptionAsync(async () => _result = await run.WaitAsync(_bound));
        }

        [TearDown]
        public async Task TearDown()
        {
            await _fake.DisposeAsync();
            _workspace.Dispose();
        }

        [Test]
        public void It_returns_a_confirmed_kill_instead_of_throwing()
        {
            _exception.Should().BeNull();
            _result
                .Should()
                .BeOfType<PsqlRunResult.Killed>()
                .Which.Reason.Should()
                .Be(PsqlKillReason.Cancelled);
        }

        [Test]
        public void It_returns_only_after_the_process_is_gone() =>
            ReplayWorkspace.IsRunning(((PsqlRunResult.Killed)_result).ProcessId).Should().BeFalse();

        [Test]
        public void It_gave_the_process_exactly_the_allowlisted_environment()
        {
            _environmentSeen
                .Keys.Should()
                .OnlyContain(name =>
                    LibpqEnvironment.AllowedVariables.Contains(name, StringComparer.OrdinalIgnoreCase)
                    || FakePsql.ShellInjectedVariables.Contains(name, StringComparer.OrdinalIgnoreCase)
                );
            _environmentSeen
                .Keys.Should()
                .Contain(LibpqEnvironment.AllowedVariables.Where(name => name != "PGSSLROOTCERT"));
        }

        [Test]
        public void It_gave_the_process_the_operation_name_and_the_password_file()
        {
            _environmentSeen["PGAPPNAME"].Should().Be("dms-provision:" + _workspace.Operation.Value);
            _environmentSeen["PGPASSFILE"].Should().Be(_workspace.PasswordFilePath);
            _environmentSeen["PGSSLMODE"].Should().Be("prefer");
            _environmentSeen["PGCONNECT_TIMEOUT"].Should().Be("20");
            _environmentSeen["PGCLIENTENCODING"].Should().Be("UTF8");
            _environmentSeen["LC_ALL"].Should().Be("C");
            _passwordFileExistedWhileRunning.Should().BeTrue();
        }

        [Test]
        public void It_gave_the_process_no_value_carrying_the_password() =>
            _environmentSeen.Values.Should().NotContain(value => value.Contains(ReplayWorkspace.Password));

        [Test]
        public void It_deletes_the_password_file_after_the_kill() =>
            File.Exists(_workspace.PasswordFilePath).Should().BeFalse();

        [Test]
        public void It_logs_the_kill_with_its_reason()
        {
            LogEntry killed = _workspace.Logger.Entries.Last();
            killed.EventId.Should().Be(PsqlRestoreRunner.ReplayKilled);
            killed.Field("Reason").Should().Be(PsqlKillReason.Cancelled);
            _workspace.LoggedText().Should().NotContain(text => text.Contains(ReplayWorkspace.Password));
        }
    }

    [TestFixture]
    public class Given_a_password_file_the_runner_cannot_delete
    {
        private ReplayWorkspace _workspace = null!;
        private FakePsql _fake = null!;
        private CredentialTrap? _trap;
        private PsqlRunResult _result = null!;
        private bool _passwordFileRemained;

        [SetUp]
        public async Task Setup()
        {
            _workspace = new ReplayWorkspace();
            string signal = Path.Combine(_workspace.Root, "started");
            _fake = FakePsql.Blocking(signal);
            using CancellationTokenSource cancellation = new();
            Task<PsqlRunResult> run = _workspace.RunAsync(
                _workspace.Runner(_fake.ExecutablePath),
                cancellation.Token
            );
            await FakePsql.WaitForFileAsync(signal, _bound);
            _trap = new CredentialTrap(_workspace.OperationDirectory.Path);

            await cancellation.CancelAsync();
            _result = await run.WaitAsync(_bound);
            _passwordFileRemained = File.Exists(_workspace.PasswordFilePath);
        }

        [TearDown]
        public async Task TearDown()
        {
            _trap?.Dispose();
            await _fake.DisposeAsync();
            _workspace.Dispose();
        }

        [Test]
        public void It_still_returns_the_kill() => _result.Should().BeOfType<PsqlRunResult.Killed>();

        [Test]
        public void It_could_not_delete_the_file() => _passwordFileRemained.Should().BeTrue();

        [Test]
        public void It_logs_the_retained_file_without_any_value()
        {
            _workspace
                .Logger.Entries.Should()
                .ContainSingle(entry => entry.EventId == PsqlRestoreRunner.PasswordFileRetained)
                .Which.Field("Operation")
                .Should()
                .Be(_workspace.Operation.Value);
            _workspace
                .LoggedText()
                .Should()
                .NotContain(text =>
                    text.Contains(ReplayWorkspace.Password)
                    || text.Contains(PrivateOperationDirectory.CredentialFileName)
                );
        }
    }

    [TestFixture]
    public class Given_a_replay_that_outlives_the_restore_timeout
    {
        private static readonly TimeSpan _restoreTimeout = TimeSpan.FromMinutes(30);

        private ReplayWorkspace _workspace = null!;
        private FakePsql _fake = null!;
        private PsqlRunResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _workspace = new ReplayWorkspace();
            string signal = Path.Combine(_workspace.Root, "started");
            _fake = FakePsql.Blocking(signal);
            FakeTimeProvider time = new();
            Task<PsqlRunResult> run = _workspace.RunAsync(
                _workspace.Runner(_fake.ExecutablePath, _restoreTimeout, time)
            );

            await FakePsql.WaitForFileAsync(signal, _bound);
            time.Advance(_restoreTimeout - TimeSpan.FromSeconds(1));
            run.IsCompleted.Should().BeFalse("the bound has not elapsed");
            time.Advance(TimeSpan.FromSeconds(1));
            _result = await run.WaitAsync(_bound);
        }

        [TearDown]
        public async Task TearDown()
        {
            await _fake.DisposeAsync();
            _workspace.Dispose();
        }

        [Test]
        public void It_kills_the_process_when_the_bound_elapses() =>
            _result
                .Should()
                .BeOfType<PsqlRunResult.Killed>()
                .Which.Reason.Should()
                .Be(PsqlKillReason.TimedOut);

        [Test]
        public void It_returns_only_after_the_process_is_gone() =>
            ReplayWorkspace.IsRunning(((PsqlRunResult.Killed)_result).ProcessId).Should().BeFalse();

        [Test]
        public void It_logs_the_elapsed_time_from_the_clock_it_was_given() =>
            _workspace.Logger.Entries.Last().Field("Elapsed").Should().Be(_restoreTimeout);

        [Test]
        public void It_deletes_the_password_file() =>
            File.Exists(_workspace.PasswordFilePath).Should().BeFalse();
    }

    [TestFixture]
    public class Given_a_replay_whose_process_exits_while_a_child_keeps_its_pipes
    {
        private static readonly TimeSpan _terminationBound = TimeSpan.FromSeconds(2);

        private ReplayWorkspace _workspace = null!;
        private FakePsql _fake = null!;
        private PsqlRunResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _workspace = new ReplayWorkspace();
            string signal = Path.Combine(_workspace.Root, "started");
            _fake = FakePsql.HoldingPipeAfterExit(signal);
            Task<PsqlRunResult> run = _workspace.RunAsync(
                _workspace.Runner(_fake.ExecutablePath, terminationBound: _terminationBound)
            );
            await FakePsql.WaitForFileAsync(signal, _bound);
            _result = await run.WaitAsync(_bound);
        }

        [TearDown]
        public async Task TearDown()
        {
            await _fake.DisposeAsync();
            _workspace.Dispose();
        }

        [Test]
        public void It_returns_within_the_termination_bound_with_the_termination_unconfirmed() =>
            _result.Should().BeOfType<PsqlRunResult.Unconfirmed>().Which.Reason.Should().BeNull();

        [Test]
        public void It_observed_the_process_itself_exit() =>
            ReplayWorkspace.IsRunning(((PsqlRunResult.Unconfirmed)_result).ProcessId).Should().BeFalse();

        [Test]
        public void It_logs_the_unconfirmed_termination_without_a_stop_reason()
        {
            LogEntry unconfirmed = _workspace.Logger.Entries.Last();
            unconfirmed.EventId.Should().Be(PsqlRestoreRunner.ReplayUnconfirmed);
            unconfirmed.Field("Reason").Should().Be("none");
        }

        [Test]
        public void It_deletes_the_password_file() =>
            File.Exists(_workspace.PasswordFilePath).Should().BeFalse();
    }

    [TestFixture]
    public class Given_a_kill_that_fails_after_cancellation
    {
        private static readonly TimeSpan _terminationBound = TimeSpan.FromSeconds(2);

        private ReplayWorkspace _workspace = null!;
        private FakePsql _fake = null!;
        private PsqlRunResult _result = null!;
        private Exception? _exception;
        private bool _stillRunningAtReturn;

        [SetUp]
        public async Task Setup()
        {
            _workspace = new ReplayWorkspace();
            string signal = Path.Combine(_workspace.Root, "started");
            _fake = FakePsql.Blocking(signal);
            using CancellationTokenSource cancellation = new();
            Task<PsqlRunResult> run = _workspace.RunAsync(
                _workspace.Runner(
                    _fake.ExecutablePath,
                    terminationBound: _terminationBound,
                    kill: _ => throw new Win32Exception(5, "Access is denied.")
                ),
                cancellation.Token
            );
            await FakePsql.WaitForFileAsync(signal, _bound);
            await cancellation.CancelAsync();
            _exception = await Record.ExceptionAsync(async () => _result = await run.WaitAsync(_bound));
            _stillRunningAtReturn =
                _result is PsqlRunResult.Unconfirmed unconfirmed
                && ReplayWorkspace.IsRunning(unconfirmed.ProcessId);
        }

        [TearDown]
        public async Task TearDown()
        {
            await _fake.DisposeAsync();
            _workspace.Dispose();
        }

        [Test]
        public void It_returns_an_unconfirmed_termination_with_the_stop_reason_instead_of_throwing()
        {
            _exception.Should().BeNull();
            _result
                .Should()
                .BeOfType<PsqlRunResult.Unconfirmed>()
                .Which.Reason.Should()
                .Be(PsqlKillReason.Cancelled);
        }

        [Test]
        public void It_reported_honestly_while_the_process_was_still_running() =>
            _stillRunningAtReturn.Should().BeTrue();

        [Test]
        public void It_logs_the_unconfirmed_termination_with_the_stop_reason()
        {
            LogEntry unconfirmed = _workspace.Logger.Entries.Last();
            unconfirmed.EventId.Should().Be(PsqlRestoreRunner.ReplayUnconfirmed);
            unconfirmed.Field("Reason").Should().Be("Cancelled");
        }

        [Test]
        public void It_deletes_the_password_file() =>
            File.Exists(_workspace.PasswordFilePath).Should().BeFalse();
    }

    [TestFixture]
    public class Given_an_executable_that_cannot_start
    {
        private ReplayWorkspace _workspace = null!;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            _workspace = new ReplayWorkspace();
            _exception = await Record.ExceptionAsync(() =>
                _workspace
                    .RunAsync(_workspace.Runner(Path.Combine(_workspace.Root, "no-such-psql")))
                    .WaitAsync(_bound)
            );
        }

        [TearDown]
        public void TearDown() => _workspace.Dispose();

        [Test]
        public void It_propagates_the_runtime_start_failure() =>
            _exception.Should().BeOfType<Win32Exception>();

        [Test]
        public void It_still_deletes_the_password_file() =>
            File.Exists(_workspace.PasswordFilePath).Should().BeFalse();

        [Test]
        public void It_never_logged_the_password() =>
            _workspace.LoggedText().Should().NotContain(text => text.Contains(ReplayWorkspace.Password));
    }

    [TestFixture]
    public class Given_arguments_the_runner_refuses
    {
        private ReplayWorkspace _workspace = null!;
        private Exception? _relativePath;
        private Exception? _zeroTimeout;
        private Exception? _wrongArtifactKind;

        [SetUp]
        public async Task Setup()
        {
            _workspace = new ReplayWorkspace();
            _relativePath = Record.Exception(() => _workspace.Runner("psql"));
            _zeroTimeout = Record.Exception(() =>
                _workspace.Runner(Path.Combine(_workspace.Root, "psql"), TimeSpan.Zero)
            );
            PsqlRestoreRunner runner = _workspace.Runner(Path.Combine(_workspace.Root, "psql"));
            StagedArtifact backup = _workspace.Artifact with { Kind = DmsArtifactKind.MssqlBackup };
            _wrongArtifactKind = await Record.ExceptionAsync(() =>
                runner.RunAsync(
                    _workspace.AdminConnection,
                    _workspace.Database,
                    backup,
                    _workspace.OperationDirectory,
                    _workspace.Operation,
                    CancellationToken.None
                )
            );
        }

        [TearDown]
        public void TearDown() => _workspace.Dispose();

        [Test]
        public void It_refuses_a_relative_psql_path() => _relativePath.Should().BeOfType<ArgumentException>();

        [Test]
        public void It_refuses_a_non_positive_restore_timeout() =>
            _zeroTimeout.Should().BeOfType<ArgumentOutOfRangeException>();

        [Test]
        public void It_refuses_an_artifact_that_is_not_a_sql_dump_before_writing_anything()
        {
            _wrongArtifactKind.Should().BeOfType<ArgumentException>();
            File.Exists(_workspace.PasswordFilePath).Should().BeFalse();
        }
    }
}
