// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

internal sealed partial class CdcConnectorTemplatePinnedImageFixture
{
    public async Task AssertSqlServerCaptureInventoryAsync(CancellationToken token)
    {
        await AssertSqlServer2025Async(token);
        // ValidateOnly checks the production capture inventory, Agent, permissions and snapshot prerequisites.
        await AssertMessageContractSourceLayoutAsync(token);
        await (await CreateProviderObserverAsync(token)).AssertCaptureInventoryAsync(token);
        string readiness = await ReadSqlServerScalarAsync(
            """
            SELECT CASE WHEN
                (SELECT snapshot_isolation_state FROM sys.databases WHERE database_id = DB_ID()) = 1
                AND EXISTS (SELECT 1 FROM sys.dm_server_services
                    WHERE servicename LIKE N'SQL Server Agent%' AND status_desc = N'Running')
                AND EXISTS (SELECT 1 FROM msdb.dbo.cdc_jobs WHERE database_id = DB_ID() AND job_type = N'capture')
                THEN 'ready' ELSE 'unavailable' END;
            """,
            token
        );
        if (readiness.Trim() != "ready")
        {
            _settings.StopOnPrerequisiteFailure(
                CdcProvider.SqlServer,
                "SQL Server 2025 Agent/capture job/snapshot-isolation prerequisites unavailable; details redacted."
            );
        }
    }

    public async Task<MessageContractSqlServerPosition> CaptureSqlServerHeartbeatBarrierAsync(
        CancellationToken token
    ) => await (await CreateProviderObserverAsync(token)).CaptureSqlServerHeartbeatBarrierAsync(token);

    public async Task<MessageContractSqlServerFence> FenceSqlServerSourceAsync(
        CdcConnectorTemplateRequest request,
        string phase,
        CancellationToken token
    ) => await (await CreateProviderFencesAsync(request, token)).FenceSqlServerSourceAsync(phase, token);
}
