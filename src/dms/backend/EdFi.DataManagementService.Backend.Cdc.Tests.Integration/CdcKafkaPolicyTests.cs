// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using Confluent.Kafka;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Cdc.Tests.Integration.CdcKafkaPolicyFixture;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture]
[Category(CdcControllerCategories.KafkaPolicy)]
[Category("DatabaseIntegration")] // Repository umbrella for Docker-backed qualification.
[Category("KafkaIntegration")]
[Category("CdcAuthorizationEnabled")]
[Category("CdcProductionDurability")]
[NonParallelizable]
public sealed class Given_authorized_three_broker_cdc_policy
{
    private CdcKafkaPolicyFixture _fixture = null!;
    private CancellationTokenSource _timeout = null!;
    private CdcDeploymentRequest _request = null!;
    private CdcDeploymentRequest _peer = null!;
    private CancellationToken Token => _timeout.Token;

    [OneTimeSetUp]
    public async Task Initialize()
    {
        _fixture = new(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(6));
        try
        {
            await _fixture.InitializeAsync(timeout.Token);
            _request = _fixture.Request();
            _peer = _fixture.Request("b");
            await _fixture.PrepareOffsetsAsync(_request, timeout.Token);
            Value(await _fixture.StartPolicyWorkerAsync(_request, true, timeout.Token));
            await _fixture.ProvisionTopicsAsync(_request, timeout.Token);
            await _fixture.ProvisionTopicsAsync(_peer, timeout.Token);
            foreach (var request in new[] { _request, _peer })
            {
                var plan = CdcDeploymentKafkaPolicy.Build(request);
                foreach (var topic in plan.BindingTopics)
                {
                    await _fixture.ProduceAsync(
                        "connector-" + request.Binding.InstanceKey,
                        topic.Name,
                        timeout.Token
                    );
                }
            }
        }
        catch
        {
            await _fixture.DisposeAsync();
            _fixture = null!;
            throw;
        }
    }

    [SetUp]
    public void Setup() => _timeout = new(TimeSpan.FromMinutes(4));

    [TearDown]
    public void Teardown() => _timeout.Dispose();

    [OneTimeTearDown]
    public async Task Cleanup()
    {
        if (_fixture is not null)
        {
            await _fixture.DisposeAsync();
        }
    }

    [Test]
    public async Task It_inspects_retirement_offsets_with_existing_admin_authority_and_rejects_denied_access()
    {
        var scope = new CdcArtifactCleanupScope(
            _request,
            _request.Binding.ToCompleteBindingIdentity(),
            CoreCdc.CdcArtifactNameGenerator.RecoverFromBinding(_request.Binding).Inventory!.GovernedArtifacts
        );
        using var admin = new AdminClientBuilder(new AdminClientConfig(_fixture.Client("admin")))
            .SetLogHandler((_, _) => { })
            .SetErrorHandler((_, _) => { })
            .Build();
        var groups = (await admin.ListConsumerGroupsAsync())
            .Valid.Select(group => group.GroupId)
            .Order()
            .ToArray();
        var before = Value(await _fixture.Adapter.InspectAclsAsync(_request, Token));
        Value(await _fixture.Adapter.InspectRetirementOffsetsAsync(scope, Token))
            .Should()
            .Be(CdcRetirementOffsetState.Absent);
        string key = JsonSerializer.Serialize(
            new object[] { _request.Binding.ConnectorName, new { server = "fixture" } }
        );
        await CdcRetirementOffsetStoreProbe.WriteAsync(
            _fixture.Client("admin"),
            _fixture.OffsetTopic,
            key,
            "{}",
            Token
        );
        Value(await _fixture.Adapter.InspectRetirementOffsetsAsync(scope, Token))
            .Should()
            .Be(CdcRetirementOffsetState.Present);
        using var denied = Value(
            CdcKafkaAdminAdapter.Create(new AdminClientConfig(_fixture.Client("consumer-a")), _fixture)
        );
        (await denied.InspectRetirementOffsetsAsync(scope, Token))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        await AssertDeniedRetirementRetainsBindingAsync(denied);
        await CdcRetirementOffsetStoreProbe.WriteAsync(
            _fixture.Client("admin"),
            _fixture.OffsetTopic,
            key,
            null!,
            Token
        );
        Value(await _fixture.Adapter.InspectRetirementOffsetsAsync(scope, Token))
            .Should()
            .Be(CdcRetirementOffsetState.Absent);
        Value(await _fixture.Adapter.InspectAclsAsync(_request, Token))
            .Grants.Should()
            .BeEquivalentTo(before.Grants);
        (await admin.ListConsumerGroupsAsync())
            .Valid.Select(group => group.GroupId)
            .Order()
            .Should()
            .Equal(groups);
    }

