// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Confluent.Kafka;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Npgsql;
using NpgsqlTypes;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Cdc.Tests.Integration.CdcProviderAdmissionFixture;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

public sealed partial class Given_Cdc_Controller_Record_Size_Increase
{
    [Test]
    public async Task It_replays_the_uncommitted_over_budget_materialized_record_after_capacity_alignment()
    {
        var before = Observed(
            await _fixture.Infrastructure.Connect.ReadOffsetEvidenceAsync(_fixture.Request, Token)
        );
        before.State.Should().Be(CdcConnectOffsetState.Streaming);
        long high = PublicHighWatermark();
        // Reuse the E18/19-05 materialized-row boundary. Synthetic padding is intentionally far
        // from the producer threshold; exact serialization/framing conformance belongs to 19-05.
        await WriteSizedMaterializedRowAsync();
        await CdcControllerFixture.WaitAsync(
            async ct =>
            {
                var status = Observed(
                    await _fixture.Infrastructure.Connect.ReadStatusAsync(_fixture.Request, ct)
                );
                return status.Tasks.Any(t => t.State == CoreCdc.CdcConnectorRuntimeState.Failed);
            },
            TimeSpan.FromSeconds(90),
            TimeSpan.FromMilliseconds(250),
            Token
        );
        using var http = new HttpClient { BaseAddress = _fixture.Infrastructure.ConnectEndpoint };
        using var statusBody = JsonDocument.Parse(
            await http.GetStringAsync(
                "/connectors/" + _fixture.Request.Binding.ConnectorName + "/status",
                Token
            )
        );
        bool sizeFailure = statusBody
            .RootElement.GetProperty("tasks")
            .EnumerateArray()
            .Any(t =>
                t.TryGetProperty("trace", out var trace)
                && (trace.GetString() ?? "").Contains(
                    "org.apache.kafka.common.errors.RecordTooLargeException:",
                    StringComparison.Ordinal
                )
            );
        var failureTypes = statusBody
            .RootElement.GetProperty("tasks")
            .EnumerateArray()
            .Where(t => t.TryGetProperty("trace", out _))
            .SelectMany(t =>
                Regex
                    .Matches(
                        t.GetProperty("trace").GetString()!,
                        @"(?:[a-zA-Z_$][a-zA-Z0-9_$]*\.)+[A-Za-z_$][A-Za-z0-9_$]*(?:Exception|Error)(?=:)"
                    )
                    .Select(m => m.Value)
            )
            .Distinct()
            .ToArray();
        _evidence.Add(new { FailureTypes = failureTypes });
        await TestContext.Progress.WriteLineAsync(
            "Producer failure classes: " + string.Join(", ", failureTypes)
        );
        sizeFailure.Should().BeTrue("only a real producer RecordTooLargeException qualifies this recovery");
        PublicHighWatermark().Should().Be(high);
        var failedOffset = Observed(
            await _fixture.Infrastructure.Connect.ReadOffsetEvidenceAsync(_fixture.Request, Token)
        );
        failedOffset.State.Should().Be(CdcConnectOffsetState.Streaming);
        _evidence.Add(
            new
            {
                Failure = "RecordTooLargeException",
                PublicHighWatermark = high,
                OffsetBefore = Position(before),
                OffsetAtFailure = Position(failedOffset),
            }
        );
        var result = await IncreaseAsync();
        _evidence.Add(result);
        result.Succeeded.Should().BeTrue("{0}", JsonSerializer.Serialize(result));
        result.Ready.Should().BeTrue();
        var limits = await CaptureLimitsAsync("recovered-producer");
        limits.Request.Should().Be(Ceiling);
        using var consumer = new ConsumerBuilder<byte[], byte[]>(
            new ConsumerConfig
            {
                BootstrapServers = _fixture.Infrastructure.Resources.ControllerKafkaBootstrapServers,
                GroupId = "t36-recovery-observation",
                EnableAutoCommit = false,
                MaxPartitionFetchBytes = Ceiling * 2,
                FetchMaxBytes = Ceiling * 2,
            }
        ).SetLogHandler((_, _) => { }).SetErrorHandler((_, _) => { }).Build();
        consumer.Assign(new TopicPartitionOffset(_fixture.Request.Binding.TopicName, 0, high));
        var record = consumer.Consume(TimeSpan.FromSeconds(60));
        record.Should().NotBeNull();
        Encoding.UTF8.GetString(record!.Message.Key).Should().Be("aaaaaaaa-bbbb-cccc-dddd-000000000301");
        record
            .Message.Value.Length.Should()
            .BeGreaterThan(_scope.PreviousMaxRecordBytes)
            .And.BeLessThan(Ceiling);
        record.Partition.Value.Should().Be(0);
        using var value = JsonDocument.Parse(record.Message.Value);
        value
            .RootElement.GetProperty("document")
            .GetProperty("syntheticRecordSizePadding")
            .GetString()!
            .Length.Should()
            .Be(1_500_000);
        await CdcControllerFixture.WaitAsync(
            async ct =>
            {
                var current = Observed(
                    await _fixture.Infrastructure.Connect.ReadOffsetEvidenceAsync(_fixture.Request, ct)
                );
                return current.State == CdcConnectOffsetState.Streaming
                    && Position(current) != Position(failedOffset);
            },
            TimeSpan.FromSeconds(60),
            TimeSpan.FromMilliseconds(250),
            Token
        );
        var recovered = Observed(
            await _fixture.Infrastructure.Connect.ReadOffsetEvidenceAsync(_fixture.Request, Token)
        );
        _evidence.Add(
            new
            {
                PublishedBytes = record.Message.Value.Length,
                Partition = record.Partition.Value,
                KafkaOffset = record.Offset.Value,
                OffsetRecovered = Position(recovered),
                SameBinding = true,
            }
        );
        (await _fixture.Infrastructure.Bindings.ExactMatchBindingAsync(_fixture.Request.Binding, Token))
            .Status.Should()
            .Be(CoreCdc.CdcControlPlaneOperationStatus.Succeeded);
        AssertNoOffsetReset();
    }

