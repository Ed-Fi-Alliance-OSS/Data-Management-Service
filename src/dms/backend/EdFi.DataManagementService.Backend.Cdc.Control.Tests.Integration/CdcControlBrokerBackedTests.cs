// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using Confluent.Kafka.Admin;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using FluentAssertions.Execution;
using NUnit.Framework;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Control.Tests.Integration;

/// <summary>
/// The CDC control plane's Kafka, Connect, and Debezium evidence, taken from a real authorizer-enabled
/// broker, the pinned Connect worker, and a streaming connector rather than from fakes.
/// </summary>
/// <remarks>
/// <para>
/// These are the guarantees no fake can establish. Cleanup policy and durability are what the broker
/// stores rather than what a create request asked for; an effective ACL grant is what the authorizer
/// returns for a MATCH filter, including the over-broad patterns that must fail closed; the record-size
/// verdict comes from the broker's own limits; the committed-offset JSON and the Debezium metric object
/// names are the worker's and the connector's, not this suite's.
/// </para>
/// <para>
/// One stack serves the whole class. The tests are ordered because the later ones need the earlier
/// ones' artifacts — the binding topics must exist before their grants, the connector must be running
/// before its offsets and lag can be observed, and teardown must run last because it removes what the
/// rest asserted on.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("CdcControlBrokerBacked")]
public sealed class Given_CdcControlBrokerBackedStack
{
    private const string SchemaHistoryTopicIsPostgresqlAbsent =
        "a PostgreSQL binding has no schema-history topic";

    private static readonly TimeSpan StackStartupTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(6);

    private CdcControlBrokerFixture _fixture = null!;
    private CdcConnectorTemplateResult? _rendered;

    private CoreCdc.CdcArtifactInventory Inventory => _fixture.Inventory;

    [OneTimeSetUp]
    public async Task StartStackAsync()
    {
        using CancellationTokenSource cancellation = new(StackStartupTimeout);
        _fixture = await CdcControlBrokerFixture.StartAsync(cancellation.Token);
    }

    [OneTimeTearDown]
    public async Task StopStackAsync()
    {
        if (_fixture is not null)
        {
            await _fixture.DisposeAsync();
        }
    }

