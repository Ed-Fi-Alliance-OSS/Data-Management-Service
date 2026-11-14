// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Confluent.Kafka;
using Confluent.Kafka.Admin;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture]
public sealed class Given_PinnedImageFixtureKafkaRecordSizeLimits
{
    private const int Budget = 16_384;
    private Dictionary<string, ConfigEntryResult> _broker = null!;
    private ConsumerConfig _consumer = null!;

    [SetUp]
    public void Setup()
    {
        _broker = new()
        {
            ["message.max.bytes"] = new() { Value = "8192" },
            ["socket.request.max.bytes"] = new() { Value = "104857600" },
        };
        _consumer = new() { MaxPartitionFetchBytes = 1_048_576, FetchMaxBytes = 50_000_000 };
    }

    [Test]
    public void It_observes_kafka_limits_and_allows_the_topic_override_to_exceed_the_broker_default()
    {
        var limits = Observe();
        limits
            .Should()
            .Be(new MessageContractSizeLimits(Budget, Budget, 8192, 104857600, 1_048_576, 50_000_000, 1));
    }

    [TestCase("message.max.bytes", "redacted")]
    [TestCase("message.max.bytes", "0")]
    [TestCase("socket.request.max.bytes", "16384")]
    [TestCase("socket.request.max.bytes", "16383")]
    public void It_rejects_invalid_broker_limits_or_missing_request_headroom(string key, string value)
    {
        _broker[key].Value = value;
        Assert.Throws<AssertionException>(() => Observe());
    }

    [Test]
    public void It_rejects_a_topic_limit_that_has_not_been_aligned()
    {
        Assert.Throws<AssertionException>(() =>
            CdcConnectorTemplatePinnedImageFixture.ObserveRecordSizeLimits(
                Budget,
                Budget - 1,
                _broker,
                _consumer
            )
        );
    }

    [TestCase(true)]
    [TestCase(false)]
    public void It_rejects_insufficient_consumer_fetch_capacity(bool partition)
    {
        if (partition)
        {
            _consumer.MaxPartitionFetchBytes = Budget - 1;
        }
        else
        {
            _consumer.FetchMaxBytes = Budget - 1;
        }
        Assert.Throws<AssertionException>(() => Observe());
    }

    private MessageContractSizeLimits Observe() =>
        CdcConnectorTemplatePinnedImageFixture.ObserveRecordSizeLimits(Budget, Budget, _broker, _consumer);
}
