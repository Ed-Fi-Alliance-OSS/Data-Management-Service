// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

/// <summary>Attach to existing endpoints only. Call after measured writes and before capturing Kafka ends.
/// Production Connect parsing validates the complete source partition and provider offset.</summary>
internal sealed class MessageContractProviderFences(
    MessageContractProviderObserver provider,
    CdcDeploymentRequest request,
    ICdcConnectTransport connect
)
{
    public async Task<MessageContractPostgresqlFence> FencePostgresqlSourceAsync(
        string phase,
        CancellationToken token
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        token = timeout.Token;
        CoreCdc.CdcPostgresqlWalPosition barrier = await provider.CapturePostgresqlWalAsync(token);
        await provider.AdvanceHeartbeatAsync(token);
        while (true)
        {
            var observed = await ReadStreamingOffsetAsync(token);
            if (
                observed is not null
                && CoreCdc
                    .CdcPostgresqlProviderPosition.CompareCommittedOffsetToBarrier(
                        barrier,
                        observed.Postgresql
                    )
                    .Succeeded
            )
            {
                ulong processed = unchecked((ulong)observed.Postgresql.LsnProc!.Value);
                await TestContext.Out.WriteLineAsync(
                    $"{phase}: WAL barrier={barrier.Value}, committed lsn_proc={processed}, matching single server partition"
                );
                return new(phase, barrier.Value, processed);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), token);
        }
    }

    public async Task<MessageContractSqlServerFence> FenceSqlServerSourceAsync(
        string phase,
        CancellationToken token
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(4));
        token = timeout.Token;
        MessageContractSqlServerPosition barrier = await provider.CaptureSqlServerHeartbeatBarrierAsync(
            token
        );
        CoreCdc.CdcSqlServerProviderPosition position =
            CoreCdc.CdcSqlServerProviderPosition.HeartbeatAfterImage(
                CoreCdc
                    .CdcSqlServerProviderPositionParser.ParseLsn(barrier.CommitLsn, "$.commitLsn")
                    .Lsn!.Value,
                CoreCdc
                    .CdcSqlServerProviderPositionParser.ParseLsn(barrier.ChangeLsn, "$.changeLsn")
                    .Lsn!.Value
            );
        while (true)
        {
            var observed = await ReadStreamingOffsetAsync(token);
            if (
                observed is not null
                && CoreCdc
                    .CdcSqlServerProviderPositionParser.CompareCommittedOffsetToBarrier(
                        position,
                        observed.SqlServer
                    )
                    .Succeeded
            )
            {
                MessageContractSqlServerPosition committed = new(
                    observed.SqlServer.CommitLsn!,
                    observed.SqlServer.ChangeLsn!,
                    observed.SqlServer.EventSerialNo!.Value
                );
                await TestContext.Out.WriteLineAsync(
                    $"{phase}: captured heartbeat barrier={barrier}; committed={committed}; matching server/database partition"
                );
                return new(phase, barrier, committed);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), token);
        }
    }

    public async Task AssertConnectorIncludeListAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var response = await connect.ReadConfigurationAsync(request, timeout.Token);
        response.Should().BeOfType<CdcTransportResult<IReadOnlyDictionary<string, string>>.Observed>();
        var config = ((CdcTransportResult<IReadOnlyDictionary<string, string>>.Observed)response).Value;
        config["table.include.list"].Should().Be(@"dms\.DocumentCache,dms\.Document,dms\.CdcHeartbeat");
    }

    private async Task<CdcConnectOffsetEvidence?> ReadStreamingOffsetAsync(CancellationToken token)
    {
        var status = await connect.ReadStatusAsync(request, token);
        if (status is not CdcTransportResult<CdcConnectStatus>.Observed { Value.IsRunning: true })
        {
            return null;
        }
        var response = await connect.ReadOffsetEvidenceAsync(request, token);
        if (response is not CdcTransportResult<CdcConnectOffsetEvidence>.Observed observed)
        {
            return null;
        }
        observed
            .Value.State.Should()
            .NotBe(CdcConnectOffsetState.Multiple)
            .And.NotBe(CdcConnectOffsetState.SourcePartitionMismatch)
            .And.NotBe(CdcConnectOffsetState.Malformed);
        return observed.Value.State == CdcConnectOffsetState.Streaming ? observed.Value : null;
    }
}

internal sealed record MessageContractPostgresqlFence(string Phase, ulong WalBarrier, ulong CommittedLsnProc);

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
