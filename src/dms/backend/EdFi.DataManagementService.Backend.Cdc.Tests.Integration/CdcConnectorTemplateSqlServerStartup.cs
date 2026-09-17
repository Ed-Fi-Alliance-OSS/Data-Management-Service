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
            StartSqlServerAttemptAsync,
            ReadFailedSqlServerContainerStateAsync,
            RetainSqlServerStartupAttemptAsync,
            async token =>
            {
                // Only this unprovisioned provider is replaced. A failed removal aborts recovery.
                await _docker.RunAsync(["rm", "-f", "-v", ProviderContainerName], token);
            },
            _settings.KeepContainers,
            cancellationToken,
            CdcSqlServerStartupBudgets.Default
        );
        if (result.Attempts > 1)
        {
            string path = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "admission-evidence-sql-recovery-" + Guid.NewGuid().ToString("N") + ".json"
            );
            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(
                    new
                    {
                        Stage = "unprovisioned-sql-recovery",
                        Outcome = "Ready",
                        result.Attempts,
                        result.Signature,
                        result.Injected,
                    }
                ),
                cancellationToken
            );
            TestContext.AddTestAttachment(path, "Sanitized SQL Server startup recovery outcome");
        }
    }

    private async Task StartSqlServerAttemptAsync(CancellationToken cancellationToken)
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
        await WaitForSqlServerAsync(cancellationToken);
    }

    private async Task RetainSqlServerStartupAttemptAsync(
        int attempt,
        CdcSqlServerContainerState container,
        bool recreationPermitted,
        CancellationToken cancellationToken
    )
    {
        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "admission-evidence-sql-startup-" + Guid.NewGuid().ToString("N") + ".json"
        );
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(
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
                }
            ),
            cancellationToken
        );
        TestContext.AddTestAttachment(path, "Sanitized SQL Server fixture startup attempt");
    }
}
