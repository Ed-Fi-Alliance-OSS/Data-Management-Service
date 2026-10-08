// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using FluentAssertions.Execution;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture]
[Category("CdcMessageContract")]
[Property("CdcInvariant", "CDC-INV-07")]
public sealed class Given_MessageContractKafkaBoundariesSharedAssertions
{
    private const string Uuid = "00112233-4455-6677-8899-aabbccddeeff";
    private CdcConnectorTemplateRequest _request = null!;
    private JsonElement _expected;
    private MessageContractKafkaRecord _record = null!;

    [SetUp]
    public void Setup()
    {
        _request = CdcConnectorTemplateTestData.BuildRequest(
            CdcProvider.Postgresql,
            topicPrefix: "attached.documents",
            instanceKey: "attached"
        );
        _expected = JsonSerializer.SerializeToElement(
            new
            {
                contractVersion = 1,
                documentUuid = Uuid,
                contentVersion = 23,
                document = new
                {
                    id = Uuid,
                    firstName = "Independent",
                    _etag = "stream-etag",
                },
            }
        );
        _record = new(
            _request.PublicTopicName,
            0,
            42,
            MessageContractKafkaBytes.From(Encoding.UTF8.GetBytes(Uuid)),
            MessageContractKafkaBytes.From(JsonSerializer.SerializeToUtf8Bytes(_expected)),
            [],
            0
        );
    }

    [TestCase(CdcProvider.Postgresql, "604800000")]
    [TestCase(CdcProvider.Postgresql, "1209600000")]
    [TestCase(CdcProvider.SqlServer, "604800000")]
    [TestCase(CdcProvider.SqlServer, "1209600000")]
    public void It_accepts_public_retention_and_shorter_progress_broker_default(
        CdcProvider provider,
        string publicRetention
    )
    {
        var request = CdcConnectorTemplateTestData.BuildRequest(provider);
        var binding = request.Binding with { PartitionCount = 3 };
        MessageContractRecordAssertions.AssertTopicPolicy(
            binding,
            request.PublicTopicName,
            TopicConfig("compact", publicRetention),
            3
        );
        var progressConfig = TopicConfig("compact", "86400000");
        progressConfig["delete.retention.ms"].Source = ConfigSource.DefaultConfig;
        MessageContractRecordAssertions.AssertTopicPolicy(
            binding,
            request.ProgressTopicName,
            progressConfig,
            1
        );
    }

    [TestCase("604799999")]
    [TestCase("86400000")]
    public void It_rejects_public_retention_below_seven_days(string retention)
    {
        Action act = () =>
            MessageContractRecordAssertions.AssertTopicPolicy(
                _request.Binding,
                _request.PublicTopicName,
                TopicConfig("compact", retention),
                _request.Binding.PartitionCount
            );
        act.Should().Throw<AssertionException>();
    }

    [TestCase(false, "delete")]
    [TestCase(false, "compact,delete")]
    [TestCase(true, "delete")]
    [TestCase(true, "compact,delete")]
    public void It_rejects_non_compact_only_cleanup_for_both_topics(bool progress, string cleanupPolicy)
    {
        Action act = () =>
            MessageContractRecordAssertions.AssertTopicPolicy(
                _request.Binding,
                progress ? _request.ProgressTopicName : _request.PublicTopicName,
                TopicConfig(cleanupPolicy, "604800000"),
                1
            );
        act.Should().Throw<AssertionException>();
    }

    [TestCase(false, 1)]
    [TestCase(false, 4)]
    [TestCase(true, 0)]
    [TestCase(true, 3)]
    public void It_rejects_wrong_partition_counts_for_both_topics(bool progress, int partitionCount)
    {
        Action act = () =>
            MessageContractRecordAssertions.AssertTopicPolicy(
                _request.Binding with
                {
                    PartitionCount = 3,
                },
                progress ? _request.ProgressTopicName : _request.PublicTopicName,
                TopicConfig("compact", "604800000"),
                partitionCount
            );
        act.Should().Throw<AssertionException>();
    }

    private static Dictionary<string, ConfigEntryResult> TopicConfig(
        string cleanupPolicy,
        string retention
    ) =>
        new()
        {
            ["cleanup.policy"] = new() { Name = "cleanup.policy", Value = cleanupPolicy },
            ["delete.retention.ms"] = new() { Name = "delete.retention.ms", Value = retention },
        };

