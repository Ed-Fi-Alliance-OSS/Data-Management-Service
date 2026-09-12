// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

public partial class Given_CdcKafkaAdminAdapter
{
    [TestCase("empty", CdcRetirementOffsetState.Absent)]
    [TestCase("present", CdcRetirementOffsetState.Present)]
    [TestCase("tombstone", CdcRetirementOffsetState.Absent)]
    [TestCase("unrelated", CdcRetirementOffsetState.Absent)]
    [TestCase("prefix", CdcRetirementOffsetState.Absent)]
    [TestCase("two-keys", CdcRetirementOffsetState.Present)]
    [TestCase("after-snapshot", CdcRetirementOffsetState.Absent)]
    [TestCase("compacted-gap", CdcRetirementOffsetState.Absent)]
    [TestCase("multiple-partitions", CdcRetirementOffsetState.Present)]
    public async Task It_inspects_only_retiring_namespace_through_the_captured_end(
        string scenario,
        CdcRetirementOffsetState expected
    )
    {
        var (adapter, consumer) = OffsetSnapshot(scenario);
        using (adapter)
        {
            Observed(
                    await adapter.InspectRetirementOffsetsAsync(
                        CdcArtifactCleanupTestData.Scope(_request),
                        CancellationToken.None
                    )
                )
                .Should()
                .Be(expected);
        }
        A.CallTo(() => consumer.Dispose()).MustHaveHappenedOnceExactly();
        Fake.GetCalls(consumer)
            .Should()
            .OnlyContain(call =>
                new[] { "Assign", "Consume", "Pause", "Dispose" }.Contains(call.Method.Name)
            );
        JsonSerializer.Serialize(expected).Should().NotContain(Sentinel);
    }

    [TestCase("malformed-key")]
    [TestCase("malformed-partition")]
    [TestCase("malformed-value")]
    [TestCase("null-key")]
    [TestCase("truncated")]
    [TestCase("incomplete")]
    [TestCase("denied")]
    [TestCase("timeout")]
    [TestCase("low-changed")]
    [TestCase("cross-partition")]
    [TestCase("duplicate-partitions")]
    [TestCase("missing-topic")]
    public async Task It_rejects_unverifiable_retirement_offset_storage(string scenario)
    {
        var (adapter, _) = OffsetSnapshot(scenario);
        using (adapter)
        {
            var result = await adapter.InspectRetirementOffsetsAsync(
                CdcArtifactCleanupTestData.Scope(_request),
                CancellationToken.None
            );
            result.State.Should().Be(CdcTransportEvidenceState.Unavailable);
            JsonSerializer.Serialize(result).Should().NotContain(Sentinel);
        }
    }