    private async Task AssertDeniedRetirementRetainsBindingAsync(CdcKafkaAdminAdapter denied)
    {
        // Synthetic ownership isolates the controller's state-preservation boundary in this broker-only lane.
        // The provider lanes separately qualify physical CREATE and real capture cleanup.
        string root = Path.Combine(
            Path.GetTempPath(),
            "cdc-denied-retirement-" + Guid.NewGuid().ToString("N")
        );
        try
        {
            var store = new LocalCdcWorkflowJournalStore(root);
            var provisioner = A.Fake<ICdcManagedDatabaseProvisioner>();
            A.CallTo(() => provisioner.CreateDatabase()).Returns(true);
            A.CallTo(() => provisioner.ReadSourceFingerprintAsync(A<CancellationToken>._))
                .Returns(_request.Binding.PhysicalSourceFingerprint);
            var provisioned = await new CdcManagedDatabaseProvisioning(store).ProvisionAsync(
                _request.TargetIdentity,
                provisioner,
                purpose: CdcWorkflowPurpose.InitialCdcProvisioning
            );
            var services = new ServiceCollection().AddDmsCdcControlPlane();
            services.Configure<CoreCdc.CdcBindingStateStoreOptions>(options => options.RootPath = root);
            using var providerServices = services.BuildServiceProvider();
            var bindings = providerServices.GetRequiredService<CoreCdc.ICdcBindingLifecycleService>();
            await using (
                var session = await store.AcquireAsync(
                    _request.Timing.CallTimeout,
                    _request.Timing.PollInterval,
                    Token
                )
            )
            {
                await session.RecordIntentAsync(
                    _request.TargetIdentity,
                    provisioned.WorkflowId,
                    Guid.NewGuid(),
                    CdcWorkflowEffect.ReserveBinding,
                    [],
                    Token
                );
                (await bindings.CreateBindingIfAbsentAsync(_request.Binding, Token))
                    .Status.Should()
                    .Be(CoreCdc.CdcControlPlaneOperationStatus.Succeeded);
            }
            using var http = CdcConnectRestAdapter.CreateHttpClient();
            var provider = A.Fake<ICdcProviderArtifactCleanupAdapter>(options => options.Strict());
            var retirement = new CdcBindingRetirement(
                root,
                new CdcConnectRestAdapter(http),
                denied,
                provider
            );
            var result = await retirement.RetireAsync(_request, _request.Binding.Generation, true, Token);
            result.Succeeded.Should().BeFalse();
            result
                .Diagnostics.Should()
                .ContainSingle()
                .Which.Component.Should()
                .Be(CdcDeploymentComponent.Kafka);
            (await bindings.ExactMatchBindingAsync(_request.Binding, Token))
                .Status.Should()
                .Be(CoreCdc.CdcControlPlaneOperationStatus.Succeeded);
            Fake.GetCalls(provider).Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Test]
    public async Task It_prepares_live_worker_only_offsets_before_the_qualified_worker_starts()
    {
        _fixture.WorkerLaunches.Should().Be(1);
        _fixture.WorkerStartedAt.Should().BeAfter(_fixture.OffsetVerifiedAt);
        var plan = CdcDeploymentKafkaPolicy.Build(_request);
        await _fixture.WaitForReplicasAsync(plan.OffsetStore.Name, Token);
        var evidence = await _fixture.EvidenceAsync(_request, Token);
        var offsets = Value(evidence.Topics[plan.OffsetStore.Name]);
        offsets.PartitionReplicas.Values.Should().OnlyContain(ids => ids.Count >= 3);
        offsets.Configuration["cleanup.policy"].Value.Should().Be("compact");
        offsets.Configuration["min.insync.replicas"].Should().Be(new CdcKafkaConfigurationValue("2", true));
        Value(evidence.Acls)
            .Grants.Where(g => g.ResourceName == plan.OffsetStore.Name)
            .Should()
            .BeEquivalentTo(plan.OffsetStoreGrants);
        Value(await _fixture.Controller.ObserveOffsetStoreAsync(_request, Token))
            .PolicyState.Should()
            .Be(CoreCdc.CdcConnectOffsetStorePolicyState.Satisfied);
        Attach(
            "offset-before-worker",
            new
            {
                _fixture.OffsetVerifiedAt,
                _fixture.WorkerStartedAt,
                brokers = Value(evidence.Brokers).Brokers.Count,
                authorization = "enabled",
                durability = "production",
                aclProof = true,
            }
        );
    }

    [TestCase("a")]
    [TestCase("b")]
    public async Task It_allows_only_the_configured_public_topic_and_consumer_group(string instance)
    {
        var request = _fixture.Request(instance);
        (
            await _fixture.ReadAsync(
                "consumer-" + instance,
                request.Binding.TopicName,
                request.WorkerPolicy.Consumers.Single().Group.Value,
                Token
            )
        )
            .Should()
            .Be(ErrorCode.NoError);
    }

    [TestCase("a", "peer", "topic")]
    [TestCase("b", "peer", "topic")]
    [TestCase("a", "progress", "topic")]
    [TestCase("b", "progress", "topic")]
    [TestCase("a", "history", "topic")]
    [TestCase("b", "history", "topic")]
    [TestCase("a", "offsets", "topic")]
    [TestCase("b", "offsets", "topic")]
    [TestCase("a", "configs", "topic")]
    [TestCase("b", "configs", "topic")]
    [TestCase("a", "status", "topic")]
    [TestCase("b", "status", "topic")]
    [TestCase("a", "public", "peer-group")]
    [TestCase("b", "public", "peer-group")]
    [TestCase("a", "public", "worker-group")]
    [TestCase("b", "public", "worker-group")]
    public async Task It_denies_cross_binding_and_internal_reads(
        string instance,
        string resource,
        string groupKind
    )
    {
        var request = _fixture.Request(instance);
        var peer = _fixture.Request(instance == "a" ? "b" : "a");
        var plan = CdcDeploymentKafkaPolicy.Build(request);
        string topic = resource switch
        {
            "peer" => peer.Binding.TopicName,
            "progress" => plan.BindingTopics.Single(t => t.Role == CdcKafkaTopicRole.Progress).Name,
            "history" => plan.BindingTopics.Single(t => t.Role == CdcKafkaTopicRole.SchemaHistory).Name,
            "offsets" => _fixture.OffsetTopic,
            "configs" => _fixture.ConfigTopic,
            "status" => _fixture.StatusTopic,
            _ => request.Binding.TopicName,
        };
        string group = groupKind switch
        {
            "peer-group" => peer.WorkerPolicy.Consumers.Single().Group.Value,
            "worker-group" => _fixture.WorkerGroup,
            _ => request.WorkerPolicy.Consumers.Single().Group.Value,
        };
        (await _fixture.ReadAsync("consumer-" + instance, topic, group, Token))
            .Should()
            .Be(
                groupKind == "topic" ? ErrorCode.TopicAuthorizationFailed : ErrorCode.GroupAuthorizationFailed
            );
    }

    [Test]
    public async Task It_denies_consumer_writes_and_connector_access_to_worker_offsets()
    {
        Func<Task> write = () => _fixture.ProduceAsync("consumer-a", _request.Binding.TopicName, Token);
        (await write.Should().ThrowAsync<ProduceException<string, string>>())
            .Which.Error.Code.Should()
            .Be(ErrorCode.TopicAuthorizationFailed);
        Func<Task> offset = () => _fixture.ProduceAsync("connector-a", _fixture.OffsetTopic, Token);
        (await offset.Should().ThrowAsync<ProduceException<string, string>>())
            .Which.Error.Code.Should()
            .Be(ErrorCode.TopicAuthorizationFailed);
    }

    [TestCase(CdcKafkaTopicRole.SharedOffsets, "cleanup.policy", "delete")]
    [TestCase(CdcKafkaTopicRole.SharedOffsets, "min.insync.replicas", "1")]
    [TestCase(CdcKafkaTopicRole.Public, "cleanup.policy", "compact,delete")]
    [TestCase(CdcKafkaTopicRole.Public, "min.insync.replicas", "1")]
    [TestCase(CdcKafkaTopicRole.Public, "delete.retention.ms", "604799999")]
    [TestCase(CdcKafkaTopicRole.Public, "max.message.bytes", "1048575")]
    [TestCase(CdcKafkaTopicRole.Public, "max.message.bytes", "1048577")]
    [TestCase(CdcKafkaTopicRole.Progress, "cleanup.policy", "delete")]
    [TestCase(CdcKafkaTopicRole.SchemaHistory, "cleanup.policy", "compact")]
    [TestCase(CdcKafkaTopicRole.SchemaHistory, "retention.ms", "3600000")]
    [TestCase(CdcKafkaTopicRole.SchemaHistory, "retention.bytes", "1048576")]
    public async Task It_rejects_live_topic_drift_without_repair(
        CdcKafkaTopicRole role,
        string key,
        string changed
    )
    {
        var plan = CdcDeploymentKafkaPolicy.Build(_request);
        var intent = plan.BindingTopics.Append(plan.OffsetStore).Single(t => t.Role == role);
        await _fixture.SetConfigAsync(intent.Name, key, changed);
        try
        {
            var before = await _fixture.SnapshotAsync(_request, Token);
            var live = await _fixture.Adapter.InspectTopicAsync(_request, intent.Name, Token);
            CdcDeploymentKafkaPolicy
                .ObserveTopic(_request, intent, live)
                .State.Should()
                .Be(CoreCdc.CdcKafkaPolicyItemState.Invalid);
            await _fixture.Controller.ObserveBindingAsync(_request, Token);
            await _fixture.Controller.ObserveOffsetStoreAsync(_request, Token);
            (await _fixture.SnapshotAsync(_request, Token)).Should().Be(before);
            // Exact-match creation must return the drift, never silently alter existing configuration.
            var matched = await _fixture.Adapter.CreateMissingTopicAsync(_request, intent, Token);
            Value(matched).Configuration[key].Value.Should().Be(changed);
            if (role == CdcKafkaTopicRole.SharedOffsets)
            {
                (await _fixture.StartPolicyWorkerAsync(_request, false, Token))
                    .State.Should()
                    .Be(CdcTransportEvidenceState.Unavailable);
                _fixture.WorkerLaunches.Should().Be(1);
            }
        }
        finally
        {
            await _fixture.SetConfigAsync(intent.Name, key, intent.Configuration[key]);
        }
    }

    [TestCase(CdcKafkaTopicRole.SharedOffsets)]
    [TestCase(CdcKafkaTopicRole.Public)]
    public async Task It_requires_explicit_isr_even_when_the_broker_default_is_two(CdcKafkaTopicRole role)
    {
        var plan = CdcDeploymentKafkaPolicy.Build(_request);
        var intent = plan.BindingTopics.Append(plan.OffsetStore).Single(t => t.Role == role);
        await _fixture.SetConfigAsync(intent.Name, "min.insync.replicas", "", remove: true);
        try
        {
            var live = await _fixture.Adapter.InspectTopicAsync(_request, intent.Name, Token);
            Value(live)
                .Configuration["min.insync.replicas"]
                .Should()
                .Be(new CdcKafkaConfigurationValue("2", false));
            CdcDeploymentKafkaPolicy
                .ObserveTopic(_request, intent, live)
                .State.Should()
                .Be(CoreCdc.CdcKafkaPolicyItemState.Invalid);
        }
        finally
        {
            await _fixture.SetConfigAsync(intent.Name, "min.insync.replicas", "2");
        }
    }

    [Test]
    public async Task It_retains_tombstones_with_the_public_policy_and_accepts_stronger_retention()
    {
        var intent = CdcDeploymentKafkaPolicy.Build(_request).BindingTopics[0];
        var tombstone = await _fixture.ProduceAsync("connector-a", intent.Name, Token, tombstone: true);
        (await _fixture.ReadTombstoneAsync(tombstone.TopicPartitionOffset, Token)).Should().BeTrue();
        await _fixture.SetConfigAsync(intent.Name, "delete.retention.ms", "1209600000");
        try
        {
            CdcDeploymentKafkaPolicy
                .ObserveTopic(
                    _request,
                    intent,
                    await _fixture.Adapter.InspectTopicAsync(_request, intent.Name, Token)
                )
                .State.Should()
                .Be(CoreCdc.CdcKafkaPolicyItemState.Satisfied);
        }
        finally
        {
            await _fixture.SetConfigAsync(
                intent.Name,
                "delete.retention.ms",
                intent.Configuration["delete.retention.ms"]
            );
        }
    }

    [TestCase(CdcKafkaTopicRole.Public)]
    [TestCase(CdcKafkaTopicRole.Progress)]
    [TestCase(CdcKafkaTopicRole.SchemaHistory)]
    public async Task It_rejects_changed_partition_identity_and_actual_weak_replica_assignments(
        CdcKafkaTopicRole role
    )
    {
        var request = _fixture.Request("partition-" + role.ToString().ToLowerInvariant());
        var intent = CdcDeploymentKafkaPolicy.Build(request).BindingTopics.Single(t => t.Role == role);
        // These topics are fixture-owned drift resources, never active admitted bindings.
        await _fixture.CreateWeakTopicAsync(intent);
        try
        {
            var weak = await _fixture.Adapter.InspectTopicAsync(request, intent.Name, Token);
            Value(weak).PartitionReplicas.Values.Should().OnlyContain(ids => ids.Count == 1);
            CdcDeploymentKafkaPolicy
                .ObserveTopic(request, intent, weak)
                .State.Should()
                .Be(CoreCdc.CdcKafkaPolicyItemState.Invalid);
        }
        finally
        {
            await _fixture.DeleteTopicAsync(intent.Name);
        }
        await WaitAsync(
            async () =>
                (await _fixture.Adapter.InspectTopicAsync(request, intent.Name, Token)).State
                == CdcTransportEvidenceState.Absent,
            Token
        );
        await _fixture.CreateTopicAsync(request, intent, Token);
        try
        {
            await _fixture.AddPartitionAsync(intent.Name);
            var changed = await _fixture.Adapter.CreateMissingTopicAsync(request, intent, Token);
            Value(changed).PartitionReplicas.Count.Should().Be(2);
            CdcDeploymentKafkaPolicy
                .ObserveTopic(request, intent, changed)
                .State.Should()
                .Be(CoreCdc.CdcKafkaPolicyItemState.Invalid);
        }
        finally
        {
            await _fixture.DeleteTopicAsync(intent.Name);
        }
    }

    [Test]
    public async Task It_reads_capacity_from_every_broker_and_rejects_one_under_capacity_replica()
    {
        CdcDeploymentKafkaPolicy
            .ObserveBrokerCapacity(_request, await _fixture.EvidenceAsync(_request, Token))
            .Should()
            .Be(CoreCdc.CdcKafkaPolicyItemState.Satisfied);
        try
        {
            await _fixture.ChangeBrokerCapacityAsync(524_288, Token);
            var evidence = await _fixture.EvidenceAsync(_request, Token);
            Value(evidence.Brokers).Brokers.Count.Should().Be(3);
            Value(evidence.Brokers)
                .Brokers.Single(b => b.BrokerId == 1)
                .ReplicaFetchMaxBytes.Should()
                .Be(524_288);
            CdcDeploymentKafkaPolicy
                .ObserveBrokerCapacity(_request, evidence)
                .Should()
                .Be(CoreCdc.CdcKafkaPolicyItemState.Invalid);
            Attach(
                "broker-capacity-drift",
                new
                {
                    brokerCount = 3,
                    underCapacityBrokerCount = 1,
                    policy = "invalid",
                }
            );
        }
        finally
        {
            await _fixture.ChangeBrokerCapacityAsync(1_048_576, Token);
        }
    }

    [Test]
    public async Task It_preserves_unavailable_acl_authority_when_description_is_denied()
    {
        CdcKafkaAclGrant denied = new(
            "User:consumer-a",
            CdcKafkaAclResourceType.Cluster,
            "kafka-cluster",
            CdcKafkaAclOperation.Describe,
            Permission: CdcKafkaAclPermission.Deny
        );
        await _fixture.AddGrantAsync(denied);
        try
        {
            var configuration = new AdminClientConfig(_fixture.Client("consumer-a"));
            configuration.SaslUsername.Should().Be("consumer-a");
            using var restricted = Value(CdcKafkaAdminAdapter.Create(configuration, _fixture));
            (await _fixture.BrokerDeniesAclDescriptionAsync(Token))
                .Should()
                .BeTrue("the broker's Java client must confirm denied ACL inspection");
            var acls = await restricted.InspectAclsAsync(_request, Token);
            if (acls is CdcTransportResult<CdcKafkaAclEvidence>.Observed unexpected)
            {
                await TestContext.Progress.WriteLineAsync(
                    JsonSerializer.Serialize(
                        new
                        {
                            unexpected.Value.AuthorizationEnabled,
                            unexpected.Value.InventoryComplete,
                            grantCount = unexpected.Value.Grants.Count,
                        }
                    )
                );
            }
            acls.State.Should().Be(CdcTransportEvidenceState.Unavailable);
            acls.Diagnostics.Should().Contain(d => d.Failure == CdcDeploymentFailure.AuthenticationFailed);
            CdcDeploymentKafkaPolicy
                .ValidateAcls(_request, acls, false)
                .State.Should()
                .Be(CoreCdc.CdcKafkaPolicyItemState.Unknown);
        }
        finally
        {
            await _fixture.RemoveGrantAsync(denied);
        }
    }

    [Test]
    public async Task It_reconciles_only_missing_safe_grants_and_observation_cannot_repair_them()
    {
        var grant = CdcDeploymentKafkaPolicy
            .Build(_request)
            .BindingGrants.Single(g =>
                g.Principal == "User:consumer-a"
                && g.ResourceType == CdcKafkaAclResourceType.Topic
                && g.Operation == CdcKafkaAclOperation.Read
            );
        await _fixture.RemoveGrantAsync(grant);
        try
        {
            var before = await _fixture.SnapshotAsync(_request, Token);
            var missing = CdcDeploymentKafkaPolicy.ValidateAcls(
                _request,
                await _fixture.Adapter.InspectAclsAsync(_request, Token),
                false
            );
            missing.CanAddMissingGrants.Should().BeTrue();
            missing.MissingGrants.Should().ContainSingle().Which.Should().Be(grant);
            await _fixture.Controller.ObserveBindingAsync(_request, Token);
            (await _fixture.SnapshotAsync(_request, Token)).Should().Be(before);
            await _fixture.Adapter.ReconcileMissingGrantsAsync(_request, false, Token);
            // One authorized effect, followed only by live read-back until the grant propagates.
            // A native acknowledgement or the adapter's first read-back is not reusable readiness.
            await WaitAsync(
                async () =>
                    CdcDeploymentKafkaPolicy
                        .ValidateAcls(
                            _request,
                            await _fixture.Adapter.InspectAclsAsync(_request, Token),
                            false
                        )
                        .State == CoreCdc.CdcKafkaPolicyItemState.Satisfied,
                Token
            );
        }
        finally
        {
            await _fixture.AddGrantAsync(grant);
        }
    }

    [TestCase("wildcard")]
    [TestCase("prefix")]
    [TestCase("wildcard-principal")]
    [TestCase("peer")]
    [TestCase("internal")]
    [TestCase("deny")]
    public async Task It_fails_closed_on_unsafe_effective_grants_even_with_a_missing_required_grant(
        string kind
    )
    {
        var plan = CdcDeploymentKafkaPolicy.Build(_request);
        var missing = plan.BindingGrants.First(g => g.Principal == "User:connector-a");
        CdcKafkaAclGrant unsafeGrant = new(
            kind == "wildcard-principal" ? "User:*" : "User:consumer-a",
            CdcKafkaAclResourceType.Topic,
            kind switch
            {
                "wildcard" => "*",
                "prefix" => "edfi.documents",
                "peer" => _peer.Binding.TopicName,
                "internal" => _fixture.ConfigTopic,
                _ => _request.Binding.TopicName,
            },
            CdcKafkaAclOperation.Read,
            kind == "prefix" ? CdcKafkaAclPattern.Prefixed : CdcKafkaAclPattern.Literal,
            kind == "deny" ? CdcKafkaAclPermission.Deny : CdcKafkaAclPermission.Allow
        );
        await _fixture.AddGrantAsync(unsafeGrant);
        await _fixture.RemoveGrantAsync(missing);
        try
        {
            var before = await _fixture.SnapshotAsync(_request, Token);
            var result = await _fixture.Adapter.ReconcileMissingGrantsAsync(_request, false, Token);
            var validation = CdcDeploymentKafkaPolicy.ValidateAcls(_request, result, false);
            validation.UnsafeGrants.Should().BeTrue();
            validation.CanAddMissingGrants.Should().BeFalse();
            if (kind is "wildcard" or "prefix" or "peer")
            {
                (
                    await _fixture.ReadAsync(
                        "consumer-a",
                        _peer.Binding.TopicName,
                        _request.WorkerPolicy.Consumers.Single().Group.Value,
                        Token
                    )
                )
                    .Should()
                    .Be(
                        ErrorCode.NoError,
                        "the broker really applies the unsafe grant that the controller rejects"
                    );
            }
            if (kind is "wildcard" or "prefix" or "wildcard-principal" or "deny")
            {
                var identity = _request.Binding.ToCompleteBindingIdentity();
                var inventory = CoreCdc
                    .CdcArtifactNameGenerator.RecoverFromCompleteBindingIdentity(identity)
                    .Inventory!;
                var cleanup = await _fixture.Adapter.DeleteAsync(
                    new(_request, identity, inventory.GovernedArtifacts),
                    CoreCdc.CdcGovernedArtifactKind.PublicTopicAcls,
                    Token
                );
                cleanup
                    .State.Should()
                    .Be(
                        CdcTransportEvidenceState.Unavailable,
                        "governed teardown cannot delete an unsafe effective grant owned outside its exact inventory"
                    );
            }
            Value(result).Grants.Should().NotContain(missing);
            await _fixture.Controller.ObserveBindingAsync(_request, Token);
            (await _fixture.SnapshotAsync(_request, Token)).Should().Be(before);
        }
        finally
        {
            await _fixture.RemoveGrantAsync(unsafeGrant);
            await _fixture.AddGrantAsync(missing);
        }
    }

    [Test]
    public async Task It_cleans_only_the_governed_binding_topics_and_exact_topic_grants()
    {
        var request = _fixture.Request("retiring");
        await _fixture.ProvisionTopicsAsync(request, Token);
        var peerBefore = await _fixture.EvidenceAsync(_peer, Token);
        var identity = request.Binding.ToCompleteBindingIdentity();
        var inventory = CoreCdc
            .CdcArtifactNameGenerator.RecoverFromCompleteBindingIdentity(identity)
            .Inventory!;
        CdcArtifactCleanupScope scope = new(request, identity, inventory.GovernedArtifacts);
        foreach (
            var kind in new[]
            {
                CoreCdc.CdcGovernedArtifactKind.PublicTopicAcls,
                CoreCdc.CdcGovernedArtifactKind.ProgressTopicAcls,
                CoreCdc.CdcGovernedArtifactKind.SchemaHistoryTopicAcls,
                CoreCdc.CdcGovernedArtifactKind.PublicTopic,
                CoreCdc.CdcGovernedArtifactKind.ProgressTopic,
                CoreCdc.CdcGovernedArtifactKind.SchemaHistoryTopic,
            }
        )
        {
            // A lost/delayed read-back can require another governed cleanup pass. Reconcile absence,
            // including NotFound after an earlier delete, without substituting a native acknowledgement.
            await WaitAsync(
                async () =>
                {
                    var result = await _fixture.Adapter.DeleteAsync(scope, kind, Token);
                    return result is CdcTransportResult<CoreCdc.CdcGovernedArtifact>.Observed observed
                        && observed.Value.CleanupState
                            is CoreCdc.CdcCleanupState.Deleted
                                or CoreCdc.CdcCleanupState.NotFound;
                },
                Token
            );
        }
        // Group grants belong to deployment inventory and survive per-binding cleanup.
        var remaining = Value(await _fixture.Adapter.InspectAclsAsync(request, Token)).Grants;
        remaining
            .Should()
            .Contain(
                CdcDeploymentKafkaPolicy
                    .Build(request)
                    .BindingGrants.Single(g => g.ResourceType == CdcKafkaAclResourceType.Group)
            );
        var peerAfter = await _fixture.EvidenceAsync(_peer, Token);
        // Snapshot includes all ACLs: compare only peer/shared policy and exact grants after this binding's deletion.
        Value(await _fixture.Controller.ObserveOffsetStoreAsync(_peer, Token))
            .PolicyState.Should()
            .Be(CoreCdc.CdcConnectOffsetStorePolicyState.Satisfied);
        CdcDeploymentKafkaPolicy
            .ValidateAcls(_peer, await _fixture.Adapter.InspectAclsAsync(_peer, Token), false)
            .State.Should()
            .Be(CoreCdc.CdcKafkaPolicyItemState.Satisfied);
        foreach (var topic in CdcDeploymentKafkaPolicy.Build(_peer).BindingTopics)
        {
            CdcDeploymentKafkaPolicy
                .ObserveTopic(
                    _peer,
                    topic,
                    await _fixture.Adapter.InspectTopicAsync(_peer, topic.Name, Token)
                )
                .State.Should()
                .Be(CoreCdc.CdcKafkaPolicyItemState.Satisfied);
        }

        peerAfter.Topics.Should().BeEquivalentTo(peerBefore.Topics);
        var deletedGrants = CdcDeploymentKafkaPolicy
            .Build(request)
            .BindingGrants.Where(g => g.ResourceType == CdcKafkaAclResourceType.Topic)
            .ToHashSet();
        Value(peerAfter.Acls)
            .Grants.Should()
            .BeEquivalentTo(Value(peerBefore.Acls).Grants.Where(g => !deletedGrants.Contains(g)));
        foreach (string topic in new[] { _fixture.ConfigTopic, _fixture.StatusTopic })
        {
            Value(await _fixture.Adapter.InspectTopicAsync(request, topic, Token));
        }
    }

    private static void Attach(string name, object evidence)
    {
        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            TestContext.CurrentContext.Test.ID + "-" + name + ".json"
        );
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true })
        );
        TestContext.AddTestAttachment(path);
    }
}

