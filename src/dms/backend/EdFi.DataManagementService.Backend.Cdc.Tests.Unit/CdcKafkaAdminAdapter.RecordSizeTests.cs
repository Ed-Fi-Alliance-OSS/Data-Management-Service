// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json;
using Confluent.Kafka.Admin;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

public partial class Given_CdcKafkaAdminAdapter
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task It_CdcRecordSizeIncrease_reconciles_deployment_broker_size_changes(bool lostResponse)
    {
        var desired = CdcRecordSizeRollout.WithPolicy(_request, 134_217_728, 134_217_728);
        var deployment = A.Fake<ICdcKafkaBrokerSizeDeployment>();
        _adapter = new(_client, _authorization, brokerSizes: deployment);
        var preservedMessage = _brokerConfig["message.max.bytes"].Value;
        _brokerConfig["socket.request.max.bytes"].Value = "200000000";
        A.CallTo(() =>
                deployment.ApplyAsync(
                    A<CdcDeploymentRequest>._,
                    A<IReadOnlyList<CdcKafkaBrokerCapacity>>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(
                (
                    CdcDeploymentRequest _,
                    IReadOnlyList<CdcKafkaBrokerCapacity> changes,
                    CancellationToken _
                ) =>
                {
                    var change = changes.Single();
                    change.SocketRequestMaxBytes.Should().Be(200000000);
                    change.ReplicaFetchMaxBytes.Should().Be(134217728);
                    change.ReplicaFetchResponseMaxBytes.Should().Be(134217728);
                    _brokerConfig["replica.fetch.max.bytes"].Value = "134217728";
                    _brokerConfig["replica.fetch.response.max.bytes"].Value = "134217728";
                    if (lostResponse)
                    {
                        throw new IOException(Sentinel);
                    }
                }
            );
        var result = await _adapter.IncreaseBrokerLimitsAsync(desired, CancellationToken.None);
        result.State.Should().Be(CdcTransportEvidenceState.Observed);
        _brokerConfig["message.max.bytes"].Value.Should().Be(preservedMessage);
        JsonSerializer.Serialize(result).Should().NotContain(Sentinel);
        A.CallTo(() =>
                _client.IncrementalAlterConfigsAsync(
                    A<Dictionary<ConfigResource, List<ConfigEntry>>>._,
                    A<IncrementalAlterConfigsOptions>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_CdcRecordSizeIncrease_requires_deployment_authority_for_static_limits()
    {
        var desired = CdcRecordSizeRollout.WithPolicy(_request, 134_217_728, 134_217_728);
        var result = await _adapter.IncreaseBrokerLimitsAsync(desired, CancellationToken.None);
        result.State.Should().Be(CdcTransportEvidenceState.Unavailable);
        A.CallTo(() =>
                _client.IncrementalAlterConfigsAsync(
                    A<Dictionary<ConfigResource, List<ConfigEntry>>>._,
                    A<IncrementalAlterConfigsOptions>._
                )
            )
            .MustNotHaveHappened();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_CdcRecordSizeIncrease_changes_only_public_topic_size_and_reconciles_lost_response(
        bool lostResponse
    )
    {
        var desired = CdcRecordSizeRollout.WithPolicy(_request, 100_000_000, 100_000_000);
        var before = _topicConfig.ToDictionary(p => p.Key, p => p.Value.Value);
        A.CallTo(() =>
                _client.IncrementalAlterConfigsAsync(
                    A<Dictionary<ConfigResource, List<ConfigEntry>>>._,
                    A<IncrementalAlterConfigsOptions>._
                )
            )
            .ReturnsLazily(
                (Dictionary<ConfigResource, List<ConfigEntry>> changes, IncrementalAlterConfigsOptions _) =>
                {
                    changes.Should().ContainSingle();
                    changes.Keys.Single().Type.Should().Be(ResourceType.Topic);
                    changes.Keys.Single().Name.Should().Be(desired.Binding.TopicName);
                    var entry = changes.Values.Single().Single();
                    entry.Name.Should().Be("max.message.bytes");
                    entry.IncrementalOperation.Should().Be(AlterConfigOpType.Set);
                    _topicConfig[entry.Name].Value = entry.Value;
                    if (lostResponse)
                    {
                        throw new TimeoutException(Sentinel);
                    }
                    return Task.FromResult(new List<IncrementalAlterConfigsResult>());
                }
            );
        var result = await _adapter.IncreasePublicTopicLimitAsync(
            desired,
            _request.ConnectorPolicy.MaxRecordBytes,
            CancellationToken.None
        );
        result.State.Should().Be(CdcTransportEvidenceState.Observed);
        before["max.message.bytes"] = desired.ConnectorPolicy.MaxRecordBytes.ToString(
            CultureInfo.InvariantCulture
        );
        _topicConfig.ToDictionary(p => p.Key, p => p.Value.Value).Should().BeEquivalentTo(before);
    }

    [TestCase("weak-broker")]
    [TestCase("wrong-ceiling")]
    [TestCase("inherited")]
    [TestCase("lowering")]
    public async Task It_CdcRecordSizeIncrease_rejects_unordered_topic_changes_without_mutation(
        string scenario
    )
    {
        var desired = CdcRecordSizeRollout.WithPolicy(_request, 100_000_000, 100_000_000);
        int previous = _request.ConnectorPolicy.MaxRecordBytes;
        switch (scenario)
        {
            case "weak-broker":
                _brokerConfig["replica.fetch.max.bytes"].Value = "1";
                break;
            case "wrong-ceiling":
                _topicConfig["max.message.bytes"].Value = "1";
                break;
            case "inherited":
                _topicConfig["max.message.bytes"].Source = ConfigSource.DefaultConfig;
                break;
            case "lowering":
                previous = 120_000_000;
                break;
        }
        (await _adapter.IncreasePublicTopicLimitAsync(desired, previous, CancellationToken.None))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        A.CallTo(() =>
                _client.IncrementalAlterConfigsAsync(
                    A<Dictionary<ConfigResource, List<ConfigEntry>>>._,
                    A<IncrementalAlterConfigsOptions>._
                )
            )
            .MustNotHaveHappened();
    }
}