    [Test]
    public async Task It_preserves_retirement_offset_inspection_cancellation()
    {
        var (adapter, _) = OffsetSnapshot("timeout");
        using (adapter)
        using (CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(25)))
        {
            Func<Task> act = () =>
                adapter.InspectRetirementOffsetsAsync(
                    CdcArtifactCleanupTestData.Scope(_request),
                    cancellation.Token
                );
            await act.Should().ThrowAsync<OperationCanceledException>();
        }
    }

    private (CdcKafkaAdminAdapter, IConsumer<byte[], byte[]>) OffsetSnapshot(string scenario)
    {
        _request = CdcArtifactCleanupTestData.WithTiming(_request);
        var plan = CdcDeploymentKafkaPolicy.Build(_request).OffsetStore;
        bool multiple = scenario is "multiple-partitions" or "duplicate-partitions";
        if (multiple)
        {
            plan = plan with { PartitionCount = 2 };
        }
        _metadata = MetadataFor(
            plan,
            scenario == "missing-topic" ? ErrorCode.UnknownTopicOrPart : ErrorCode.NoError
        );
        _topicConfig = plan.Configuration.ToDictionary(
            pair => pair.Key,
            pair => Config(pair.Key, pair.Value, ConfigSource.DynamicTopicConfig)
        );
        string topic = plan.Name;
        var consumer = A.Fake<IConsumer<byte[], byte[]>>(options => options.Strict());
        A.CallTo(() => consumer.Dispose()).DoesNothing();
        A.CallTo(() => consumer.Assign(A<IEnumerable<TopicPartitionOffset>>._)).DoesNothing();
        A.CallTo(() => consumer.Pause(A<IEnumerable<TopicPartition>>._)).DoesNothing();
        string key = JsonSerializer.Serialize(
            new object[] { _request.Binding.ConnectorName, new { server = Sentinel } }
        );
        Queue<ConsumeResult<byte[], byte[]>> records = [];
        ConsumeResult<byte[], byte[]> Record(
            long offset,
            string recordKey,
            string value = "{}",
            bool eof = false
        ) =>
            new()
            {
                Topic = topic,
                Partition = 0,
                Offset = offset,
                IsPartitionEOF = eof,
                Message = new()
                {
                    Key = recordKey is null ? null! : Encoding.UTF8.GetBytes(recordKey),
                    Value = value is null ? null! : Encoding.UTF8.GetBytes(value),
                },
            };
        long high = 2;
        switch (scenario)
        {
            case "empty":
                high = 0;
                break;
            case "present":
                records.Enqueue(Record(0, key));
                break;
            case "tombstone":
                records.Enqueue(Record(0, key));
                records.Enqueue(Record(1, key, null!));
                break;
            case "unrelated":
                records.Enqueue(Record(0, "[\"other\",{}]", "not-json"));
                break;
            case "prefix":
                records.Enqueue(
                    Record(
                        0,
                        key.Replace(_request.Binding.ConnectorName, _request.Binding.ConnectorName + "-other")
                    )
                );
                break;
            case "two-keys":
                records.Enqueue(Record(0, key));
                records.Enqueue(Record(1, key.Replace(Sentinel, "other"), null!));
                break;
            case "after-snapshot":
                records.Enqueue(Record(2, key));
                break;
            case "malformed-key":
                records.Enqueue(Record(0, "not-json"));
                break;
            case "malformed-partition":
                records.Enqueue(
                    Record(0, JsonSerializer.Serialize(new[] { _request.Binding.ConnectorName }))
                );
                break;
            case "malformed-value":
                records.Enqueue(Record(0, key, "not-json"));
                break;
            case "null-key":
                records.Enqueue(Record(0, null!));
                break;
            case "truncated":
                records.Enqueue(Record(1, key, eof: true));
                break;
            case "cross-partition":
                var wrong = Record(0, key);
                wrong.Partition = 3;
                records.Enqueue(wrong);
                break;
        }
        if (multiple)
        {
            records.Enqueue(Record(0, key));
            var second = Record(
                1,
                scenario == "duplicate-partitions" ? key : key.Replace(Sentinel, "other"),
                null!
            );
            second.Partition = 1;
            records.Enqueue(second);
            var end = Record(high, key, eof: true);
            end.Partition = 1;
            records.Enqueue(end);
        }
        records.Enqueue(Record(high, key, eof: true));
        A.CallTo(() => consumer.Consume(A<CancellationToken>._))
            .ReturnsLazily(
                (CancellationToken token) =>
                {
                    if (scenario == "timeout")
                    {
                        token.WaitHandle.WaitOne();
                        token.ThrowIfCancellationRequested();
                    }
                    if (scenario == "denied")
                    {
                        throw new KafkaException(new Error(ErrorCode.TopicAuthorizationFailed, Sentinel));
                    }
                    return scenario == "incomplete" ? null! : records.Dequeue();
                }
            );
        int calls = 0;
        var adapter = new CdcKafkaAdminAdapter(_client, _authorization)
        {
            CreateOffsetConsumer = () => consumer,
            ListOffsets = (specifications, _) =>
            {
                calls++;
                var spec = specifications.Single();
                long low = scenario == "low-changed" && calls > 2 ? 1 : 0;
                long offset = spec.OffsetSpec is OffsetSpec.LatestSpec ? high : low;
                var result = HistoryOffsets(topic, offset);
                result.ResultInfos[0].TopicPartitionOffsetError = new(
                    new TopicPartitionOffset(spec.TopicPartition, offset),
                    ErrorCode.NoError
                );
                return Task.FromResult(result);
            },
        };
        return (adapter, consumer);
    }
}