    [Test]
    [Order(1)]
    public async Task It_rejects_a_worker_created_offset_store_that_inherits_its_durability_from_the_broker()
    {
        using CancellationTokenSource cancellation = new(OperationTimeout);

        // A Connect worker that starts before the control plane creates the offset store itself, and
        // it sets only cleanup.policy on it. The remaining governed value is left to the broker
        // default, and a default is not evidence — so this is the state a Connect-first deployment
        // presents, and the control plane must refuse it rather than read the default as compliance.
        (await _fixture.TopicExistsAsync(_fixture.OffsetStoreTopicName))
            .Should()
            .BeTrue("the Connect worker creates its offset store before accepting any connector");

        await _fixture.GrantConnectWorkerOffsetStoreAclsAsync(cancellation.Token);

        CdcConnectOffsetStorePolicyObservation observation =
            await _fixture.KafkaAdmin.EnsureConnectOffsetStoreAsync(
                _fixture.ObservationContext,
                cancellation.Token
            );

        using AssertionScope _ = new();
        observation.PolicyState.Should().Be(CdcConnectOffsetStorePolicyState.Invalid);
        observation.CleanupPolicy.Should().Be("compact", "the worker does set the cleanup policy");
        observation
            .MinInSyncReplicas.Should()
            .Be(
                1,
                "the effective value is reported, and reporting a broker default is not the same as accepting it as the topic's own policy"
            );
        observation.AclState.Should().Be(CdcConnectOffsetStoreItemState.Satisfied);
        observation
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Component == CdcDiagnosticComponent.ConnectOffsetStore
                && diagnostic.Severity == CdcDiagnosticSeverity.Error
                && diagnostic.Path == "$.minInSyncReplicas"
            );
    }

    [Test]
    [Order(2)]
    public async Task It_reports_the_shared_connect_offset_store_as_compacted_durable_and_worker_only()
    {
        using CancellationTokenSource cancellation = new(OperationTimeout);

        // The deployment obligation the previous test names: an explicit topic-level override on the
        // shared store. With it supplied, the same pass validates the same worker-created topic.
        await _fixture.SetTopicConfigAsync(_fixture.OffsetStoreTopicName, "min.insync.replicas", "1");

        CdcConnectOffsetStorePolicyObservation observation =
            await _fixture.KafkaAdmin.EnsureConnectOffsetStoreAsync(
                _fixture.ObservationContext,
                cancellation.Token
            );

        using AssertionScope _ = new();
        observation.PolicyState.Should().Be(CdcConnectOffsetStorePolicyState.Satisfied);
        observation.OffsetStorageTopic.Should().Be(_fixture.OffsetStoreTopicName);
        observation.CleanupPolicy.Should().Be("compact");
        observation.ReplicationFactor.Should().Be(1);
        observation.MinInSyncReplicas.Should().Be(1);
        observation.AclState.Should().Be(CdcConnectOffsetStoreItemState.Satisfied);
        observation.Diagnostics.Should().BeEmpty();
    }

    [Test]
    [Order(3)]
    public async Task It_fails_closed_when_the_offset_store_carries_a_grant_beyond_the_connect_worker()
    {
        using CancellationTokenSource cancellation = new(OperationTimeout);

        await _fixture.CreateTopicAclsAsync(
            _fixture.OffsetStoreTopicName,
            CdcControlBrokerFixture.ConsumerPrincipal,
            [AclOperation.Read],
            ResourcePatternType.Literal,
            cancellation.Token
        );

        try
        {
            CdcConnectOffsetStorePolicyObservation observation =
                await _fixture.KafkaAdmin.EnsureConnectOffsetStoreAsync(
                    _fixture.ObservationContext,
                    cancellation.Token
                );

            using AssertionScope _ = new();
            observation.AclState.Should().Be(CdcConnectOffsetStoreItemState.Invalid);
            observation.PolicyState.Should().Be(CdcConnectOffsetStorePolicyState.Invalid);
            observation
                .Diagnostics.Should()
                .Contain(diagnostic =>
                    diagnostic.Component == CdcDiagnosticComponent.ConnectOffsetStore
                    && diagnostic.Severity == CdcDiagnosticSeverity.Error
                );
        }
        finally
        {
            await _fixture.DeleteTopicAclsAsync(
                _fixture.OffsetStoreTopicName,
                CdcControlBrokerFixture.ConsumerPrincipal,
                ResourcePatternType.Literal,
                CancellationToken.None
            );
        }
    }

    [Test]
    [Order(4)]
    public async Task It_fails_closed_when_the_offset_store_is_covered_by_an_over_broad_topic_pattern()
    {
        using CancellationTokenSource cancellation = new(OperationTimeout);
        string prefix = _fixture.OffsetStoreTopicName[..8];

        await _fixture.CreateTopicAclsAsync(
            prefix,
            CdcControlBrokerFixture.ConnectWorkerPrincipal,
            [AclOperation.Read],
            ResourcePatternType.Prefixed,
            cancellation.Token
        );

        try
        {
            // Only a real authorizer answers a MATCH filter with the prefixed pattern that covers this
            // topic; a literal-only read would never see the grant that widens access to it.
            CdcConnectOffsetStorePolicyObservation observation =
                await _fixture.KafkaAdmin.EnsureConnectOffsetStoreAsync(
                    _fixture.ObservationContext,
                    cancellation.Token
                );

            observation.AclState.Should().Be(CdcConnectOffsetStoreItemState.Invalid);
        }
        finally
        {
            await _fixture.DeleteTopicAclsAsync(
                prefix,
                CdcControlBrokerFixture.ConnectWorkerPrincipal,
                ResourcePatternType.Prefixed,
                CancellationToken.None
            );
        }
    }

    [Test]
    [Order(5)]
    public async Task It_creates_the_binding_topics_with_the_explicit_policy_values_the_broker_reports_back()
    {
        using CancellationTokenSource cancellation = new(OperationTimeout);

        CdcKafkaBindingTopicPolicies policies = await _fixture.KafkaAdmin.BindingTopicsAsync(
            Inventory,
            CdcKafkaProvisioningMode.CreateOrValidate,
            cancellation.Token
        );

        IReadOnlyDictionary<string, ConfigEntryResult> publicTopicConfig =
            await _fixture.ReadTopicConfigAsync(Inventory.TopicName);
        IReadOnlyDictionary<string, ConfigEntryResult> progressTopicConfig =
            await _fixture.ReadTopicConfigAsync(Inventory.ProgressTopicName);

        using AssertionScope _ = new();
        policies.PublicTopic.State.Should().Be(CdcKafkaPolicyItemState.Satisfied);
        policies.PublicTopic.TopicName.Should().Be(Inventory.TopicName);
        policies.PublicTopic.PartitionCount.Should().Be(3);
        policies.ProgressTopic.State.Should().Be(CdcKafkaPolicyItemState.Satisfied);
        policies.ProgressTopic.PartitionCount.Should().Be(1);
        policies.SchemaHistoryTopic.Should().BeNull(SchemaHistoryTopicIsPostgresqlAbsent);
        policies.Diagnostics.Should().BeEmpty();

        // Read straight from the broker: the governed values must be explicit topic-level overrides,
        // never a broker default the topic happens to inherit today.
        ConfigValue(publicTopicConfig, "cleanup.policy").Should().Be("compact");
        IsExplicitOverride(publicTopicConfig, "cleanup.policy").Should().BeTrue();
        ConfigValue(publicTopicConfig, "max.message.bytes")
            .Should()
            .Be(CdcControlBrokerFixture.MaxRecordBytes.ToString(CultureInfo.InvariantCulture));
        IsExplicitOverride(publicTopicConfig, "max.message.bytes").Should().BeTrue();
        IsExplicitOverride(publicTopicConfig, "min.insync.replicas").Should().BeTrue();
        long.Parse(ConfigValue(publicTopicConfig, "delete.retention.ms")!, CultureInfo.InvariantCulture)
            .Should()
            .BeGreaterThanOrEqualTo(604800000);
        ConfigValue(progressTopicConfig, "cleanup.policy").Should().Be("compact");
    }

    [Test]
    [Order(6)]
    public async Task It_validates_conforming_binding_topics_idempotently_without_recreating_them()
    {
        using CancellationTokenSource cancellation = new(OperationTimeout);

        CdcKafkaBindingTopicPolicies policies = await _fixture.KafkaAdmin.BindingTopicsAsync(
            Inventory,
            CdcKafkaProvisioningMode.CreateOrValidate,
            cancellation.Token
        );

        using AssertionScope _ = new();
        policies.PublicTopic.State.Should().Be(CdcKafkaPolicyItemState.Satisfied);
        policies.ProgressTopic.State.Should().Be(CdcKafkaPolicyItemState.Satisfied);
        policies.Diagnostics.Should().BeEmpty();
    }

    [Test]
    [Order(7)]
    public async Task It_accepts_a_record_size_budget_the_broker_limits_admit()
    {
        using CancellationTokenSource cancellation = new(OperationTimeout);

        CdcKafkaRecordSizeEvidence evidence = await _fixture.KafkaAdmin.VerifyRecordSizeAsync(
            Inventory,
            cancellation.Token
        );

        using AssertionScope _ = new();
        evidence.Policy.State.Should().Be(CdcKafkaPolicyItemState.Satisfied);
        evidence.Policy.MaxRecordBytes.Should().Be(CdcControlBrokerFixture.MaxRecordBytes);
        evidence
            .Policy.MaxMessageBytes.Should()
            .BeGreaterThanOrEqualTo(CdcControlBrokerFixture.MaxRecordBytes);
        evidence.Diagnostics.Should().BeEmpty();
    }

    [Test]
    [Order(8)]
    public async Task It_reports_a_budget_the_effective_message_limit_does_not_cover_as_unknown()
    {
        using CancellationTokenSource cancellation = new(OperationTimeout);

        // The public topic's explicit override is the effective limit for its produce path. A budget
        // above it is never presumed adequate, which is the whole point of reading the live limit.
        CdcKafkaRecordSizeEvidence evidence = await _fixture
            .KafkaAdminWith(options => options.MaxRecordBytes = CdcControlBrokerFixture.OversizedRecordBytes)
            .VerifyRecordSizeAsync(Inventory, cancellation.Token);

        using AssertionScope _ = new();
        evidence.Policy.State.Should().Be(CdcKafkaPolicyItemState.Unknown);
        evidence.Policy.MaxMessageBytes.Should().Be(CdcControlBrokerFixture.MaxRecordBytes);
        evidence.Diagnostics.Should().NotBeEmpty();
    }

    [Test]
    [Order(9)]
    public async Task It_rejects_a_budget_the_broker_replica_fetch_limits_cannot_carry()
    {
        using CancellationTokenSource cancellation = new(OperationTimeout);

        // A topic may carry a larger per-topic message limit than the broker can replicate. That is
        // exactly the misconfiguration the broker-limit check exists to catch, and it is only visible
        // against a real broker's own defaults.
        CoreCdc.CdcArtifactInventory oversizedInventory = CdcControlBrokerFixture.BuildVariantInventory(
            CdcControlBrokerFixture.OversizedGeneration
        );
        CdcKafkaAdminAdapter admin = _fixture.KafkaAdminWith(options =>
            options.MaxRecordBytes = CdcControlBrokerFixture.OversizedRecordBytes
        );

        await admin.BindingTopicsAsync(
            oversizedInventory,
            CdcKafkaProvisioningMode.CreateOrValidate,
            cancellation.Token
        );
        CdcKafkaRecordSizeEvidence evidence = await admin.VerifyRecordSizeAsync(
            oversizedInventory,
            cancellation.Token
        );

        using AssertionScope _ = new();
        evidence.Policy.State.Should().Be(CdcKafkaPolicyItemState.Invalid);
        evidence.Policy.MaxMessageBytes.Should().Be(CdcControlBrokerFixture.OversizedRecordBytes);
        evidence.Diagnostics.Should().NotBeEmpty();
    }

    [Test]
    [Order(10)]
    public async Task It_provisions_and_verifies_the_binding_grants_against_a_real_authorizer()
    {
        using CancellationTokenSource cancellation = new(OperationTimeout);
        await _fixture.CreateConsumerGroupAclAsync(
            CdcControlBrokerFixture.ConsumerGroup,
            CdcControlBrokerFixture.ConsumerPrincipal,
            cancellation.Token
        );

        CdcKafkaPolicyObservation observation = await _fixture.KafkaAdmin.EnsureBindingKafkaPolicyAsync(
            _fixture.ObservationContext,
            Inventory,
            cancellation.Token
        );

        IReadOnlyList<AclBinding> publicTopicGrants = await _fixture.DescribeTopicAclsAsync(
            Inventory.TopicName
        );
        IReadOnlyList<AclBinding> progressTopicGrants = await _fixture.DescribeTopicAclsAsync(
            Inventory.ProgressTopicName
        );

        using AssertionScope _ = new();
        observation.PolicyState.Should().Be(CdcKafkaPolicyState.Satisfied);
        observation.PublicTopicAcls.State.Should().Be(CdcKafkaPolicyItemState.Satisfied);
        observation.ProgressTopicAcls.State.Should().Be(CdcKafkaPolicyItemState.Satisfied);
        observation.SchemaHistoryTopicAcls.Should().BeNull(SchemaHistoryTopicIsPostgresqlAbsent);
        observation.Diagnostics.Should().BeEmpty();

        // The grants the authorizer actually holds, located by principal and operation rather than by
        // the order the broker happens to return them in.
        HasGrant(publicTopicGrants, CdcControlBrokerFixture.ConnectorPrincipal, AclOperation.Write)
            .Should()
            .BeTrue();
        HasGrant(publicTopicGrants, CdcControlBrokerFixture.ConnectorPrincipal, AclOperation.Describe)
            .Should()
            .BeTrue();
        HasGrant(publicTopicGrants, CdcControlBrokerFixture.ConsumerPrincipal, AclOperation.Read)
            .Should()
            .BeTrue();
        HasGrant(progressTopicGrants, CdcControlBrokerFixture.ConsumerPrincipal, AclOperation.Read)
            .Should()
            .BeFalse("the progress topic is connector-internal state no instance consumer may read");
    }

    [Test]
    [Order(11)]
    public async Task It_fails_closed_when_an_instance_consumer_can_read_another_instances_topic()
    {
        using CancellationTokenSource cancellation = new(OperationTimeout);
        string otherInstanceTopic = $"{Inventory.TopicName}.other-instance";

        await _fixture.CreateTopicAclsAsync(
            otherInstanceTopic,
            CdcControlBrokerFixture.ConsumerPrincipal,
            [AclOperation.Read],
            ResourcePatternType.Literal,
            cancellation.Token
        );

        try
        {
            CdcKafkaPolicyObservation observation = await _fixture.KafkaAdmin.EnsureBindingKafkaPolicyAsync(
                _fixture.ObservationContext,
                Inventory,
                cancellation.Token
            );

            using AssertionScope _ = new();
            observation.PublicTopicAcls.State.Should().Be(CdcKafkaPolicyItemState.Invalid);
            observation.PolicyState.Should().Be(CdcKafkaPolicyState.Invalid);
            observation
                .Diagnostics.Should()
                .Contain(diagnostic => diagnostic.Severity == CdcDiagnosticSeverity.Error);
        }
        finally
        {
            await _fixture.DeleteTopicAclsAsync(
                otherInstanceTopic,
                CdcControlBrokerFixture.ConsumerPrincipal,
                ResourcePatternType.Literal,
                CancellationToken.None
            );
        }
    }

    [Test]
    [Order(12)]
    public async Task It_fails_closed_when_an_instance_consumer_can_read_the_progress_topic()
    {
        using CancellationTokenSource cancellation = new(OperationTimeout);

        await _fixture.CreateTopicAclsAsync(
            Inventory.ProgressTopicName,
            CdcControlBrokerFixture.ConsumerPrincipal,
            [AclOperation.Read],
            ResourcePatternType.Literal,
            cancellation.Token
        );

        try
        {
            CdcKafkaPolicyObservation observation = await _fixture.KafkaAdmin.EnsureBindingKafkaPolicyAsync(
                _fixture.ObservationContext,
                Inventory,
                cancellation.Token
            );

            using AssertionScope _ = new();
            observation.ProgressTopicAcls.State.Should().Be(CdcKafkaPolicyItemState.Invalid);
            observation.PolicyState.Should().Be(CdcKafkaPolicyState.Invalid);
        }
        finally
        {
            await _fixture.DeleteTopicAclsAsync(
                Inventory.ProgressTopicName,
                CdcControlBrokerFixture.ConsumerPrincipal,
                ResourcePatternType.Literal,
                CancellationToken.None
            );
        }
    }

    [Test]
    [Order(13)]
    public async Task It_validates_the_rendered_connector_against_the_pinned_plugin_and_registers_it()
    {
        using CancellationTokenSource cancellation = new(OperationTimeout);
        CdcConnectorTemplateResult rendered = await RenderedConnectorAsync(cancellation.Token);
        string connectorClass = rendered.Config["connector.class"];

        // The pinned image is what decides whether the rendered configuration is loadable at all: the
        // plugin validates its own properties, and a class the image does not ship answers 404.
        CdcConnectResult<CdcConnectConfigValidation> validation =
            await _fixture.Connect.ValidateConnectorPluginConfigAsync(
                connectorClass,
                rendered.Config,
                cancellation.Token
            );

        // Asserted before anything waits on the worker: a plugin the image does not ship, or a
        // configuration it refuses, must be reported as itself rather than as a state that never
        // arrives. Waiting first would report every such failure as an indistinguishable timeout.
        validation.Succeeded.Should().BeTrue("{0}", Summary(validation.Failure));
        validation
            .Value!.ErrorCount.Should()
            .Be(0, "{0}", string.Join(",", validation.Value.ErrorPropertyNames));

        CdcConnectResult registration = await _fixture.Connect.PutConnectorConfigAsync(
            _fixture.ConnectorName,
            rendered.Config,
            cancellation.Token
        );
        registration.Succeeded.Should().BeTrue("{0}", Summary(registration.Failure));

        CdcConnectorStatus status = await _fixture.WaitForConnectorStateAsync("RUNNING", cancellation.Token);

        using AssertionScope _ = new();
        status.Tasks.Should().NotBeEmpty();
        status.Tasks.Should().OnlyContain(task => task.State == "RUNNING");
    }

    [Test]
    [Order(14)]
    public async Task It_reads_back_the_live_connector_configuration_the_worker_holds()
    {
        using CancellationTokenSource cancellation = new(OperationTimeout);
        CdcConnectorTemplateResult rendered = await RenderedConnectorAsync(cancellation.Token);

        CdcConnectResult<IReadOnlyDictionary<string, string>> readBack =
            await _fixture.Connect.GetConnectorConfigAsync(_fixture.ConnectorName, cancellation.Token);

        using AssertionScope _ = new();
        readBack.Succeeded.Should().BeTrue();
        foreach (KeyValuePair<string, string> property in rendered.Config)
        {
            readBack.Value!.Should().ContainKey(property.Key);
            readBack.Value![property.Key].Should().Be(property.Value);
        }

        // The worker adds the connector's own name to what it stores, which the template never renders.
        readBack.Value!["name"].Should().Be(_fixture.ConnectorName);
    }

    [Test]
    [Order(15)]
    public async Task It_observes_provider_heartbeat_progress_and_the_committed_source_offsets()
    {
        using CancellationTokenSource cancellation = new(OperationTimeout);

        long startingSequence = await _fixture.ReadHeartbeatSequenceAsync(cancellation.Token);
        long advancedSequence = await _fixture.WaitForHeartbeatProgressAsync(
            startingSequence,
            cancellation.Token
        );
        CdcConnectorOffsets offsets = await _fixture.WaitForCommittedOffsetsAsync(cancellation.Token);

        using AssertionScope _ = new();
        advancedSequence.Should().BeGreaterThan(startingSequence);
        offsets.Entries.Should().NotBeEmpty();
        offsets
            .Entries.Should()
            .OnlyContain(entry => entry.Partition.ValueKind == System.Text.Json.JsonValueKind.Object);
    }

    [Test]
    [Order(16)]
    public async Task It_reads_the_debezium_source_lag_current_value_and_every_quantile_over_jolokia()
    {
        using CancellationTokenSource cancellation = new(OperationTimeout);

        CdcConnectorLagReadResult result = await ReadLagUntilAvailableAsync(cancellation.Token);

        using AssertionScope _ = new();
        result.Outcome.Should().Be(CdcConnectorLagReadOutcome.Succeeded);
        result.Reading.Should().NotBeNull();
        result.Reading!.CurrentMilliseconds.Should().BeGreaterThanOrEqualTo(0);
        result.Reading.P50Milliseconds.Should().BeGreaterThanOrEqualTo(0);
        result.Reading.P95Milliseconds.Should().BeGreaterThanOrEqualTo(0);
        result.Reading.P99Milliseconds.Should().BeGreaterThanOrEqualTo(0);
    }

    [Test]
    [Order(17)]
    public async Task It_restarts_the_running_connector_and_returns_it_to_running()
    {
        using CancellationTokenSource cancellation = new(OperationTimeout);

        CdcConnectResult restart = await _fixture.Connect.RestartConnectorAsync(
            _fixture.ConnectorName,
            cancellation.Token
        );
        restart.Succeeded.Should().BeTrue("{0}", Summary(restart.Failure));

        CdcConnectorStatus status = await _fixture.WaitForConnectorStateAsync("RUNNING", cancellation.Token);
        CdcConnectorOffsets offsets = await _fixture.WaitForCommittedOffsetsAsync(cancellation.Token);

        using AssertionScope _ = new();
        status.ConnectorState.Should().Be("RUNNING");
        offsets
            .Entries.Should()
            .NotBeEmpty("a restart resumes from the shared offset store rather than discarding it");
    }

    [Test]
    [Order(18)]
    public async Task It_requires_a_stopped_connector_before_its_committed_offsets_can_be_deleted()
    {
        using CancellationTokenSource cancellation = new(OperationTimeout);

        // The worker refuses an offsets delete for a connector that is not STOPPED, which is why the
        // retirement sequence stops first and deletes the connector only afterwards.
        CdcConnectResult refusedWhileRunning = await _fixture.Connect.DeleteConnectorOffsetsAsync(
            _fixture.ConnectorName,
            cancellation.Token
        );

        CdcConnectResult stop = await _fixture.Connect.StopConnectorAsync(
            _fixture.ConnectorName,
            cancellation.Token
        );
        stop.Succeeded.Should().BeTrue("{0}", Summary(stop.Failure));
        await _fixture.WaitForConnectorStateAsync("STOPPED", cancellation.Token);

        CdcConnectResult deleted = await _fixture.Connect.DeleteConnectorOffsetsAsync(
            _fixture.ConnectorName,
            cancellation.Token
        );
        CdcConnectResult<CdcConnectorOffsets> remaining = await _fixture.Connect.GetConnectorOffsetsAsync(
            _fixture.ConnectorName,
            cancellation.Token
        );

        using AssertionScope _ = new();
        refusedWhileRunning.Succeeded.Should().BeFalse();
        deleted.Succeeded.Should().BeTrue("{0}", Summary(deleted.Failure));
        remaining.Succeeded.Should().BeTrue();
        remaining.Value!.Entries.Should().BeEmpty();
    }

    [Test]
    [Order(19)]
    public async Task It_deletes_exactly_the_binding_governed_artifacts_and_leaves_the_shared_offset_store()
    {
        using CancellationTokenSource cancellation = new(OperationTimeout);

        CdcConnectResult connectorDeleted = await _fixture.Connect.DeleteConnectorAsync(
            _fixture.ConnectorName,
            cancellation.Token
        );

        IReadOnlyList<CdcGovernedArtifact> artifacts = await _fixture.KafkaAdmin.DeleteBindingArtifactsAsync(
            Inventory,
            cancellation.Token
        );

        using AssertionScope _ = new();
        connectorDeleted.Succeeded.Should().BeTrue();

        // Located by kind and name, never by position: the order the adapter reports artifacts in is
        // not part of the contract.
        Artifact(artifacts, CdcGovernedArtifactKind.PublicTopic)
            .ArtifactName.Should()
            .Be(Inventory.TopicName);
        Artifact(artifacts, CdcGovernedArtifactKind.PublicTopic)
            .CleanupState.Should()
            .Be(CdcCleanupState.Deleted);
        Artifact(artifacts, CdcGovernedArtifactKind.PublicTopicAcls)
            .CleanupState.Should()
            .Be(CdcCleanupState.Deleted);
        Artifact(artifacts, CdcGovernedArtifactKind.ProgressTopic)
            .ArtifactName.Should()
            .Be(Inventory.ProgressTopicName);
        Artifact(artifacts, CdcGovernedArtifactKind.ProgressTopic)
            .CleanupState.Should()
            .Be(CdcCleanupState.Deleted);
        artifacts
            .Should()
            .NotContain(artifact => artifact.ArtifactKind == CdcGovernedArtifactKind.SchemaHistoryTopic);
        artifacts
            .Should()
            .NotContain(artifact =>
                string.Equals(artifact.ArtifactName, _fixture.OffsetStoreTopicName, StringComparison.Ordinal)
            );

        (await _fixture.WaitForTopicAbsentAsync(Inventory.TopicName, cancellation.Token)).Should().BeTrue();
        (await _fixture.WaitForTopicAbsentAsync(Inventory.ProgressTopicName, cancellation.Token))
            .Should()
            .BeTrue();
        (await _fixture.DescribeTopicAclsAsync(Inventory.TopicName)).Should().BeEmpty();

        // The shared store is worker state for every binding, so no binding's teardown may touch it.
        (await _fixture.TopicExistsAsync(_fixture.OffsetStoreTopicName))
            .Should()
            .BeTrue();
        (await _fixture.DescribeTopicAclsAsync(_fixture.OffsetStoreTopicName)).Should().NotBeEmpty();
    }

    /// <summary>
    /// Renders once and reuses the result: rendering runs provider setup, and the exact-match pass is
    /// meaningful only against the artifacts the first pass created.
    /// </summary>
    private async Task<CdcConnectorTemplateResult> RenderedConnectorAsync(CancellationToken cancellationToken)
    {
        _rendered ??= await _fixture.RenderConnectorAsync(cancellationToken);
        return _rendered;
    }

    /// <summary>
    /// Debezium registers its streaming MBean once the connector leaves the snapshot phase, so the
    /// first read after registration can legitimately find no metrics yet.
    /// </summary>
    private async Task<CdcConnectorLagReadResult> ReadLagUntilAvailableAsync(
        CancellationToken cancellationToken
    )
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        CdcConnectorLagReadResult result = new(CdcConnectorLagReadOutcome.MetricsAbsent, null, "not read");

        while (DateTimeOffset.UtcNow < deadline)
        {
            result = await _fixture.LagReader.ReadAsync(
                CoreCdc.CdcProvider.Postgresql,
                _fixture.ControlOptions.TopicPrefix,
                cancellationToken
            );

            if (result.Succeeded)
            {
                return result;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        return result;
    }

    private static CdcGovernedArtifact Artifact(
        IReadOnlyList<CdcGovernedArtifact> artifacts,
        CdcGovernedArtifactKind kind
    ) =>
        artifacts.SingleOrDefault(artifact => artifact.ArtifactKind == kind)
        ?? throw new InvalidOperationException($"No governed artifact of kind {kind} was reported.");

    /// <summary>
    /// The adapter's own bounded failure summary, which carries the status and the operation but never
    /// a worker response body, so an assertion message stays as safe as the diagnostics do.
    /// </summary>
    private static string Summary(CdcConnectFailure? failure) =>
        failure is null ? "no failure was reported" : failure.Summary;

    private static bool HasGrant(
        IReadOnlyList<AclBinding> bindings,
        string principal,
        AclOperation operation
    ) =>
        bindings.Any(binding =>
            string.Equals(binding.Entry.Principal, principal, StringComparison.Ordinal)
            && binding.Entry.Operation == operation
            && binding.Entry.PermissionType == AclPermissionType.Allow
        );

    private static string? ConfigValue(
        IReadOnlyDictionary<string, ConfigEntryResult> entries,
        string configName
    ) => entries.TryGetValue(configName, out ConfigEntryResult? entry) ? entry.Value : null;

    private static bool IsExplicitOverride(
        IReadOnlyDictionary<string, ConfigEntryResult> entries,
        string configName
    ) => entries.TryGetValue(configName, out ConfigEntryResult? entry) && !entry.IsDefault;
}
