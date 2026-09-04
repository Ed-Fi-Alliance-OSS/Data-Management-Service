// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit;

/// <summary>
/// Record-size and broker-limit verification. Kafka's own defaults are never assumed: a limit that
/// cannot be read keeps the policy unknown rather than presumed adequate.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CdcKafkaRecordSize")]
public class Given_CdcKafkaRecordSize
{
    private const int MaxRecordBytes = 4_194_304;
    private const int GenerousLimit = 104_857_600;

    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task It_accepts_broker_limits_that_all_carry_the_record_size_budget()
    {
        CdcKafkaRecordSizeEvidence evidence = await RunAsync(Broker());

        evidence.Policy.State.Should().Be(CdcKafkaPolicyItemState.Satisfied);
        evidence.Policy.MaxRecordBytes.Should().Be(MaxRecordBytes);
        evidence.Policy.MaxMessageBytes.Should().Be(MaxRecordBytes);
        evidence.Diagnostics.Should().BeEmpty();
    }

    [TestCase(CdcKafkaAdminAdapter.SocketRequestMaxBytesConfigName)]
    [TestCase(CdcKafkaAdminAdapter.ReplicaFetchMaxBytesConfigName)]
    [TestCase(CdcKafkaAdminAdapter.ReplicaFetchResponseMaxBytesConfigName)]
    public async Task It_rejects_a_broker_limit_below_the_record_size_budget(string configName)
    {
        CdcKafkaRecordSizeEvidence evidence = await RunAsync(
            Broker(limits => limits[configName] = MaxRecordBytes - 1)
        );

        evidence.Policy.State.Should().Be(CdcKafkaPolicyItemState.Invalid);
        evidence
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Expected!.StartsWith(configName, StringComparison.Ordinal)
                && diagnostic.Observed == (MaxRecordBytes - 1).ToString(CultureInfo.InvariantCulture)
            );
    }

    [Test]
    public async Task It_rejects_every_broker_limit_that_is_below_the_budget_in_one_pass()
    {
        CdcKafkaRecordSizeEvidence evidence = await RunAsync(
            Broker(limits =>
            {
                limits[CdcKafkaAdminAdapter.SocketRequestMaxBytesConfigName] = 1024;
                limits[CdcKafkaAdminAdapter.ReplicaFetchMaxBytesConfigName] = 1024;
                limits[CdcKafkaAdminAdapter.ReplicaFetchResponseMaxBytesConfigName] = 1024;
            })
        );

        evidence.Policy.State.Should().Be(CdcKafkaPolicyItemState.Invalid);
        evidence
            .Diagnostics.Where(diagnostic => diagnostic.Category == CdcDiagnosticCategory.KafkaPolicyInvalid)
            .Should()
            .HaveCount(3);
    }

    [Test]
    public async Task It_reads_the_least_limit_across_every_broker()
    {
        IAdminClient adminClient = A.Fake<IAdminClient>();
        A.CallTo(() => adminClient.GetMetadata(A<TimeSpan>._)).Returns(Cluster(0, 1));
        StubBroker(adminClient, 0, GenerousLimits());
        StubBroker(
            adminClient,
            1,
            GenerousLimits(limits =>
                limits[CdcKafkaAdminAdapter.ReplicaFetchMaxBytesConfigName] = MaxRecordBytes - 1
            )
        );
        StubTopic(adminClient, MaxRecordBytes);

        CdcKafkaRecordSizeEvidence evidence = await RunAsync(adminClient);

        evidence.Policy.State.Should().Be(CdcKafkaPolicyItemState.Invalid);
    }

    [Test]
    public async Task It_reports_unknown_when_the_effective_message_limit_is_below_the_budget()
    {
        IAdminClient adminClient = A.Fake<IAdminClient>();
        A.CallTo(() => adminClient.GetMetadata(A<TimeSpan>._)).Returns(Cluster(0));
        StubBroker(
            adminClient,
            0,
            GenerousLimits(limits =>
                limits[CdcKafkaAdminAdapter.MessageMaxBytesConfigName] = MaxRecordBytes - 1
            )
        );
        StubTopic(adminClient, null);

        CdcKafkaRecordSizeEvidence evidence = await RunAsync(adminClient);

        evidence.Policy.State.Should().Be(CdcKafkaPolicyItemState.Unknown);
        evidence
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Expected == CdcKafkaAdminAdapter.MessageMaxBytesConfigName);
    }

    [Test]
    public async Task It_prefers_the_topic_override_over_the_broker_message_limit()
    {
        IAdminClient adminClient = A.Fake<IAdminClient>();
        A.CallTo(() => adminClient.GetMetadata(A<TimeSpan>._)).Returns(Cluster(0));
        StubBroker(
            adminClient,
            0,
            GenerousLimits(limits =>
                limits[CdcKafkaAdminAdapter.MessageMaxBytesConfigName] = MaxRecordBytes - 1
            )
        );
        StubTopic(adminClient, MaxRecordBytes);

        CdcKafkaRecordSizeEvidence evidence = await RunAsync(adminClient);

        evidence.Policy.State.Should().Be(CdcKafkaPolicyItemState.Satisfied);
        evidence.Policy.MaxMessageBytes.Should().Be(MaxRecordBytes);
    }

    [Test]
    public async Task It_falls_back_to_the_broker_message_limit_when_the_topic_has_no_override()
    {
        IAdminClient adminClient = A.Fake<IAdminClient>();
        A.CallTo(() => adminClient.GetMetadata(A<TimeSpan>._)).Returns(Cluster(0));
        StubBroker(adminClient, 0, GenerousLimits());
        StubTopic(adminClient, null);

        CdcKafkaRecordSizeEvidence evidence = await RunAsync(adminClient);

        evidence.Policy.State.Should().Be(CdcKafkaPolicyItemState.Satisfied);
        evidence.Policy.MaxMessageBytes.Should().Be(GenerousLimit);
    }

    [TestCase(CdcKafkaAdminAdapter.SocketRequestMaxBytesConfigName)]
    [TestCase(CdcKafkaAdminAdapter.ReplicaFetchMaxBytesConfigName)]
    [TestCase(CdcKafkaAdminAdapter.ReplicaFetchResponseMaxBytesConfigName)]
    public async Task It_reports_unknown_for_an_undescribable_broker_limit(string configName)
    {
        CdcKafkaRecordSizeEvidence evidence = await RunAsync(Broker(limits => limits.Remove(configName)));

        evidence.Policy.State.Should().Be(CdcKafkaPolicyItemState.Unknown);
        evidence.Diagnostics.Should().Contain(diagnostic => diagnostic.Expected == configName);
    }

    [Test]
    public async Task It_reports_unknown_when_no_broker_reports_its_configuration()
    {
        IAdminClient adminClient = A.Fake<IAdminClient>();
        A.CallTo(() => adminClient.GetMetadata(A<TimeSpan>._)).Returns(new Metadata([], [], 0, "broker"));
        StubTopic(adminClient, MaxRecordBytes);

        CdcKafkaRecordSizeEvidence evidence = await RunAsync(adminClient);

        evidence.Policy.State.Should().Be(CdcKafkaPolicyItemState.Unknown);
    }

    [Test]
    public async Task It_reports_unknown_when_the_broker_is_unreachable()
    {
        IAdminClient adminClient = A.Fake<IAdminClient>();
        A.CallTo(() => adminClient.GetMetadata(A<TimeSpan>._))
            .Throws(new KafkaException(ErrorCode.Local_AllBrokersDown));

        CdcKafkaRecordSizeEvidence evidence = await RunAsync(adminClient);

        evidence.Policy.State.Should().Be(CdcKafkaPolicyItemState.Unknown);
        evidence.Policy.MaxMessageBytes.Should().BeNull();
    }

    [Test]
    public async Task It_reports_unknown_when_broker_configuration_cannot_be_described()
    {
        IAdminClient adminClient = A.Fake<IAdminClient>();
        A.CallTo(() => adminClient.GetMetadata(A<TimeSpan>._)).Returns(Cluster(0));
        A.CallTo(() =>
                adminClient.DescribeConfigsAsync(
                    A<IEnumerable<ConfigResource>>._,
                    A<DescribeConfigsOptions>._
                )
            )
            .Throws(new KafkaException(ErrorCode.ClusterAuthorizationFailed));

        CdcKafkaRecordSizeEvidence evidence = await RunAsync(adminClient);

        evidence.Policy.State.Should().Be(CdcKafkaPolicyItemState.Unknown);
    }

    [Test]
    public async Task It_never_names_a_secret_in_record_size_diagnostics()
    {
        IAdminClient adminClient = A.Fake<IAdminClient>();
        A.CallTo(() => adminClient.GetMetadata(A<TimeSpan>._))
            .Throws(
                new KafkaException(
                    new Error(ErrorCode.Local_Authentication, "sasl.password=hunter2 host=broker.internal")
                )
            );

        CdcKafkaRecordSizeEvidence evidence = await RunAsync(adminClient);

        string rendered = string.Join(
            '|',
            evidence.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Message}{diagnostic.Expected}{diagnostic.Observed}"
            )
        );
        rendered.Should().NotContain("hunter2");
        rendered.Should().NotContain("broker.internal");
    }

    private static async Task<CdcKafkaRecordSizeEvidence> RunAsync(IAdminClient adminClient)
    {
        CdcArtifactInventory inventory = Inventory();
        CdcKafkaAdminAdapter adapter = new(
            adminClient,
            Options.Create(ControlOptions()),
            new FixedTimeProvider(ObservedAt),
            NullLogger<CdcKafkaAdminAdapter>.Instance
        );

        CdcKafkaRecordSizeEvidence evidence = await adapter.VerifyRecordSizeAsync(
            inventory,
            CancellationToken.None
        );

        AssertContractShape(evidence.Policy, inventory);

        return evidence;
    }

    /// <summary>
    /// Runs the record-size policy through the shared observation validator. The topic and ACL items are
    /// placeholders until the composed observation lands, so this asserts only the record-size evidence
    /// this story owns: positive values present unless unknown, and a budget the message limit carries.
    /// </summary>
    private static void AssertContractShape(CdcKafkaRecordSizePolicy policy, CdcArtifactInventory inventory)
    {
        CdcTargetIdentity targetIdentity = new(
            inventory.DeploymentKey,
            "default",
            "1",
            inventory.InstanceKey,
            inventory.Generation,
            inventory.Provider
        );
        CdcKafkaTopicPolicy topic = new(
            inventory.TopicName,
            CdcKafkaPolicyItemState.Satisfied,
            1,
            "compact",
            1,
            1
        );

        CdcKafkaPolicyObservation observation = new(
            CdcJsonContract.CurrentContractVersion,
            "operation-1",
            ObservedAt,
            targetIdentity,
            inventory.Provider,
            null,
            policy.State switch
            {
                CdcKafkaPolicyItemState.Invalid => CdcKafkaPolicyState.Invalid,
                CdcKafkaPolicyItemState.Unknown => CdcKafkaPolicyState.Unknown,
                _ => CdcKafkaPolicyState.Satisfied,
            },
            "local",
            topic,
            topic with
            {
                TopicName = inventory.ProgressTopicName,
            },
            null,
            new(inventory.TopicName, CdcKafkaPolicyItemState.Satisfied),
            new(inventory.ProgressTopicName, CdcKafkaPolicyItemState.Satisfied),
            null,
            policy,
            []
        );

        CdcKafkaPolicyObservationValidator
            .Validate(observation, new("operation-1", targetIdentity, null, ObservedAt.AddMinutes(1)))
            .Succeeded.Should()
            .BeTrue("every emitted record-size policy must satisfy the shared Kafka policy contract");
    }

    private static IAdminClient Broker(Action<Dictionary<string, int>>? mutateLimits = null)
    {
        IAdminClient adminClient = A.Fake<IAdminClient>();
        A.CallTo(() => adminClient.GetMetadata(A<TimeSpan>._)).Returns(Cluster(0));
        StubBroker(adminClient, 0, GenerousLimits(mutateLimits));
        StubTopic(adminClient, MaxRecordBytes);

        return adminClient;
    }

    private static Dictionary<string, int> GenerousLimits(Action<Dictionary<string, int>>? mutate = null)
    {
        Dictionary<string, int> limits = new(StringComparer.Ordinal)
        {
            [CdcKafkaAdminAdapter.SocketRequestMaxBytesConfigName] = GenerousLimit,
            [CdcKafkaAdminAdapter.MessageMaxBytesConfigName] = GenerousLimit,
            [CdcKafkaAdminAdapter.ReplicaFetchMaxBytesConfigName] = GenerousLimit,
            [CdcKafkaAdminAdapter.ReplicaFetchResponseMaxBytesConfigName] = GenerousLimit,
        };
        mutate?.Invoke(limits);

        return limits;
    }

    private static void StubBroker(IAdminClient adminClient, int brokerId, Dictionary<string, int> limits)
    {
        string resourceName = brokerId.ToString(CultureInfo.InvariantCulture);

        A.CallTo(() =>
                adminClient.DescribeConfigsAsync(
                    A<IEnumerable<ConfigResource>>.That.Matches(resources =>
                        resources.Single().Type == ResourceType.Broker
                        && resources.Single().Name == resourceName
                    ),
                    A<DescribeConfigsOptions>._
                )
            )
            .Returns(
                new List<DescribeConfigsResult>
                {
                    new()
                    {
                        Entries = limits.ToDictionary(
                            limit => limit.Key,
                            limit => new ConfigEntryResult
                            {
                                Name = limit.Key,
                                Value = limit.Value.ToString(CultureInfo.InvariantCulture),
                                Source = ConfigSource.StaticBrokerConfig,
                            },
                            StringComparer.Ordinal
                        ),
                    },
                }
            );
    }

    private static void StubTopic(IAdminClient adminClient, int? maxMessageBytes)
    {
        Dictionary<string, ConfigEntryResult> entries = new(StringComparer.Ordinal);
        if (maxMessageBytes is { } value)
        {
            entries[CdcKafkaAdminAdapter.MaxMessageBytesConfigName] = new()
            {
                Name = CdcKafkaAdminAdapter.MaxMessageBytesConfigName,
                Value = value.ToString(CultureInfo.InvariantCulture),
                Source = ConfigSource.DynamicTopicConfig,
            };
        }

        A.CallTo(() =>
                adminClient.DescribeConfigsAsync(
                    A<IEnumerable<ConfigResource>>.That.Matches(resources =>
                        resources.Single().Type == ResourceType.Topic
                    ),
                    A<DescribeConfigsOptions>._
                )
            )
            .Returns(new List<DescribeConfigsResult> { new() { Entries = entries } });
    }

    private static Metadata Cluster(params int[] brokerIds) =>
        new([.. brokerIds.Select(brokerId => new BrokerMetadata(brokerId, "broker", 9092))], [], 0, "broker");

    private static CdcArtifactInventory Inventory() =>
        CdcArtifactNameGenerator
            .Render(new("dms-local", "edfi.dms", "data-store-1", 1, CdcProvider.Postgresql))
            .Inventory!;

    private static CdcControlOptions ControlOptions() =>
        new()
        {
            DeploymentKey = "dms-local",
            InstanceKey = "data-store-1",
            TopicPrefix = "edfi.dms",
            Generation = 1,
            PartitionCount = 3,
            KafkaBootstrapServers = "localhost:9092",
            ConnectBaseUri = "http://localhost:8083",
            ConnectWorkerKey = "worker-1",
            ConnectOffsetStorageTopic = "connect-offsets",
            DurabilityProfile = CdcControlOptions.LocalDurabilityProfile,
            MaxRecordBytes = MaxRecordBytes,
            ConnectorKafkaPrincipal = "User:connector",
            ConnectWorkerPrincipal = "User:connect-worker",
            DmsBaseUrl = "http://localhost:8080",
            DmsBearerToken = "token",
        };

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
