// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

// The PostgreSQL fixture hosts the isolated broker/topic. Records below are synthetic public
// envelopes derived from E18, produced directly to Kafka; this is provider-neutral consumer evidence.
[TestFixture]
[Category("DatabaseIntegration")]
[Category("CdcMessageContract")]
[Category("CdcMessageContractKafka")]
[Category("PostgresqlIntegration")]
public sealed class Given_MessageContractConsumerBroker
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);
    private readonly Dictionary<string, Snapshot> _states = [];
    private readonly Dictionary<string, MessageContractKafkaScan> _scans = [];
    private readonly List<MessageContractConsumerApplyResult> _bootstrapResults = [];
    private readonly List<IReadOnlyList<MessageContractKafkaBoundary>> _observations = [];
    private MessageContractFixture[] _rows = [];
    private JsonElement[] _higher = [];
    private JsonElement _continued;
    private MessageContractConsumerBootstrap _consumer = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        CancellationToken token = timeout.Token;
        await using CdcConnectorTemplatePinnedImageFixture fixture =
            await CdcConnectorTemplatePinnedImageFixture.StartAsync(CdcProvider.Postgresql, token);
        CdcConnectorTemplateRequest request = await fixture.CreateRequestAsync(token, partitionCount: 3);
        await AssertTopicAndFetchPolicyAsync(fixture, request, token);
        _rows = MessageContractFixtureCatalog
            .LoadAll(AppContext.BaseDirectory)
            .Where(f =>
                f.SourceRecord.GetProperty("provider").GetString() == "postgresql"
                && f.SourceRecord.GetProperty("operation").GetString() == "c"
            )
            .DistinctBy(f => f.MaterializedCase)
            .ToArray();
        _rows.Should().HaveCount(4);
        _rows.Select(r => Uuid(r.ExpectedEnvelope)).Should().OnlyHaveUniqueItems();
        _higher = _rows.Select(r => WithHigherVersion(r.ExpectedEnvelope, 100)).ToArray();
        _continued = WithHigherVersion(_rows[0].ExpectedEnvelope, 200);

        using IProducer<byte[], byte[]> producer = new ProducerBuilder<byte[], byte[]>(
            new ProducerConfig
            {
                BootstrapServers = fixture.HostKafkaBootstrapServers,
                EnableIdempotence = true,
                Acks = Acks.All,
                CompressionType = CompressionType.None,
                MessageTimeoutMs = 10000,
            }
        ).SetLogHandler((_, _) => { }).Build();
        for (int partition = 0; partition < 2; partition++)
        {
            int live = partition * 2;
            int deleted = live + 1;
            await ProduceAsync(partition, _rows[live].ExpectedEnvelope);
            await ProduceAsync(partition, _higher[live]);
            await ProduceAsync(partition, _rows[live].ExpectedEnvelope); // Lower-version replay.
            await ProduceAsync(partition, _higher[live]); // Identical duplicate bytes.
            await ProduceAsync(partition, _rows[deleted].ExpectedEnvelope);
            await ProduceAsync(partition, _rows[deleted].ExpectedEnvelope, delete: true);
        }

        // The synchronous harness hook observes real broker watermarks on every restart.
        // It deliberately does not reuse the earlier capture or fabricate empty-partition positions.
        _consumer = new(
            Epoch,
            () =>
            {
                var bounds = fixture
                    .CaptureKafkaBoundariesAsync(request.PublicTopicName, token)
                    .GetAwaiter()
                    .GetResult();
                _observations.Add(bounds);
                return bounds
                    .Select(b => new MessageContractPartitionBounds(b.Partition, b.StartOffset, b.EndOffset))
                    .ToArray();
            }
        );
        IReadOnlyList<MessageContractKafkaBoundary> initial = _observations.Single();
        var handles = initial.ToDictionary(b => b.Partition, b => _consumer.StartPartitionScan(b.Partition));
        _scans["BOOTSTRAP"] = await fixture.ConsumeThroughAsync(initial, token);
        Save("READ"); // Transport has read every record; durable state has applied none.
        MessageContractKafkaRecord delayed = _scans["BOOTSTRAP"]
            .Records.Where(r => r.Partition == 1)
            .MaxBy(r => r.Offset)!;
        delayed.Value.IsNull.Should().BeTrue();
        foreach (MessageContractKafkaRecord record in _scans["BOOTSTRAP"].Records)
        {
            _consumer.Stage(handles[record.Partition], ToConsumerRecord(record));
            if (record == delayed)
            {
                continue;
            }
            _bootstrapResults.Add(_consumer.CompleteApply(handles[record.Partition]));
        }
        CompletePartition(handles[0], _scans["BOOTSTRAP"]);
        _consumer.CompleteScan(handles[2], End(_scans["BOOTSTRAP"], 2));
        Save("PENDING-APPLY");
        Action prematureCheckpoint = () =>
            _consumer.CompleteCheckpoint(handles[1], End(_scans["BOOTSTRAP"], 1));
        Action prematureScan = () => _consumer.CompleteScan(handles[1], End(_scans["BOOTSTRAP"], 1));
        prematureCheckpoint.Should().Throw<InvalidOperationException>();
        prematureScan.Should().Throw<InvalidOperationException>();
        _consumer.AdvanceTime(TimeSpan.FromHours(2));
        _bootstrapResults.Add(_consumer.CompleteApply(handles[1]));
        Save("APPLIED");
        CompletePartition(handles[1], _scans["BOOTSTRAP"]);
        Save("ACTIVE-CHECKPOINTS");
        _consumer.CompleteCheckpoint(handles[2], End(_scans["BOOTSTRAP"], 2));
        Save("BOOTSTRAP");

        _consumer.AdvanceTime(TimeSpan.FromHours(23));
        var idle = await fixture.CaptureKafkaBoundariesAsync(request.PublicTopicName, token);
        _consumer.BeginRenewal(idle.ToDictionary(b => b.Partition, b => b.EndOffset));
        _scans["IDLE"] = await ScanFromDurableAsync(fixture, idle, token);
        Save("IDLE-READ");
        foreach (var bound in _scans["IDLE"].CompletedBoundaries)
        {
            var handle = _consumer.StartPartitionScan(bound.Partition);
            _consumer.CompleteScan(handle, bound.EndOffset);
            if (bound.Partition != 2)
            {
                _consumer.CompleteCheckpoint(handle, bound.EndOffset);
            }
        }
        Save("IDLE-PENDING-CHECKPOINT");
        _consumer.CompleteCheckpoint(_consumer.StartPartitionScan(2), End(_scans["IDLE"], 2));
        Save("IDLE");

        await ProduceAsync(0, _continued);
        await ProduceAsync(1, _higher[2], delete: true);
        var continuation = await fixture.CaptureKafkaBoundariesAsync(request.PublicTopicName, token);
        _consumer.BeginRenewal(continuation.ToDictionary(b => b.Partition, b => b.EndOffset));
        _scans["CONTINUATION"] = await ScanFromDurableAsync(fixture, continuation, token);
        ApplyAndCheckpoint(_scans["CONTINUATION"]);
        Save("CONTINUATION");

        foreach (string fault in new[] { "MISSING", "CORRUPT" })
        {
            var staleHandle = _consumer.StartPartitionScan(0);
            if (fault == "MISSING")
            {
                _consumer.LoseCheckpoints();
            }
            else
            {
                _consumer.CorruptCheckpoints();
            }
            Save($"{fault}-INVALIDATED");
            Action stale = () => _consumer.CompleteCheckpoint(staleHandle, staleHandle.NextOffset);
            stale.Should().Throw<InvalidOperationException>();
            _scans[fault] = await ScanFromDurableAsync(fixture, _observations[^1], token);
            ApplyAndCheckpoint(_scans[fault]);
            Save(fault);
        }
        await RetainEvidenceAsync(token);

        async Task ProduceAsync(int partition, JsonElement envelope, bool delete = false)
        {
            try
            {
                var delivery = await producer.ProduceAsync(
                    new TopicPartition(request.PublicTopicName, partition),
                    new Message<byte[], byte[]>
                    {
                        Key = Encoding.UTF8.GetBytes(Uuid(envelope)),
                        Value = delete ? null! : JsonSerializer.SerializeToUtf8Bytes(envelope),
                    },
                    token
                );
                delivery.Status.Should().Be(PersistenceStatus.Persisted);
            }
            catch (KafkaException)
            {
                throw new InvalidOperationException(
                    "Consumer contract record publication failed. Details redacted."
                );
            }
        }
    }

    [Test]
    [Property("ScenarioId", "MC-CONSUMER-BROKER-BOOTSTRAP-DURABILITY")]
    public void It_requires_durable_application_and_every_checkpoint_including_the_empty_partition()
    {
        _observations[0].Select(b => b.Partition).Should().Equal(0, 1, 2);
        _observations[0].Single(b => b.Partition == 2).EndOffset.Should().Be(0);
        _states["READ"].Documents.Should().BeEmpty();
        _states["READ"].Checkpoints.Should().BeEmpty();
        _states["READ"]
            .NextOffsets.Should()
            .BeEquivalentTo(_observations[0].ToDictionary(b => b.Partition, b => b.StartOffset));
        foreach (string phase in new[] { "READ", "PENDING-APPLY", "APPLIED", "ACTIVE-CHECKPOINTS" })
        {
            _states[phase].Valid.Should().BeFalse(phase);
        }
        _states["PENDING-APPLY"].Documents.ContainsKey(Uuid(_rows[3].ExpectedEnvelope)).Should().BeTrue();
        _states["APPLIED"].Documents.ContainsKey(Uuid(_rows[3].ExpectedEnvelope)).Should().BeFalse();
        _states["ACTIVE-CHECKPOINTS"].Checkpoints.Keys.Should().BeEquivalentTo([0, 1]);
        _states["BOOTSTRAP"].Valid.Should().BeTrue();
        _states["BOOTSTRAP"].ProofCompletedAt.Should().Be(Epoch.AddHours(2));
        AssertDurableEnds("BOOTSTRAP");
    }

    [Test]
    [Property("ScenarioId", "MC-CONSUMER-BROKER-ORDERING")]
    public void It_reconstructs_independently_expected_state_from_higher_lower_duplicate_and_null_records()
    {
        _scans["BOOTSTRAP"].Records.Should().HaveCount(12);
        _scans["BOOTSTRAP"].Records.Count(r => r.Value.IsNull).Should().Be(2);
        foreach (var group in _scans["BOOTSTRAP"].Records.GroupBy(r => Encoding.UTF8.GetString(r.Key.Bytes)))
        {
            group.Select(r => r.Partition).Distinct().Should().ContainSingle();
            group.Select(r => r.Offset).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        }
        _bootstrapResults.Count(r => r == MessageContractConsumerApplyResult.Inserted).Should().Be(4);
        foreach (
            var result in new[]
            {
                MessageContractConsumerApplyResult.Replaced,
                MessageContractConsumerApplyResult.Stale,
                MessageContractConsumerApplyResult.Duplicate,
                MessageContractConsumerApplyResult.Deleted,
            }
        )
        {
            _bootstrapResults.Count(r => r == result).Should().Be(2);
        }
        AssertDocuments("BOOTSTRAP", _higher[0], _higher[2]);
    }

    [Test]
    [Property("ScenarioId", "MC-CONSUMER-BROKER-IDLE-RENEWAL")]
    public void It_renews_an_idle_proof_from_real_unchanged_ends_only_after_all_checkpoints_complete()
    {
        _scans["IDLE"].Records.Should().BeEmpty();
        foreach (var bound in _scans["IDLE"].CompletedBoundaries)
        {
            bound.StartOffset.Should().Be(bound.EndOffset);
            bound.EndOffset.Should().Be(End(_scans["BOOTSTRAP"], bound.Partition));
        }
        foreach (string phase in new[] { "IDLE-READ", "IDLE-PENDING-CHECKPOINT" })
        {
            _states[phase].Valid.Should().BeTrue();
            _states[phase].RenewalInProgress.Should().BeTrue();
            _states[phase].ProofCompletedAt.Should().Be(Epoch.AddHours(2));
        }
        _states["IDLE"].ProofCompletedAt.Should().Be(Epoch.AddHours(25));
        _states["IDLE"].RenewalInProgress.Should().BeFalse();
        _states["IDLE"].Attempt.Should().Be(1);
        AssertDocuments("IDLE", _higher[0], _higher[2]);
        AssertDurableEnds("IDLE");
    }

    [Test]
    [Property("ScenarioId", "MC-CONSUMER-BROKER-CONTINUATION")]
    public void It_continues_from_durable_next_offsets_without_replaying_bootstrap()
    {
        _scans["CONTINUATION"].Records.Should().HaveCount(2);
        foreach (var bound in _scans["CONTINUATION"].CompletedBoundaries)
        {
            bound.StartOffset.Should().Be(_states["BOOTSTRAP"].Checkpoints[bound.Partition]);
        }
        foreach (var record in _scans["CONTINUATION"].Records)
        {
            record.Offset.Should().Be(_states["BOOTSTRAP"].NextOffsets[record.Partition]);
        }
        _states["CONTINUATION"].Attempt.Should().Be(1);
        _states["CONTINUATION"].Valid.Should().BeTrue();
        AssertDocuments("CONTINUATION", _continued);
        AssertDurableEnds("CONTINUATION");
    }

    [Test]
    [Property("ScenarioId", "MC-CONSUMER-BROKER-CHECKPOINT-MISSING")]
    public void It_reconstructs_from_earliest_after_checkpoint_loss() => AssertReconstruction("MISSING", 2);

    [Test]
    [Property("ScenarioId", "MC-CONSUMER-BROKER-CHECKPOINT-CORRUPT")]
    public void It_reconstructs_from_earliest_after_checkpoint_corruption() =>
        AssertReconstruction("CORRUPT", 3);

    private void AssertReconstruction(string fault, int attempt)
    {
        Snapshot invalidated = _states[$"{fault}-INVALIDATED"];
        invalidated.Valid.Should().BeFalse();
        invalidated.Documents.Should().BeEmpty();
        invalidated.Checkpoints.Should().BeEmpty();
        invalidated.Attempt.Should().Be(attempt);
        var fresh = _observations[attempt - 1];
        _scans[fault].CompletedBoundaries.Should().Equal(fresh);
        invalidated
            .NextOffsets.Should()
            .BeEquivalentTo(fresh.ToDictionary(b => b.Partition, b => b.StartOffset));
        _scans[fault].Records.Should().HaveCount(14);
        _states[fault].Valid.Should().BeTrue();
        AssertDocuments(fault, _continued);
        AssertDurableEnds(fault);
    }

    private async Task<MessageContractKafkaScan> ScanFromDurableAsync(
        CdcConnectorTemplatePinnedImageFixture fixture,
        IReadOnlyList<MessageContractKafkaBoundary> ends,
        CancellationToken token
    )
    {
        var starts = ends.Select(b =>
                b with
                {
                    StartOffset = _consumer.StartPartitionScan(b.Partition).NextOffset,
                }
            )
            .ToArray();
        return await fixture.ConsumeThroughAsync(starts, token);
    }

    private void ApplyAndCheckpoint(MessageContractKafkaScan scan)
    {
        var handles = scan.CompletedBoundaries.ToDictionary(
            b => b.Partition,
            b => _consumer.StartPartitionScan(b.Partition)
        );
        foreach (var record in scan.Records)
        {
            var handle = handles[record.Partition];
            _consumer.Stage(handle, ToConsumerRecord(record));
            _consumer.CompleteApply(handle);
        }
        foreach (var handle in handles.Values)
        {
            CompletePartition(handle, scan);
        }
    }

    private void CompletePartition(MessageContractConsumerScan handle, MessageContractKafkaScan scan)
    {
        long end = End(scan, handle.Partition);
        _consumer.CompleteScan(handle, end);
        _consumer.CompleteCheckpoint(handle, end);
    }

    private static long End(MessageContractKafkaScan scan, int partition) =>
        scan.CompletedBoundaries.Single(b => b.Partition == partition).EndOffset;

    private static MessageContractConsumerRecord ToConsumerRecord(MessageContractKafkaRecord record)
    {
        record.Key.IsNull.Should().BeFalse();
        return new(
            record.Partition,
            record.Offset,
            record.Key.Bytes,
            record.Value.Bytes,
            record.Value.IsNull
        );
    }

    private void Save(string phase) =>
        _states.Add(
            phase,
            new(
                _consumer.IsValid,
                _consumer.RenewalInProgress,
                _consumer.Attempt,
                _consumer.ProofCompletedAt,
                _consumer.DurableNextOffsets.ToDictionary(),
                _consumer.Checkpoints.ToDictionary(),
                _consumer.Documents.ToDictionary()
            )
        );

    private void AssertDocuments(string phase, params JsonElement[] expected)
    {
        var documents = _states[phase].Documents;
        documents.Keys.Should().BeEquivalentTo(expected.Select(Uuid));
        foreach (var envelope in expected)
        {
            MessageContractJson.ShouldEqual(documents[Uuid(envelope)].Envelope, envelope);
        }
    }

    private void AssertDurableEnds(string phase)
    {
        var ends = _scans[phase].CompletedBoundaries.ToDictionary(b => b.Partition, b => b.EndOffset);
        _states[phase].NextOffsets.Should().BeEquivalentTo(ends);
        _states[phase].Checkpoints.Should().BeEquivalentTo(ends);
    }

    private static string Uuid(JsonElement envelope) => envelope.GetProperty("documentUuid").GetString()!;

    private static JsonElement WithHigherVersion(JsonElement envelope, long increment)
    {
        JsonNode value = JsonNode.Parse(envelope.GetRawText())!;
        value["contentVersion"] = envelope.GetProperty("contentVersion").GetInt64() + increment;
        // Synthetic public fixture variant, with an opaque validator copied by the consumer.
        value["document"]!["_etag"] = $"opaque-consumer-broker-{increment}";
        return JsonSerializer.SerializeToElement(value);
    }

    private static async Task AssertTopicAndFetchPolicyAsync(
        CdcConnectorTemplatePinnedImageFixture fixture,
        CdcConnectorTemplateRequest request,
        CancellationToken token
    )
    {
        using IAdminClient admin = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = fixture.HostKafkaBootstrapServers }
        )
            .SetLogHandler((_, _) => { })
            .Build();
        var configs = await admin
            .DescribeConfigsAsync(
                [new ConfigResource { Type = ResourceType.Topic, Name = request.PublicTopicName }],
                new DescribeConfigsOptions { RequestTimeout = TimeSpan.FromSeconds(10) }
            )
            .WaitAsync(token);
        var entries = configs.Single().Entries;
        entries["cleanup.policy"].Value.Should().Be("compact");
        entries["delete.retention.ms"].Source.Should().Be(ConfigSource.DynamicTopicConfig);
        long.Parse(entries["delete.retention.ms"].Value).Should().BeGreaterThanOrEqualTo(604800000);
        ConsumerConfig consumer = fixture.CreateByteConsumerConfig();
        consumer
            .MaxPartitionFetchBytes.Should()
            .BeGreaterThanOrEqualTo(request.DeploymentPolicy.MaxRecordBytes);
        consumer.FetchMaxBytes.Should().BeGreaterThanOrEqualTo(request.DeploymentPolicy.MaxRecordBytes);
        consumer.EnableAutoCommit.Should().BeFalse();
        consumer.EnableAutoOffsetStore.Should().BeFalse();
    }

    private async Task RetainEvidenceAsync(CancellationToken token)
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "TestResults",
            "MessageContractConsumerBroker"
        );
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"cdc-message-contract-observations-{Guid.NewGuid():N}.json");
        var evidence = new
        {
            Evidence = "Real Kafka transport; synthetic E18-derived public values; simulated durable persistence and clock",
            ConnectImage = Environment.GetEnvironmentVariable(MessageContractRunner.ImageVariable)
                ?? string.Empty,
            States = _states.Select(s => new
            {
                Phase = s.Key,
                s.Value.Valid,
                s.Value.RenewalInProgress,
                s.Value.Attempt,
                s.Value.ProofCompletedAt,
                s.Value.NextOffsets,
                s.Value.Checkpoints,
                DocumentCount = s.Value.Documents.Count,
            }),
            Scans = _scans.Select(s => new
            {
                Phase = s.Key,
                Bounds = s.Value.CompletedBoundaries.Select(b => new
                {
                    b.Partition,
                    b.StartOffset,
                    b.EndOffset,
                }),
                Records = s.Value.Records.Select(r => new
                {
                    r.Partition,
                    r.Offset,
                    KafkaNull = r.Value.IsNull,
                    KeyBytes = r.Key.Bytes.Length,
                    ValueBytes = r.Value.Bytes.Length,
                }),
            }),
        };
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }),
            token
        );
        TestContext.AddTestAttachment(
            path,
            "Consumer barriers and durable transitions; bodies and physical topic names omitted"
        );
    }

    private sealed record Snapshot(
        bool Valid,
        bool RenewalInProgress,
        int Attempt,
        DateTimeOffset ProofCompletedAt,
        Dictionary<int, long> NextOffsets,
        Dictionary<int, long> Checkpoints,
        Dictionary<string, MessageContractConsumerDocument> Documents
    );
}
