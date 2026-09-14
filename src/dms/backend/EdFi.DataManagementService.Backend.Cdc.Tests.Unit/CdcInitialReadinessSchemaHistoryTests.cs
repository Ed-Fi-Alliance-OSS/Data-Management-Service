// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Confluent.Kafka;
using Confluent.Kafka.Admin;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using NUnit.Framework;
using DdlProvider = EdFi.DataManagementService.Backend.Ddl.CdcProvider;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

public partial class Given_CdcKafkaAdminAdapter
{
    [TestCase(0, 3, 0, CdcSqlServerSchemaHistoryState.Valid)]
    [TestCase(0, 0, 0, CdcSqlServerSchemaHistoryState.EmptyWithRetainedOffset)]
    [TestCase(1, 3, 1, CdcSqlServerSchemaHistoryState.RequiredRecordLost)]
    public async Task It_reads_initial_schema_history_retention_independently_of_running_task(
        long low,
        long high,
        long finalLow,
        CdcSqlServerSchemaHistoryState expected
    )
    {
        string topic = SchemaHistoryTopic();
        List<OffsetSpec> specifications = [];
        Queue<long> offsets = new([low, high, finalLow]);
        using var adapter = new CdcKafkaAdminAdapter(_client, _authorization)
        {
            ListOffsets = (specs, options) =>
            {
                var spec = specs.Single();
                spec.TopicPartition.Should().Be(new TopicPartition(topic, 0));
                specifications.Add(spec.OffsetSpec);
                options.IsolationLevel.Should().Be(IsolationLevel.ReadCommitted);
                options.RequestTimeout.Should().Be(_request.Timing.CallTimeout);
                return Task.FromResult(HistoryOffsets(topic, offsets.Dequeue()));
            },
        };
        Observed(await adapter.InspectSchemaHistoryAsync(_request, CancellationToken.None))
            .Should()
            .Be(expected);
        specifications.Should().HaveCount(3);
        specifications[0].Should().BeOfType<OffsetSpec.EarliestSpec>();
        specifications[1].Should().BeOfType<OffsetSpec.LatestSpec>();
        specifications[2].Should().BeOfType<OffsetSpec.EarliestSpec>();
    }

    [TestCase("negative")]
    [TestCase("wrong-partition")]
    [TestCase("duplicate")]
    [TestCase("denied")]
    [TestCase("changed-low")]
    public async Task It_rejects_unusable_schema_history_retention(string failure)
    {
        string topic = SchemaHistoryTopic();
        int calls = 0;
        using var adapter = new CdcKafkaAdminAdapter(_client, _authorization)
        {
            ListOffsets = (_, _) =>
            {
                calls++;
                long offset = calls == 2 ? 3 : 0;
                var result = HistoryOffsets(
                    failure == "wrong-partition" ? "other" : topic,
                    failure == "negative" ? -1 : offset,
                    failure == "denied" ? ErrorCode.TopicAuthorizationFailed : ErrorCode.NoError
                );
                if (failure == "duplicate")
                {
                    result.ResultInfos.Add(result.ResultInfos[0]);
                }
                if (failure == "changed-low" && calls == 3)
                {
                    result = HistoryOffsets(topic, 1);
                }
                return Task.FromResult(result);
            },
        };
        (await adapter.InspectSchemaHistoryAsync(_request, CancellationToken.None))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
    }

    [TestCase(ErrorCode.UnknownTopicOrPart, CdcTransportEvidenceState.Observed)]
    [TestCase(ErrorCode.TopicAuthorizationFailed, CdcTransportEvidenceState.Unavailable)]
    public async Task It_distinguishes_missing_schema_history_from_unavailable_broker_authority(
        ErrorCode error,
        CdcTransportEvidenceState expected
    )
    {
        SchemaHistoryTopic();
        _metadata = MetadataFor(
            _plan.BindingTopics.Single(t => t.Role == CdcKafkaTopicRole.SchemaHistory),
            error
        );
        var result = await _adapter.InspectSchemaHistoryAsync(_request, CancellationToken.None);
        result.State.Should().Be(expected);
        if (expected == CdcTransportEvidenceState.Observed)
        {
            Observed(result).Should().Be(CdcSqlServerSchemaHistoryState.Missing);
        }
    }

    private string SchemaHistoryTopic()
    {
        _request = CdcDeploymentRequestTestData.Request(DdlProvider.SqlServer);
        _plan = CdcDeploymentKafkaPolicy.Build(_request);
        var topic = _plan.BindingTopics.Single(t => t.Role == CdcKafkaTopicRole.SchemaHistory);
        _metadata = MetadataFor(topic);
        return topic.Name;
    }

    private static ListOffsetsResult HistoryOffsets(
        string topic,
        long offset,
        ErrorCode error = ErrorCode.NoError
    ) =>
        new()
        {
            ResultInfos =
            [
                new()
                {
                    TopicPartitionOffsetError = new(
                        new TopicPartitionOffset(topic, 0, offset),
                        new Error(error)
                    ),
                    Timestamp = -1,
                },
            ],
        };
}
