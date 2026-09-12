// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using EdFi.DataManagementService.Backend.Ddl;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;
using ItemState = EdFi.DataManagementService.Core.DocumentCache.Cdc.CdcKafkaPolicyItemState;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture]
public partial class Given_CdcKafkaAdminAdapter
{
    private const string Sentinel = "secret-password-private-host-document-body";
    private IAdminClient _client = null!;
    private ICdcKafkaAuthorizationInspection _authorization = null!;
    private CdcKafkaAdminAdapter _adapter = null!;
    private CdcDeploymentRequest _request = null!;
    private CdcDeploymentKafkaPolicyPlan _plan = null!;
    private Metadata _metadata = null!;
    private Dictionary<string, ConfigEntryResult> _topicConfig = null!;
    private Dictionary<string, ConfigEntryResult> _brokerConfig = null!;
    private CdcKafkaAuthorizationDeploymentEvidence _authority = null!;
    private List<AclBinding> _grants = null!;
    private CdcTransportResult<CdcKafkaTopicEvidence> _initial = null!;

    [SetUp]
    public async Task Setup()
    {
        _request = CdcDeploymentRequestTestData.Request(
            worker: CdcDeploymentRequestTestData.Worker(
                authorization: CdcKafkaAuthorizationProfile.AuthorizationEnabled
            )
        );
        _plan = CdcDeploymentKafkaPolicy.Build(_request);
        _client = A.Fake<IAdminClient>(options => options.Strict());
        A.CallTo(() => _client.Dispose()).DoesNothing();
        _authorization = A.Fake<ICdcKafkaAuthorizationInspection>();
        _adapter = new(_client, _authorization)
        {
            DescribeCluster = _ =>
                Task.FromResult(new DescribeClusterResult { AuthorizedOperations = [AclOperation.Describe] }),
        };
        _metadata = MetadataFor(_plan.BindingTopics[0]);
        _topicConfig = _plan
            .BindingTopics[0]
            .Configuration.ToDictionary(
                pair => pair.Key,
                pair => Config(pair.Key, pair.Value, ConfigSource.DynamicTopicConfig)
            );
        _brokerConfig = new[]
        {
            "socket.request.max.bytes",
            "replica.fetch.max.bytes",
            "replica.fetch.response.max.bytes",
            "message.max.bytes",
        }.ToDictionary(key => key, key => Config(key, "104857600", ConfigSource.StaticBrokerConfig));
        _authority = new(true, [new(0, true, false, [])], []);
        _grants = _plan.OffsetStoreGrants.Concat(_plan.BindingGrants).Select(Binding).ToList();
        A.CallTo(() => _client.GetMetadata(A<string>._, A<TimeSpan>._)).ReturnsLazily(() => _metadata);
        A.CallTo(() => _client.GetMetadata(A<TimeSpan>._)).ReturnsLazily(() => _metadata);
        A.CallTo(() =>
                _client.DescribeConfigsAsync(A<IEnumerable<ConfigResource>>._, A<DescribeConfigsOptions>._)
            )
            .ReturnsLazily(
                (IEnumerable<ConfigResource> resources, DescribeConfigsOptions _) =>
                    Task.FromResult(
                        resources
                            .Select(resource => new DescribeConfigsResult
                            {
                                ConfigResource = resource,
                                Entries = resource.Type == ResourceType.Topic ? _topicConfig : _brokerConfig,
                            })
                            .ToList()
                    )
            );
        A.CallTo(() => _authorization.InspectAsync(A<IReadOnlyList<int>>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
                new CdcTransportResult<CdcKafkaAuthorizationDeploymentEvidence>.Observed(_authority)
            );
        A.CallTo(() => _client.DescribeAclsAsync(A<AclBindingFilter>._, A<DescribeAclsOptions>._))
            .ReturnsLazily(() => Task.FromResult(new DescribeAclsResult { AclBindings = _grants }));
        _initial = await InspectTopic();
        Fake.ClearRecordedCalls(_client);
    }

    [TearDown]
    public void Teardown()
    {
        _adapter.Dispose();
        _client.Dispose();
    }

    [Test]
    public void It_observes_actual_partition_assignments() =>
        Observed(_initial)
            .PartitionReplicas.Should()
            .BeEquivalentTo(new Dictionary<int, IReadOnlyList<int>> { [0] = new[] { 0 } });

    [TestCase(ConfigSource.DynamicTopicConfig, true)]
    [TestCase(ConfigSource.DynamicBrokerConfig, false)]
    [TestCase(ConfigSource.DynamicDefaultBrokerConfig, false)]
    [TestCase(ConfigSource.StaticBrokerConfig, false)]
    [TestCase(ConfigSource.DefaultConfig, false)]
    public async Task It_preserves_config_origin_even_when_is_default_is_false(
        ConfigSource source,
        bool explicitOverride
    )
    {
        _topicConfig["min.insync.replicas"] = Config("min.insync.replicas", "1", source);
        Observed(await InspectTopic())
            .Configuration["min.insync.replicas"]
            .IsTopicOverride.Should()
            .Be(explicitOverride);
    }

    [TestCase("sensitive")]
    [TestCase("null")]
    [TestCase("unknown")]
    [TestCase("unsupported")]
    [TestCase("wrong-name")]
    [TestCase("missing")]
    public async Task It_does_not_infer_unavailable_configuration(string scenario)
    {
        ConfigEntryResult entry = _topicConfig["min.insync.replicas"];
        switch (scenario)
        {
            case "sensitive":
                entry.IsSensitive = true;
                break;
            case "null":
                entry.Value = null!;
                break;
            case "unknown":
                entry.Source = ConfigSource.UnknownConfig;
                break;
            case "unsupported":
                entry.Source = (ConfigSource)999;
                break;
            case "wrong-name":
                entry.Name = Sentinel;
                break;
            case "missing":
                _topicConfig.Remove("min.insync.replicas");
                break;
        }
        Observed(await InspectTopic()).Configuration.Should().NotContainKey("min.insync.replicas");
    }

    [TestCase(ErrorCode.UnknownTopicOrPart, CdcTransportEvidenceState.Absent)]
    [TestCase(ErrorCode.TopicAuthorizationFailed, CdcTransportEvidenceState.Unavailable)]
    [TestCase(ErrorCode.SaslAuthenticationFailed, CdcTransportEvidenceState.Unavailable)]
    [TestCase(ErrorCode.Local_Transport, CdcTransportEvidenceState.Unavailable)]
    [TestCase(ErrorCode.LeaderNotAvailable, CdcTransportEvidenceState.Unavailable)]
    [TestCase(ErrorCode.RequestTimedOut, CdcTransportEvidenceState.Unavailable)]
    public async Task It_distinguishes_authoritative_absence_from_lookup_failure(
        ErrorCode error,
        CdcTransportEvidenceState state
    )
    {
        _metadata = MetadataFor(_plan.BindingTopics[0], error);
        (await InspectTopic()).State.Should().Be(state);
    }

    [TestCase("missing-topic")]
    [TestCase("duplicate-topic")]
    [TestCase("empty-partitions")]
    [TestCase("duplicate-partition")]
    [TestCase("partition-gap")]
    [TestCase("empty-replicas")]
    [TestCase("duplicate-replica")]
    [TestCase("negative-replica")]
    [TestCase("partition-error")]
    [TestCase("missing-config")]
    [TestCase("duplicate-config")]
    [TestCase("wrong-config")]
    public async Task It_rejects_malformed_topic_responses(string scenario)
    {
        TopicMetadata topic = _metadata.Topics[0];
        switch (scenario)
        {
            case "missing-topic":
                _metadata.Topics.Clear();
                break;
            case "duplicate-topic":
                _metadata.Topics.Add(topic);
                break;
            case "empty-partitions":
                topic.Partitions.Clear();
                break;
            case "duplicate-partition":
                topic.Partitions.Add(topic.Partitions[0]);
                break;
            case "partition-gap":
                topic.Partitions[0] = Partition(2, [0]);
                break;
            case "empty-replicas":
                topic.Partitions[0] = Partition(0, []);
                break;
            case "duplicate-replica":
                topic.Partitions[0] = Partition(0, [0, 0]);
                break;
            case "negative-replica":
                topic.Partitions[0] = Partition(0, [-1]);
                break;
            case "partition-error":
                topic.Partitions[0] = Partition(0, [0], ErrorCode.LeaderNotAvailable);
                break;
            default:
                A.CallTo(() =>
                        _client.DescribeConfigsAsync(
                            A<IEnumerable<ConfigResource>>._,
                            A<DescribeConfigsOptions>._
                        )
                    )
                    .ReturnsLazily(
                        (IEnumerable<ConfigResource> resources, DescribeConfigsOptions _) =>
                        {
                            DescribeConfigsResult config = new()
                            {
                                ConfigResource = resources.Single(),
                                Entries = _topicConfig,
                            };
                            if (scenario == "wrong-config")
                            {
                                config.ConfigResource = new() { Type = ResourceType.Broker, Name = Sentinel };
                            }
                            return Task.FromResult(
                                scenario switch
                                {
                                    "missing-config" => new List<DescribeConfigsResult>(),
                                    "duplicate-config" => [config, config],
                                    _ => [config],
                                }
                            );
                        }
                    );
                break;
        }
        (await InspectTopic()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
    }

    [Test]
    public async Task It_observes_every_broker_and_all_capacity_limits()
    {
        _metadata.Brokers.Add(new(1, "another-private-broker", 9092));
        // The native client routes these requests to a specific broker; its contract rejects
        // a batch containing more than one broker even when the simulated response could supply it.
        A.CallTo(() =>
                _client.DescribeConfigsAsync(
                    A<IEnumerable<ConfigResource>>.That.Matches(resources =>
                        resources.Count(resource => resource.Type == ResourceType.Broker) > 1
                    ),
                    A<DescribeConfigsOptions>._
                )
            )
            .Throws(new KafkaException(new Error(ErrorCode.Local_InvalidArg)));
        CdcKafkaBrokerEvidence evidence = Observed(
            await _adapter.InspectBrokersAsync(_request, CancellationToken.None)
        );
        evidence
            .Brokers.Should()
            .BeEquivalentTo(
                new[]
                {
                    new CdcKafkaBrokerCapacity(0, 104857600, 104857600, 104857600, 104857600),
                    new CdcKafkaBrokerCapacity(1, 104857600, 104857600, 104857600, 104857600),
                }
            );
        evidence.InventoryComplete.Should().BeTrue();
    }

    [TestCase("socket.request.max.bytes")]
    [TestCase("replica.fetch.max.bytes")]
    [TestCase("replica.fetch.response.max.bytes")]
    [TestCase("message.max.bytes")]
    public async Task It_requires_each_broker_limit(string key)
    {
        _brokerConfig.Remove(key);
        (await _adapter.InspectBrokersAsync(_request, CancellationToken.None))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
    }

    [TestCase("-1")]
    [TestCase("0")]
    [TestCase("not-a-number")]
    [TestCase("9223372036854775808")]
    public async Task It_rejects_malformed_broker_limits(string value)
    {
        _brokerConfig["message.max.bytes"].Value = value;
        (await _adapter.InspectBrokersAsync(_request, CancellationToken.None))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
    }

    [TestCase("no-brokers")]
    [TestCase("duplicate-broker")]
    [TestCase("negative-id")]
    [TestCase("changed-inventory")]
    [TestCase("changed-address")]
    public async Task It_rejects_incomplete_or_changed_broker_inventory(string scenario)
    {
        switch (scenario)
        {
            case "no-brokers":
                _metadata.Brokers.Clear();
                break;
            case "duplicate-broker":
                _metadata.Brokers.Add(_metadata.Brokers[0]);
                break;
            case "negative-id":
                _metadata.Brokers[0] = new(-1, Sentinel, 9092);
                break;
            default:
                Metadata after = MetadataFor(_plan.BindingTopics[0]);
                if (scenario == "changed-inventory")
                {
                    after.Brokers.Add(new(1, Sentinel, 9092));
                }
                else
                {
                    after.Brokers[0] = new(0, Sentinel, 9092);
                }
                A.CallTo(() => _client.GetMetadata(A<TimeSpan>._)).ReturnsNextFromSequence(_metadata, after);
                break;
        }
        (await _adapter.InspectBrokersAsync(_request, CancellationToken.None))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
    }

    [Test]
    public async Task It_requests_all_acl_patterns_resources_principals_hosts_and_permissions()
    {
        await _adapter.InspectAclsAsync(_request, CancellationToken.None);
        A.CallTo(() =>
                _client.DescribeAclsAsync(
                    A<AclBindingFilter>.That.Matches(filter =>
                        filter.PatternFilter.Type == ResourceType.Any
                        && filter.PatternFilter.ResourcePatternType == ResourcePatternType.Any
                        && IsNull(filter.PatternFilter.Name)
                        && IsNull(filter.EntryFilter.Principal)
                        && IsNull(filter.EntryFilter.Host)
                        && filter.EntryFilter.Operation == AclOperation.Any
                        && filter.EntryFilter.PermissionType == AclPermissionType.Any
                    ),
                    A<DescribeAclsOptions>.That.Matches(options =>
                        options.RequestTimeout == _request.Timing.CallTimeout
                    )
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [TestCase(CdcKafkaAclResourceType.Topic, CdcKafkaAclPattern.Literal, CdcKafkaAclPermission.Allow, "*")]
    [TestCase(
        CdcKafkaAclResourceType.Topic,
        CdcKafkaAclPattern.Prefixed,
        CdcKafkaAclPermission.Allow,
        "edfi."
    )]
    [TestCase(CdcKafkaAclResourceType.Group, CdcKafkaAclPattern.Literal, CdcKafkaAclPermission.Allow, "*")]
    [TestCase(CdcKafkaAclResourceType.Topic, CdcKafkaAclPattern.Literal, CdcKafkaAclPermission.Deny, "*")]
    public async Task It_preserves_unsafe_grants_for_policy_rejection(
        CdcKafkaAclResourceType type,
        CdcKafkaAclPattern pattern,
        CdcKafkaAclPermission permission,
        string resource
    )
    {
        CdcKafkaAclGrant grant = new(
            _request.WorkerPolicy.Consumers[0].Principal.Value,
            type,
            resource,
            CdcKafkaAclOperation.Read,
            pattern,
            permission
        );
        _grants.Add(Binding(grant));
        CdcTransportResult<CdcKafkaAclEvidence> evidence = await _adapter.InspectAclsAsync(
            _request,
            CancellationToken.None
        );
        Observed(evidence).Grants.Should().Contain(grant);
        CdcDeploymentKafkaPolicy.ValidateAcls(_request, evidence, false).UnsafeGrants.Should().BeTrue();
    }

    [TestCase("denied")]
    [TestCase("unsupported")]
    [TestCase("revoked-during-read")]
    public async Task It_requires_live_cluster_describe_permission_around_acl_inventory(string scenario)
    {
        int inspections = 0;
        _adapter.Dispose();
        _adapter = new(_client, _authorization)
        {
            DescribeCluster = options =>
            {
                options.IncludeAuthorizedOperations.Should().BeTrue();
                options.RequestTimeout.Should().Be(_request.Timing.CallTimeout);
                inspections++;
                return Task.FromResult(
                    new DescribeClusterResult
                    {
                        AuthorizedOperations = scenario switch
                        {
                            "unsupported" => null!,
                            "revoked-during-read" when inspections == 1 => [AclOperation.Describe],
                            _ => [],
                        },
                    }
                );
            },
        };
        // A successful empty native response must not hide unavailable/denied inspection.
        _grants.Clear();
        var result = await _adapter.InspectAclsAsync(_request, CancellationToken.None);
        result.State.Should().Be(CdcTransportEvidenceState.Unavailable);
        CdcDeploymentKafkaPolicy.ValidateAcls(_request, result, false).State.Should().Be(ItemState.Unknown);
        result
            .Diagnostics.Single()
            .Failure.Should()
            .Be(
                scenario == "unsupported"
                    ? CdcDeploymentFailure.Unavailable
                    : CdcDeploymentFailure.AuthenticationFailed
            );
    }

    [Test]
    public async Task It_accepts_empty_acl_inventory_when_live_inspection_permission_is_confirmed()
    {
        _grants.Clear();
        var result = await _adapter.InspectAclsAsync(_request, CancellationToken.None);
        Observed(result).Grants.Should().BeEmpty();
        CdcDeploymentKafkaPolicy.ValidateAcls(_request, result, false).CanAddMissingGrants.Should().BeTrue();
    }

    [TestCase(4, CdcKafkaAclResourceType.Cluster)]
    [TestCase(5, CdcKafkaAclResourceType.TransactionalId)]
    public async Task It_maps_acl_wire_resource_types_missing_from_the_client_enum(
        int wireType,
        CdcKafkaAclResourceType resourceType
    )
    {
        var principal = _request.WorkerPolicy.Consumers[0].Principal.Value;
        _grants.Add(
            new()
            {
                Pattern = new()
                {
                    Type = (ResourceType)wireType,
                    Name = resourceType == CdcKafkaAclResourceType.Cluster ? "kafka-cluster" : "transaction",
                    ResourcePatternType = ResourcePatternType.Literal,
                },
                Entry = new()
                {
                    Principal = principal,
                    Host = "*",
                    Operation = AclOperation.Alter,
                    PermissionType = AclPermissionType.Allow,
                },
            }
        );
        var evidence = await _adapter.InspectAclsAsync(_request, CancellationToken.None);
        Observed(evidence)
            .Grants.Should()
            .Contain(grant => grant.Principal == principal && grant.ResourceType == resourceType);
        CdcDeploymentKafkaPolicy.ValidateAcls(_request, evidence, false).UnsafeGrants.Should().BeTrue();
    }

    [Test]
    public async Task It_includes_group_inherited_grants_and_superusers_from_deployment_authority()
    {
        CdcKafkaAclGrant inherited = new(
            "consumer",
            CdcKafkaAclResourceType.Topic,
            _plan.OffsetStore.Name,
            CdcKafkaAclOperation.Read
        );
        _authority = _authority with
        {
            Brokers = [new(0, true, true, ["consumer"])],
            InheritedGrants = [inherited],
        };
        CdcTransportResult<CdcKafkaAclEvidence> result = await _adapter.InspectAclsAsync(
            _request,
            CancellationToken.None
        );
        Observed(result).Grants.Should().Contain(inherited);
        Observed(result).SuperuserPrincipals.Should().Contain("consumer");
        Observed(result).AllowEveryoneIfNoAclFound.Should().BeTrue();
        CdcDeploymentKafkaPolicy.ValidateAcls(_request, result, false).UnsafeGrants.Should().BeTrue();
    }

    [TestCase("incomplete")]
    [TestCase("missing-broker")]
    [TestCase("duplicate-broker")]
    [TestCase("wrong-broker")]
    [TestCase("mixed-authorizers")]
    [TestCase("malformed-inherited")]
    [TestCase("missing-superuser")]
    [TestCase("unavailable")]
    [TestCase("absent")]
    [TestCase("unauthorized")]
    public async Task It_keeps_incomplete_authorization_evidence_unknown(string scenario)
    {
        switch (scenario)
        {
            case "incomplete":
                _authority = _authority with { InventoryComplete = false };
                break;
            case "missing-broker":
                _authority = _authority with { Brokers = [] };
                break;
            case "duplicate-broker":
                _authority = _authority with { Brokers = [_authority.Brokers[0], _authority.Brokers[0]] };
                break;
            case "wrong-broker":
                _authority = _authority with { Brokers = [new(1, true, false, [])] };
                break;
            case "mixed-authorizers":
                _metadata.Brokers.Add(new(1, Sentinel, 9092));
                _authority = _authority with
                {
                    Brokers = [new(0, true, false, []), new(1, false, false, [])],
                };
                break;
            case "malformed-inherited":
                _authority = _authority with
                {
                    InheritedGrants =
                    [
                        new("", CdcKafkaAclResourceType.Topic, "*", CdcKafkaAclOperation.Read),
                    ],
                };
                break;
            case "missing-superuser":
                _authority = _authority with { Brokers = [new(0, true, false, [""])] };
                break;
            default:
                CdcDeploymentFailure failure =
                    scenario == "unauthorized"
                        ? CdcDeploymentFailure.AuthenticationFailed
                        : CdcDeploymentFailure.Unavailable;
                A.CallTo(() => _authorization.InspectAsync(A<IReadOnlyList<int>>._, A<CancellationToken>._))
                    .Returns(
                        scenario == "absent"
                            ? new CdcTransportResult<CdcKafkaAuthorizationDeploymentEvidence>.Absent()
                            : new CdcTransportResult<CdcKafkaAuthorizationDeploymentEvidence>.Unavailable(
                                new(CdcDeploymentComponent.Kafka, failure)
                            )
                    );
                break;
        }
        CdcTransportResult<CdcKafkaAclEvidence> evidence = await _adapter.InspectAclsAsync(
            _request,
            CancellationToken.None
        );
        evidence.State.Should().Be(CdcTransportEvidenceState.Unavailable);
        CdcDeploymentKafkaPolicy.ValidateAcls(_request, evidence, false).State.Should().Be(ItemState.Unknown);
    }

    [Test]
    public async Task It_explicitly_observes_authorization_disabled_without_claiming_enabled_policy()
    {
        _authority = new(true, [new(0, false, false, [])], []);
        CdcTransportResult<CdcKafkaAclEvidence> evidence = await _adapter.InspectAclsAsync(
            _request,
            CancellationToken.None
        );
        Observed(evidence).AuthorizationEnabled.Should().BeFalse();
        CdcDeploymentKafkaPolicy.ValidateAcls(_request, evidence, false).State.Should().Be(ItemState.Invalid);
        CdcDeploymentRequest local = CdcDeploymentRequestTestData.Request();
        CdcDeploymentKafkaPolicy.ValidateAcls(local, evidence, false).State.Should().Be(ItemState.Satisfied);
        A.CallTo(() => _client.DescribeAclsAsync(A<AclBindingFilter>._, A<DescribeAclsOptions>._))
            .MustNotHaveHappened();
    }

    [TestCase("resource")]
    [TestCase("operation")]
    [TestCase("pattern")]
    [TestCase("permission")]
    [TestCase("principal")]
    [TestCase("host")]
    [TestCase("name")]
    public async Task It_rejects_malformed_acl_responses(string scenario)
    {
        AclBinding binding = _grants[0];
        switch (scenario)
        {
            case "resource":
                binding.Pattern.Type = ResourceType.Unknown;
                break;
            case "operation":
                binding.Entry.Operation = AclOperation.Unknown;
                break;
            case "pattern":
                binding.Pattern.ResourcePatternType = ResourcePatternType.Any;
                break;
            case "permission":
                binding.Entry.PermissionType = AclPermissionType.Any;
                break;
            case "principal":
                binding.Entry.Principal = "";
                break;
            case "host":
                binding.Entry.Host = null!;
                break;
            case "name":
                binding.Pattern.Name = "";
                break;
        }
        (await _adapter.InspectAclsAsync(_request, CancellationToken.None))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
    }

    [TestCase("success")]
    [TestCase("conflict")]
    [TestCase("timeout")]
    [TestCase("transport")]
    public async Task It_reconciles_topic_creation_from_live_state_for_every_outcome(string outcome)
    {
        _metadata = MetadataFor(_plan.BindingTopics[0], ErrorCode.UnknownTopicOrPart);
        A.CallTo(() =>
                _client.CreateTopicsAsync(A<IEnumerable<TopicSpecification>>._, A<CreateTopicsOptions>._)
            )
            .ReturnsLazily(() =>
            {
                _metadata = MetadataFor(_plan.BindingTopics[0]);
                return Outcome(outcome);
            });
        CdcTransportResult<CdcKafkaTopicEvidence> result = await _adapter.CreateMissingTopicAsync(
            _request,
            _plan.BindingTopics[0],
            CancellationToken.None
        );
        result.State.Should().Be(CdcTransportEvidenceState.Observed);
        A.CallTo(() => _client.GetMetadata(_plan.BindingTopics[0].Name, _request.Timing.CallTimeout))
            .MustHaveHappenedTwiceExactly();
        A.CallTo(() =>
                _client.CreateTopicsAsync(
                    A<IEnumerable<TopicSpecification>>.That.Matches(topics =>
                        topics.Single().Name == _plan.BindingTopics[0].Name
                        && topics.Single().NumPartitions == _plan.BindingTopics[0].PartitionCount
                        && topics.Single().ReplicationFactor == _plan.BindingTopics[0].ReplicationFactor
                        && topics.Single().Configs.Count == _plan.BindingTopics[0].Configuration.Count
                    ),
                    A<CreateTopicsOptions>.That.Matches(options =>
                        options.RequestTimeout == _request.Timing.CallTimeout
                        && options.OperationTimeout == _request.Timing.CallTimeout
                    )
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_preserves_incompatible_existing_topics_without_creation_or_repair()
    {
        _topicConfig["cleanup.policy"].Value = "delete";
        _metadata.Topics[0].Partitions.Add(Partition(1, [0, 1]));
        CdcKafkaTopicEvidence result = Observed(
            await _adapter.CreateMissingTopicAsync(_request, _plan.BindingTopics[0], CancellationToken.None)
        );
        result.Configuration["cleanup.policy"].Value.Should().Be("delete");
        result.PartitionReplicas.Should().HaveCount(2);
        // Strict fake permits no mutation method in this scenario.
    }

    [Test]
    public async Task It_never_creates_a_topic_when_presence_is_unknown()
    {
        _metadata = MetadataFor(_plan.BindingTopics[0], ErrorCode.TopicAuthorizationFailed);
        (await _adapter.CreateMissingTopicAsync(_request, _plan.BindingTopics[0], CancellationToken.None))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        A.CallTo(() =>
                _client.CreateTopicsAsync(A<IEnumerable<TopicSpecification>>._, A<CreateTopicsOptions>._)
            )
            .MustNotHaveHappened();
    }

    [TestCase("name")]
    [TestCase("partitions")]
    [TestCase("replicas")]
    [TestCase("config")]
    public async Task It_rejects_creation_intent_outside_the_binding_policy(string scenario)
    {
        CdcKafkaTopicIntent intent = _plan.BindingTopics[0];
        intent = scenario switch
        {
            "name" => intent with { Name = Sentinel },
            "partitions" => intent with { PartitionCount = 100 },
            "replicas" => intent with { ReplicationFactor = 10 },
            _ => intent with { Configuration = new Dictionary<string, string>() },
        };
        CdcTransportResult<CdcKafkaTopicEvidence> result = await _adapter.CreateMissingTopicAsync(
            _request,
            intent,
            CancellationToken.None
        );
        result.Diagnostics.Single().Failure.Should().Be(CdcDeploymentFailure.InvalidInput);
        Fake.GetCalls(_client).Should().BeEmpty();
    }

    [TestCase("success", CdcDeploymentFailure.ValidationFailed)]
    [TestCase("timeout", CdcDeploymentFailure.Timeout)]
    [TestCase("conflict", CdcDeploymentFailure.Conflict)]
    public async Task It_never_reports_creation_success_from_acknowledgement_alone(
        string outcome,
        CdcDeploymentFailure failure
    )
    {
        _metadata = MetadataFor(_plan.BindingTopics[0], ErrorCode.UnknownTopicOrPart);
        A.CallTo(() =>
                _client.CreateTopicsAsync(A<IEnumerable<TopicSpecification>>._, A<CreateTopicsOptions>._)
            )
            .ReturnsLazily(() => Outcome(outcome));
        (await _adapter.CreateMissingTopicAsync(_request, _plan.BindingTopics[0], CancellationToken.None))
            .Diagnostics.Single()
            .Failure.Should()
            .Be(failure);
    }

    [TestCase(true, "success")]
    [TestCase(false, "success")]
    [TestCase(false, "timeout")]
    [TestCase(false, "conflict")]
    public async Task It_adds_only_missing_safe_grants_and_reinspects(bool sharedOffsets, string outcome)
    {
        CdcKafkaAclGrant missing = (sharedOffsets ? _plan.OffsetStoreGrants : _plan.BindingGrants)[0];
        _grants.RemoveAll(binding => binding.Equals(Binding(missing)));
        A.CallTo(() => _client.CreateAclsAsync(A<IEnumerable<AclBinding>>._, A<CreateAclsOptions>._))
            .ReturnsLazily(
                (IEnumerable<AclBinding> bindings, CreateAclsOptions _) =>
                {
                    _grants.AddRange(bindings);
                    return Outcome(outcome);
                }
            );
        CdcTransportResult<CdcKafkaAclEvidence> result = await _adapter.ReconcileMissingGrantsAsync(
            _request,
            sharedOffsets,
            CancellationToken.None
        );
        CdcDeploymentKafkaPolicy
            .ValidateAcls(_request, result, sharedOffsets)
            .State.Should()
            .Be(ItemState.Satisfied);
        A.CallTo(() =>
                _client.CreateAclsAsync(
                    A<IEnumerable<AclBinding>>.That.Matches(bindings =>
                        bindings.Count() == 1 && bindings.Single().Equals(Binding(missing))
                    ),
                    A<CreateAclsOptions>.That.Matches(options =>
                        options.RequestTimeout == _request.Timing.CallTimeout
                    )
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _client.DescribeAclsAsync(A<AclBindingFilter>._, A<DescribeAclsOptions>._))
            .MustHaveHappenedTwiceExactly();
    }

    [TestCase("complete")]
    [TestCase("unsafe")]
    [TestCase("unknown")]
    public async Task It_does_not_mutate_acls_without_missing_safe_grants(string scenario)
    {
        if (scenario == "unsafe")
        {
            _grants.Clear();
            _grants.Add(
                Binding(new("consumer", CdcKafkaAclResourceType.Topic, "*", CdcKafkaAclOperation.Read))
            );
        }
        if (scenario == "unknown")
        {
            _authority = _authority with { InventoryComplete = false };
        }
        await _adapter.ReconcileMissingGrantsAsync(_request, false, CancellationToken.None);
        A.CallTo(() => _client.CreateAclsAsync(A<IEnumerable<AclBinding>>._, A<CreateAclsOptions>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_keeps_partial_acl_creation_invalid_after_acknowledgement()
    {
        _grants.Clear();
        A.CallTo(() => _client.CreateAclsAsync(A<IEnumerable<AclBinding>>._, A<CreateAclsOptions>._))
            .Returns(Task.CompletedTask);
        CdcTransportResult<CdcKafkaAclEvidence> result = await _adapter.ReconcileMissingGrantsAsync(
            _request,
            false,
            CancellationToken.None
        );
        CdcDeploymentKafkaPolicy.ValidateAcls(_request, result, false).State.Should().Be(ItemState.Invalid);
    }

    [TestCase("topic")]
    [TestCase("brokers")]
    [TestCase("acls")]
    [TestCase("create")]
    [TestCase("grants")]
    public async Task It_preserves_caller_cancellation_before_any_call(string operation)
    {
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        Func<Task> act = () => Invoke(operation, cancellation.Token);
        (await act.Should().ThrowAsync<OperationCanceledException>())
            .Which.CancellationToken.Should()
            .Be(cancellation.Token);
        Fake.GetCalls(_client).Should().BeEmpty();
    }

    [Test]
    public async Task It_cancels_an_inflight_native_wait_without_exposing_exception_text()
    {
        TaskCompletionSource<List<DescribeConfigsResult>> pending = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        A.CallTo(() =>
                _client.DescribeConfigsAsync(A<IEnumerable<ConfigResource>>._, A<DescribeConfigsOptions>._)
            )
            .ReturnsLazily(() =>
            {
                entered.SetResult();
                return pending.Task;
            });
        using CancellationTokenSource cancellation = new();
        Task<CdcTransportResult<CdcKafkaTopicEvidence>> running = _adapter.InspectTopicAsync(
            _request,
            _plan.BindingTopics[0].Name,
            cancellation.Token
        );
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        Func<Task> act = () => running;
        (await act.Should().ThrowAsync<OperationCanceledException>())
            .Which.CancellationToken.Should()
            .Be(cancellation.Token);
        pending.SetResult([]);
    }

    [Test]
    public async Task It_bounds_a_native_call_that_does_not_finish()
    {
        _request = WithTimeout(_request, TimeSpan.FromMilliseconds(20));
        TaskCompletionSource<List<DescribeConfigsResult>> pending = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        A.CallTo(() =>
                _client.DescribeConfigsAsync(A<IEnumerable<ConfigResource>>._, A<DescribeConfigsOptions>._)
            )
            .Returns(pending.Task);
        CdcTransportResult<CdcKafkaTopicEvidence> result = await InspectTopic();
        result.Diagnostics.Single().Failure.Should().Be(CdcDeploymentFailure.Timeout);
        pending.SetResult([]);
    }

    [Test]
    public async Task It_propagates_timeout_to_the_deployment_inspection_token()
    {
        _request = WithTimeout(_request, TimeSpan.FromMilliseconds(20));
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        A.CallTo(() => _authorization.InspectAsync(A<IReadOnlyList<int>>._, A<CancellationToken>._))
            .ReturnsLazily(
                async (IReadOnlyList<int> _, CancellationToken token) =>
                {
                    using CancellationTokenRegistration registration = token.Register(() =>
                        cancelled.TrySetResult()
                    );
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    CdcTransportResult<CdcKafkaAuthorizationDeploymentEvidence> result =
                        new CdcTransportResult<CdcKafkaAuthorizationDeploymentEvidence>.Observed(_authority);
                    return result;
                }
            );
        CdcTransportResult<CdcKafkaAclEvidence> result = await _adapter.InspectAclsAsync(
            _request,
            CancellationToken.None
        );
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        result.Diagnostics.Single().Failure.Should().Be(CdcDeploymentFailure.Timeout);
    }

    [TestCase(ErrorCode.Local_Authentication, CdcDeploymentFailure.AuthenticationFailed)]
    [TestCase(ErrorCode.SaslAuthenticationFailed, CdcDeploymentFailure.AuthenticationFailed)]
    [TestCase(ErrorCode.TopicAuthorizationFailed, CdcDeploymentFailure.AuthenticationFailed)]
    [TestCase(ErrorCode.GroupAuthorizationFailed, CdcDeploymentFailure.AuthenticationFailed)]
    [TestCase(ErrorCode.ClusterAuthorizationFailed, CdcDeploymentFailure.AuthenticationFailed)]
    [TestCase(ErrorCode.Local_TimedOut, CdcDeploymentFailure.Timeout)]
    [TestCase(ErrorCode.Local_Transport, CdcDeploymentFailure.Unavailable)]
    public async Task It_sanitizes_native_failures(ErrorCode error, CdcDeploymentFailure failure)
    {
        A.CallTo(() => _client.GetMetadata(A<string>._, A<TimeSpan>._))
            .Throws(new KafkaException(new Error(error, Sentinel)));
        CdcTransportResult<CdcKafkaTopicEvidence> result = await InspectTopic();
        result.Diagnostics.Single().Failure.Should().Be(failure);
        (JsonSerializer.Serialize(result) + result + result.Diagnostics.Single().Message)
            .Should()
            .NotContain(Sentinel);
    }

    [Test]
    public async Task It_excludes_raw_evidence_and_creation_inputs_from_serialization_and_logging()
    {
        _topicConfig["cleanup.policy"].Value = Sentinel;
        CdcTransportResult<CdcKafkaTopicEvidence> result = await InspectTopic();
        CdcKafkaTopicIntent intent = _plan.BindingTopics[0] with
        {
            Name = Sentinel,
            Configuration = new Dictionary<string, string> { [Sentinel] = Sentinel },
        };
        string output =
            JsonSerializer.Serialize(result)
            + result
            + JsonSerializer.Serialize(Observed(result))
            + Observed(result)
            + JsonSerializer.Serialize(intent)
            + intent
            + JsonSerializer.Serialize(_authority)
            + _authority;
        output.Should().NotContain(Sentinel).And.NotContain(_plan.BindingTopics[0].Name);
    }

    [Test]
    public void It_builds_a_safe_native_client_and_owns_its_lifetime()
    {
        CdcTransportResult<CdcKafkaAdminAdapter> result = CdcKafkaAdminAdapter.Create(
            new AdminClientConfig { BootstrapServers = "localhost:1" },
            _authorization
        );
        using CdcKafkaAdminAdapter adapter = Observed(result);
        adapter.ToString().Should().Be(nameof(CdcKafkaAdminAdapter));
    }

    [Test]
    public void It_sanitizes_invalid_native_client_configuration()
    {
        AdminClientConfig config = new();
        config.Set(Sentinel, Sentinel);
        CdcTransportResult<CdcKafkaAdminAdapter> result = CdcKafkaAdminAdapter.Create(config, _authorization);
        result.State.Should().Be(CdcTransportEvidenceState.Unavailable);
        (JsonSerializer.Serialize(result) + result).Should().NotContain(Sentinel);
    }

    [TestCase(ErrorCode.TopicAuthorizationFailed, CdcDeploymentFailure.AuthenticationFailed)]
    [TestCase(ErrorCode.RequestTimedOut, CdcDeploymentFailure.Timeout)]
    [TestCase(ErrorCode.UnknownTopicOrPart, CdcDeploymentFailure.Unavailable)]
    public async Task It_preserves_per_resource_configuration_failure_classification(
        ErrorCode code,
        CdcDeploymentFailure failure
    )
    {
        A.CallTo(() =>
                _client.DescribeConfigsAsync(A<IEnumerable<ConfigResource>>._, A<DescribeConfigsOptions>._)
            )
            .Throws(
                new DescribeConfigsException([
                    new()
                    {
                        ConfigResource = new() { Type = ResourceType.Topic, Name = Sentinel },
                        Error = new(code, Sentinel),
                    },
                ])
            );
        CdcTransportResult<CdcKafkaTopicEvidence> result = await InspectTopic();
        result.Diagnostics.Single().Failure.Should().Be(failure);
        JsonSerializer.Serialize(result).Should().NotContain(Sentinel);
    }

    [TestCase(ErrorCode.TopicAlreadyExists, CdcDeploymentFailure.Conflict)]
    [TestCase(ErrorCode.TopicAuthorizationFailed, CdcDeploymentFailure.AuthenticationFailed)]
    [TestCase(ErrorCode.RequestTimedOut, CdcDeploymentFailure.Timeout)]
    public async Task It_preserves_per_topic_creation_failure_when_reconciliation_still_finds_absence(
        ErrorCode code,
        CdcDeploymentFailure failure
    )
    {
        _metadata = MetadataFor(_plan.BindingTopics[0], ErrorCode.UnknownTopicOrPart);
        A.CallTo(() =>
                _client.CreateTopicsAsync(A<IEnumerable<TopicSpecification>>._, A<CreateTopicsOptions>._)
            )
            .Throws(new CreateTopicsException([new() { Topic = Sentinel, Error = new(code, Sentinel) }]));
        CdcTransportResult<CdcKafkaTopicEvidence> result = await _adapter.CreateMissingTopicAsync(
            _request,
            _plan.BindingTopics[0],
            CancellationToken.None
        );
        result.Diagnostics.Single().Failure.Should().Be(failure);
        JsonSerializer.Serialize(result).Should().NotContain(Sentinel);
    }

    [Test]
    public async Task It_reconciles_an_actual_timed_out_creation_wait_after_the_broker_committed()
    {
        _request = WithTimeout(_request, TimeSpan.FromMilliseconds(100));
        _metadata = MetadataFor(_plan.BindingTopics[0], ErrorCode.UnknownTopicOrPart);
        TaskCompletionSource pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        A.CallTo(() =>
                _client.CreateTopicsAsync(A<IEnumerable<TopicSpecification>>._, A<CreateTopicsOptions>._)
            )
            .ReturnsLazily(() =>
            {
                _metadata = MetadataFor(_plan.BindingTopics[0]);
                return pending.Task;
            });
        CdcTransportResult<CdcKafkaTopicEvidence> result = await _adapter.CreateMissingTopicAsync(
            _request,
            _plan.BindingTopics[0],
            CancellationToken.None
        );
        result.State.Should().Be(CdcTransportEvidenceState.Observed);
        pending.SetResult();
    }

    [Test]
    public async Task It_propagates_cancellation_of_a_mutation_and_leaves_reconciliation_to_the_next_invocation()
    {
        _metadata = MetadataFor(_plan.BindingTopics[0], ErrorCode.UnknownTopicOrPart);
        TaskCompletionSource pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        A.CallTo(() =>
                _client.CreateTopicsAsync(A<IEnumerable<TopicSpecification>>._, A<CreateTopicsOptions>._)
            )
            .ReturnsLazily(() =>
            {
                entered.SetResult();
                return pending.Task;
            });
        using CancellationTokenSource cancellation = new();
        Task<CdcTransportResult<CdcKafkaTopicEvidence>> running = _adapter.CreateMissingTopicAsync(
            _request,
            _plan.BindingTopics[0],
            cancellation.Token
        );
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        Func<Task> act = () => running;
        (await act.Should().ThrowAsync<OperationCanceledException>())
            .Which.CancellationToken.Should()
            .Be(cancellation.Token);
        A.CallTo(() => _client.GetMetadata(A<string>._, A<TimeSpan>._)).MustHaveHappenedOnceExactly();
        pending.SetResult();
    }

    [TestCase(CdcProvider.Postgresql, CdcKafkaTopicRole.Public)]
    [TestCase(CdcProvider.Postgresql, CdcKafkaTopicRole.Progress)]
    [TestCase(CdcProvider.Postgresql, CdcKafkaTopicRole.SharedOffsets)]
    [TestCase(CdcProvider.SqlServer, CdcKafkaTopicRole.Public)]
    [TestCase(CdcProvider.SqlServer, CdcKafkaTopicRole.Progress)]
    [TestCase(CdcProvider.SqlServer, CdcKafkaTopicRole.SchemaHistory)]
    [TestCase(CdcProvider.SqlServer, CdcKafkaTopicRole.SharedOffsets)]
    public async Task It_creates_each_policy_topic_with_exact_configuration(
        CdcProvider provider,
        CdcKafkaTopicRole role
    )
    {
        _request = CdcDeploymentRequestTestData.Request(provider);
        _plan = CdcDeploymentKafkaPolicy.Build(_request);
        CdcKafkaTopicIntent intent = _plan
            .BindingTopics.Append(_plan.OffsetStore)
            .Single(topic => topic.Role == role);
        _topicConfig = intent.Configuration.ToDictionary(
            pair => pair.Key,
            pair => Config(pair.Key, pair.Value, ConfigSource.DynamicTopicConfig)
        );
        _metadata = MetadataFor(intent, ErrorCode.UnknownTopicOrPart);
        List<TopicSpecification> submitted = [];
        A.CallTo(() =>
                _client.CreateTopicsAsync(A<IEnumerable<TopicSpecification>>._, A<CreateTopicsOptions>._)
            )
            .ReturnsLazily(
                (IEnumerable<TopicSpecification> topics, CreateTopicsOptions _) =>
                {
                    submitted.AddRange(topics);
                    _metadata = MetadataFor(intent);
                    return Task.CompletedTask;
                }
            );
        CdcTransportResult<CdcKafkaTopicEvidence> result = await _adapter.CreateMissingTopicAsync(
            _request,
            intent,
            CancellationToken.None
        );
        result.State.Should().Be(CdcTransportEvidenceState.Observed);
        submitted.Should().ContainSingle().Which.Configs.Should().BeEquivalentTo(intent.Configuration);
    }

    private static bool IsNull(string value) => value is null;

    private Task<CdcTransportResult<CdcKafkaTopicEvidence>> InspectTopic() =>
        _adapter.InspectTopicAsync(_request, _plan.BindingTopics[0].Name, CancellationToken.None);

    private async Task Invoke(string operation, CancellationToken token)
    {
        switch (operation)
        {
            case "topic":
                await _adapter.InspectTopicAsync(_request, _plan.BindingTopics[0].Name, token);
                break;
            case "brokers":
                await _adapter.InspectBrokersAsync(_request, token);
                break;
            case "acls":
                await _adapter.InspectAclsAsync(_request, token);
                break;
            case "create":
                await _adapter.CreateMissingTopicAsync(_request, _plan.BindingTopics[0], token);
                break;
            case "grants":
                await _adapter.ReconcileMissingGrantsAsync(_request, false, token);
                break;
        }
    }

    private static Task Outcome(string outcome) =>
        outcome switch
        {
            "conflict" => Task.FromException(
                new KafkaException(new Error(ErrorCode.TopicAlreadyExists, Sentinel))
            ),
            "timeout" => Task.FromException(new TimeoutException(Sentinel)),
            "transport" => Task.FromException(
                new KafkaException(new Error(ErrorCode.Local_Transport, Sentinel))
            ),
            _ => Task.CompletedTask,
        };

    private static T Observed<T>(CdcTransportResult<T> result)
        where T : notnull => result.Should().BeOfType<CdcTransportResult<T>.Observed>().Subject.Value;

    private static ConfigEntryResult Config(string key, string value, ConfigSource source) =>
        new()
        {
            Name = key,
            Value = value,
            Source = source,
            IsDefault = false,
        };

    private static Metadata MetadataFor(CdcKafkaTopicIntent intent, ErrorCode error = ErrorCode.NoError) =>
        new(
            [new(0, "private-broker", 9092)],
            [
                new(
                    intent.Name,
                    Enumerable.Range(0, intent.PartitionCount).Select(id => Partition(id, [0])).ToList(),
                    new(error)
                ),
            ],
            0,
            "private-broker"
        );

    private static PartitionMetadata Partition(int id, int[] replicas, ErrorCode error = ErrorCode.NoError) =>
        new(id, 0, replicas, replicas, new(error));

    private static AclBinding Binding(CdcKafkaAclGrant grant) =>
        new()
        {
            Pattern = new()
            {
                Type = Enum.Parse<ResourceType>(grant.ResourceType.ToString()),
                Name = grant.ResourceName,
                ResourcePatternType = Enum.Parse<ResourcePatternType>(grant.Pattern.ToString()),
            },
            Entry = new()
            {
                Principal = grant.Principal,
                Host = grant.Host,
                Operation = Enum.Parse<AclOperation>(grant.Operation.ToString()),
                PermissionType = Enum.Parse<AclPermissionType>(grant.Permission.ToString()),
            },
        };

    private static CdcDeploymentRequest WithTimeout(CdcDeploymentRequest request, TimeSpan timeout) =>
        new(
            request.Binding,
            request.DmsSettings,
            request.ProviderSetup,
            request.ConnectEndpoint,
            request.WorkerMetricsEndpoint,
            request.ConnectorPolicy,
            request.WorkerPolicy,
            request.ProviderConnectionProperties,
            request.KafkaClientSecurityProperties,
            new(timeout, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1))
        );
}
