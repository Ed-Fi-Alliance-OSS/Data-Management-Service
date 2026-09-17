// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.Json;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

internal sealed partial class CdcConnectorTemplatePinnedImageFixture
{
    public async Task AssertSqlServerCaptureInventoryAsync(CancellationToken token)
    {
        await AssertSqlServer2025Async(token);
        // ValidateOnly checks the production capture inventory, Agent, permissions and snapshot prerequisites.
        await AssertMessageContractSourceLayoutAsync(token);
        string inventory = await ReadSqlServerScalarAsync(
            """
            SELECT STRING_AGG(OBJECT_SCHEMA_NAME(source_object_id) + '.' + OBJECT_NAME(source_object_id), ',')
                WITHIN GROUP (ORDER BY OBJECT_NAME(source_object_id)) FROM cdc.change_tables;
            """,
            token
        );
        inventory.Trim().Should().Be("dms.CdcHeartbeat,dms.Document,dms.DocumentCache");
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

    /// <summary>A retained heartbeat after the preceding writes supplies an actual function-mode
    /// after-image barrier. Reading its source table or Kafka ends alone is insufficient.</summary>
    public async Task<MessageContractSqlServerPosition> CaptureSqlServerHeartbeatBarrierAsync(
        CancellationToken token
    )
    {
        long sequence = await ReadProviderHeartbeatSequenceAsync(token);
        await AdvanceHeartbeatAsync(token);
        string capture = SqlServerCaptureInstances
            .Single(d => d.TableKind == CdcSourceTableKind.CdcHeartbeat)
            .CaptureInstanceName.Value;
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            MessageContractSqlServerPosition position = new("", "", 2);
            await WithProviderConnectionAsync(
                async connection =>
                {
                    await using DbCommand command = connection.CreateCommand();
                    command.CommandText = $"""
                    DECLARE @from_lsn binary(10) = sys.fn_cdc_get_min_lsn(@capture);
                    DECLARE @to_lsn binary(10) = sys.fn_cdc_get_max_lsn();
                    IF @from_lsn <> 0x00000000000000000000 AND @to_lsn >= @from_lsn
                    SELECT TOP (1) [__$start_lsn], [__$seqval]
                    FROM cdc.fn_cdc_get_all_changes_{capture}(@from_lsn, @to_lsn, N'all')
                    WHERE [__$operation] = 4 AND [HeartbeatSequence] > @sequence
                    ORDER BY [__$start_lsn], [__$seqval];
                    """;
                    DbParameter captureParameter = command.CreateParameter();
                    captureParameter.ParameterName = "capture";
                    captureParameter.Value = capture;
                    command.Parameters.Add(captureParameter);
                    DbParameter sequenceParameter = command.CreateParameter();
                    sequenceParameter.ParameterName = "sequence";
                    sequenceParameter.Value = sequence;
                    command.Parameters.Add(sequenceParameter);
                    await using DbDataReader reader = await command.ExecuteReaderAsync(token);
                    if (await reader.ReadAsync(token))
                    {
                        position = new(FormatLsn((byte[])reader[0]), FormatLsn((byte[])reader[1]), 2);
                    }
                },
                token
            );
            if (position.CommitLsn.Length > 0)
            {
                return position;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), token);
        }
        throw new AssertionException(
            "SQL Server heartbeat after-image capture timed out; capture job/function prerequisite unavailable. Details redacted."
        );

        static string FormatLsn(byte[] bytes)
        {
            bytes.Length.Should().Be(10);
            string hex = Convert.ToHexString(bytes).ToLowerInvariant();
            return $"{hex[..8]}:{hex[8..16]}:{hex[16..]}";
        }
    }

    public async Task<MessageContractSqlServerFence> FenceSqlServerSourceAsync(
        CdcConnectorTemplateRequest request,
        string phase,
        CancellationToken token
    )
    {
        MessageContractSqlServerPosition barrier = await CaptureSqlServerHeartbeatBarrierAsync(token);
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await AssertRegisteredConnectorReachesRunningStateAsync(request, token);
            var observed = await TryReadCommittedSourceOffsetAsync(request, token);
            if (observed is not null)
            {
                observed.SourcePartitionEvidence.Properties.Count.Should().Be(2);
                (observed.SourcePartitionEvidence.Properties["server"] == request.ConnectorName.Value)
                    .Should()
                    .BeTrue();
                (observed.SourcePartitionEvidence.Properties["database"] == SqlServerDatabaseName)
                    .Should()
                    .BeTrue();
                if (
                    CommittedSourceOffsetRetainsOrAdvances(
                        Provider,
                        barrier.OffsetJson,
                        observed.CanonicalOffsetJson
                    )
                )
                {
                    using JsonDocument offset = JsonDocument.Parse(observed.CanonicalOffsetJson);
                    MessageContractSqlServerPosition committed = new(
                        offset.RootElement.GetProperty("commit_lsn").GetString()!,
                        offset.RootElement.GetProperty("change_lsn").GetString()!,
                        offset.RootElement.GetProperty("event_serial_no").GetInt64()
                    );
                    await TestContext.Out.WriteLineAsync(
                        $"{phase}: captured heartbeat barrier={barrier}; committed={committed}; matching server/database partition"
                    );
                    return new(phase, barrier, committed);
                }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), token);
        }
        throw new AssertionException(
            $"SQL Server {phase} committed source fence timed out; barrier={barrier}. Details redacted."
        );
    }
}

internal sealed record MessageContractSqlServerPosition(
    string CommitLsn,
    string ChangeLsn,
    long EventSerialNo
)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string OffsetJson =>
        JsonSerializer.Serialize(
            new
            {
                commit_lsn = CommitLsn,
                change_lsn = ChangeLsn,
                event_serial_no = EventSerialNo,
            }
        );
}

internal sealed record MessageContractSqlServerFence(
    string Phase,
    MessageContractSqlServerPosition Barrier,
    MessageContractSqlServerPosition Committed
);
