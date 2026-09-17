// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[Category("CdcMessageContract")]
public abstract class Given_MessageContractKafkaFixture
{
    protected abstract CdcProvider Provider { get; }

    [Test]
    public async Task It_verifies_the_live_source_layout_and_exercises_shared_rows_and_bounded_byte_observations()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        CancellationToken token = cancellation.Token;
        await using CdcConnectorTemplatePinnedImageFixture fixture =
            await CdcConnectorTemplatePinnedImageFixture.StartAsync(Provider, token);
        CdcConnectorTemplateRequest request = await fixture.CreateRequestAsync(token);
        await fixture.AssertMessageContractSourceLayoutAsync(token);
        string providerName = Provider == CdcProvider.Postgresql ? "postgresql" : "sqlserver";
        MessageContractFixture[] rows = MessageContractFixtureCatalog
            .LoadAll(AppContext.BaseDirectory)
            .Where(f =>
                f.SourceRecord.GetProperty("provider").GetString() == providerName
                && f.SourceRecord.GetProperty("operation").GetString() == "c"
            )
            .DistinctBy(f => f.MaterializedCase)
            .ToArray();
        rows.Should().HaveCount(4);
        foreach (MessageContractFixture row in rows)
        {
            await fixture.WriteMaterializedRowAsync(row.CacheRow, update: false, token);
        }
        await fixture.WriteMaterializedRowAsync(rows[0].CacheRow, update: true, token);
        await fixture.WriteProjectionWorkAsync(rows[0].CacheRow.GetProperty("documentId").GetInt64(), token);
        await fixture.DeleteCacheRowAsync(rows[0].CacheRow.GetProperty("documentId").GetInt64(), token);
        await fixture.WriteCacheRowAsync(rows[0].CacheRow, update: false, token);
        await fixture.DeleteCanonicalRowAsync(rows[1].CacheRow.GetProperty("documentId").GetInt64(), token);
        await fixture.WriteCanonicalRowAsync(rows[1].CacheRow, token);
        await fixture.WriteCacheRowAsync(rows[1].CacheRow, update: false, token);

        CdcConnectorTemplateResult rendered = fixture.Render(request);
        await fixture.AssertRuntimeLoadsRequiredClassesAsync(rendered, token);
        await fixture.RegisterRenderedConnectorConfigDirectlyAsync(rendered, token);
        await fixture.AssertRegisteredConnectorReachesRunningStateAsync(request, token);
        await fixture.AdvanceHeartbeatAsync(token);
        var committed = await fixture.AssertHeartbeatAndCommittedOffsetProgressAsync(request, token);
        var readBack = await fixture.TryReadCommittedSourceOffsetAsync(request, token);
        readBack.Should().NotBeNull();
        CdcConnectorTemplatePinnedImageFixture
            .CommittedSourceOffsetRetainsOrAdvances(
                Provider,
                committed.CanonicalOffsetJson,
                readBack!.CanonicalOffsetJson
            )
            .Should()
            .BeTrue();
        MessageContractConnectorStatus status = await fixture.ReadConnectorStatusAsync(request, token);
        status.ConnectorState.Should().Be("RUNNING");
        status.TaskStates.Should().Equal("RUNNING");

