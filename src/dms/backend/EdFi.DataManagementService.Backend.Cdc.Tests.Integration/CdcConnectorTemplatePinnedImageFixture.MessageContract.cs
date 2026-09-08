// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Confluent.Kafka;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using Npgsql;
using NpgsqlTypes;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

// Focused operations on the existing fixture's provider, rendered connector and broker resources.
internal sealed partial class CdcConnectorTemplatePinnedImageFixture
{
    private int _brokerHostPort;
    public string HostKafkaBootstrapServers => $"127.0.0.1:{_brokerHostPort}";

    public async Task WriteMaterializedRowAsync(JsonElement cacheRow, bool update, CancellationToken token)
    {
        await WithProviderConnectionAsync(
            async connection =>
            {
                await using DbTransaction transaction = await connection.BeginTransactionAsync(token);
                foreach (
                    CdcSourceTableKind table in new[]
                    {
                        CdcSourceTableKind.Document,
                        CdcSourceTableKind.DocumentCache,
                    }
                )
                {
                    await using DbCommand command = BuildRowCommand(connection, cacheRow, table, update);
                    command.Transaction = transaction;
                    (await command.ExecuteNonQueryAsync(token)).Should().Be(1);
                }
                await transaction.CommitAsync(token);
            },
            token
        );
    }

    public Task WriteCanonicalRowAsync(JsonElement cacheRow, CancellationToken token) =>
        WriteRowAsync(cacheRow, CdcSourceTableKind.Document, false, token);

    public Task WriteCacheRowAsync(JsonElement cacheRow, bool update, CancellationToken token) =>
        WriteRowAsync(cacheRow, CdcSourceTableKind.DocumentCache, update, token);

    private Task WriteRowAsync(
        JsonElement row,
        CdcSourceTableKind table,
        bool update,
        CancellationToken token
    ) =>
        WithProviderConnectionAsync(
            async connection =>
            {
                await using DbCommand command = BuildRowCommand(connection, row, table, update);
                (await command.ExecuteNonQueryAsync(token)).Should().Be(1);
            },
            token
        );

    private DbCommand BuildRowCommand(
        DbConnection connection,
        JsonElement row,
        CdcSourceTableKind kind,
        bool update
    )
    {
        CdcSourceTableInventory table = BuildRequiredSourceTableInventory(Provider)
            .Single(t => t.TableKind == kind);
        DbCommand command = connection.CreateCommand();
        foreach (CdcSourceColumnInventory column in table.Columns)
        {
            string name = column.ColumnName.Value;
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = name switch
            {
                "DocumentId" => row.GetProperty("documentId").GetInt64(),
                "DocumentUuid" => row.GetProperty("documentUuid").GetGuid(),
                "ResourceKeyId" => (short)1,
                "CreatedByOwnershipTokenId" => DBNull.Value,
                "ContentVersion" => row.GetProperty("contentVersion").GetInt64(),
                "LastModifiedAt" or "ContentLastModifiedAt" or "CreatedAt" or "ComputedAt" => DateTimeOffset
                    .Parse(row.GetProperty("lastModifiedAt").GetString()!, CultureInfo.InvariantCulture)
                    .UtcDateTime,
                "DocumentJson" => row.GetProperty("documentJson").GetRawText(),
                _ => row.GetProperty(char.ToLowerInvariant(name[0]) + name[1..]).GetString()!,
            };
            if (name == "CreatedByOwnershipTokenId")
            {
                parameter.DbType = DbType.Int16;
            }

            if (name is "LastModifiedAt" or "ContentLastModifiedAt" or "CreatedAt" or "ComputedAt")
            {
                parameter.DbType = Provider == CdcProvider.Postgresql ? DbType.DateTime : DbType.DateTime2;
            }

            if (name == "DocumentJson" && parameter is NpgsqlParameter jsonParameter)
            {
                jsonParameter.NpgsqlDbType = NpgsqlDbType.Jsonb;
            }

            command.Parameters.Add(parameter);
        }
        command.CommandText = update
            ? $"UPDATE {table.EmittedQuotedTableName} SET "
                + string.Join(
                    ", ",
                    table
                        .Columns.Where(c => c.ColumnName.Value != "DocumentId")
                        .Select(c => $"{c.EmittedQuotedColumnName} = @{c.ColumnName.Value}")
                )
                + $" WHERE {Quote("DocumentId")} = @DocumentId"
            : $"INSERT INTO {table.EmittedQuotedTableName} ("
                + string.Join(", ", table.Columns.Select(c => c.EmittedQuotedColumnName))
                + ") VALUES ("
                + string.Join(", ", table.Columns.Select(c => $"@{c.ColumnName.Value}"))
                + ")";
        return command;
    }

