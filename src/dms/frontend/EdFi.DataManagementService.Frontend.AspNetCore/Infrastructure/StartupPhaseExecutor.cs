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
    public const string LoadDmsInstances = "LoadDmsInstances";
    public const string InitializeDatabase = "InitializeDatabase";
    public const string InitializeApiSchemas = "InitializeApiSchemas";
    public const string InitializeBackendMappings = "InitializeBackendMappings";
    public const string WarmUpOidcMetadataCache = "WarmUpOidcMetadataCache";
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

    public void WriteFailed(string phase, string summary, Exception exception)
    {
        _startupStatusSignal.WriteFailed(phase, summary, exception);
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
            throw;
        }
    }

    public void WriteReady(string summary)
    {
        _startupStatusSignal.WriteReady(summary);
    }

    private void HandleFatalFailure(string phase, string failureSummary, int exitCode, Exception exception)
    {
        _startupStatusSignal.WriteFailed(phase, failureSummary, exception);

        _logger.LogCritical(
            exception,
            "Fatal startup failure in phase {StartupPhase}. {FailureSummary}",
            phase,
            failureSummary
        );

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
