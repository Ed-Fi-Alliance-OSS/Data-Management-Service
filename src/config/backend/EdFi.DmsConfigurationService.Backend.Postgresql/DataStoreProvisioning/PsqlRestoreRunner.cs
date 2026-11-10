// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;

/// <summary>Why a replay was stopped before it exited on its own.</summary>
internal enum PsqlKillReason
{
    /// <summary>The caller's token was cancelled.</summary>
    Cancelled,

    /// <summary>The <c>RestoreTimeout</c> bound elapsed.</summary>
    TimedOut,
}

/// <summary>
/// What a replay run proves, and nothing more: a process that exited on its own, with both output streams
/// ended, reports its exit code and the SQLSTATE extracted from its standard error, if any; a process the
/// runner killed and confirmed gone reports why; a process whose termination could not be confirmed within
/// the bound (it did not exit after the stop, or a descendant still holds an output pipe) reports that,
/// with the stop reason when one was requested. The outcome protocol (6.9a, D-7b) is the engine's: an
/// exit is definite evidence, the other two leave the server side in doubt until the <c>psql</c> session
/// is proven gone.
/// </summary>
internal abstract record PsqlRunResult
{
    public sealed record Exited(int ProcessId, int ExitCode, string? SqlState) : PsqlRunResult;

    public sealed record Killed(int ProcessId, PsqlKillReason Reason) : PsqlRunResult;

    public sealed record Unconfirmed(int ProcessId, PsqlKillReason? Reason) : PsqlRunResult;

    /// <summary>The run failed before any process was started (the settings, the password file, the start info), so nothing reached the server.</summary>
    public sealed record NotStarted(Exception Cause) : PsqlRunResult;
}

/// <summary>
/// Runs the <c>psql</c> replay of a staged SQL artifact (D-10) against one database: the password goes
/// through a private <see cref="PgPassFile"/>, the hosts, ports, user, and database are arguments, the
/// environment is exactly the <see cref="LibpqEnvironment"/> allowlist with <c>PGAPPNAME</c> naming the
/// operation (D-7b), standard input is closed, standard output is discarded, and standard error reaches
/// nothing but the <see cref="PsqlStderrSqlState"/> buffer (D-10b). The caller's token and
/// <c>RestoreTimeout</c> are linked (6.10); either kills the process tree, and every wait that follows is
/// bounded, so the run returns within the termination bound of the stop even when something it started
/// lingers. The password file is deleted when the run ends, however it ends. Logs carry the operation id,
/// process id, exit code, elapsed time, and the validated SQLSTATE: never an argument, a path, or a line
/// of output.
/// </summary>
internal sealed class PsqlRestoreRunner
{
    /// <summary>The <c>Diagnostic.Operation</c> of the replay.</summary>
    public const string RestoreStep = "RestoreIntoStaging";

    /// <summary><c>psql</c>'s exit status when the connection to the server could not be made or went bad.</summary>
    public const int ConnectionFailedExitCode = 2;

    /// <summary><c>psql</c>'s exit status when <c>ON_ERROR_STOP</c> stopped the script on an error.</summary>
    public const int ScriptErrorExitCode = 3;

    internal static readonly EventId ReplayStarted = new(1438_30, nameof(ReplayStarted));
    internal static readonly EventId ReplayExited = new(1438_31, nameof(ReplayExited));
    internal static readonly EventId ReplayKilled = new(1438_32, nameof(ReplayKilled));
    internal static readonly EventId ReplayUnconfirmed = new(1438_33, nameof(ReplayUnconfirmed));
    internal static readonly EventId PasswordFileRetained = new(1438_34, nameof(PasswordFileRetained));

    /// <summary>The D-10 arguments every replay carries, before the file and connection arguments.</summary>
    public static IReadOnlyList<string> FixedArguments { get; } =
    [
        "--no-password",
        "--no-psqlrc",
        "--set",
        "ON_ERROR_STOP=1",
        "--set",
        "VERBOSITY=sqlstate",
        "--single-transaction",
        "--quiet",
    ];

