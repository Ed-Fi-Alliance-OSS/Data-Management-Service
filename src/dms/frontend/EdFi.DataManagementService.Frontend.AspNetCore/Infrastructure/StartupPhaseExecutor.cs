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

    public async Task RunFatalAsync(
        string phase,
        string successSummary,
        string failureSummary,
        Func<Task> action,
        int exitCode = -1
    )
    {
        _startupStatusSignal.WriteStarting(phase, $"Starting {phase}.");

        try
        {
            await action();
            _startupStatusSignal.WriteCompleted(phase, successSummary);
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
}