[TestFixture]
[Category(CdcControllerCategories.KafkaPolicy)]
[Category("DatabaseIntegration")] // Repository umbrella for Docker-backed qualification.
[Category("KafkaIntegration")]
[Category("CdcAuthorizationDisabledLocal")]
[NonParallelizable]
public sealed class Given_explicit_authorization_disabled_local_kafka_policy
{
    [Test]
    public async Task It_labels_local_policy_without_claiming_acl_or_production_durability_proof()
    {
        await using CdcKafkaPolicyFixture fixture = new(false);
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(4));
        await fixture.InitializeAsync(timeout.Token);
        var request = fixture.Request();
        await fixture.PrepareOffsetsAsync(request, timeout.Token);
        var result = Value(await fixture.Controller.ObserveOffsetStoreAsync(request, timeout.Token));
        result.PolicyState.Should().Be(CoreCdc.CdcConnectOffsetStorePolicyState.Satisfied);
        result.Diagnostics.Should().Contain(d => d.Code == "authorizationDisabledLocal");
        var acls = Value(await fixture.Adapter.InspectAclsAsync(request, timeout.Token));
        acls.AuthorizationEnabled.Should().BeFalse();
        var policy = CdcDeploymentKafkaPolicy.ValidateAcls(
            request,
            new CdcTransportResult<CdcKafkaAclEvidence>.Observed(acls),
            true
        );
        policy.AuthorizationProfile.Should().Be(CdcKafkaAuthorizationProfile.AuthorizationDisabledLocal);
        // The same real broker evidence rejects a request that demands authorization and RF3/ISR2.
        await using CdcKafkaPolicyFixture requestFactory = new(true);
        var secured = requestFactory.Request();
        CdcDeploymentKafkaPolicy
            .ValidateAcls(secured, new CdcTransportResult<CdcKafkaAclEvidence>.Observed(acls), true)
            .State.Should()
            .Be(CoreCdc.CdcKafkaPolicyItemState.Invalid);
    }
}