    private readonly string _psqlPath;
    private readonly TimeSpan _restoreTimeout;
    private readonly TimeSpan _terminationBound;
    private readonly PsqlProcess.KillProcess? _kill;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <param name="psqlPath">4.3 <c>Postgresql:PsqlPath</c>, fully qualified.</param>
    /// <param name="restoreTimeout">4.3 <c>RestoreTimeout</c>: the bound of one replay.</param>
    /// <param name="timeProvider">Drives the bounds and the elapsed time in logs.</param>
    /// <param name="logger">Receives the run's value-free progress entries.</param>
    public PsqlRestoreRunner(
        string psqlPath,
        TimeSpan restoreTimeout,
        TimeProvider timeProvider,
        ILogger logger
    )
        : this(psqlPath, restoreTimeout, PsqlProcess.DefaultTerminationBound, timeProvider, logger) { }

    /// <summary>The runner with the termination bound and the kill exposed, so a test can prove the unconfirmed paths quickly.</summary>
    internal PsqlRestoreRunner(
        string psqlPath,
        TimeSpan restoreTimeout,
        TimeSpan terminationBound,
        TimeProvider timeProvider,
        ILogger logger,
        PsqlProcess.KillProcess? kill = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(psqlPath);
        if (!Path.IsPathFullyQualified(psqlPath))
        {
            throw new ArgumentException("The psql path must be fully qualified.", nameof(psqlPath));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(restoreTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(restoreTimeout, TimeSpan.FromHours(6));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(terminationBound, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _psqlPath = psqlPath;
        _restoreTimeout = restoreTimeout;
        _terminationBound = terminationBound;
        _kill = kill;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Replays <paramref name="artifact"/> into <paramref name="database"/> with the credentials of
    /// <paramref name="adminConnection"/>, keeping the password file inside <paramref name="directory"/>.
    /// Cancellation does not propagate as an exception: a cancelled run returns
    /// <see cref="PsqlRunResult.Killed"/> or <see cref="PsqlRunResult.Unconfirmed"/>, so the engine can run
    /// the outcome protocol before the orchestrator surfaces the cancellation (6.10). A failure before the
    /// process is started, cancellation included, returns <see cref="PsqlRunResult.NotStarted"/>; the system's
    /// refusal to create the process itself propagates as its <see cref="System.ComponentModel.Win32Exception"/>,
    /// whose code the engine classifies.
    /// </summary>
    public async Task<PsqlRunResult> RunAsync(
        PostgresqlAdminConnection adminConnection,
        DmsDatabaseName database,
        StagedArtifact artifact,
        PrivateOperationDirectory directory,
        DmsOperationId operation,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(adminConnection);
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(directory);
        if (artifact.Kind != DmsArtifactKind.PostgresqlSqlDump)
        {
            throw new ArgumentException(
                "The psql runner replays PostgreSQL SQL dumps only.",
                nameof(artifact)
            );
        }

        LibpqConnectionSettings connection;
        string passwordFilePath;
        try
        {
            connection = adminConnection.ToLibpqSettings();
            passwordFilePath = await PgPassFile.WriteAsync(
                directory,
                connection,
                database.Value,
                cancellationToken
            );
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new PsqlRunResult.NotStarted(exception);
        }

        try
        {
            ProcessStartInfo startInfo;
            try
            {
                startInfo = CreateStartInfo(
                    _psqlPath,
                    connection,
                    database.Value,
                    artifact.FilePath,
                    passwordFilePath,
                    directory.Path,
                    operation
                );
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return new PsqlRunResult.NotStarted(exception);
            }

            return await RunAsync(startInfo, operation, cancellationToken);
        }
        finally
        {
            if (!PgPassFile.TryDelete(passwordFilePath))
            {
                // The operation directory's release retries and reports it as credential residue (6.11).
                _logger.LogWarning(
                    PasswordFileRetained,
                    "The replay of operation {Operation} could not delete its password file; the operation directory's cleanup reports it",
                    operation.Value
                );
            }
        }
    }

    /// <summary>
    /// The start info of one replay: the fixed arguments, then <c>--file</c>, then the connection
    /// arguments, with the allowlisted environment. The password appears in no argument and no variable;
    /// only the password file's path does.
    /// </summary>
    internal static ProcessStartInfo CreateStartInfo(
        string psqlPath,
        LibpqConnectionSettings connection,
        string database,
        string artifactPath,
        string passwordFilePath,
        string workingDirectory,
        DmsOperationId operation
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordFilePath);

        ProcessStartInfo startInfo = PsqlProcess.CreateStartInfo(psqlPath, workingDirectory);
        foreach (string argument in FixedArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add("--file");
        startInfo.ArgumentList.Add(artifactPath);
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add(connection.HostArgument);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(connection.PortArgument);
        startInfo.ArgumentList.Add("--username");
        startInfo.ArgumentList.Add(connection.Username);
        startInfo.ArgumentList.Add("--dbname");
        startInfo.ArgumentList.Add(database);

        LibpqEnvironment.Apply(startInfo, connection, passwordFilePath, operation.RestoreApplicationName);
        return startInfo;
    }

    private async Task<PsqlRunResult> RunAsync(
        ProcessStartInfo startInfo,
        DmsOperationId operation,
        CancellationToken cancellationToken
    )
    {
        PsqlStderrSqlState standardError = new();
        using CancellationTokenSource deadline = new(_restoreTimeout, _timeProvider);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token
        );

        long startedAt = _timeProvider.GetTimestamp();
        _logger.LogInformation(
            ReplayStarted,
            "The psql replay of operation {Operation} is starting.",
            operation.Value
        );

        // The last check before the process exists: a stop observed here starts nothing, so the run stays the
        // definite NotStarted the engine lets cleanup act on. A stop arriving after it finds a started process
        // and follows the kill-and-resolve protocol below.
        if (stop.IsCancellationRequested)
        {
            return new PsqlRunResult.NotStarted(new OperationCanceledException(stop.Token));
        }

        PsqlProcess.Completion completion = await PsqlProcess.RunAsync(
            startInfo,
            consumeStandardOutput: null,
            standardError.Append,
            _terminationBound,
            _timeProvider,
            _kill,
            stop.Token
        );
        TimeSpan elapsed = _timeProvider.GetElapsedTime(startedAt);
        string? sqlState = standardError.ExtractAndClear();
        PsqlKillReason? stopReason = StopReasonOf(cancellationToken, deadline);

        switch (completion.Termination)
        {
            case PsqlProcess.Termination.Killed:
                PsqlKillReason reason = stopReason ?? PsqlKillReason.TimedOut;
                _logger.LogWarning(
                    ReplayKilled,
                    "The psql replay of operation {Operation} (process {ProcessId}) was killed after {Elapsed}: {Reason}.",
                    operation.Value,
                    completion.ProcessId,
                    elapsed,
                    reason
                );
                return new PsqlRunResult.Killed(completion.ProcessId, reason);

            case PsqlProcess.Termination.Unconfirmed:
                _logger.LogWarning(
                    ReplayUnconfirmed,
                    "The psql replay of operation {Operation} (process {ProcessId}) could not be confirmed gone after {Elapsed}; stop reason {Reason}.",
                    operation.Value,
                    completion.ProcessId,
                    elapsed,
                    stopReason?.ToString() ?? "none"
                );
                return new PsqlRunResult.Unconfirmed(completion.ProcessId, stopReason);

            default:
                int exitCode =
                    completion.ExitCode
                    ?? throw new InvalidOperationException("An exited process reported no exit code.");
                _logger.LogInformation(
                    ReplayExited,
                    "The psql replay of operation {Operation} (process {ProcessId}) exited with code {ExitCode} after {Elapsed}; SQLSTATE {SqlState}.",
                    operation.Value,
                    completion.ProcessId,
                    exitCode,
                    elapsed,
                    sqlState ?? "none"
                );
                return new PsqlRunResult.Exited(completion.ProcessId, exitCode, sqlState);
        }
    }

    /// <summary>Which bound requested the stop, the caller's first; null when none fired.</summary>
    private static PsqlKillReason? StopReasonOf(CancellationToken caller, CancellationTokenSource deadline)
    {
        if (caller.IsCancellationRequested)
        {
            return PsqlKillReason.Cancelled;
        }

        return deadline.IsCancellationRequested ? PsqlKillReason.TimedOut : null;
    }
}