    [TestCase("source")]
    [TestCase("heartbeat")]
    [Property("CdcInvariant", "CDC-INV-08")]
    public void It_rejects_forbidden_metadata_even_when_public_records_match(string kind)
    {
        MessageContractRecordAssertions.AssertUpsert(_request, _record, Uuid, 0, _expected);
        string forbidden =
            kind == "source"
                ? _request.ConnectorName.Value + ".dms.DocumentProjectionWork"
                : "__debezium-heartbeat." + _request.ConnectorName.Value;
        Metadata metadata = new(
            [],
            [
                new(_request.PublicTopicName, [], ErrorCode.NoError),
                new(_request.ProgressTopicName, [], ErrorCode.NoError),
                new(forbidden, [], ErrorCode.NoError),
            ],
            0,
            "fixture"
        );
        using var scope = new AssertionScope();
        MessageContractRecordAssertions.AssertNoRawTopics(metadata, _request.ConnectorName.Value);
        string[] failures = scope.Discard();
        failures.Should().ContainSingle().Which.Should().Contain("raw source topics");
    }

    [Test]
    [Property("CdcInvariant", "CDC-INV-08")]
    public void It_accepts_public_progress_and_unrelated_topics()
    {
        Metadata metadata = new(
            [],
            [
                new(_request.PublicTopicName, [], ErrorCode.NoError),
                new(_request.ProgressTopicName, [], ErrorCode.NoError),
                new("unrelated.documents", [], ErrorCode.NoError),
            ],
            0,
            "fixture"
        );
        MessageContractRecordAssertions.AssertNoRawTopics(metadata, _request.ConnectorName.Value);
    }

    [TestCase("topic")]
    [TestCase("partition")]
    [TestCase("key")]
    [TestCase("null")]
    [TestCase("envelope")]
    [TestCase("header")]
    public void It_preserves_full_record_validation_without_a_provider_row(string mismatch)
    {
        MessageContractKafkaRecord changed = mismatch switch
        {
            "topic" => _record with { Topic = "wrong-topic" },
            "partition" => _record with { Partition = 1 },
            "key" => _record with { Key = MessageContractKafkaBytes.From([1]) },
            "null" => _record with { Value = MessageContractKafkaBytes.From(null!) },
            "envelope" => _record with { Value = MessageContractKafkaBytes.From("{}"u8.ToArray()) },
            "header" => _record with { Headers = [new("extra", MessageContractKafkaBytes.From([]))] },
            _ => throw new ArgumentException("Unknown mismatch"),
        };
        // Null deliberately has no JSON bytes; stop at the record-level assertion.
        if (mismatch == "null")
        {
            Action act = () =>
                MessageContractRecordAssertions.AssertUpsert(_request, changed, Uuid, 0, _expected);
            act.Should().Throw<AssertionException>();
            return;
        }
        using var scope = new AssertionScope();
        MessageContractRecordAssertions.AssertUpsert(_request, changed, Uuid, 0, _expected);
        string[] failures = scope.Discard();
        failures.Should().ContainSingle();
    }

    [Test]
    public void It_uses_the_existing_partition_vectors_for_every_binding_count()
    {
        string root = MessageContractFixtureCatalog.ResolveFixtureRoot(AppContext.BaseDirectory);
        using JsonDocument vectors = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "cdc/message-contract/partition-vectors.json"))
        );
        string algorithm = vectors.RootElement.GetProperty("algorithm").GetString()!;
        foreach (JsonElement vector in vectors.RootElement.GetProperty("vectors").EnumerateArray())
        {
            foreach (JsonElement partition in vector.GetProperty("partitions").EnumerateArray())
            {
                MessageContractPartition
                    .ForUuid(
                        vector.GetProperty("uuid").GetString()!,
                        partition.GetProperty("count").GetInt32(),
                        algorithm
                    )
                    .Should()
                    .Be(partition.GetProperty("expected").GetInt32());
            }
        }
    }

    [Test]
    public void It_uses_the_attached_broker_endpoint()
    {
        var observer = new MessageContractKafkaObserver("attached-broker:29092");
        observer.CreateByteConsumerConfig().BootstrapServers.Should().Be("attached-broker:29092");
    }
}