    private static string Position(CdcConnectOffsetEvidence evidence) =>
        JsonSerializer.Serialize(new { evidence.Postgresql, evidence.SqlServer });

    private long PublicHighWatermark()
    {
        using var consumer = new ConsumerBuilder<Ignore, byte[]>(
            new ConsumerConfig
            {
                BootstrapServers = _fixture.Infrastructure.Resources.ControllerKafkaBootstrapServers,
                GroupId = "t36-watermark-observation",
                EnableAutoCommit = false,
            }
        ).SetLogHandler((_, _) => { }).SetErrorHandler((_, _) => { }).Build();
        return consumer
            .QueryWatermarkOffsets(new(_fixture.Request.Binding.TopicName, 0), TimeSpan.FromSeconds(10))
            .High.Value;
    }

    private async Task WriteSizedMaterializedRowAsync()
    {
        var row = JsonNode.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "Fixtures/record-size-cache-row.json"),
                Token
            )
        )!;
        row["documentJson"]!["syntheticRecordSizePadding"] = new string('x', 1_500_000);
        // Own ordinary emitted source tables and preserve the document/cache UUID and version invariants.
        // The shared materialized row supplies the cache body; this is not an API/projector conformance test.
        await using DbConnection connection =
            provider == CdcProvider.Postgresql
                ? new NpgsqlConnection(_fixture.ConnectionString)
                : new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync(Token);
        if (provider == CdcProvider.SqlServer)
        {
            // Admit the synthetic NVARCHAR LOB to source capture before testing Kafka's independent
            // byte budget. This is owner preparation of the isolated SQL Server, not a rollout effect.
            await using var configure = connection.CreateCommand();
            configure.CommandText = """
                DECLARE @advanced int = (SELECT CONVERT(int, value_in_use) FROM sys.configurations WHERE name = N'show advanced options');
                EXEC sys.sp_configure N'show advanced options', 1;
                RECONFIGURE;
                EXEC sys.sp_configure N'max text repl size', 4000000;
                RECONFIGURE;
                EXEC sys.sp_configure N'show advanced options', @advanced;
                RECONFIGURE;
                """;
            await configure.ExecuteNonQueryAsync(Token);
            configure.CommandText =
                "SELECT CONVERT(int, value_in_use) FROM sys.configurations WHERE name = N'max text repl size (B)'";
            int sourceLimit = Convert.ToInt32(
                await configure.ExecuteScalarAsync(Token),
                CultureInfo.InvariantCulture
            );
            sourceLimit.Should().Be(Ceiling * 2);
            _evidence.Add(new { SourceMaxTextReplicationBytes = sourceLimit });
        }
        await using var transaction = await connection.BeginTransactionAsync(Token);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            provider == CdcProvider.Postgresql
                ? "INSERT INTO dms.\"Document\" (\"DocumentId\", \"DocumentUuid\", \"ResourceKeyId\", \"ContentLastModifiedAt\") OVERRIDING SYSTEM VALUE SELECT 970301, @uuid, \"ResourceKeyId\", @modified FROM dms.\"ResourceKey\" LIMIT 1;"
                : "SET IDENTITY_INSERT dms.Document ON; INSERT INTO dms.Document (DocumentId, DocumentUuid, ResourceKeyId, ContentLastModifiedAt) SELECT TOP (1) 970301, @uuid, ResourceKeyId, @modified FROM dms.ResourceKey; SET IDENTITY_INSERT dms.Document OFF;";
        Add("uuid", row["documentUuid"]!.GetValue<Guid>(), DbType.Guid);
        Add(
            "modified",
            DateTimeOffset
                .Parse(row["lastModifiedAt"]!.GetValue<string>(), CultureInfo.InvariantCulture)
                .UtcDateTime,
            provider == CdcProvider.Postgresql ? DbType.DateTime : DbType.DateTime2
        );
        await command.ExecuteNonQueryAsync(Token);
        command.CommandText =
            "INSERT INTO dms.\"DocumentCache\" (\"DocumentId\", \"DocumentUuid\", \"ProjectName\", \"ResourceName\", \"ResourceVersion\", \"ContentVersion\", \"StreamEtag\", \"LastModifiedAt\", \"DocumentJson\") SELECT \"DocumentId\", \"DocumentUuid\", @project, @resource, @version, \"ContentVersion\", @etag, \"ContentLastModifiedAt\", @json FROM dms.\"Document\" WHERE \"DocumentId\" = 970301";
        Add("project", row["projectName"]!.GetValue<string>(), DbType.String);
        Add("resource", row["resourceName"]!.GetValue<string>(), DbType.String);
        Add("version", row["resourceVersion"]!.GetValue<string>(), DbType.String);
        Add("etag", row["streamEtag"]!.GetValue<string>(), DbType.String);
        Add("json", row["documentJson"]!.ToJsonString(), DbType.String);
        if (command.Parameters["json"] is NpgsqlParameter json)
        {
            json.NpgsqlDbType = NpgsqlDbType.Jsonb;
        }
        (await command.ExecuteNonQueryAsync(Token)).Should().Be(1);
        await transaction.CommitAsync(Token);

        void Add(string name, object value, DbType type)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.DbType = type;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
    }
}
