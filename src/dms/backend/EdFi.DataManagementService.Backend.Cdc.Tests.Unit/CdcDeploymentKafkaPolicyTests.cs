// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using NUnit.Framework;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;
using ItemState = EdFi.DataManagementService.Core.DocumentCache.Cdc.CdcKafkaPolicyItemState;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture]
public class Given_CdcDeploymentKafkaPolicy
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
    private CdcDeploymentRequest _request = null!;
    private CdcDeploymentKafkaPolicyPlan _plan = null!;
    private CdcKafkaDeploymentEvidence _evidence = null!;
    private CoreCdc.CdcKafkaPolicyObservation _observation = null!;

    [SetUp]
    public void Setup()
    {
        _request = Request();
        _plan = CdcDeploymentKafkaPolicy.Build(_request);
        _evidence = Evidence(_request);
        _observation = Observe();
    }

    [Test]
    public void It_accepts_the_initial_complete_observation() =>
        _observation.PolicyState.Should().Be(CoreCdc.CdcKafkaPolicyState.Satisfied);

    [TestCase(CdcProvider.Postgresql, 2)]
    [TestCase(CdcProvider.SqlServer, 3)]
    public void It_builds_only_binding_derived_topics_and_keeps_shared_offsets_separate(
        CdcProvider provider,
        int count
    )
    {
        CdcDeploymentRequest request = Request(provider);
        CdcDeploymentKafkaPolicyPlan plan = CdcDeploymentKafkaPolicy.Build(request);
        plan.BindingTopics.Should().HaveCount(count);
        plan.BindingTopics.Select(topic => topic.Name)
            .Should()
            .Contain(request.Binding.TopicName)
            .And.Contain(request.Binding.TopicName + ".cdc-progress")
            .And.NotContain(plan.OffsetStore.Name);
        plan.OffsetStore.Name.Should().Be(request.WorkerPolicy.OffsetStorageTopic.Value);
        plan.OffsetStore.Role.Should().Be(CdcKafkaTopicRole.SharedOffsets);
        plan.BindingTopics.Single(topic => topic.Role == CdcKafkaTopicRole.Public)
            .PartitionCount.Should()
            .Be(4);
        plan.BindingTopics.Where(topic => topic.Role != CdcKafkaTopicRole.Public)
            .Should()
            .OnlyContain(topic => topic.PartitionCount == 1);
        if (provider == CdcProvider.SqlServer)
        {
            plan.BindingTopics.Single(topic => topic.Role == CdcKafkaTopicRole.SchemaHistory)
                .Name.Should()
                .Be(request.Binding.TopicName + ".schema-history");
        }
    }

    [Test]
    public void It_emits_the_public_retention_and_size_contract_only_on_the_public_topic()
    {
        _plan
            .BindingTopics[0]
            .Configuration.Should()
            .BeEquivalentTo(
                new Dictionary<string, string>
                {
                    ["cleanup.policy"] = "compact",
                    ["min.insync.replicas"] = "1",
                    ["delete.retention.ms"] = "604800000",
                    ["max.message.bytes"] = "1048576",
                }
            );
        _plan
            .BindingTopics[1]
            .Configuration.Should()
            .BeEquivalentTo(
                new Dictionary<string, string>
                {
                    ["cleanup.policy"] = "compact",
                    ["min.insync.replicas"] = "1",
                }
            );
        _plan.OffsetStore.Configuration.Should().BeEquivalentTo(_plan.BindingTopics[1].Configuration);
    }

    [Test]
    public void It_emits_infinite_delete_only_history_retention()
    {
        CdcDeploymentKafkaPolicyPlan plan = CdcDeploymentKafkaPolicy.Build(Request(CdcProvider.SqlServer));
        plan.BindingTopics[2]
            .Configuration.Should()
            .BeEquivalentTo(
                new Dictionary<string, string>
                {
                    ["cleanup.policy"] = "delete",
                    ["min.insync.replicas"] = "1",
                    ["retention.ms"] = "-1",
                    ["retention.bytes"] = "-1",
                }
            );
    }

    [TestCase(CdcProvider.Postgresql, false)]
    [TestCase(CdcProvider.SqlServer, false)]
    [TestCase(CdcProvider.Postgresql, true)]
    [TestCase(CdcProvider.SqlServer, true)]
    public void It_accepts_complete_live_evidence_and_maps_valid_core_contracts(
        CdcProvider provider,
        bool production
    )
    {
        _request = Request(provider, production);
        _evidence = Evidence(_request);
        Observe().PolicyState.Should().Be(CoreCdc.CdcKafkaPolicyState.Satisfied);
        Offset().PolicyState.Should().Be(CoreCdc.CdcConnectOffsetStorePolicyState.Satisfied);
    }

    [TestCase(CdcKafkaTopicRole.Public, "cleanup.policy", "delete")]
    [TestCase(CdcKafkaTopicRole.Public, "cleanup.policy", "compact,delete")]
    [TestCase(CdcKafkaTopicRole.Public, "cleanup.policy", "compact,compact")]
    [TestCase(CdcKafkaTopicRole.Public, "cleanup.policy", "COMPACT")]
    [TestCase(CdcKafkaTopicRole.Public, "delete.retention.ms", "604799999")]
    [TestCase(CdcKafkaTopicRole.Public, "delete.retention.ms", "-1")]
    [TestCase(CdcKafkaTopicRole.Public, "delete.retention.ms", "9223372036854775808")]
    [TestCase(CdcKafkaTopicRole.Public, "max.message.bytes", "1048575")]
    [TestCase(CdcKafkaTopicRole.Public, "max.message.bytes", "1048577")]
    [TestCase(CdcKafkaTopicRole.Public, "max.message.bytes", "0")]
    [TestCase(CdcKafkaTopicRole.Public, "max.message.bytes", "2147483648")]
    [TestCase(CdcKafkaTopicRole.Progress, "cleanup.policy", "delete")]
    [TestCase(CdcKafkaTopicRole.SchemaHistory, "cleanup.policy", "compact")]
    [TestCase(CdcKafkaTopicRole.SchemaHistory, "retention.ms", "0")]
    [TestCase(CdcKafkaTopicRole.SchemaHistory, "retention.bytes", "1000000")]
    [TestCase(CdcKafkaTopicRole.SharedOffsets, "cleanup.policy", "compact,delete")]
    public void It_rejects_configuration_drift(CdcKafkaTopicRole role, string key, string value)
    {
        UseSqlServer();
        SetConfig(role, key, value);
        AssertRejected(role);
    }

    [TestCase(CdcKafkaTopicRole.Public, "delete.retention.ms")]
    [TestCase(CdcKafkaTopicRole.Public, "max.message.bytes")]
    [TestCase(CdcKafkaTopicRole.Public, "min.insync.replicas")]
    [TestCase(CdcKafkaTopicRole.Progress, "min.insync.replicas")]
    [TestCase(CdcKafkaTopicRole.SchemaHistory, "min.insync.replicas")]
    [TestCase(CdcKafkaTopicRole.SharedOffsets, "min.insync.replicas")]
    public void It_rejects_broker_defaults_in_place_of_required_topic_overrides(
        CdcKafkaTopicRole role,
        string key
    )
    {
        UseSqlServer();
        string name = Intent(role).Name;
        CdcKafkaTopicEvidence topic = Topic(name);
        SetConfig(role, key, topic.Configuration[key].Value, isOverride: false);
        AssertRejected(role);
    }

    [TestCase("604800000")]
    [TestCase("604800001")]
    [TestCase("9223372036854775807")]
    public void It_accepts_retention_at_or_above_the_contract(string value)
    {
        SetConfig(CdcKafkaTopicRole.Public, "delete.retention.ms", value);
        Observe().PolicyState.Should().Be(CoreCdc.CdcKafkaPolicyState.Satisfied);
    }

    [TestCase(CdcKafkaTopicRole.Public)]
    [TestCase(CdcKafkaTopicRole.Progress)]
    [TestCase(CdcKafkaTopicRole.SchemaHistory)]
    public void It_rejects_partition_count_changes(CdcKafkaTopicRole role)
    {
        UseSqlServer();
        string name = Intent(role).Name;
        CdcKafkaTopicEvidence topic = Topic(name);
        Dictionary<int, IReadOnlyList<int>> partitions = new(topic.PartitionReplicas)
        {
            [topic.PartitionReplicas.Count] = new[] { 0 },
        };
        SetTopic(name, topic with { PartitionReplicas = partitions });
        AssertRejected(role);
    }

    [Test]
    public void It_accepts_existing_shared_offset_partition_counts_without_rebinding()
    {
        string name = _plan.OffsetStore.Name;
        SetTopic(
            name,
            Topic(name) with
            {
                PartitionReplicas = new Dictionary<int, IReadOnlyList<int>>
                {
                    [0] = new[] { 0 },
                    [1] = new[] { 0 },
                },
            }
        );
        Offset().PolicyState.Should().Be(CoreCdc.CdcConnectOffsetStorePolicyState.Satisfied);
    }

    [TestCase(CdcKafkaTopicRole.Public)]
    [TestCase(CdcKafkaTopicRole.Progress)]
    [TestCase(CdcKafkaTopicRole.SchemaHistory)]
    [TestCase(CdcKafkaTopicRole.SharedOffsets)]
    public void It_checks_actual_replicas_for_every_partition(CdcKafkaTopicRole role)
    {
        UseSqlServer(production: true);
        string name = Intent(role).Name;
        CdcKafkaTopicEvidence topic = Topic(name);
        Dictionary<int, IReadOnlyList<int>> partitions = new(topic.PartitionReplicas)
        {
            [0] = new[] { 0, 1 },
        };
        SetTopic(name, topic with { PartitionReplicas = partitions });
        AssertRejected(role);
    }

    [TestCase(CdcKafkaTopicRole.Public)]
    [TestCase(CdcKafkaTopicRole.Progress)]
    [TestCase(CdcKafkaTopicRole.SchemaHistory)]
    [TestCase(CdcKafkaTopicRole.SharedOffsets)]
    public void It_rejects_production_isr_one(CdcKafkaTopicRole role)
    {
        UseSqlServer(production: true);
        SetConfig(role, "min.insync.replicas", "1");
        AssertRejected(role);
    }

    [Test]
    public void It_accepts_stronger_production_durability()
    {
        UseSqlServer(production: true);
        foreach (CdcKafkaTopicIntent intent in _plan.BindingTopics.Append(_plan.OffsetStore))
        {
            SetConfig(intent.Role, "min.insync.replicas", "3");
            CdcKafkaTopicEvidence topic = Topic(intent.Name);
            SetTopic(
                intent.Name,
                topic with
                {
                    PartitionReplicas = topic.PartitionReplicas.ToDictionary(
                        pair => pair.Key,
                        _ => (IReadOnlyList<int>)new[] { 0, 1, 2, 3 }
                    ),
                }
            );
        }
        Observe().PolicyState.Should().Be(CoreCdc.CdcKafkaPolicyState.Satisfied);
        Offset().PolicyState.Should().Be(CoreCdc.CdcConnectOffsetStorePolicyState.Satisfied);
    }

    [TestCase("min.insync.replicas")]
    [TestCase("cleanup.policy")]
    public void It_preserves_unknown_configuration_evidence(string key)
    {
        string name = _plan.OffsetStore.Name;
        CdcKafkaTopicEvidence topic = Topic(name);
        Dictionary<string, CdcKafkaConfigurationValue> config = new(topic.Configuration);
        config.Remove(key);
        SetTopic(name, topic with { Configuration = config });
        Offset().PolicyState.Should().Be(CoreCdc.CdcConnectOffsetStorePolicyState.Unknown);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void It_distinguishes_authoritative_absence_from_failed_lookup(bool absent)
    {
        CdcTransportResult<CdcKafkaTopicEvidence> result = absent
            ? new CdcTransportResult<CdcKafkaTopicEvidence>.Absent()
            : Unknown<CdcKafkaTopicEvidence>();
        _evidence = _evidence with
        {
            Topics = new Dictionary<string, CdcTransportResult<CdcKafkaTopicEvidence>>
            {
                [_plan.BindingTopics[0].Name] = result,
                [_plan.OffsetStore.Name] = result,
            },
        };
        Observe().PublicTopic.State.Should().Be(absent ? ItemState.Invalid : ItemState.Unknown);
        Offset()
            .PolicyState.Should()
            .Be(
                absent
                    ? CoreCdc.CdcConnectOffsetStorePolicyState.Invalid
                    : CoreCdc.CdcConnectOffsetStorePolicyState.Unknown
            );
    }

    [TestCase(1, 33554432)]
    [TestCase(33554432, 33554432)]
    [TestCase(33554433, 33554433)]
    [TestCase(int.MaxValue, int.MaxValue)]
    public void It_reuses_the_positive_int32_ceiling_and_buffer_default(int ceiling, int buffer)
    {
        _request = Request(ceiling: ceiling);
        _evidence = Evidence(_request);
        CdcDeploymentKafkaPolicy.Build(_request).ProducerBufferBytes.Should().Be(buffer);
        Observe().PolicyState.Should().Be(CoreCdc.CdcKafkaPolicyState.Satisfied);
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void It_rejects_nonpositive_record_ceilings(int ceiling)
    {
        Action act = () => Request(ceiling: ceiling);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestCase(33554431)]
    [TestCase(1)]
    public void It_rejects_buffers_below_the_documented_minimum(int buffer)
    {
        Action act = () => Request(buffer: buffer);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestCase("request")]
    [TestCase("buffer")]
    [TestCase("heap")]
    public void It_rejects_live_producer_or_heap_drift(string field)
    {
        CdcKafkaProducerCapacityEvidence producer = (
            (CdcTransportResult<CdcKafkaProducerCapacityEvidence>.Observed)_evidence.Producer
        ).Value;
        producer = field switch
        {
            "request" => producer with { MaxRequestBytes = _plan.MaxRecordBytes - 1 },
            "buffer" => producer with { BufferBytes = _plan.ProducerBufferBytes - 1 },
            _ => producer with { WorkerHeapBytes = _plan.ProducerBufferBytes },
        };
        _evidence = _evidence with
        {
            Producer = new CdcTransportResult<CdcKafkaProducerCapacityEvidence>.Observed(producer),
        };
        Observe().RecordSizePolicy.State.Should().Be(ItemState.Invalid);
    }

    [TestCase("request")]
    [TestCase("replica")]
    [TestCase("response")]
    public void It_checks_each_broker_capacity_including_nonleaders(string field)
    {
        CdcKafkaBrokerCapacity broker = new(
            3,
            _plan.MaxRecordBytes,
            _plan.MaxRecordBytes,
            _plan.MaxRecordBytes
        );
        broker = field switch
        {
            "request" => broker with { SocketRequestMaxBytes = _plan.MaxRecordBytes - 1 },
            "replica" => broker with { ReplicaFetchMaxBytes = _plan.MaxRecordBytes - 1 },
            _ => broker with { ReplicaFetchResponseMaxBytes = _plan.MaxRecordBytes - 1 },
        };
        _evidence = _evidence with
        {
            Brokers = new CdcTransportResult<CdcKafkaBrokerEvidence>.Observed(
                new(
                    true,
                    ((CdcTransportResult<CdcKafkaBrokerEvidence>.Observed)_evidence.Brokers)
                        .Value.Brokers.Select(existing =>
                            existing.BrokerId == broker.BrokerId ? broker : existing
                        )
                        .ToArray()
                )
            ),
        };
        Observe().RecordSizePolicy.State.Should().Be(ItemState.Invalid);
    }

    [TestCase("duplicate-replica")]
    [TestCase("negative-broker")]
    [TestCase("partition-gap")]
    [TestCase("empty-partitions")]
    [TestCase("local-two-replicas")]
    [TestCase("source-topic-mismatch")]
    public void It_rejects_malformed_topic_identity_or_assignments(string scenario)
    {
        string name = _plan.BindingTopics[0].Name;
        CdcKafkaTopicEvidence topic = Topic(name);
        Dictionary<int, IReadOnlyList<int>> partitions = new(topic.PartitionReplicas);
        switch (scenario)
        {
            case "duplicate-replica":
                partitions[0] = new[] { 0, 0 };
                break;
            case "negative-broker":
                partitions[0] = new[] { -1 };
                break;
            case "partition-gap":
                partitions[10] = partitions[0];
                partitions.Remove(0);
                break;
            case "empty-partitions":
                partitions.Clear();
                break;
            case "local-two-replicas":
                partitions[0] = new[] { 0, 1 };
                break;
            default:
                topic = topic with { Name = "wrong-topic" };
                break;
        }
        SetTopic(name, topic with { PartitionReplicas = partitions });
        Observe().PublicTopic.State.Should().Be(ItemState.Invalid);
    }

    [Test]
    public void It_requires_isr_to_fit_actual_replicas()
    {
        UseSqlServer(production: true);
        SetConfig(CdcKafkaTopicRole.SharedOffsets, "min.insync.replicas", "4");
        Offset().PolicyState.Should().Be(CoreCdc.CdcConnectOffsetStorePolicyState.Invalid);
    }

    [Test]
    public void It_accepts_an_explicit_larger_producer_buffer()
    {
        _request = Request(buffer: 67_108_864);
        _evidence = Evidence(_request);
        CdcDeploymentKafkaPolicy.Build(_request).ProducerBufferBytes.Should().Be(67_108_864);
        Observe().RecordSizePolicy.State.Should().Be(ItemState.Satisfied);
    }

    [TestCase("incomplete", ItemState.Unknown)]
    [TestCase("empty", ItemState.Unknown)]
    [TestCase("duplicate", ItemState.Invalid)]
    [TestCase("missing-assigned-broker", ItemState.Invalid)]
    public void It_does_not_infer_broker_inventory_from_partial_or_contradictory_results(
        string scenario,
        ItemState state
    )
    {
        CdcKafkaBrokerEvidence brokers = (
            (CdcTransportResult<CdcKafkaBrokerEvidence>.Observed)_evidence.Brokers
        ).Value;
        brokers = scenario switch
        {
            "incomplete" => brokers with { InventoryComplete = false },
            "empty" => brokers with { Brokers = [] },
            "duplicate" => brokers with { Brokers = [.. brokers.Brokers, brokers.Brokers[0]] },
            _ => brokers with { Brokers = brokers.Brokers.Where(broker => broker.BrokerId != 0).ToArray() },
        };
        _evidence = _evidence with
        {
            Brokers = new CdcTransportResult<CdcKafkaBrokerEvidence>.Observed(brokers),
        };
        Observe().RecordSizePolicy.State.Should().Be(state);
    }

    [TestCase("worker-principal")]
    [TestCase("kafka-connector-principal")]
    public void It_rejects_cluster_administration_that_could_bypass_the_service_grant_set(string principal)
    {
        CdcKafkaAclGrant grant = new(
            principal,
            CdcKafkaAclResourceType.Cluster,
            "kafka-cluster",
            CdcKafkaAclOperation.Alter
        );
        SetAcls(Acls() with { Grants = [.. Acls().Grants, grant] });
        CdcDeploymentKafkaPolicy.ValidateAcls(_request, _evidence.Acls, true).UnsafeGrants.Should().BeTrue();
    }

    [Test]
    public void It_rejects_other_principals_accessing_a_configured_consumer_group()
    {
        CdcKafkaAclGrant grant = new(
            "unknown-principal",
            CdcKafkaAclResourceType.Group,
            "consumer-group",
            CdcKafkaAclOperation.Read
        );
        SetAcls(Acls() with { Grants = [.. Acls().Grants, grant] });
        Observe().PublicTopicAcls.State.Should().Be(ItemState.Invalid);
    }

    [Test]
    public void It_preserves_unknown_for_malformed_acl_records_without_offering_grant_repair()
    {
        CdcKafkaAclGrant malformed = _plan.BindingGrants[0] with { Pattern = (CdcKafkaAclPattern)99 };
        SetAcls(Acls() with { Grants = [malformed] });
        CdcKafkaAclValidation result = CdcDeploymentKafkaPolicy.ValidateAcls(_request, _evidence.Acls, false);
        result.State.Should().Be(ItemState.Unknown);
        result.CanAddMissingGrants.Should().BeFalse();
    }

    [TestCase("worker")]
    [TestCase("connector")]
    [TestCase("consumer")]
    [TestCase("wildcard-principal")]
    [TestCase("wildcard-group")]
    public void It_rejects_ambiguous_or_nonliteral_role_intent(string scenario)
    {
        CdcWorkerDeploymentPolicy worker = _request.WorkerPolicy;
        CdcSafeName connector = scenario == "worker" ? worker.WorkerPrincipal : worker.ConnectorPrincipal;
        CdcSafeName administrator =
            scenario == "connector" ? worker.ConnectorPrincipal : worker.DeploymentAdministratorPrincipal;
        CdcConsumerAccess consumer = scenario switch
        {
            "consumer" => new(worker.WorkerPrincipal, new("consumer-group")),
            "wildcard-principal" => new(new("User:*"), new("consumer-group")),
            "wildcard-group" => new(new("consumer"), new("*")),
            _ => worker.Consumers[0],
        };
        Action act = () =>
            new CdcWorkerDeploymentPolicy(
                worker.WorkerKey,
                worker.OffsetStorageTopic,
                worker.QualifiedImageDigest,
                worker.HeapBytes,
                worker.ClientConfigurationOverridePolicy,
                worker.DurabilityProfile,
                worker.AuthorizationProfile,
                worker.WorkerPrincipal,
                connector,
                administrator,
                [consumer]
            );
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_requires_authoritative_broker_and_producer_evidence()
    {
        _evidence = _evidence with
        {
            Brokers = Unknown<CdcKafkaBrokerEvidence>(),
            Producer = Unknown<CdcKafkaProducerCapacityEvidence>(),
        };
        Observe().RecordSizePolicy.State.Should().Be(ItemState.Unknown);
    }

    [Test]
    public void It_distinguishes_literal_worker_connector_consumer_and_administrator_roles()
    {
        _plan
            .OffsetStoreGrants.Should()
            .HaveCount(3)
            .And.OnlyContain(grant =>
                grant.Principal == "worker-principal"
                && grant.ResourceName == _plan.OffsetStore.Name
                && grant.Pattern == CdcKafkaAclPattern.Literal
            );
        _plan
            .OffsetStoreGrants.Select(grant => grant.Operation)
            .Should()
            .BeEquivalentTo(
                new[] { CdcKafkaAclOperation.Read, CdcKafkaAclOperation.Write, CdcKafkaAclOperation.Describe }
            );
        _plan
            .BindingGrants.Should()
            .OnlyContain(grant =>
                grant.Principal != "worker-principal"
                && grant.Principal != "kafka-administrator-principal"
                && grant.ResourceName != _plan.OffsetStore.Name
            );
        _plan.BindingGrants.Where(grant => grant.Principal == "consumer").Should().HaveCount(3);
        _plan
            .BindingGrants.Should()
            .Contain(grant =>
                grant.ResourceType == CdcKafkaAclResourceType.Group
                && grant.ResourceName == "consumer-group"
                && grant.Operation == CdcKafkaAclOperation.Read
            );
    }

    [Test]
    public void It_emits_the_four_history_client_grants_only_for_the_connector()
    {
        UseSqlServer();
        _plan
            .BindingGrants.Where(grant => grant.ResourceName == _plan.BindingTopics[2].Name)
            .Should()
            .HaveCount(4)
            .And.OnlyContain(grant => grant.Principal == "kafka-connector-principal");
    }

    [TestCase(true)]
    [TestCase(false)]
    public void It_returns_missing_grants_as_eligible_reconciliation_intent(bool offsets)
    {
        CdcKafkaAclEvidence acls = Acls();
        CdcKafkaAclGrant missing = (offsets ? _plan.OffsetStoreGrants : _plan.BindingGrants)[0];
        SetAcls(acls with { Grants = acls.Grants.Where(grant => grant != missing).ToArray() });
        CdcKafkaAclValidation result = CdcDeploymentKafkaPolicy.ValidateAcls(
            _request,
            _evidence.Acls,
            offsets
        );
        result.State.Should().Be(ItemState.Invalid);
        result.CanAddMissingGrants.Should().BeTrue();
        result.MissingGrants.Should().Equal(missing);
    }

    [TestCase("progress")]
    [TestCase("history")]
    [TestCase("offsets")]
    [TestCase("worker-config")]
    [TestCase("peer")]
    [TestCase("wildcard-topic")]
    [TestCase("prefixed-topic")]
    [TestCase("wildcard-principal")]
    [TestCase("peer-group")]
    [TestCase("wildcard-group")]
    [TestCase("cluster")]
    [TestCase("extra-operation")]
    [TestCase("worker-on-public")]
    [TestCase("connector-on-offsets")]
    [TestCase("unknown-on-history")]
    public void It_rejects_unsafe_effective_grants_without_offering_repair(string scenario)
    {
        UseSqlServer();
        CdcKafkaAclGrant grant = new(
            "consumer",
            CdcKafkaAclResourceType.Topic,
            _plan.BindingTopics[0].Name,
            CdcKafkaAclOperation.Read
        );
        grant = scenario switch
        {
            "progress" => grant with { ResourceName = _plan.BindingTopics[1].Name },
            "history" => grant with { ResourceName = _plan.BindingTopics[2].Name },
            "offsets" => grant with { ResourceName = _plan.OffsetStore.Name },
            "worker-config" => grant with { ResourceName = "connect-configs" },
            "peer" => grant with { ResourceName = "peer-public-topic" },
            "wildcard-topic" => grant with { ResourceName = "*" },
            "prefixed-topic" => grant with { ResourceName = "edfi", Pattern = CdcKafkaAclPattern.Prefixed },
            "wildcard-principal" => grant with { Principal = "User:*" },
            "peer-group" => grant with
            {
                ResourceType = CdcKafkaAclResourceType.Group,
                ResourceName = "peer-group",
            },
            "wildcard-group" => grant with
            {
                ResourceType = CdcKafkaAclResourceType.Group,
                ResourceName = "*",
            },
            "cluster" => grant with
            {
                ResourceType = CdcKafkaAclResourceType.Cluster,
                ResourceName = "kafka-cluster",
                Operation = CdcKafkaAclOperation.All,
            },
            "extra-operation" => grant with { Operation = CdcKafkaAclOperation.Write },
            "worker-on-public" => grant with { Principal = "worker-principal" },
            "connector-on-offsets" => grant with
            {
                Principal = "kafka-connector-principal",
                ResourceName = _plan.OffsetStore.Name,
            },
            _ => grant with { Principal = "unknown-principal", ResourceName = _plan.BindingTopics[2].Name },
        };
        SetAcls(Acls() with { Grants = [.. Acls().Grants, grant] });
        bool offsets = scenario is "offsets" or "connector-on-offsets";
        CdcKafkaAclValidation result = CdcDeploymentKafkaPolicy.ValidateAcls(
            _request,
            _evidence.Acls,
            offsets
        );
        result.UnsafeGrants.Should().BeTrue();
        result.CanAddMissingGrants.Should().BeFalse();
        result.State.Should().Be(ItemState.Invalid);
    }

    [Test]
    public void It_does_not_count_allows_overridden_by_denies_as_complete()
    {
        CdcKafkaAclGrant deny = _plan.BindingGrants[0] with
        {
            Permission = CdcKafkaAclPermission.Deny,
            Host = "192.0.2.1",
        };
        SetAcls(Acls() with { Grants = [.. Acls().Grants, deny] });
        CdcDeploymentKafkaPolicy.ValidateAcls(_request, _evidence.Acls, false).UnsafeGrants.Should().BeTrue();
    }

    [TestCase(true)]
    [TestCase(false)]
    public void It_requires_complete_acl_and_authorizer_evidence(bool incomplete)
    {
        if (incomplete)
        {
            SetAcls(Acls() with { InventoryComplete = false });
        }
        else
        {
            _evidence = _evidence with { Acls = Unknown<CdcKafkaAclEvidence>() };
        }
        Observe().PublicTopicAcls.State.Should().Be(ItemState.Unknown);
        Offset().AclState.Should().Be(CoreCdc.CdcConnectOffsetStoreItemState.Unknown);
    }

    [TestCase("consumer")]
    [TestCase("worker-principal")]
    [TestCase("kafka-connector-principal")]
    public void It_rejects_superuser_service_or_consumer_identities(string principal)
    {
        SetAcls(Acls() with { SuperuserPrincipals = [principal] });
        Observe().PublicTopicAcls.State.Should().Be(ItemState.Invalid);
    }

    [Test]
    public void It_rejects_default_allow_authorization()
    {
        SetAcls(Acls() with { AllowEveryoneIfNoAclFound = true });
        Observe().PublicTopicAcls.State.Should().Be(ItemState.Invalid);
    }

    [Test]
    public void It_allows_deployment_administration_outside_consumer_grants()
    {
        CdcKafkaAclGrant admin = new(
            "kafka-administrator-principal",
            CdcKafkaAclResourceType.Topic,
            "*",
            CdcKafkaAclOperation.All
        );
        SetAcls(Acls() with { Grants = [.. Acls().Grants, admin], SuperuserPrincipals = [admin.Principal] });
        Observe().PolicyState.Should().Be(CoreCdc.CdcKafkaPolicyState.Satisfied);
    }

    [Test]
    public void It_labels_authorization_disabled_local_results_and_requires_observed_mode()
    {
        _request = CdcDeploymentRequestTestData.Request();
        _plan = CdcDeploymentKafkaPolicy.Build(_request);
        _evidence = Evidence(_request);
        _plan.BindingGrants.Should().BeEmpty();
        Observe().Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "authorizationDisabledLocal");
        Offset().Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "authorizationDisabledLocal");
        _evidence = _evidence with { Acls = Unknown<CdcKafkaAclEvidence>() };
        Observe().PublicTopicAcls.State.Should().Be(ItemState.Unknown);
    }

    [Test]
    public void It_rejects_authorization_mode_drift()
    {
        SetAcls(Acls() with { AuthorizationEnabled = false });
        Observe().PublicTopicAcls.State.Should().Be(ItemState.Invalid);
    }

    [Test]
    public void It_does_not_echo_raw_configuration_or_acl_evidence()
    {
        const string secret = "Password=sentinel-secret;Host=private-source";
        SetConfig(CdcKafkaTopicRole.Public, "cleanup.policy", secret);
        SetAcls(
            Acls() with
            {
                Grants =
                [
                    .. Acls().Grants,
                    new(
                        secret,
                        CdcKafkaAclResourceType.Topic,
                        _plan.BindingTopics[0].Name,
                        CdcKafkaAclOperation.All
                    ),
                ],
            }
        );
        JsonSerializer.Serialize(Observe()).Should().NotContain(secret).And.NotContain("sentinel-secret");
        JsonSerializer.Serialize(_evidence).Should().NotContain(secret);
        Topic(_plan.BindingTopics[0].Name).ToString().Should().NotContain(secret);
        Acls().ToString().Should().NotContain(secret);
    }

    private static CdcDeploymentRequest Request(
        CdcProvider provider = CdcProvider.Postgresql,
        bool production = false,
        int ceiling = 1_048_576,
        int? buffer = null
    )
    {
        CdcDeploymentRequest request = CdcDeploymentRequestTestData.Request(
            provider,
            changeBinding: binding => binding with { PartitionCount = 4 },
            worker: CdcDeploymentRequestTestData.Worker(
                heapBytes: (long)int.MaxValue + 1,
                durability: production
                    ? CdcKafkaDurabilityProfile.Production
                    : CdcKafkaDurabilityProfile.LocalSingleBroker,
                authorization: CdcKafkaAuthorizationProfile.AuthorizationEnabled
            )
        );
        return new(
            request.Binding,
            request.DmsSettings,
            request.ProviderSetup,
            request.ConnectEndpoint,
            request.WorkerMetricsEndpoint,
            new("broker:9092", ceiling, buffer),
            request.WorkerPolicy,
            request.ProviderConnectionProperties,
            request.KafkaClientSecurityProperties,
            request.Timing
        );
    }

    private static CdcKafkaDeploymentEvidence Evidence(CdcDeploymentRequest request)
    {
        CdcDeploymentKafkaPolicyPlan plan = CdcDeploymentKafkaPolicy.Build(request);
        Dictionary<string, CdcTransportResult<CdcKafkaTopicEvidence>> topics = new(StringComparer.Ordinal);
        foreach (CdcKafkaTopicIntent intent in plan.BindingTopics.Append(plan.OffsetStore))
        {
            topics[intent.Name] = new CdcTransportResult<CdcKafkaTopicEvidence>.Observed(
                new(
                    intent.Name,
                    Enumerable
                        .Range(0, intent.PartitionCount)
                        .ToDictionary(
                            id => id,
                            _ => (IReadOnlyList<int>)Enumerable.Range(0, intent.ReplicationFactor).ToArray()
                        ),
                    intent.Configuration.ToDictionary(
                        pair => pair.Key,
                        pair => new CdcKafkaConfigurationValue(pair.Value, true)
                    )
                )
            );
        }
        return new(
            topics,
            new CdcTransportResult<CdcKafkaBrokerEvidence>.Observed(
                new(
                    true,
                    Enumerable
                        .Range(0, 4)
                        .Select(id => new CdcKafkaBrokerCapacity(
                            id,
                            plan.MaxRecordBytes,
                            plan.MaxRecordBytes,
                            plan.MaxRecordBytes
                        ))
                        .ToArray()
                )
            ),
            new CdcTransportResult<CdcKafkaProducerCapacityEvidence>.Observed(
                new(plan.MaxRecordBytes, plan.ProducerBufferBytes, request.WorkerPolicy.HeapBytes)
            ),
            new CdcTransportResult<CdcKafkaAclEvidence>.Observed(
                new(
                    request.WorkerPolicy.AuthorizationProfile
                        == CdcKafkaAuthorizationProfile.AuthorizationEnabled,
                    true,
                    false,
                    [],
                    [.. plan.OffsetStoreGrants, .. plan.BindingGrants]
                )
            )
        );
    }

    private CoreCdc.CdcObservationValidationContext Context() =>
        new("policy-test", _request.TargetIdentity, _request.Binding.PhysicalSourceFingerprint, ObservedAt);

    private CoreCdc.CdcKafkaPolicyObservation Observe()
    {
        CoreCdc.CdcKafkaPolicyObservation result = CdcDeploymentKafkaPolicy.ObserveBinding(
            _request,
            "policy-test",
            ObservedAt,
            _evidence
        );
        CoreCdc
            .CdcKafkaPolicyObservationValidator.ValidateForBinding(result, _request.Binding, Context())
            .Diagnostics.Should()
            .BeEmpty();
        return result;
    }

    private CoreCdc.CdcConnectOffsetStorePolicyObservation Offset()
    {
        CoreCdc.CdcConnectOffsetStorePolicyObservation result = CdcDeploymentKafkaPolicy.ObserveOffsetStore(
            _request,
            "policy-test",
            ObservedAt,
            _evidence
        );
        CoreCdc
            .CdcConnectOffsetStorePolicyObservationValidator.Validate(result, Context())
            .Diagnostics.Should()
            .BeEmpty();
        return result;
    }

    private void UseSqlServer(bool production = false)
    {
        _request = Request(CdcProvider.SqlServer, production);
        _plan = CdcDeploymentKafkaPolicy.Build(_request);
        _evidence = Evidence(_request);
    }

    private CdcKafkaTopicIntent Intent(CdcKafkaTopicRole role) =>
        role == CdcKafkaTopicRole.SharedOffsets
            ? _plan.OffsetStore
            : _plan.BindingTopics.Single(topic => topic.Role == role);

    private CdcKafkaTopicEvidence Topic(string name) =>
        ((CdcTransportResult<CdcKafkaTopicEvidence>.Observed)_evidence.Topics[name]).Value;

    private void SetTopic(string name, CdcKafkaTopicEvidence topic) =>
        _evidence = _evidence with
        {
            Topics = new Dictionary<string, CdcTransportResult<CdcKafkaTopicEvidence>>(_evidence.Topics)
            {
                [name] = new CdcTransportResult<CdcKafkaTopicEvidence>.Observed(topic),
            },
        };

    private void SetConfig(CdcKafkaTopicRole role, string key, string value, bool isOverride = true)
    {
        string name = Intent(role).Name;
        CdcKafkaTopicEvidence topic = Topic(name);
        SetTopic(
            name,
            topic with
            {
                Configuration = new Dictionary<string, CdcKafkaConfigurationValue>(topic.Configuration)
                {
                    [key] = new(value, isOverride),
                },
            }
        );
    }

    private void AssertRejected(CdcKafkaTopicRole role)
    {
        if (role == CdcKafkaTopicRole.SharedOffsets)
        {
            Offset().PolicyState.Should().Be(CoreCdc.CdcConnectOffsetStorePolicyState.Invalid);
        }
        else
        {
            Observe().PolicyState.Should().Be(CoreCdc.CdcKafkaPolicyState.Invalid);
        }
    }

    private CdcKafkaAclEvidence Acls() =>
        ((CdcTransportResult<CdcKafkaAclEvidence>.Observed)_evidence.Acls).Value;

    private void SetAcls(CdcKafkaAclEvidence acls) =>
        _evidence = _evidence with { Acls = new CdcTransportResult<CdcKafkaAclEvidence>.Observed(acls) };

    private static CdcTransportResult<T> Unknown<T>()
        where T : notnull =>
        new CdcTransportResult<T>.Unavailable(
            new(CdcDeploymentComponent.Kafka, CdcDeploymentFailure.Unavailable)
        );
}