    public Task DeleteCanonicalRowAsync(long documentId, CancellationToken token) =>
        ExecuteProviderMutationAsync(
            $"DELETE FROM {Quote("dms")}.{Quote("Document")} WHERE {Quote("DocumentId")} = @DocumentId",
            documentId,
            token
        );

    public Task DeleteCacheRowAsync(long documentId, CancellationToken token) =>
        ExecuteProviderMutationAsync(
            $"DELETE FROM {Quote("dms")}.{Quote("DocumentCache")} WHERE {Quote("DocumentId")} = @DocumentId",
            documentId,
            token
        );

    public Task WriteProjectionWorkAsync(long documentId, CancellationToken token) =>
        ExecuteProviderMutationAsync(
            $"INSERT INTO {Quote("dms")}.{Quote("DocumentProjectionWork")} "
                + $"({Quote("DocumentId")}, {Quote("RequiredContentVersion")}, {Quote("FirstEnqueuedAt")}, {Quote("LastEnqueuedAt")}) "
                + $"VALUES (@DocumentId, 1, {CurrentTimestamp}, {CurrentTimestamp})",
            documentId,
            token
        );

    public Task AdvanceHeartbeatAsync(CancellationToken token) =>
        ExecuteProviderMutationAsync(
            $"UPDATE {Quote("dms")}.{Quote("CdcHeartbeat")} SET "
                + $"{Quote("HeartbeatSequence")} = {Quote("HeartbeatSequence")} + 1, {Quote("HeartbeatAt")} = {CurrentTimestamp} "
                + $"WHERE {Quote("HeartbeatId")} = 1",
            0,
            token
        );

    private string CurrentTimestamp =>
        Provider == CdcProvider.Postgresql ? "clock_timestamp()" : "SYSUTCDATETIME()";

    private string Quote(string identifier) =>
        Provider == CdcProvider.Postgresql ? $"\"{identifier}\"" : $"[{identifier}]";

    private Task ExecuteProviderMutationAsync(string sql, long documentId, CancellationToken token) =>
        WithProviderConnectionAsync(
            async connection =>
            {
                await using DbCommand command = connection.CreateCommand();
                command.CommandText = sql;
                DbParameter parameter = command.CreateParameter();
                parameter.ParameterName = "DocumentId";
                parameter.Value = documentId;
                command.Parameters.Add(parameter);
                (await command.ExecuteNonQueryAsync(token)).Should().Be(1);
            },
            token
        );

    private async Task WithProviderConnectionAsync(Func<DbConnection, Task> action, CancellationToken token)
    {
        try
        {
            int port = await ReadMappedProviderPortAsync(token);
            await using DbConnection connection = CreateProviderAdminConnection(port);
            await connection.OpenAsync(token);
            await action(connection);
        }
        catch (DbException)
        {
            throw new InvalidOperationException(
                "CDC message contract provider operation failed. Details redacted."
            );
        }
    }

