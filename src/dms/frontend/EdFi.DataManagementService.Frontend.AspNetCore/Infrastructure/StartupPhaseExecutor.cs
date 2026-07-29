// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

internal interface IStartupProcessExit
{
    void Exit(int exitCode);
}

internal sealed class EnvironmentStartupProcessExit : IStartupProcessExit
{
    public void Exit(int exitCode)
    {
        Environment.Exit(exitCode);
    }
}

internal static class DmsStartupPhases
{
    public const string ConfigureServices = "ConfigureServices";
    public const string BuildApplication = "BuildApplication";
    public const string LoadDataStores = "LoadDataStores";
    public const string InitializeApiSchemas = "InitializeApiSchemas";
    public const string InitializeBackendMappings = "InitializeBackendMappings";
    public const string InitializeAuthMetadata = "InitializeAuthMetadata";
    public const string ConfigureEndpoints = "ConfigureEndpoints";
    public const string Ready = "Ready";
}

internal sealed class StartupPhaseExecutor(
    IStartupStatusSignal startupStatusSignal,
    IStartupProcessExit startupProcessExit,
    ILogger<StartupPhaseExecutor> logger
)
{
    private readonly IStartupStatusSignal _startupStatusSignal = startupStatusSignal;
    private readonly IStartupProcessExit _startupProcessExit = startupProcessExit;
    private readonly ILogger<StartupPhaseExecutor> _logger = logger;

    public string StatusFilePath => _startupStatusSignal.FilePath;

    public void WriteStarting(string phase, string summary)
    {
        _startupStatusSignal.WriteStarting(phase, summary);
    }

    public void WriteCompleted(string phase, string summary)
    {
        _startupStatusSignal.WriteCompleted(phase, summary);
    }

    /// <summary>
    /// Records a phase failure that does not terminate the process, and deliberately emits no
    /// Critical event: its one caller is the configuration-validation route, which leaves the host
    /// up serving short-circuited requests rather than dying. A terminating path must use
    /// <see cref="WriteFatalFailure"/> instead, or it silently drops the phase-labelled Critical
    /// event that every fatal phase emits. Despite the name, this is not the general case.
    /// </summary>
    public void WriteFailed(string phase, string summary, Exception exception)
    {
        _startupStatusSignal.WriteFailed(phase, summary, exception);
    }

    /// <summary>
    /// Records a phase failure that is terminating the process, for phases that are not routed
    /// through <see cref="RunFatalAsync"/> and terminate by rethrow rather than the exit hook.
    /// Emits the same phase-labelled Critical event as the fatal phases so a log search by phase
    /// name covers every fatal phase uniformly. Callers rethrow; this method does not exit.
    /// Non-terminating failures use <see cref="WriteFailed"/>, which emits no Critical event.
    /// </summary>
    public void WriteFatalFailure(string phase, string failureSummary, Exception exception)
    {
        // The status file is written first because it is the artifact CI collects; nothing that
        // can fail should precede it.
        _startupStatusSignal.WriteFailed(phase, failureSummary, exception);

        _logger.LogCritical(
            exception,
            "Fatal startup failure in phase {StartupPhase}. {FailureSummary}",
            phase,
            failureSummary
        );
    }

    public async Task RunFatalAsync(
        string phase,
        string startingSummary,
        string successSummary,
        string failureSummary,
        Func<Task> action,
        int exitCode = -1
    )
    {
        StartupStatusSnapshot startupStatusSnapshot = CaptureStartupStatusSnapshot();
        _startupStatusSignal.WriteStarting(phase, startingSummary);

        try
        {
            await action();
            _startupStatusSignal.WriteCompleted(phase, successSummary);
        }
        catch (OperationCanceledException)
        {
            RestoreStartupStatusSnapshot(startupStatusSnapshot);
            throw;
        }
        catch (Exception ex)
        {
            HandleFatalFailure(phase, failureSummary, exitCode, ex);
            // Production exit handling terminates the process before control returns; tests use the rethrow to assert fatal-path behavior.
            throw;
        }
    }

    public void WriteReady(string summary)
    {
        _startupStatusSignal.WriteReady(summary);
    }

    private void HandleFatalFailure(string phase, string failureSummary, int exitCode, Exception exception)
    {
        WriteFatalFailure(phase, failureSummary, exception);

        _startupProcessExit.Exit(exitCode);
    }

    private StartupStatusSnapshot CaptureStartupStatusSnapshot()
    {
        try
        {
            if (!File.Exists(StatusFilePath))
            {
                return new StartupStatusSnapshot(IsCaptured: true, Existed: false, Contents: string.Empty);
            }

            return new StartupStatusSnapshot(
                IsCaptured: true,
                Existed: true,
                Contents: File.ReadAllText(StatusFilePath)
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Unable to snapshot DMS startup status file at {FilePath}",
                StatusFilePath
            );
            return new StartupStatusSnapshot(IsCaptured: false, Existed: false, Contents: string.Empty);
        }
    }

    private void RestoreStartupStatusSnapshot(StartupStatusSnapshot startupStatusSnapshot)
    {
        if (!startupStatusSnapshot.IsCaptured)
        {
            return;
        }

        try
        {
            if (!startupStatusSnapshot.Existed)
            {
                if (File.Exists(StatusFilePath))
                {
                    File.Delete(StatusFilePath);
                }

                return;
            }

            string? directory = Path.GetDirectoryName(StatusFilePath);

            if (directory is not null && directory.Length > 0)
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(StatusFilePath, startupStatusSnapshot.Contents);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Unable to restore DMS startup status file at {FilePath} after cancellation",
                StatusFilePath
            );
        }
    }

    private readonly record struct StartupStatusSnapshot(bool IsCaptured, bool Existed, string Contents);
}
