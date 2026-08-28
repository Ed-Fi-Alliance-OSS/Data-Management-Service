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
/// Binding-scoped Kafka topic provisioning and validation. Topics are located by name rather than by
/// position, and every returned policy set is run through <see cref="CdcKafkaPolicyObservationValidator"/>
/// so no scenario can pass while emitting evidence the shared contract rejects.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CdcKafkaTopicPolicy")]
public class Given_CdcKafkaTopicPolicy
{
    private const int PartitionCount = 3;
    private const int MaxRecordBytes = 4_194_304;
    private const long SevenDaysMilliseconds = 604800000;

    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task It_creates_every_absent_binding_topic_with_explicit_policy_values()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        IAdminClient adminClient = EmptyThenPopulatedBroker(inventory, ConformingTopics(inventory));

        CdcKafkaBindingTopicPolicies policies = await RunAsync(adminClient, inventory);

        policies.PublicTopic.State.Should().Be(CdcKafkaPolicyItemState.Satisfied);
        policies.ProgressTopic.State.Should().Be(CdcKafkaPolicyItemState.Satisfied);

        TopicSpecification publicTopic = CreatedTopic(adminClient, inventory.TopicName);
        publicTopic.NumPartitions.Should().Be(PartitionCount);
        publicTopic.ReplicationFactor.Should().Be(1);
        publicTopic.Configs[CdcKafkaAdminAdapter.CleanupPolicyConfigName].Should().Be("compact");
        publicTopic
            .Configs[CdcKafkaAdminAdapter.DeleteRetentionConfigName]
            .Should()
            .Be(SevenDaysMilliseconds.ToString(CultureInfo.InvariantCulture));
        publicTopic
            .Configs[CdcKafkaAdminAdapter.MaxMessageBytesConfigName]
            .Should()
            .Be(MaxRecordBytes.ToString(CultureInfo.InvariantCulture));
        publicTopic.Configs[CdcKafkaAdminAdapter.MinInSyncReplicasConfigName].Should().Be("1");

