// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

internal sealed partial class CdcConnectorTemplatePinnedImageFixture
{
    private async Task StartSqlServerWithRecoveryAsync(CancellationToken cancellationToken)
    {
        CdcSqlServerStartupResult result = await CdcSqlServerFixtureStartup.RunAsync(
            launchContainer: LaunchSqlServerContainerAsync,
            recordFreshStart: RecordFreshSqlServerStartAsync,
            waitForReadiness: WaitForSqlServerAsync,
            inspectFailure: ReadFailedSqlServerContainerStateAsync,
            retainFailure: RetainSqlServerStartupAttemptAsync,
            removeContainer: async token =>
            {
                // Only this unprovisioned provider is replaced. A failed removal aborts recovery.
                await _docker.RunAsync(["rm", "-f", "-v", ProviderContainerName], token);
            },
            keepContainer: _settings.KeepContainers,
            cancellationToken: cancellationToken,
            budgets: CdcSqlServerStartupBudgets.Default
        );
        if (result.Attempts > 1)
        {
            await AttachSqlServerEvidenceAsync(
                "recovery",
                new
                {
                    Stage = "unprovisioned-sql-recovery",
                    Outcome = "Ready",
                    result.Attempts,
                    result.Signature,
                    result.Injected,
                },
                "Sanitized SQL Server startup recovery outcome",
                cancellationToken
            );
        }
    }

    private async Task LaunchSqlServerContainerAsync(CancellationToken cancellationToken)
    {
        _sqlServerReadinessProbeCount = 0;
        _sqlServerReadinessExitCode = 0;
        _sqlServerReadinessState = "NotObserved";
        await _docker.RunAsync(
            [
                "run",
                "--detach",
                "--name",
                ProviderContainerName,
                "--network",
                NetworkName,
                "-p",
                "127.0.0.1::1433",
                "-e",
                "ACCEPT_EULA=Y",
                "-e",
                $"MSSQL_SA_PASSWORD={ConnectorDatabasePassword}",
                "-e",
                "MSSQL_AGENT_ENABLED=true",
                _settings.ProviderImage,
            ],
            cancellationToken
        );
    }

    private Task RecordFreshSqlServerStartAsync(CancellationToken cancellationToken) =>
        AttachSqlServerEvidenceAsync(
            "fresh",
            new { Stage = "unprovisioned-sql-fresh-start" },
            "Fresh SQL Server fixture start",
            cancellationToken
        );

    private async Task RetainSqlServerStartupAttemptAsync(
        int attempt,
        CdcSqlServerContainerState container,
        bool recreationPermitted,
        CancellationToken cancellationToken
    )
    {
        await AttachSqlServerEvidenceAsync(
            "startup",
            new
            {
                Provider = Provider.ToString(),
                Stage = "unprovisioned-sql-startup",
                Attempt = attempt,
                ProbeCount = _sqlServerReadinessProbeCount,
                LastProbeState = _sqlServerReadinessState,
                Container = container,
                RecreationPermitted = recreationPermitted,
                Signature = container.RecoverySignature,
                Resources = await ReadSqlServerStartupResourcesAsync(
                    _docker,
                    _settings.ProviderImage,
                    ProviderContainerName,
                    cancellationToken
                ),
            },
            "Sanitized SQL Server fixture startup attempt",
            cancellationToken
        );
    }

    private async Task AttachSqlServerEvidenceAsync(
        string kind,
        object evidence,
        string description,
        CancellationToken cancellationToken
    )
    {
        // The Contract lane also exercises this fixture with simulated Docker commands.
        if (_docker is not DockerCli { IsOffline: false })
        {
            return;
        }
        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"admission-evidence-sql-{kind}-{Guid.NewGuid():N}.json"
        );
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(evidence), cancellationToken);
        TestContext.AddTestAttachment(path, description);
    }
}