        var publicBounds = await fixture.CaptureKafkaBoundariesAsync(request.PublicTopicName, token);
        MessageContractKafkaScan scan = await fixture.ConsumeThroughAsync(publicBounds, token);
        scan.CompletedBoundaries.Should().Equal(publicBounds);
        // SQL Server can replay capture rows after its snapshot. Harness qualification checks
        // availability of every shared key; provider ordering/snapshot contracts belong to MC-08/09.
        scan.Records.Count.Should().BeGreaterThanOrEqualTo(4);
        scan.Records.All(r => !r.Key.IsNull && r.Headers.Count == 0).Should().BeTrue();
        scan.Records.Where(r => !r.Value.IsNull)
            .Select(r => Encoding.UTF8.GetString(r.Key.Bytes))
            .Distinct()
            .Should()
            .BeEquivalentTo(rows.Select(r => r.CacheRow.GetProperty("documentUuid").GetString()));
        await fixture.RestartRegisteredConnectorAsync(request, token);
        await fixture.AssertRegisteredConnectorReachesRunningStateAsync(request, token);
        await AssertByteObserverAsync(fixture, token);
    }

    private static async Task AssertByteObserverAsync(
        CdcConnectorTemplatePinnedImageFixture fixture,
        CancellationToken token
    )
    {
        string topic = $"mc-harness-observer-{Guid.NewGuid():N}";
        using IAdminClient admin = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = fixture.HostKafkaBootstrapServers }
        ).Build();
        await admin.CreateTopicsAsync([
            new TopicSpecification
            {
                Name = topic,
                NumPartitions = 3,
                ReplicationFactor = 1,
            },
        ]);
        using IProducer<byte[], byte[]> producer = new ProducerBuilder<byte[], byte[]>(
            new ProducerConfig
            {
                BootstrapServers = fixture.HostKafkaBootstrapServers,
                MessageTimeoutMs = 10000,
            }
        ).Build();
        byte[][] values = [null!, [], Encoding.UTF8.GetBytes("null"), [0, 255, 128, 13, 10]];
        for (int index = 0; index < values.Length; index++)
        {
            await producer.ProduceAsync(
                new TopicPartition(topic, index % 2),
                new Message<byte[], byte[]>
                {
                    Key = [(byte)index],
                    Value = values[index],
                    Headers = new Headers
                    {
                        { "duplicate", null! },
                        { "duplicate", [] },
                        { "binary", [255, 0, 128] },
                    },
                },
                token
            );
        }
        var bounds = await fixture.CaptureKafkaBoundariesAsync(topic, token);
        bounds.Should().HaveCount(3);
        bounds.Single(b => b.Partition == 2).EndOffset.Should().Be(0);
        await producer.ProduceAsync(
            new TopicPartition(topic, 0),
            new Message<byte[], byte[]> { Key = [9], Value = [9] },
            token
        );
        MessageContractKafkaScan scan = await fixture.ConsumeThroughAsync(bounds, token);
        scan.CompletedBoundaries.Should().Equal(bounds);
        scan.Records.Should().HaveCount(4, "the late record lies beyond the frozen exclusive boundary");
        foreach (MessageContractKafkaRecord record in scan.Records)
        {
            int index = record.Key.Bytes.Single();
            record.Partition.Should().Be(index % 2);
            record.Offset.Should().Be(index / 2);
            record.Value.IsNull.Should().Be(index == 0);
            record.Value.Bytes.Should().Equal(values[index] ?? []);
            record.Headers.Select(h => h.Key).Should().Equal("duplicate", "duplicate", "binary");
            record.Headers[0].Value.IsNull.Should().BeTrue();
            record.Headers[1].Value.IsNull.Should().BeFalse();
            record.Headers[1].Value.Bytes.Should().BeEmpty();
            record.Headers[2].Value.Bytes.Should().Equal(255, 0, 128);
            record.BrokerTimestamp.Should().BeGreaterThan(0);
        }
        var empty = bounds.Select(b => b with { StartOffset = b.EndOffset }).ToArray();
        (await fixture.ConsumeThroughAsync(empty, token)).Records.Should().BeEmpty();
        Func<Task> invalid = () =>
            fixture.ConsumeThroughAsync([bounds[0] with { EndOffset = long.MaxValue }], token);
        await invalid
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*bounds are no longer retained*");
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        Func<Task> interrupted = () => fixture.ConsumeThroughAsync(bounds, canceled.Token);
        await interrupted.Should().ThrowAsync<OperationCanceledException>();
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("CdcMessageContractKafka")]
[Category("PostgresqlIntegration")]
[Property("ScenarioId", "MC-KAFKA-HARNESS-PG")]
public sealed class Given_MessageContractKafkaFixturePostgresql : Given_MessageContractKafkaFixture
{
    protected override CdcProvider Provider => CdcProvider.Postgresql;
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("CdcMessageContractKafka")]
[Category("MssqlIntegration")]
[Property("ScenarioId", "MC-KAFKA-HARNESS-SQL")]
public sealed class Given_MessageContractKafkaFixtureSqlServer : Given_MessageContractKafkaFixture
{
    protected override CdcProvider Provider => CdcProvider.SqlServer;
}

[TestFixture]
[Category("CdcMessageContract")]
public sealed class Given_MessageContractKafkaBoundaries
{
    [TestCase(-1, 0, 0)]
    [TestCase(0, -1, 0)]
    [TestCase(0, 2, 1)]
    public void It_rejects_invalid_bounds(int partition, long start, long end)
    {
        Action act = () =>
            CdcConnectorTemplatePinnedImageFixture.ValidateBoundaries([new("topic", partition, start, end)]);
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_rejects_duplicate_partitions_and_empty_assignments()
    {
        MessageContractKafkaBoundary bound = new("topic", 0, 0, 1);
        Action duplicate = () => CdcConnectorTemplatePinnedImageFixture.ValidateBoundaries([bound, bound]);
        Action empty = () => CdcConnectorTemplatePinnedImageFixture.ValidateBoundaries([]);
        duplicate.Should().Throw<ArgumentException>();
        empty.Should().Throw<ArgumentException>();
    }
}