    /// <summary>Provider setup already verifies all column types/ordinals/nullability against the inventory.
    /// These live catalog checks additionally verify the physical key layout, including the non-indexed UUID.</summary>
    public async Task AssertMessageContractSourceLayoutAsync(CancellationToken token)
    {
        await RunProviderSetupAsync(CdcProviderSetupMode.ValidateOnly, token);
        string sql =
            Provider == CdcProvider.Postgresql
                ? """
                    SELECT
                      (SELECT count(*) FROM pg_index i JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY(i.indkey)
                       WHERE i.indrelid = 'dms."DocumentCache"'::regclass AND a.attname = 'DocumentUuid'),
                      (SELECT count(*) FROM pg_constraint c WHERE c.conrelid = 'dms."DocumentCache"'::regclass
                       AND c.contype = 'p' AND pg_get_constraintdef(c.oid) = 'PRIMARY KEY ("DocumentId")'),
                      (SELECT count(*) FROM pg_constraint c WHERE c.conrelid = 'dms."DocumentCache"'::regclass
                       AND c.contype = 'f' AND c.confrelid = 'dms."Document"'::regclass
                       AND pg_get_constraintdef(c.oid) = 'FOREIGN KEY ("DocumentId") REFERENCES dms."Document"("DocumentId") ON DELETE CASCADE');
                    """
                : """
                    SELECT
                      (SELECT count(*) FROM sys.index_columns i JOIN sys.columns c ON c.object_id = i.object_id AND c.column_id = i.column_id
                       WHERE i.object_id = OBJECT_ID(N'dms.DocumentCache') AND c.name = N'DocumentUuid'),
                      (SELECT count(*) FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                       JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                       WHERE i.object_id = OBJECT_ID(N'dms.DocumentCache') AND i.is_primary_key = 1 AND c.name = N'DocumentId'),
                      (SELECT count(*) FROM sys.foreign_key_columns f JOIN sys.columns c ON c.object_id = f.parent_object_id AND c.column_id = f.parent_column_id
                       JOIN sys.columns r ON r.object_id = f.referenced_object_id AND r.column_id = f.referenced_column_id
                       WHERE f.parent_object_id = OBJECT_ID(N'dms.DocumentCache') AND f.referenced_object_id = OBJECT_ID(N'dms.Document')
                       AND c.name = N'DocumentId' AND r.name = N'DocumentId');
                    """;
        await WithProviderConnectionAsync(
            async connection =>
            {
                await using DbCommand command = connection.CreateCommand();
                command.CommandText = sql;
                await using DbDataReader reader = await command.ExecuteReaderAsync(token);
                (await reader.ReadAsync(token)).Should().BeTrue();
                Convert
                    .ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture)
                    .Should()
                    .Be(0, "cache UUID is a custom message key without an index");
                Convert
                    .ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture)
                    .Should()
                    .Be(1, "DocumentId is the cache primary key");
                Convert
                    .ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture)
                    .Should()
                    .Be(1, "cache DocumentId references canonical DocumentId");
            },
            token
        );
    }

    public async Task<MessageContractConnectorStatus> ReadConnectorStatusAsync(
        CdcConnectorTemplateRequest request,
        CancellationToken token
    )
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(
            $"/connectors/{Uri.EscapeDataString(request.ConnectorName.Value)}/status",
            token
        );
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("CDC connector status read failed. Details redacted.");
        }

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return new(
            ReadState(body.RootElement.GetProperty("connector")),
            body.RootElement.GetProperty("tasks").EnumerateArray().Select(ReadState).ToArray()
        );
    }

    private static string ReadState(JsonElement element) =>
        element.GetProperty("state").GetString() switch
        {
            "RUNNING" => "RUNNING",
            "FAILED" => "FAILED",
            "PAUSED" => "PAUSED",
            "UNASSIGNED" => "UNASSIGNED",
            "RESTARTING" => "RESTARTING",
            "STOPPED" => "STOPPED",
            _ => "UNKNOWN",
        };

    internal ConsumerConfig CreateByteConsumerConfig() =>
        new()
        {
            BootstrapServers = HostKafkaBootstrapServers,
            GroupId = $"{_resourcePrefix}-observer-{Guid.NewGuid():N}",
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            EnablePartitionEof = true,
            AutoOffsetReset = AutoOffsetReset.Error,
            AllowAutoCreateTopics = false,
            MaxPartitionFetchBytes = 70_000_000,
            FetchMaxBytes = 140_000_000,
        };

    private IConsumer<byte[], byte[]> CreateByteConsumer() =>
        new ConsumerBuilder<byte[], byte[]>(CreateByteConsumerConfig())
            .SetLogHandler((_, _) => { })
            .SetErrorHandler((_, _) => { })
            .Build();

    public Task<IReadOnlyList<MessageContractKafkaBoundary>> CaptureKafkaBoundariesAsync(
        string topic,
        CancellationToken token
    ) =>
        Task.Run(
            () =>
            {
                token.ThrowIfCancellationRequested();
                using IConsumer<byte[], byte[]> consumer = CreateByteConsumer();
                using IAdminClient admin = new AdminClientBuilder(
                    new AdminClientConfig { BootstrapServers = HostKafkaBootstrapServers }
                )
                    .SetLogHandler((_, _) => { })
                    .Build();
                try
                {
                    Metadata metadata = admin.GetMetadata(topic, TimeSpan.FromSeconds(10));
                    TopicMetadata found = metadata.Topics.Single(t => t.Topic == topic);
                    if (found.Error.IsError || found.Partitions.Count == 0)
                    {
                        throw new InvalidOperationException("CDC Kafka topic metadata unavailable.");
                    }

                    return (IReadOnlyList<MessageContractKafkaBoundary>)
                        found
                            .Partitions.OrderBy(p => p.PartitionId)
                            .Select(p =>
                            {
                                token.ThrowIfCancellationRequested();
                                WatermarkOffsets offsets = consumer.QueryWatermarkOffsets(
                                    new TopicPartition(topic, p.PartitionId),
                                    TimeSpan.FromSeconds(10)
                                );
                                return new MessageContractKafkaBoundary(
                                    topic,
                                    p.PartitionId,
                                    offsets.Low.Value,
                                    offsets.High.Value
                                );
                            })
                            .ToArray();
                }
                catch (KafkaException)
                {
                    throw new InvalidOperationException("CDC Kafka boundary read failed. Details redacted.");
                }
            },
            token
        );

    /// <summary>Reads exactly [start, captured exclusive end) on each partition. Completion requires actual
    /// consumer scan positions, including gaps in a compacted log. A timeout never proves absence.</summary>
    public Task<MessageContractKafkaScan> ConsumeThroughAsync(
        IReadOnlyList<MessageContractKafkaBoundary> boundaries,
        CancellationToken token
    ) =>
        Task.Run(
            () =>
            {
                ValidateBoundaries(boundaries);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                using IConsumer<byte[], byte[]> consumer = CreateByteConsumer();
                try
                {
                    var pending = boundaries.ToDictionary(b => new TopicPartition(b.Topic, b.Partition));
                    var positions = boundaries.ToDictionary(
                        b => new TopicPartition(b.Topic, b.Partition),
                        b => b.StartOffset
                    );
                    List<MessageContractKafkaRecord> records = [];
                    foreach (var (partition, bound) in pending)
                    {
                        timeout.Token.ThrowIfCancellationRequested();
                        WatermarkOffsets current = consumer.QueryWatermarkOffsets(
                            partition,
                            TimeSpan.FromSeconds(10)
                        );
                        if (current.Low.Value > bound.StartOffset || current.High.Value < bound.EndOffset)
                        {
                            throw new InvalidOperationException(
                                "CDC Kafka captured bounds are no longer retained."
                            );
                        }
                    }
                    consumer.Assign(
                        pending.Select(p => new TopicPartitionOffset(p.Key, new Offset(p.Value.StartOffset)))
                    );
                    while (pending.Count > 0)
                    {
                        timeout.Token.ThrowIfCancellationRequested();
                        foreach (var (partition, bound) in pending.ToArray())
                        {
                            long position = consumer.Position(partition).Value;
                            if (bound.StartOffset == bound.EndOffset || position >= bound.EndOffset)
                            {
                                positions[partition] = bound.EndOffset;
                                consumer.Pause([partition]);
                                pending.Remove(partition);
                            }
                        }
                        if (pending.Count == 0)
                        {
                            break;
                        }

                        ConsumeResult<byte[], byte[]> observed = consumer.Consume(timeout.Token);
                        if (
                            observed.IsPartitionEOF
                            || !pending.TryGetValue(observed.TopicPartition, out var boundForRecord)
                            || observed.Offset.Value >= boundForRecord.EndOffset
                        )
                        {
                            continue;
                        }

                        records.Add(
                            new(
                                observed.Topic,
                                observed.Partition.Value,
                                observed.Offset.Value,
                                MessageContractKafkaBytes.From(observed.Message.Key),
                                MessageContractKafkaBytes.From(observed.Message.Value),
                                observed
                                    .Message.Headers.Select(h => new MessageContractKafkaHeader(
                                        h.Key,
                                        MessageContractKafkaBytes.From(h.GetValueBytes())
                                    ))
                                    .ToArray(),
                                observed.Message.Timestamp.UnixTimestampMs
                            )
                        );
                    }
                    return new MessageContractKafkaScan(
                        records,
                        boundaries
                            .Select(b => new MessageContractKafkaBoundary(
                                b.Topic,
                                b.Partition,
                                b.StartOffset,
                                positions[new TopicPartition(b.Topic, b.Partition)]
                            ))
                            .ToArray()
                    );
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new TimeoutException("CDC Kafka scan did not reach every captured boundary.");
                }
                catch (KafkaException)
                {
                    throw new InvalidOperationException("CDC Kafka scan failed. Details redacted.");
                }
            },
            token
        );

    internal static void ValidateBoundaries(IReadOnlyList<MessageContractKafkaBoundary> boundaries)
    {
        if (
            boundaries.Count == 0
            || boundaries.Any(b =>
                b.Partition < 0
                || b.StartOffset < 0
                || b.EndOffset < b.StartOffset
                || string.IsNullOrWhiteSpace(b.Topic)
            )
            || boundaries.Select(b => (b.Topic, b.Partition)).Distinct().Count() != boundaries.Count
        )
        {
            throw new ArgumentException("Invalid CDC Kafka scan boundaries.");
        }
    }
}

