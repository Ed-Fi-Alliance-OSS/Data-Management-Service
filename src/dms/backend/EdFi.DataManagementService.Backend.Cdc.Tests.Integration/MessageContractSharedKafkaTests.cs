// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using FluentAssertions.Execution;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture]
[Category("CdcMessageContract")]
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

    [TestCase("source")]
    [TestCase("heartbeat")]
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
