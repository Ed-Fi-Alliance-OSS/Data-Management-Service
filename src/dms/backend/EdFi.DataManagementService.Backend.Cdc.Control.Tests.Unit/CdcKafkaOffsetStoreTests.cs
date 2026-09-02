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
/// Cluster-scoped Kafka Connect offset store provisioning and validation. Every case runs the returned
/// observation through <see cref="CdcConnectOffsetStorePolicyObservationValidator"/>, so no scenario can
/// pass while emitting evidence the shared contract rejects.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CdcKafkaOffsetStore")]
public class Given_CdcKafkaOffsetStore
{
    private const string OffsetStoreTopic = "connect-offsets";
    private const string WorkerPrincipal = "User:connect-worker";
    private const string ConsumerPrincipal = "User:instance-consumer";
    private const string OperationId = "operation-1";
    private const string SourceFingerprint =
        "sha256:8caa6b0ad6db6f60d8d7ce6e78d1e76094e2241678c6f241670319ab60810851";

    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);

    private static readonly CdcTargetIdentity TargetIdentity = new(
        "dms-local",
        "default",
        "1",
        "data-store-1",
        1,
        CdcProvider.Postgresql
    );

    [Test]
    public async Task It_creates_the_offset_store_when_absent()
    {
        IAdminClient adminClient = A.Fake<IAdminClient>();
        A.CallTo(() => adminClient.GetMetadata(A<TimeSpan>._))
            .ReturnsNextFromSequence(EmptyCluster(), ClusterWith(Topic(OffsetStoreTopic, 25, 1)));
        StubConfigs(adminClient, CompactConfigs(1));

        CdcConnectOffsetStorePolicyObservation observation = await RunAsync(adminClient, ControlOptions());

        observation.PolicyState.Should().Be(CdcConnectOffsetStorePolicyState.Satisfied);
        A.CallTo(() =>
                adminClient.CreateTopicsAsync(
                    A<IEnumerable<TopicSpecification>>.That.Matches(specifications =>
                        HasOffsetStoreSpecification(specifications, 1, 1)
                    ),
                    A<CreateTopicsOptions>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_creates_the_offset_store_with_the_production_durability_profile()
    {
        IAdminClient adminClient = A.Fake<IAdminClient>();
        A.CallTo(() => adminClient.GetMetadata(A<TimeSpan>._))
            .ReturnsNextFromSequence(EmptyCluster(), ClusterWith(Topic(OffsetStoreTopic, 25, 3)));
        StubConfigs(adminClient, CompactConfigs(2));

        CdcConnectOffsetStorePolicyObservation observation = await RunAsync(
            adminClient,
            ControlOptions(CdcControlOptions.ProductionDurabilityProfile)
        );

        observation.PolicyState.Should().Be(CdcConnectOffsetStorePolicyState.Satisfied);
        A.CallTo(() =>
                adminClient.CreateTopicsAsync(
                    A<IEnumerable<TopicSpecification>>.That.Matches(specifications =>
                        HasOffsetStoreSpecification(specifications, 3, 2)
                    ),
                    A<CreateTopicsOptions>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    /// <summary>
    /// The describe pass never provisions: a status read of the shared store reports it absent rather
    /// than creating it and then reporting the policy of the topic it had just made itself.
    /// </summary>
    [Test]
    public async Task It_reports_an_absent_offset_store_without_creating_it()
    {
        IAdminClient adminClient = A.Fake<IAdminClient>();
        A.CallTo(() => adminClient.GetMetadata(A<TimeSpan>._)).Returns(EmptyCluster());
        StubConfigs(adminClient, CompactConfigs(1));

        CdcConnectOffsetStorePolicyObservation observation = await RunAsync(
            adminClient,
            ControlOptions(),
            describe: true
        );

        observation.PolicyState.Should().Be(CdcConnectOffsetStorePolicyState.Unknown);
        observation
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Component == CdcDiagnosticComponent.ConnectOffsetStore
                && diagnostic.Observed == "absent"
            );
        A.CallTo(() =>
                adminClient.CreateTopicsAsync(A<IEnumerable<TopicSpecification>>._, A<CreateTopicsOptions>._)
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_validates_an_existing_offset_store_without_recreating_it()
    {
        IAdminClient adminClient = Broker(Topic(OffsetStoreTopic, 25, 1), CompactConfigs(1));

        CdcConnectOffsetStorePolicyObservation first = await RunAsync(adminClient, ControlOptions());
        CdcConnectOffsetStorePolicyObservation second = await RunAsync(adminClient, ControlOptions());

        first.PolicyState.Should().Be(CdcConnectOffsetStorePolicyState.Satisfied);
        second.PolicyState.Should().Be(CdcConnectOffsetStorePolicyState.Satisfied);
        second.CleanupPolicy.Should().Be("compact");
        second.ReplicationFactor.Should().Be(1);
        second.MinInSyncReplicas.Should().Be(1);
        second.OffsetStorageTopic.Should().Be(OffsetStoreTopic);
        second.WorkerKey.Should().Be("worker-1");
        A.CallTo(() =>
                adminClient.CreateTopicsAsync(A<IEnumerable<TopicSpecification>>._, A<CreateTopicsOptions>._)
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_tolerates_a_concurrent_creation_of_the_offset_store()
    {
        IAdminClient adminClient = A.Fake<IAdminClient>();
        A.CallTo(() => adminClient.GetMetadata(A<TimeSpan>._))
            .ReturnsNextFromSequence(EmptyCluster(), ClusterWith(Topic(OffsetStoreTopic, 25, 1)));
        A.CallTo(() =>
                adminClient.CreateTopicsAsync(A<IEnumerable<TopicSpecification>>._, A<CreateTopicsOptions>._)
            )
            .Throws(
                new CreateTopicsException([
                    new CreateTopicReport
                    {
                        Topic = OffsetStoreTopic,
                        Error = new Error(ErrorCode.TopicAlreadyExists),
                    },
                ])
            );
        StubConfigs(adminClient, CompactConfigs(1));

        CdcConnectOffsetStorePolicyObservation observation = await RunAsync(adminClient, ControlOptions());

        observation.PolicyState.Should().Be(CdcConnectOffsetStorePolicyState.Satisfied);
    }

    [Test]
    public async Task It_never_deletes_the_offset_store()
    {
        IAdminClient adminClient = Broker(Topic(OffsetStoreTopic, 25, 1), CompactConfigs(1));

        await RunAsync(adminClient, ControlOptions());

        A.CallTo(() => adminClient.DeleteTopicsAsync(A<IEnumerable<string>>._, A<DeleteTopicsOptions>._))
            .MustNotHaveHappened();
    }

    [TestCase("delete")]
    [TestCase("compact,delete")]
    public async Task It_rejects_an_offset_store_that_is_not_compact_only(string cleanupPolicy)
    {
        IAdminClient adminClient = Broker(Topic(OffsetStoreTopic, 25, 1), Configs(cleanupPolicy, 1));

        CdcConnectOffsetStorePolicyObservation observation = await RunAsync(adminClient, ControlOptions());

        observation.PolicyState.Should().Be(CdcConnectOffsetStorePolicyState.Invalid);
        observation.CleanupPolicy.Should().Be("delete");
        InvalidDiagnostics(observation)
            .Should()
            .Contain(diagnostic =>
                diagnostic.Expected == "compact"
                && diagnostic.Observed!.Contains("delete", StringComparison.Ordinal)
            );
    }

    [Test]
    public async Task It_rejects_a_replica_count_below_the_active_durability_profile()
    {
        IAdminClient adminClient = Broker(Topic(OffsetStoreTopic, 25, 1), CompactConfigs(2));

        CdcConnectOffsetStorePolicyObservation observation = await RunAsync(
            adminClient,
            ControlOptions(CdcControlOptions.ProductionDurabilityProfile)
        );

        observation.PolicyState.Should().Be(CdcConnectOffsetStorePolicyState.Invalid);
        observation.ReplicationFactor.Should().Be(1);
    }

    [Test]
    public async Task It_reads_the_least_replicated_partition_as_the_replica_count()
    {
        TopicMetadata underReplicated = new(
            OffsetStoreTopic,
            [Partition(0, 3), Partition(1, 1)],
            new Error(ErrorCode.NoError)
        );
        IAdminClient adminClient = Broker(underReplicated, CompactConfigs(2));

        CdcConnectOffsetStorePolicyObservation observation = await RunAsync(
            adminClient,
            ControlOptions(CdcControlOptions.ProductionDurabilityProfile)
        );

        observation.ReplicationFactor.Should().Be(1);
        observation.PolicyState.Should().Be(CdcConnectOffsetStorePolicyState.Invalid);
    }

    [Test]
    public async Task It_rejects_min_insync_replicas_below_the_active_durability_profile()
    {
        IAdminClient adminClient = Broker(Topic(OffsetStoreTopic, 25, 3), CompactConfigs(1));

        CdcConnectOffsetStorePolicyObservation observation = await RunAsync(
            adminClient,
            ControlOptions(CdcControlOptions.ProductionDurabilityProfile)
        );

        observation.PolicyState.Should().Be(CdcConnectOffsetStorePolicyState.Invalid);
        observation.MinInSyncReplicas.Should().Be(1);
    }

    [Test]
    public async Task It_rejects_min_insync_replicas_inherited_from_a_broker_default()
    {
        IAdminClient adminClient = Broker(
            Topic(OffsetStoreTopic, 25, 1),
            Configs("compact", 1, ConfigSource.DefaultConfig)
        );

        CdcConnectOffsetStorePolicyObservation observation = await RunAsync(adminClient, ControlOptions());

        observation.PolicyState.Should().Be(CdcConnectOffsetStorePolicyState.Invalid);
        observation.MinInSyncReplicas.Should().Be(1);
    }

    [Test]
    public async Task It_reports_unknown_when_min_insync_replicas_is_absent()
    {
        IAdminClient adminClient = Broker(
            Topic(OffsetStoreTopic, 25, 1),
            [
                new DescribeConfigsResult
                {
                    Entries = new Dictionary<string, ConfigEntryResult>(StringComparer.Ordinal)
                    {
                        [CdcKafkaAdminAdapter.CleanupPolicyConfigName] = new()
                        {
                            Name = CdcKafkaAdminAdapter.CleanupPolicyConfigName,
                            Value = "compact",
                            Source = ConfigSource.DynamicTopicConfig,
                        },
                    },
                },
            ]
        );

        CdcConnectOffsetStorePolicyObservation observation = await RunAsync(adminClient, ControlOptions());

        observation.PolicyState.Should().Be(CdcConnectOffsetStorePolicyState.Unknown);
        observation.MinInSyncReplicas.Should().BeNull();
    }

    [Test]
    public async Task It_reports_unknown_when_the_broker_is_unreachable()
    {
        IAdminClient adminClient = A.Fake<IAdminClient>();
        A.CallTo(() => adminClient.GetMetadata(A<TimeSpan>._))
            .Throws(new KafkaException(ErrorCode.Local_AllBrokersDown));

        CdcConnectOffsetStorePolicyObservation observation = await RunAsync(adminClient, ControlOptions());

        observation.PolicyState.Should().Be(CdcConnectOffsetStorePolicyState.Unknown);
        observation.AclState.Should().Be(CdcConnectOffsetStoreItemState.Unknown);
        observation.CleanupPolicy.Should().BeNull();
        observation.ReplicationFactor.Should().BeNull();
        observation.MinInSyncReplicas.Should().BeNull();
    }

    [Test]
    public async Task It_reports_unknown_when_the_offset_store_configuration_is_undescribable()
    {
        IAdminClient adminClient = A.Fake<IAdminClient>();
        A.CallTo(() => adminClient.GetMetadata(A<TimeSpan>._))
            .Returns(ClusterWith(Topic(OffsetStoreTopic, 25, 1)));
        A.CallTo(() =>
                adminClient.DescribeConfigsAsync(
                    A<IEnumerable<ConfigResource>>._,
                    A<DescribeConfigsOptions>._
                )
            )
            .Throws(new KafkaException(ErrorCode.RequestTimedOut));

        CdcConnectOffsetStorePolicyObservation observation = await RunAsync(adminClient, ControlOptions());

        observation.PolicyState.Should().Be(CdcConnectOffsetStorePolicyState.Unknown);
    }

    [Test]
    public async Task It_accepts_worker_only_read_write_and_describe_grants()
    {
        IAdminClient adminClient = Broker(
            Topic(OffsetStoreTopic, 25, 1),
            CompactConfigs(1),
            WorkerAcls(AclOperation.Read, AclOperation.Write, AclOperation.Describe)
        );

        CdcConnectOffsetStorePolicyObservation observation = await RunAsync(
            adminClient,
            ControlOptions(aclsEnabled: true)
        );

        observation.AclState.Should().Be(CdcConnectOffsetStoreItemState.Satisfied);
        observation.PolicyState.Should().Be(CdcConnectOffsetStorePolicyState.Satisfied);
    }

    [Test]
    public async Task It_rejects_a_missing_worker_grant()
    {
        IAdminClient adminClient = Broker(
            Topic(OffsetStoreTopic, 25, 1),
            CompactConfigs(1),
            WorkerAcls(AclOperation.Read, AclOperation.Write)
        );

        CdcConnectOffsetStorePolicyObservation observation = await RunAsync(
            adminClient,
            ControlOptions(aclsEnabled: true)
        );

        observation.AclState.Should().Be(CdcConnectOffsetStoreItemState.Invalid);
        observation.PolicyState.Should().Be(CdcConnectOffsetStorePolicyState.Invalid);
    }

    [Test]
    public async Task It_rejects_a_worker_grant_beyond_read_write_and_describe()
    {
        IAdminClient adminClient = Broker(
            Topic(OffsetStoreTopic, 25, 1),
            CompactConfigs(1),
            WorkerAcls(AclOperation.Read, AclOperation.Write, AclOperation.Describe, AclOperation.Delete)
        );

        CdcConnectOffsetStorePolicyObservation observation = await RunAsync(
            adminClient,
            ControlOptions(aclsEnabled: true)
        );

        observation.AclState.Should().Be(CdcConnectOffsetStoreItemState.Invalid);
    }

    [Test]
    public async Task It_rejects_an_instance_consumer_grant_on_the_offset_store()
    {
        IAdminClient adminClient = Broker(
            Topic(OffsetStoreTopic, 25, 1),
            CompactConfigs(1),
            new DescribeAclsResult
            {
                AclBindings =
                [
                    Acl(OffsetStoreTopic, WorkerPrincipal, AclOperation.Read),
                    Acl(OffsetStoreTopic, WorkerPrincipal, AclOperation.Write),
                    Acl(OffsetStoreTopic, WorkerPrincipal, AclOperation.Describe),
                    Acl(OffsetStoreTopic, ConsumerPrincipal, AclOperation.Read),
                ],
            }
        );

        CdcConnectOffsetStorePolicyObservation observation = await RunAsync(
            adminClient,
            ControlOptions(aclsEnabled: true)
        );

        observation.AclState.Should().Be(CdcConnectOffsetStoreItemState.Invalid);
    }

    [Test]
    public async Task It_rejects_an_over_broad_topic_pattern_reaching_the_offset_store()
    {
        IAdminClient adminClient = Broker(
            Topic(OffsetStoreTopic, 25, 1),
            CompactConfigs(1),
            new DescribeAclsResult
            {
                AclBindings =
                [
                    Acl(OffsetStoreTopic, WorkerPrincipal, AclOperation.Read),
                    Acl(OffsetStoreTopic, WorkerPrincipal, AclOperation.Write),
                    Acl(OffsetStoreTopic, WorkerPrincipal, AclOperation.Describe),
                    Acl("connect-", ConsumerPrincipal, AclOperation.Read, ResourcePatternType.Prefixed),
                ],
            }
        );

        CdcConnectOffsetStorePolicyObservation observation = await RunAsync(
            adminClient,
            ControlOptions(aclsEnabled: true)
        );

        observation.AclState.Should().Be(CdcConnectOffsetStoreItemState.Invalid);
    }

    [Test]
    public async Task It_rejects_a_deny_grant_for_the_worker()
    {
        IAdminClient adminClient = Broker(
            Topic(OffsetStoreTopic, 25, 1),
            CompactConfigs(1),
            new DescribeAclsResult
            {
                AclBindings =
                [
                    Acl(OffsetStoreTopic, WorkerPrincipal, AclOperation.Read),
                    Acl(OffsetStoreTopic, WorkerPrincipal, AclOperation.Write),
                    Acl(
                        OffsetStoreTopic,
                        WorkerPrincipal,
                        AclOperation.Describe,
                        permission: AclPermissionType.Deny
                    ),
                ],
            }
        );

        CdcConnectOffsetStorePolicyObservation observation = await RunAsync(
            adminClient,
            ControlOptions(aclsEnabled: true)
        );

        observation.AclState.Should().Be(CdcConnectOffsetStoreItemState.Invalid);
    }

    [Test]
    public async Task It_reports_unknown_acls_when_the_authorizer_cannot_be_queried()
    {
        IAdminClient adminClient = A.Fake<IAdminClient>();
        A.CallTo(() => adminClient.GetMetadata(A<TimeSpan>._))
            .Returns(ClusterWith(Topic(OffsetStoreTopic, 25, 1)));
        StubConfigs(adminClient, CompactConfigs(1));
        A.CallTo(() => adminClient.DescribeAclsAsync(A<AclBindingFilter>._, A<DescribeAclsOptions>._))
            .Returns(Task.FromResult<DescribeAclsResult>(null!));

        CdcConnectOffsetStorePolicyObservation observation = await RunAsync(
            adminClient,
            ControlOptions(aclsEnabled: true)
        );

        observation.AclState.Should().Be(CdcConnectOffsetStoreItemState.Unknown);
        observation.PolicyState.Should().Be(CdcConnectOffsetStorePolicyState.Unknown);
    }

    [Test]
    public async Task It_records_that_acls_were_not_verified_when_no_authorizer_is_configured()
    {
        IAdminClient adminClient = Broker(Topic(OffsetStoreTopic, 25, 1), CompactConfigs(1));

        CdcConnectOffsetStorePolicyObservation observation = await RunAsync(adminClient, ControlOptions());

        observation.AclState.Should().Be(CdcConnectOffsetStoreItemState.Satisfied);
        observation
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "connectOffsetStoreAclsNotEnforced")
            .Which.Component.Should()
            .Be(CdcDiagnosticComponent.ConnectOffsetStore);
        A.CallTo(() => adminClient.DescribeAclsAsync(A<AclBindingFilter>._, A<DescribeAclsOptions>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_reports_unknown_for_an_unrecognized_durability_profile()
    {
        IAdminClient adminClient = Broker(Topic(OffsetStoreTopic, 25, 1), CompactConfigs(1));
        CdcControlOptions options = ControlOptions();
        options.DurabilityProfile = "single-broker";

        CdcConnectOffsetStorePolicyObservation observation = await RunAsync(adminClient, options);

        observation.PolicyState.Should().Be(CdcConnectOffsetStorePolicyState.Unknown);
        A.CallTo(() => adminClient.GetMetadata(A<TimeSpan>._)).MustNotHaveHappened();
    }

    [Test]
    public async Task It_never_names_a_secret_in_offset_store_diagnostics()
    {
        IAdminClient adminClient = A.Fake<IAdminClient>();
        A.CallTo(() => adminClient.GetMetadata(A<TimeSpan>._))
            .Throws(
                new KafkaException(
                    new Error(ErrorCode.Local_Authentication, "sasl.password=hunter2 server=broker.internal")
                )
            );

        CdcConnectOffsetStorePolicyObservation observation = await RunAsync(adminClient, ControlOptions());

        string rendered = CdcJsonContract.Serialize(observation);
        rendered.Should().NotContain("hunter2");
        rendered.Should().NotContain("broker.internal");
    }

    private static async Task<CdcConnectOffsetStorePolicyObservation> RunAsync(
        IAdminClient adminClient,
        CdcControlOptions options,
        bool describe = false
    )
    {
        CdcKafkaAdminAdapter adapter = new(
            adminClient,
            Options.Create(options),
            new FixedTimeProvider(ObservedAt),
            NullLogger<CdcKafkaAdminAdapter>.Instance
        );

        CdcObservationContext context = new(OperationId, TargetIdentity, SourceFingerprint);
        CdcConnectOffsetStorePolicyObservation observation = describe
            ? await adapter.DescribeConnectOffsetStoreAsync(context, CancellationToken.None)
            : await adapter.EnsureConnectOffsetStoreAsync(context, CancellationToken.None);

        CdcConnectOffsetStorePolicyObservationValidator
            .Validate(
                observation,
                new(OperationId, TargetIdentity, SourceFingerprint, ObservedAt.AddMinutes(1))
            )
            .Succeeded.Should()
            .BeTrue("every emitted offset-store observation must satisfy its own contract");

        return observation;
    }

    private static IReadOnlyList<CdcDiagnostic> InvalidDiagnostics(
        CdcConnectOffsetStorePolicyObservation observation
    ) =>
        [
            .. observation.Diagnostics.Where(diagnostic =>
                diagnostic.Category == CdcDiagnosticCategory.ConnectOffsetStoreInvalid
            ),
        ];

    private static bool HasOffsetStoreSpecification(
        IEnumerable<TopicSpecification> specifications,
        short replicationFactor,
        int minInSyncReplicas
    )
    {
        TopicSpecification? specification = specifications.SingleOrDefault(candidate =>
            candidate.Name == OffsetStoreTopic
        );

        return specification is not null
            && specification.NumPartitions == CdcKafkaAdminAdapter.OffsetStorePartitionCount
            && specification.ReplicationFactor == replicationFactor
            && specification.Configs[CdcKafkaAdminAdapter.CleanupPolicyConfigName] == "compact"
            && specification.Configs[CdcKafkaAdminAdapter.MinInSyncReplicasConfigName]
                == minInSyncReplicas.ToString(CultureInfo.InvariantCulture);
    }

    private static IAdminClient Broker(
        TopicMetadata topicMetadata,
        List<DescribeConfigsResult> configs,
        DescribeAclsResult? acls = null
    )
    {
        IAdminClient adminClient = A.Fake<IAdminClient>();
        A.CallTo(() => adminClient.GetMetadata(A<TimeSpan>._)).Returns(ClusterWith(topicMetadata));
        StubConfigs(adminClient, configs);
        A.CallTo(() => adminClient.DescribeAclsAsync(A<AclBindingFilter>._, A<DescribeAclsOptions>._))
            .Returns(acls ?? new DescribeAclsResult { AclBindings = [] });

        return adminClient;
    }

    private static void StubConfigs(IAdminClient adminClient, List<DescribeConfigsResult> configs) =>
        A.CallTo(() =>
                adminClient.DescribeConfigsAsync(
                    A<IEnumerable<ConfigResource>>._,
                    A<DescribeConfigsOptions>._
                )
            )
            .Returns(configs);

    private static DescribeAclsResult WorkerAcls(params AclOperation[] operations) =>
        new()
        {
            AclBindings =
            [
                .. operations.Select(operation => Acl(OffsetStoreTopic, WorkerPrincipal, operation)),
            ],
        };

    private static AclBinding Acl(
        string resourceName,
        string principal,
        AclOperation operation,
        ResourcePatternType patternType = ResourcePatternType.Literal,
        AclPermissionType permission = AclPermissionType.Allow
    ) =>
        new()
        {
            Pattern = new ResourcePattern
            {
                Type = ResourceType.Topic,
                Name = resourceName,
                ResourcePatternType = patternType,
            },
            Entry = new AccessControlEntry
            {
                Principal = principal,
                Host = "*",
                Operation = operation,
                PermissionType = permission,
            },
        };

    private static List<DescribeConfigsResult> CompactConfigs(int minInSyncReplicas) =>
        Configs("compact", minInSyncReplicas);

    private static List<DescribeConfigsResult> Configs(
        string cleanupPolicy,
        int minInSyncReplicas,
        ConfigSource minInSyncReplicasSource = ConfigSource.DynamicTopicConfig
    ) =>
        [
            new DescribeConfigsResult
            {
                Entries = new Dictionary<string, ConfigEntryResult>(StringComparer.Ordinal)
                {
                    [CdcKafkaAdminAdapter.CleanupPolicyConfigName] = new()
                    {
                        Name = CdcKafkaAdminAdapter.CleanupPolicyConfigName,
                        Value = cleanupPolicy,
                        Source = ConfigSource.DynamicTopicConfig,
                    },
                    [CdcKafkaAdminAdapter.MinInSyncReplicasConfigName] = new()
                    {
                        Name = CdcKafkaAdminAdapter.MinInSyncReplicasConfigName,
                        Value = minInSyncReplicas.ToString(CultureInfo.InvariantCulture),
                        Source = minInSyncReplicasSource,
                        IsDefault = minInSyncReplicasSource == ConfigSource.DefaultConfig,
                    },
                },
            },
        ];

    private static Metadata EmptyCluster() => new([], [], 0, "broker");

    private static Metadata ClusterWith(TopicMetadata topicMetadata) => new([], [topicMetadata], 0, "broker");

    private static TopicMetadata Topic(string name, int partitionCount, int replicationFactor) =>
        new(
            name,
            [.. Enumerable.Range(0, partitionCount).Select(index => Partition(index, replicationFactor))],
            new Error(ErrorCode.NoError)
        );

    private static PartitionMetadata Partition(int partitionId, int replicationFactor)
    {
        int[] replicas = [.. Enumerable.Range(0, replicationFactor)];
        return new(partitionId, 0, replicas, replicas, new Error(ErrorCode.NoError));
    }

    private static CdcControlOptions ControlOptions(
        string durabilityProfile = CdcControlOptions.LocalDurabilityProfile,
        bool aclsEnabled = false
    ) =>
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
            ConnectOffsetStorageTopic = OffsetStoreTopic,
            DurabilityProfile = durabilityProfile,
            MaxRecordBytes = 4_194_304,
            AclsEnabled = aclsEnabled,
            ConnectorPrincipal = "User:connector",
            ConnectWorkerPrincipal = WorkerPrincipal,
            DmsBaseUrl = "http://localhost:8080",
            DmsBearerToken = "token",
        };

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
