// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Serialization;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>REST worker assignment is not a process incarnation or task restart receipt.</summary>
public sealed class CdcConnectTaskStatus(int id, CoreCdc.CdcConnectorRuntimeState state, string workerId)
{
    public int Id { get; } = id;
    public CoreCdc.CdcConnectorRuntimeState State { get; } = state;

    [JsonIgnore]
    public string WorkerId { get; } = workerId;

    public override string ToString() => nameof(CdcConnectTaskStatus);
}

public sealed class CdcConnectStatus(
    CoreCdc.CdcConnectorRuntimeObservation runtime,
    string workerId,
    IReadOnlyList<CdcConnectTaskStatus> tasks
)
{
    [JsonIgnore]
    public CoreCdc.CdcConnectorRuntimeObservation Runtime { get; } = runtime;

    [JsonIgnore]
    public string WorkerId { get; } = workerId;

    [JsonIgnore]
    public IReadOnlyList<CdcConnectTaskStatus> Tasks { get; } = tasks;
    public bool IsStopped =>
        Runtime.ConnectorState == CoreCdc.CdcConnectorRuntimeState.Stopped && Tasks.Count == 0;
    public bool IsRunning =>
        Runtime.ConnectorState == CoreCdc.CdcConnectorRuntimeState.Running
        && Tasks.Count == 1
        && Tasks[0].Id == 0
        && Tasks[0].State == CoreCdc.CdcConnectorRuntimeState.Running;

    public override string ToString() => nameof(CdcConnectStatus);
}

public enum CdcConnectOffsetState
{
    Missing,
    Multiple,
    SourcePartitionMismatch,
    Null,
    Snapshot,
    Malformed,
    Streaming,
}