internal sealed record MessageContractConnectorStatus(
    string ConnectorState,
    IReadOnlyList<string> TaskStates
);

internal sealed record MessageContractKafkaBoundary(
    string Topic,
    int Partition,
    long StartOffset,
    long EndOffset
);

internal sealed record MessageContractKafkaBytes(bool IsNull, byte[] Bytes)
{
    public static MessageContractKafkaBytes From(byte[] bytes) =>
        bytes is null ? new(true, []) : new(false, bytes.ToArray());

    public override string ToString() => IsNull ? "Kafka null" : $"{Bytes.Length} bytes";
}

internal sealed record MessageContractKafkaHeader(string Key, MessageContractKafkaBytes Value);

internal sealed record MessageContractKafkaRecord(
    string Topic,
    int Partition,
    long Offset,
    MessageContractKafkaBytes Key,
    MessageContractKafkaBytes Value,
    IReadOnlyList<MessageContractKafkaHeader> Headers,
    long BrokerTimestamp
)
{
    public override string ToString() =>
        $"Kafka record: partition {Partition}, offset {Offset}, value {Value}";
}

internal sealed record MessageContractKafkaScan(
    IReadOnlyList<MessageContractKafkaRecord> Records,
    IReadOnlyList<MessageContractKafkaBoundary> CompletedBoundaries
);