        TopicSpecification progressTopic = CreatedTopic(adminClient, inventory.ProgressTopicName);
        progressTopic.NumPartitions.Should().Be(1);
        progressTopic.Configs[CdcKafkaAdminAdapter.CleanupPolicyConfigName].Should().Be("compact");
        progressTopic.Configs.Should().NotContainKey(CdcKafkaAdminAdapter.MaxMessageBytesConfigName);
    }

    [Test]
    public async Task It_creates_the_sql_server_schema_history_topic_with_infinite_retention()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.SqlServer);
        IAdminClient adminClient = EmptyThenPopulatedBroker(inventory, ConformingTopics(inventory));

        CdcKafkaBindingTopicPolicies policies = await RunAsync(adminClient, inventory);

        policies.SchemaHistoryTopic!.State.Should().Be(CdcKafkaPolicyItemState.Satisfied);

        TopicSpecification historyTopic = CreatedTopic(adminClient, inventory.SchemaHistoryTopicName!);
        historyTopic.NumPartitions.Should().Be(1);
        historyTopic.Configs[CdcKafkaAdminAdapter.CleanupPolicyConfigName].Should().Be("delete");
        historyTopic.Configs[CdcKafkaAdminAdapter.RetentionMillisecondsConfigName].Should().Be("-1");
        historyTopic.Configs[CdcKafkaAdminAdapter.RetentionBytesConfigName].Should().Be("-1");
    }

    [Test]
    public async Task It_validates_conforming_topics_idempotently_without_recreating_them()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.SqlServer);
        IAdminClient adminClient = Broker(ConformingTopics(inventory));

        CdcKafkaBindingTopicPolicies first = await RunAsync(adminClient, inventory);
        CdcKafkaBindingTopicPolicies second = await RunAsync(adminClient, inventory);

        foreach (CdcKafkaBindingTopicPolicies policies in new[] { first, second })
        {
            policies.PublicTopic.State.Should().Be(CdcKafkaPolicyItemState.Satisfied);
            policies.ProgressTopic.State.Should().Be(CdcKafkaPolicyItemState.Satisfied);
            policies.SchemaHistoryTopic!.State.Should().Be(CdcKafkaPolicyItemState.Satisfied);
            policies.Diagnostics.Should().BeEmpty();
        }

        second.PublicTopic.TopicName.Should().Be(inventory.TopicName);
        second.PublicTopic.PartitionCount.Should().Be(PartitionCount);
        second.PublicTopic.CleanupPolicy.Should().Be("compact");
        second.ProgressTopic.PartitionCount.Should().Be(1);
        second.SchemaHistoryTopic!.CleanupPolicy.Should().Be("delete");
        A.CallTo(() =>
                adminClient.CreateTopicsAsync(A<IEnumerable<TopicSpecification>>._, A<CreateTopicsOptions>._)
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_reports_no_schema_history_evidence_for_postgresql()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        IAdminClient adminClient = Broker(ConformingTopics(inventory));

        CdcKafkaBindingTopicPolicies policies = await RunAsync(adminClient, inventory);

        policies.SchemaHistoryTopic.Should().BeNull();
    }

    [TestCase("delete")]
    [TestCase("compact,delete")]
    public async Task It_rejects_a_public_topic_whose_cleanup_policy_permits_deletion(string cleanupPolicy)
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        IAdminClient adminClient = Broker(
            ConformingTopics(inventory),
            configs =>
                configs[inventory.TopicName][CdcKafkaAdminAdapter.CleanupPolicyConfigName] = Entry(
                    cleanupPolicy
                )
        );

        CdcKafkaBindingTopicPolicies policies = await RunAsync(adminClient, inventory);

        policies.PublicTopic.State.Should().Be(CdcKafkaPolicyItemState.Invalid);
        policies.PublicTopic.CleanupPolicy.Should().Be("delete");
        DiagnosticsFor(policies, inventory.TopicName).Should().NotBeEmpty();
        policies.ProgressTopic.State.Should().Be(CdcKafkaPolicyItemState.Satisfied);
    }

    [Test]
    public async Task It_rejects_a_public_topic_with_no_explicit_tombstone_retention_override()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        IAdminClient adminClient = Broker(
            ConformingTopics(inventory),
            configs => configs[inventory.TopicName].Remove(CdcKafkaAdminAdapter.DeleteRetentionConfigName)
        );

        CdcKafkaBindingTopicPolicies policies = await RunAsync(adminClient, inventory);

        policies.PublicTopic.State.Should().Be(CdcKafkaPolicyItemState.Invalid);
        DiagnosticsFor(policies, inventory.TopicName)
            .Should()
            .Contain(diagnostic =>
                diagnostic.Expected!.Contains("delete.retention.ms", StringComparison.Ordinal)
            );
    }

    [Test]
    public async Task It_rejects_a_tombstone_retention_inherited_from_a_broker_default()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        IAdminClient adminClient = Broker(
            ConformingTopics(inventory),
            configs =>
                configs[inventory.TopicName][CdcKafkaAdminAdapter.DeleteRetentionConfigName] = Entry(
                    SevenDaysMilliseconds.ToString(CultureInfo.InvariantCulture),
                    ConfigSource.DefaultConfig
                )
        );

        CdcKafkaBindingTopicPolicies policies = await RunAsync(adminClient, inventory);

        policies.PublicTopic.State.Should().Be(CdcKafkaPolicyItemState.Invalid);
    }

    [Test]
    public async Task It_rejects_a_tombstone_retention_below_seven_days()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        IAdminClient adminClient = Broker(
            ConformingTopics(inventory),
            configs =>
                configs[inventory.TopicName][CdcKafkaAdminAdapter.DeleteRetentionConfigName] = Entry(
                    (SevenDaysMilliseconds - 1).ToString(CultureInfo.InvariantCulture)
                )
        );

        CdcKafkaBindingTopicPolicies policies = await RunAsync(adminClient, inventory);

        policies.PublicTopic.State.Should().Be(CdcKafkaPolicyItemState.Invalid);
    }

    [Test]
    public async Task It_accepts_a_tombstone_retention_above_seven_days()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        IAdminClient adminClient = Broker(
            ConformingTopics(inventory),
            configs =>
                configs[inventory.TopicName][CdcKafkaAdminAdapter.DeleteRetentionConfigName] = Entry(
                    (SevenDaysMilliseconds * 2).ToString(CultureInfo.InvariantCulture)
                )
        );

        CdcKafkaBindingTopicPolicies policies = await RunAsync(adminClient, inventory);

        policies.PublicTopic.State.Should().Be(CdcKafkaPolicyItemState.Satisfied);
    }

    [Test]
    public async Task It_rejects_a_public_topic_whose_record_size_ceiling_differs()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        IAdminClient adminClient = Broker(
            ConformingTopics(inventory),
            configs =>
                configs[inventory.TopicName][CdcKafkaAdminAdapter.MaxMessageBytesConfigName] = Entry(
                    (MaxRecordBytes + 1).ToString(CultureInfo.InvariantCulture)
                )
        );

        CdcKafkaBindingTopicPolicies policies = await RunAsync(adminClient, inventory);

        policies.PublicTopic.State.Should().Be(CdcKafkaPolicyItemState.Invalid);
    }

    [TestCase(2)]
    [TestCase(4)]
    public async Task It_rejects_a_public_topic_with_the_wrong_partition_count(int partitionCount)
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        Dictionary<string, TopicState> topics = ConformingTopics(inventory);
        topics[inventory.TopicName] = topics[inventory.TopicName] with { Partitions = partitionCount };

        CdcKafkaBindingTopicPolicies policies = await RunAsync(Broker(topics), inventory);

        policies.PublicTopic.State.Should().Be(CdcKafkaPolicyItemState.Invalid);
        policies.PublicTopic.PartitionCount.Should().Be(partitionCount);
    }

    [Test]
    public async Task It_rejects_a_progress_topic_with_more_than_one_partition()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        Dictionary<string, TopicState> topics = ConformingTopics(inventory);
        topics[inventory.ProgressTopicName] = topics[inventory.ProgressTopicName] with { Partitions = 2 };

        CdcKafkaBindingTopicPolicies policies = await RunAsync(Broker(topics), inventory);

        policies.ProgressTopic.State.Should().Be(CdcKafkaPolicyItemState.Invalid);
        policies.PublicTopic.State.Should().Be(CdcKafkaPolicyItemState.Satisfied);
    }

    [Test]
    public async Task It_rejects_a_replica_count_below_the_active_durability_profile()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        Dictionary<string, TopicState> topics = ConformingTopics(inventory, minInSyncReplicas: 2);

        CdcKafkaBindingTopicPolicies policies = await RunAsync(
            Broker(topics),
            inventory,
            CdcControlOptions.ProductionDurabilityProfile
        );

        policies.PublicTopic.State.Should().Be(CdcKafkaPolicyItemState.Invalid);
        policies.PublicTopic.ReplicationFactor.Should().Be(1);
    }

    [Test]
    public async Task It_rejects_min_insync_replicas_below_the_active_durability_profile()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        Dictionary<string, TopicState> topics = ConformingTopics(inventory, replicationFactor: 3);

        CdcKafkaBindingTopicPolicies policies = await RunAsync(
            Broker(topics),
            inventory,
            CdcControlOptions.ProductionDurabilityProfile
        );

        policies.PublicTopic.State.Should().Be(CdcKafkaPolicyItemState.Invalid);
        policies.PublicTopic.MinInSyncReplicas.Should().Be(1);
    }

    [Test]
    public async Task It_rejects_min_insync_replicas_inherited_from_a_broker_default()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        IAdminClient adminClient = Broker(
            ConformingTopics(inventory),
            configs =>
                configs[inventory.TopicName][CdcKafkaAdminAdapter.MinInSyncReplicasConfigName] = Entry(
                    "1",
                    ConfigSource.DefaultConfig
                )
        );

        CdcKafkaBindingTopicPolicies policies = await RunAsync(adminClient, inventory);

        policies.PublicTopic.State.Should().Be(CdcKafkaPolicyItemState.Invalid);
        policies.PublicTopic.MinInSyncReplicas.Should().Be(1);
    }

    [Test]
    public async Task It_rejects_a_compacted_schema_history_topic()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.SqlServer);
        IAdminClient adminClient = Broker(
            ConformingTopics(inventory),
            configs =>
                configs[inventory.SchemaHistoryTopicName!][CdcKafkaAdminAdapter.CleanupPolicyConfigName] =
                    Entry("compact")
        );

        CdcKafkaBindingTopicPolicies policies = await RunAsync(adminClient, inventory);

        policies.SchemaHistoryTopic!.State.Should().Be(CdcKafkaPolicyItemState.Invalid);
        policies.SchemaHistoryTopic.CleanupPolicy.Should().Be("compact");
    }

    [TestCase(CdcKafkaAdminAdapter.RetentionMillisecondsConfigName)]
    [TestCase(CdcKafkaAdminAdapter.RetentionBytesConfigName)]
    public async Task It_rejects_a_finitely_retained_schema_history_topic(string configName)
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.SqlServer);
        IAdminClient adminClient = Broker(
            ConformingTopics(inventory),
            configs => configs[inventory.SchemaHistoryTopicName!][configName] = Entry("86400000")
        );

        CdcKafkaBindingTopicPolicies policies = await RunAsync(adminClient, inventory);

        policies.SchemaHistoryTopic!.State.Should().Be(CdcKafkaPolicyItemState.Invalid);
        DiagnosticsFor(policies, inventory.SchemaHistoryTopicName!)
            .Should()
            .Contain(diagnostic => diagnostic.Expected!.Contains(configName, StringComparison.Ordinal));
    }

    [Test]
    public async Task It_rejects_a_schema_history_topic_with_more_than_one_partition()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.SqlServer);
        Dictionary<string, TopicState> topics = ConformingTopics(inventory);
        topics[inventory.SchemaHistoryTopicName!] = topics[inventory.SchemaHistoryTopicName!] with
        {
            Partitions = 2,
        };

        CdcKafkaBindingTopicPolicies policies = await RunAsync(Broker(topics), inventory);

        policies.SchemaHistoryTopic!.State.Should().Be(CdcKafkaPolicyItemState.Invalid);
    }

    [Test]
    public async Task It_reports_unknown_for_every_topic_when_the_broker_is_unreachable()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.SqlServer);
        IAdminClient adminClient = A.Fake<IAdminClient>();
        A.CallTo(() => adminClient.GetMetadata(A<TimeSpan>._))
            .Throws(new KafkaException(ErrorCode.Local_AllBrokersDown));

        CdcKafkaBindingTopicPolicies policies = await RunAsync(adminClient, inventory);

        policies.PublicTopic.State.Should().Be(CdcKafkaPolicyItemState.Unknown);
        policies.ProgressTopic.State.Should().Be(CdcKafkaPolicyItemState.Unknown);
        policies.SchemaHistoryTopic!.State.Should().Be(CdcKafkaPolicyItemState.Unknown);
        policies.PublicTopic.PartitionCount.Should().BeNull();
        policies.PublicTopic.CleanupPolicy.Should().BeNull();
    }

    [Test]
    public async Task It_reports_unknown_when_a_topic_configuration_is_undescribable()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        IAdminClient adminClient = Broker(ConformingTopics(inventory));
        A.CallTo(() =>
                adminClient.DescribeConfigsAsync(
                    A<IEnumerable<ConfigResource>>.That.Matches(resources =>
                        resources.Single().Name == inventory.ProgressTopicName
                    ),
                    A<DescribeConfigsOptions>._
                )
            )
            .Throws(new KafkaException(ErrorCode.RequestTimedOut));

        CdcKafkaBindingTopicPolicies policies = await RunAsync(adminClient, inventory);

        policies.ProgressTopic.State.Should().Be(CdcKafkaPolicyItemState.Unknown);
        policies.PublicTopic.State.Should().Be(CdcKafkaPolicyItemState.Satisfied);
    }

    [Test]
    public async Task It_reports_unknown_for_an_unrecognized_durability_profile()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.SqlServer);
        IAdminClient adminClient = Broker(ConformingTopics(inventory));

        CdcKafkaBindingTopicPolicies policies = await RunAsync(adminClient, inventory, "single-broker");

        policies.PublicTopic.State.Should().Be(CdcKafkaPolicyItemState.Unknown);
        policies.ProgressTopic.State.Should().Be(CdcKafkaPolicyItemState.Unknown);
        policies.SchemaHistoryTopic!.State.Should().Be(CdcKafkaPolicyItemState.Unknown);
        A.CallTo(() => adminClient.GetMetadata(A<TimeSpan>._)).MustNotHaveHappened();
    }

    private static async Task<CdcKafkaBindingTopicPolicies> RunAsync(
        IAdminClient adminClient,
        CdcArtifactInventory inventory,
        string durabilityProfile = CdcControlOptions.LocalDurabilityProfile
    )
    {
        CdcKafkaAdminAdapter adapter = new(
            adminClient,
            Options.Create(ControlOptions(inventory, durabilityProfile)),
            new FixedTimeProvider(ObservedAt),
            NullLogger<CdcKafkaAdminAdapter>.Instance
        );

        CdcKafkaBindingTopicPolicies policies = await adapter.EnsureBindingTopicsAsync(
            inventory,
            CancellationToken.None
        );

        AssertContractShape(policies, inventory);

        return policies;
    }

    /// <summary>
    /// Runs the topic policies through the shared observation validator. The ACL and record-size items
    /// are placeholders until the composed observation lands, so this asserts only the topic evidence
    /// this story owns: safe names, comma-free cleanup tokens, and numeric facts present unless unknown.
    /// </summary>
    private static void AssertContractShape(
        CdcKafkaBindingTopicPolicies policies,
        CdcArtifactInventory inventory
    )
    {
        CdcKafkaPolicyItemState[] states =
        [
            policies.PublicTopic.State,
            policies.ProgressTopic.State,
            policies.SchemaHistoryTopic?.State ?? CdcKafkaPolicyItemState.NotApplicable,
        ];

        CdcKafkaPolicyState policyState = CdcKafkaPolicyState.Satisfied;
        if (states.Contains(CdcKafkaPolicyItemState.Unknown))
        {
            policyState = CdcKafkaPolicyState.Unknown;
        }

        if (states.Contains(CdcKafkaPolicyItemState.Invalid))
        {
            policyState = CdcKafkaPolicyState.Invalid;
        }

        CdcTargetIdentity targetIdentity = new(
            inventory.DeploymentKey,
            "default",
            "1",
            inventory.InstanceKey,
            inventory.Generation,
            inventory.Provider
        );

        CdcKafkaPolicyObservation observation = new(
            CdcJsonContract.CurrentContractVersion,
            "operation-1",
            ObservedAt,
            targetIdentity,
            inventory.Provider,
            null,
            policyState,
            "local",
            policies.PublicTopic,
            policies.ProgressTopic,
            policies.SchemaHistoryTopic,
            new(inventory.TopicName, CdcKafkaPolicyItemState.Satisfied),
            new(inventory.ProgressTopicName, CdcKafkaPolicyItemState.Satisfied),
            inventory.SchemaHistoryTopicName is { } schemaHistoryTopicName
                ? new(schemaHistoryTopicName, CdcKafkaPolicyItemState.Satisfied)
                : null,
            new(CdcKafkaPolicyItemState.Satisfied, MaxRecordBytes, MaxRecordBytes),
            policies.Diagnostics
        );

        CdcKafkaPolicyObservationValidator
            .Validate(observation, new("operation-1", targetIdentity, null, ObservedAt.AddMinutes(1)))
            .Succeeded.Should()
            .BeTrue("every emitted topic policy must satisfy the shared Kafka policy contract");
    }

    private static IReadOnlyList<CdcDiagnostic> DiagnosticsFor(
        CdcKafkaBindingTopicPolicies policies,
        string topicName
    ) => [.. policies.Diagnostics.Where(diagnostic => diagnostic.ArtifactName == topicName)];

    /// <summary>
    /// The single topic specification the adapter asked the broker to create under this name. Locating
    /// by name rather than by call order keeps the assertion independent of provisioning sequence.
    /// </summary>
    private static TopicSpecification CreatedTopic(IAdminClient adminClient, string topicName) =>
        Fake.GetCalls(adminClient)
            .Where(call =>
                string.Equals(
                    call.Method.Name,
                    nameof(IAdminClient.CreateTopicsAsync),
                    StringComparison.Ordinal
                )
            )
            .SelectMany(call => (IEnumerable<TopicSpecification>)call.Arguments[0]!)
            .Single(specification => string.Equals(specification.Name, topicName, StringComparison.Ordinal));

    private static IAdminClient Broker(
        Dictionary<string, TopicState> topics,
        Action<Dictionary<string, Dictionary<string, ConfigEntryResult>>>? mutateConfigs = null
    )
    {
        Dictionary<string, Dictionary<string, ConfigEntryResult>> configs = topics.ToDictionary(
            topic => topic.Key,
            topic => new Dictionary<string, ConfigEntryResult>(topic.Value.Configs, StringComparer.Ordinal),
            StringComparer.Ordinal
        );
        mutateConfigs?.Invoke(configs);

        IAdminClient adminClient = A.Fake<IAdminClient>();
        A.CallTo(() => adminClient.GetMetadata(A<TimeSpan>._)).Returns(Cluster(topics));
        StubConfigs(adminClient, configs);

        return adminClient;
    }

    /// <summary>Reports every topic absent on the first metadata read and present afterwards.</summary>
    private static IAdminClient EmptyThenPopulatedBroker(
        CdcArtifactInventory inventory,
        Dictionary<string, TopicState> topics
    )
    {
        int topicCount = inventory.SchemaHistoryTopicName is null ? 2 : 3;
        Metadata[] reads =
        [
            .. Enumerable
                .Range(0, topicCount)
                .SelectMany(_ => new[] { new Metadata([], [], 0, "broker"), Cluster(topics) }),
        ];

        IAdminClient adminClient = A.Fake<IAdminClient>();
        A.CallTo(() => adminClient.GetMetadata(A<TimeSpan>._)).ReturnsNextFromSequence(reads);
        StubConfigs(
            adminClient,
            topics.ToDictionary(
                topic => topic.Key,
                topic => new Dictionary<string, ConfigEntryResult>(
                    topic.Value.Configs,
                    StringComparer.Ordinal
                ),
                StringComparer.Ordinal
            )
        );

        return adminClient;
    }

    private static void StubConfigs(
        IAdminClient adminClient,
        Dictionary<string, Dictionary<string, ConfigEntryResult>> configs
    )
    {
        foreach (KeyValuePair<string, Dictionary<string, ConfigEntryResult>> entry in configs)
        {
            string topicName = entry.Key;
            A.CallTo(() =>
                    adminClient.DescribeConfigsAsync(
                        A<IEnumerable<ConfigResource>>.That.Matches(resources =>
                            resources.Single().Name == topicName
                        ),
                        A<DescribeConfigsOptions>._
                    )
                )
                .Returns(
                    new List<DescribeConfigsResult>
                    {
                        new()
                        {
                            Entries = entry.Value.ToDictionary(
                                pair => pair.Key,
                                pair => pair.Value,
                                StringComparer.Ordinal
                            ),
                        },
                    }
                );
        }
    }

    private static Metadata Cluster(Dictionary<string, TopicState> topics) =>
        new([], [.. topics.Select(topic => TopicMetadataFor(topic.Key, topic.Value))], 0, "broker");

    private static TopicMetadata TopicMetadataFor(string topicName, TopicState state)
    {
        int[] replicas = [.. Enumerable.Range(0, state.ReplicationFactor)];

        return new(
            topicName,
            [
                .. Enumerable
                    .Range(0, state.Partitions)
                    .Select(index => new PartitionMetadata(
                        index,
                        0,
                        replicas,
                        replicas,
                        new Error(ErrorCode.NoError)
                    )),
            ],
            new Error(ErrorCode.NoError)
        );
    }

    private static Dictionary<string, TopicState> ConformingTopics(
        CdcArtifactInventory inventory,
        int replicationFactor = 1,
        int minInSyncReplicas = 1
    )
    {
        Dictionary<string, ConfigEntryResult> Durability() =>
            new(StringComparer.Ordinal)
            {
                [CdcKafkaAdminAdapter.MinInSyncReplicasConfigName] = Entry(
                    minInSyncReplicas.ToString(CultureInfo.InvariantCulture)
                ),
            };

        Dictionary<string, ConfigEntryResult> publicConfigs = Durability();
        publicConfigs[CdcKafkaAdminAdapter.CleanupPolicyConfigName] = Entry("compact");
        publicConfigs[CdcKafkaAdminAdapter.DeleteRetentionConfigName] = Entry(
            SevenDaysMilliseconds.ToString(CultureInfo.InvariantCulture)
        );
        publicConfigs[CdcKafkaAdminAdapter.MaxMessageBytesConfigName] = Entry(
            MaxRecordBytes.ToString(CultureInfo.InvariantCulture)
        );

        Dictionary<string, ConfigEntryResult> progressConfigs = Durability();
        progressConfigs[CdcKafkaAdminAdapter.CleanupPolicyConfigName] = Entry("compact");

        Dictionary<string, TopicState> topics = new(StringComparer.Ordinal)
        {
            [inventory.TopicName] = new(PartitionCount, replicationFactor, publicConfigs),
            [inventory.ProgressTopicName] = new(1, replicationFactor, progressConfigs),
        };

        if (inventory.SchemaHistoryTopicName is { } schemaHistoryTopicName)
        {
            Dictionary<string, ConfigEntryResult> historyConfigs = Durability();
            historyConfigs[CdcKafkaAdminAdapter.CleanupPolicyConfigName] = Entry("delete");
            historyConfigs[CdcKafkaAdminAdapter.RetentionMillisecondsConfigName] = Entry("-1");
            historyConfigs[CdcKafkaAdminAdapter.RetentionBytesConfigName] = Entry("-1");
            topics[schemaHistoryTopicName] = new(1, replicationFactor, historyConfigs);
        }

        return topics;
    }

    private static ConfigEntryResult Entry(
        string value,
        ConfigSource source = ConfigSource.DynamicTopicConfig
    ) =>
        new()
        {
            Value = value,
            Source = source,
            IsDefault = source == ConfigSource.DefaultConfig,
        };

    private static CdcArtifactInventory Inventory(CdcProvider provider) =>
        CdcArtifactNameGenerator.Render(new("dms-local", "edfi.dms", "data-store-1", 1, provider)).Inventory!;

    private static CdcControlOptions ControlOptions(
        CdcArtifactInventory inventory,
        string durabilityProfile
    ) =>
        new()
        {
            DeploymentKey = inventory.DeploymentKey,
            InstanceKey = inventory.InstanceKey,
            TopicPrefix = inventory.TopicPrefix,
            Generation = inventory.Generation,
            PartitionCount = PartitionCount,
            KafkaBootstrapServers = "localhost:9092",
            ConnectBaseUri = "http://localhost:8083",
            ConnectWorkerKey = "worker-1",
            ConnectOffsetStorageTopic = "connect-offsets",
            DurabilityProfile = durabilityProfile,
            MaxRecordBytes = MaxRecordBytes,
            ConnectorPrincipal = "User:connector",
            ConnectWorkerPrincipal = "User:connect-worker",
            DmsBaseUrl = "http://localhost:8080",
            DmsBearerToken = "token",
        };

    private sealed record TopicState(
        int Partitions,
        int ReplicationFactor,
        Dictionary<string, ConfigEntryResult> Configs
    );

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