/// <summary>In-memory provider evidence; no source identifiers or offsets enter workflow output.</summary>
public sealed class CdcConnectOffsetEvidence(
    CdcConnectOffsetState state,
    string sourcePartitionHash,
    CoreCdc.CdcPostgresqlConnectorOffset postgresql,
    CoreCdc.CdcSqlServerConnectorOffset sqlServer
)
{
    public CdcConnectOffsetState State { get; } = state;

    [JsonIgnore]
    public IReadOnlyDictionary<string, string> SourcePartition { get; init; } =
        new Dictionary<string, string>();

    [JsonIgnore]
    public string SourcePartitionHash { get; } = sourcePartitionHash;

    [JsonIgnore]
    public CoreCdc.CdcPostgresqlConnectorOffset Postgresql { get; } = postgresql;

    [JsonIgnore]
    public CoreCdc.CdcSqlServerConnectorOffset SqlServer { get; } = sqlServer;

    public override string ToString() => nameof(CdcConnectOffsetEvidence);

    internal static CdcConnectOffsetEvidence Parse(CdcDeploymentRequest request, JsonElement root)
    {
        var match = CoreCdc.CdcConnectorOffsetMatchResult.Exact;
        string hash = "";
        CdcConnectOffsetEvidence Empty(CdcConnectOffsetState state) =>
            new(
                state,
                hash,
                new(
                    match,
                    state == CdcConnectOffsetState.Snapshot,
                    state == CdcConnectOffsetState.Null,
                    null
                ),
                new(
                    match,
                    state == CdcConnectOffsetState.Snapshot,
                    state == CdcConnectOffsetState.Null,
                    null,
                    null,
                    null
                )
            );
        if (!root.TryGetProperty("offsets", out var offsets) || offsets.ValueKind != JsonValueKind.Array)
        {
            return Empty(CdcConnectOffsetState.Malformed);
        }
        if (offsets.GetArrayLength() == 0)
        {
            match = CoreCdc.CdcConnectorOffsetMatchResult.Missing;
            return Empty(CdcConnectOffsetState.Missing);
        }
        if (offsets.GetArrayLength() != 1)
        {
            match = CoreCdc.CdcConnectorOffsetMatchResult.Multiple;
            return Empty(CdcConnectOffsetState.Multiple);
        }
        var entry = offsets[0];
        if (
            entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty("partition", out var partition)
            || partition.ValueKind != JsonValueKind.Object
            || !TryString(partition, "server", out string server)
        )
        {
            return Empty(CdcConnectOffsetState.Malformed);
        }
        bool sqlServer = request.Binding.Provider == CoreCdc.CdcProvider.SqlServer;
        string catalog = "";
        if (sqlServer && !TryString(partition, "database", out catalog))
        {
            return Empty(CdcConnectOffsetState.Malformed);
        }
        var actual = CoreCdc.CdcSourcePartitionHashCalculator.Compute(
            request.Binding.Provider,
            server,
            sqlServer ? catalog : null
        );
        if (!actual.Succeeded)
        {
            return Empty(CdcConnectOffsetState.Malformed);
        }
        hash = actual.Hash!;
        string expectedCatalog = "";
        if (
            sqlServer
            && !request.ProviderConnectionProperties.Properties.TryGetValue(
                "database.names",
                out expectedCatalog!
            )
        )
        {
            return Empty(CdcConnectOffsetState.Malformed);
        }
        var expected = CoreCdc.CdcSourcePartitionHashCalculator.Compute(
            request.Binding.Provider,
            request.Binding.ConnectorName,
            sqlServer ? expectedCatalog : null
        );
        if (
            !expected.Succeeded
            || hash != expected.Hash
            || partition.EnumerateObject().Count() != (sqlServer ? 2 : 1)
        )
        {
            match = CoreCdc.CdcConnectorOffsetMatchResult.SourcePartitionMismatch;
            return Empty(CdcConnectOffsetState.SourcePartitionMismatch);
        }
        if (!entry.TryGetProperty("offset", out var offset))
        {
            return Empty(CdcConnectOffsetState.Malformed);
        }
        if (offset.ValueKind == JsonValueKind.Null)
        {
            return Empty(CdcConnectOffsetState.Null);
        }
        if (offset.ValueKind != JsonValueKind.Object)
        {
            return Empty(CdcConnectOffsetState.Malformed);
        }
        if (offset.TryGetProperty("snapshot", out var snapshot))
        {
            if (
                snapshot.ValueKind == JsonValueKind.True
                || (
                    snapshot.ValueKind == JsonValueKind.String
                    && snapshot.GetString() is "true" or "last" or "incremental"
                )
            )
            {
                return Empty(CdcConnectOffsetState.Snapshot);
            }
            if (
                snapshot.ValueKind != JsonValueKind.False
                && !(snapshot.ValueKind == JsonValueKind.String && snapshot.GetString() == "false")
            )
            {
                return Empty(CdcConnectOffsetState.Malformed);
            }
        }
        if (!sqlServer)
        {
            if (
                !offset.TryGetProperty("lsn_proc", out var lsn)
                || lsn.ValueKind != JsonValueKind.Number
                || !lsn.TryGetInt64(out long value)
            )
            {
                return Empty(CdcConnectOffsetState.Malformed);
            }
            CoreCdc.CdcPostgresqlConnectorOffset parsed = new(match, false, false, value);
            // Core owns signed Debezium lsn_proc interpretation and all position comparisons.
            var comparison = CoreCdc.CdcPostgresqlProviderPosition.CompareCommittedOffsetToBarrier(
                new(0),
                parsed
            );
            return new(
                comparison.Succeeded ? CdcConnectOffsetState.Streaming : CdcConnectOffsetState.Malformed,
                hash,
                parsed,
                new(match, false, false, null, null, null)
            )
            {
                SourcePartition = partition
                    .EnumerateObject()
                    .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal),
            };
        }
        if (
            !TryString(offset, "commit_lsn", out string commit)
            || !TryString(offset, "change_lsn", out string change)
            || !offset.TryGetProperty("event_serial_no", out var serial)
            || serial.ValueKind != JsonValueKind.Number
            || !serial.TryGetInt64(out long serialNumber)
        )
        {
            return Empty(CdcConnectOffsetState.Malformed);
        }
        CoreCdc.CdcSqlServerConnectorOffset sqlOffset = new(
            match,
            false,
            false,
            commit,
            change,
            serialNumber
        );
        var sqlComparison = CoreCdc.CdcSqlServerProviderPositionParser.CompareCommittedOffsetToBarrier(
            new(new(0, 0, 0), new(0, 0, 0), 0),
            sqlOffset
        );
        // Invalid provider text is not retained or echoed even in the typed evidence.
        return sqlComparison.Succeeded
            ? new(CdcConnectOffsetState.Streaming, hash, new(match, false, false, null), sqlOffset)
            {
                SourcePartition = partition
                    .EnumerateObject()
                    .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal),
            }
            : Empty(CdcConnectOffsetState.Malformed);
    }

    internal static bool TryString(JsonElement element, string property, out string value)
    {
        value = "";
        if (
            element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(property, out var item)
            || item.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(item.GetString())
        )
        {
            return false;
        }
        value = item.GetString()!;
        return true;
    }
}
