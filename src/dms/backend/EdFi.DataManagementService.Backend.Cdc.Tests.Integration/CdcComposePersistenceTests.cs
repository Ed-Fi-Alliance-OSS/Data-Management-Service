// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using Confluent.Kafka;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Cdc.Tests.Integration.CdcProviderAdmissionFixture;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture(CdcProvider.Postgresql, Category = "PostgresqlIntegration")]
[TestFixture(CdcProvider.SqlServer, Category = "MssqlIntegration")]
[Category(CdcControllerCategories.RecordSize)]
[Category("DatabaseIntegration")]
[Category("CdcAuthorizationDisabledLocal")]
[NonParallelizable]
public sealed class Given_Cdc_Compose_Persistence(CdcProvider provider)
{
    private CdcProviderAdmissionFixture _fixture = null!;
    private CdcDeploymentRequest _request = null!;
    private CancellationTokenSource _timeout = null!;
    private CancellationToken Token => _timeout.Token;
    private IAdminClient _admin = null!;
    private CdcKafkaAdminAdapter _sizes = null!;
    private readonly List<object> _evidence = [];
    private CdcConnectorTemplatePinnedImageFixture Resources => _fixture.Infrastructure.Resources;

    private CdcControllerStatusTarget Target(CdcDeploymentRequest request) =>
        new(request, _fixture.Runtime, 60_000);

    private string _sentinel = "";
    private TopicPartitionOffset _sentinelPosition = null!;

    [SetUp]
    public async Task Setup()
    {
        _evidence.Clear();
        _timeout = new(TimeSpan.FromMinutes(12));
        _fixture = await CdcProviderAdmissionFixture.StartAsync(provider, Token, composeKafka: true);
        // Compose --wait includes the shipped 30-second health-check interval.
        _request = _fixture.WithTiming(
            new(
                TimeSpan.FromMinutes(3),
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromMinutes(1)
            )
        );
        await _fixture.RegisterAsync(Token);
        Observed(
            await _fixture.Controllers.Admission.PreparePublicationAsync(
                _request,
                _fixture.Runtime,
                60_000,
                Token
            )
        );
        await _fixture.ReopenRuntimeAsync(Token);
        _admin = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = Resources.ControllerKafkaBootstrapServers }
        )
            .SetLogHandler((_, _) => { })
            .SetErrorHandler((_, _) => { })
            .Build();
        _sizes = Observed(
            CdcKafkaAdminAdapter.Create(
                new AdminClientConfig { BootstrapServers = Resources.ControllerKafkaBootstrapServers },
                new CdcComposeKafkaAuthorizationInspection(
                    Resources.ControllerProject,
                    Resources.ControllerKafkaBootstrapServers,
                    Resources.KafkaBootstrapServers
                ),
                Resources.ComposeBrokerSizes
            )
        );
        using var producer = new ProducerBuilder<string, string>(
            new ProducerConfig
            {
                BootstrapServers = Resources.ControllerKafkaBootstrapServers,
                Acks = Acks.All,
            }
        )
            .SetLogHandler((_, _) => { })
            .SetErrorHandler((_, _) => { })
            .Build();
        _sentinel = "compose-persistence-" + Guid.NewGuid().ToString("N");
        _sentinelPosition = (
            await producer.ProduceAsync(
                _request.Binding.TopicName,
                new() { Key = _sentinel, Value = _sentinel },
                Token
            )
        ).TopicPartitionOffset;
        await Resources.AssertComposeDataMountAsync(Token);
        await TestContext.Progress.WriteLineAsync("Compose persistence: initial admission complete.");
    }

    [TearDown]
    public async Task TearDown()
    {
        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "record-size-compose-" + Guid.NewGuid().ToString("N") + ".json"
        );
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(new { Provider = provider.ToString(), Evidence = _evidence })
        );
        TestContext.AddTestAttachment(path, "Sanitized shipped Compose recreation and continuity evidence");
        _sizes?.Dispose();
        _admin?.Dispose();
        if (_fixture is not null)
        {
            await _fixture.DisposeAsync();
        }
        _timeout.Dispose();
    }

    [Test]
    public async Task It_retains_broker_history_through_managed_down_up_and_acknowledged_size_recreation()
    {
        var stop = await _fixture.Controllers.Lifecycle.ExecuteAsync(
            Target(_request),
            CdcManagedLifecycleOperation.Stop,
            Token
        );
        stop.TargetShutdownVerified.Should().BeTrue("{0}", JsonSerializer.Serialize(stop));
        var baseline = await CaptureAsync(_request);
        string originalContainer = await Resources.ComposeBrokerContainerIdAsync(Token);
        string originalWorker = await Resources.ComposeWorkerContainerIdAsync(Token);
        originalWorker.Should().HaveLength(64);
        await Resources.RecreateControllerComposeAsync(_request, _fixture.KafkaProvisioning(), Token);
        (await Resources.ComposeWorkerContainerIdAsync(Token)).Should().NotBe(originalWorker);
        (await Resources.ComposeBrokerContainerIdAsync(Token)).Should().NotBe(originalContainer);
        await Resources.AssertComposeDataMountAsync(Token);
        var retained = await CaptureAsync(_request);
        retained
            .Should()
            .BeEquivalentTo(
                baseline,
                "verified stop must retain exact committed offsets and configuration through down/up"
            );
        Observed(await _fixture.Infrastructure.Connect.ReadStatusAsync(_request, Token))
            .IsStopped.Should()
            .BeTrue();
        AssertSentinel();
        // The exact stopped offset has already been compared. Give the resumed SQL Server
        // reader a current source event: replaying only a pre-shutdown heartbeat legitimately
        // reports the full Compose outage as lag, even after the reader catches up.
        await _fixture.ExecuteAsync(
            provider == CdcProvider.Postgresql
                ? "UPDATE dms.\"CdcHeartbeat\" SET \"HeartbeatSequence\" = \"HeartbeatSequence\" + 1, \"HeartbeatAt\" = now() WHERE \"HeartbeatId\" = 1"
                : "UPDATE dms.CdcHeartbeat SET HeartbeatSequence = HeartbeatSequence + 1, HeartbeatAt = SYSUTCDATETIME() WHERE HeartbeatId = 1",
            Token
        );
        var start = await _fixture.Controllers.Lifecycle.ExecuteAsync(
            Target(_request),
            CdcManagedLifecycleOperation.Start,
            Token
        );
        start
            .Recovery.Boundary.Should()
            .Be(CdcRecoveryBoundary.VerifiedManagedRestart, "{0}", JsonSerializer.Serialize(start));
        start.Diagnostics.Should().NotContain(d => d.Component != CdcDeploymentComponent.Metrics);
        // A completed start returns its current readiness; it need not already have consumed
        // the new event. Poll fresh validation only, without another restart or a larger lag limit.
        await CdcControllerFixture.WaitAsync(
            async ct =>
            {
                var live = Observed(
                    await _fixture.Controllers.Validation.ValidateAsync(
                        _request,
                        _fixture.Runtime,
                        CdcEstablishedValidationMode.RunningPublication,
                        60_000,
                        cancellationToken: ct
                    )
                );
                live.PreStartEligible.Should().BeTrue();
                live.Continuity.Should().Be(CoreCdc.CdcSourceHistoryContinuity.Healthy);
                live.Diagnostics.Should().NotContain(d => d.Component != CdcDeploymentComponent.Metrics);
                return live.PublicationReady;
            },
            _request.Timing.WaitTimeout,
            _request.Timing.PollInterval,
            Token
        );
        _evidence.Add(
            new
            {
                Phase = "managed-down-up",
                ContainerReplaced = true,
                RetainedExactly = true,
                SentinelRetained = true,
                InitiallyReady = start.Ready,
                FreshValidationReady = true,
            }
        );

        await TestContext.Progress.WriteLineAsync(
            "Compose persistence: managed down/up and guarded start verified."
        );
        var beforeIncrease = await CaptureAsync(_request);
        var beforeBrokers = Observed(await _fixture.Kafka.InspectBrokersAsync(_request, Token));
        const int ceiling = 2_000_000;
        const int buffer = 67_108_864;
        beforeBrokers.Brokers.Single().ReplicaFetchMaxBytes.Should().BeLessThan(ceiling);
        string beforeSizeContainer = await Resources.ComposeBrokerContainerIdAsync(Token);
        var scope = new CdcRecordSizeIncreaseScope(
            Guid.NewGuid(),
            _request.Binding.ToCompleteBindingIdentity(),
            _request.ConnectorPolicy.MaxRecordBytes,
            ceiling
        );
        var increase = await _fixture
            .Controllers.RecordSize(_sizes)
            .IncreaseAsync(
                Target(_request),
                scope,
                buffer,
                (invocation, _) =>
                    Task.FromResult(
                        new CdcRecordSizeIncreaseConfirmation(
                            scope,
                            new(invocation.InvocationId, "compose-operator", DateTimeOffset.UtcNow, true, []),
                            true
                        )
                    ),
                Token
            );
        increase.Succeeded.Should().BeTrue("{0}", JsonSerializer.Serialize(increase));
        increase.Ready.Should().BeTrue();
        await TestContext.Progress.WriteLineAsync(
            "Compose persistence: acknowledged broker-size recreation complete."
        );
        (await Resources.ComposeBrokerContainerIdAsync(Token))
            .Should()
            .NotBe(
                beforeSizeContainer,
                "the production adapter must recreate the broker, not restart its old writable layer"
            );
        await Resources.AssertComposeDataMountAsync(Token);
        var desired = CdcRecordSizeRollout.WithPolicy(_request, ceiling, buffer);
        var afterIncrease = await CaptureAsync(desired);
        afterIncrease.Cluster.Should().Be(beforeIncrease.Cluster);
        afterIncrease.TopicIds.Should().Be(beforeIncrease.TopicIds);
        afterIncrease.PartitionHash.Should().Be(beforeIncrease.PartitionHash);
        CdcConnectorTemplatePinnedImageFixture
            .CommittedSourceOffsetRetainsOrAdvances(provider, beforeIncrease.Offset, afterIncrease.Offset)
            .Should()
            .BeTrue();
        afterIncrease
            .Configuration.Where(p =>
                p.Key is not "producer.override.max.request.size" and not "producer.override.buffer.memory"
            )
            .Should()
            .BeEquivalentTo(
                beforeIncrease.Configuration.Where(p =>
                    p.Key
                        is not "producer.override.max.request.size"
                            and not "producer.override.buffer.memory"
                )
            );
        afterIncrease
            .Configuration["producer.override.max.request.size"]
            .Should()
            .Be(ceiling.ToString(System.Globalization.CultureInfo.InvariantCulture));
        afterIncrease
            .Configuration["producer.override.buffer.memory"]
            .Should()
            .Be(buffer.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var afterBrokers = Observed(await _fixture.Kafka.InspectBrokersAsync(desired, Token));
        afterBrokers
            .Brokers.Should()
            .OnlyContain(b =>
                b.ReplicaFetchMaxBytes >= ceiling
                && b.SocketRequestMaxBytes >= ceiling
                && b.ReplicaFetchResponseMaxBytes >= ceiling
            );
        AssertSentinel();
        var validation = Observed(
            await _fixture.Controllers.Validation.ValidateAsync(
                desired,
                _fixture.Runtime,
                CdcEstablishedValidationMode.RunningPublication,
                60_000,
                cancellationToken: Token
            )
        );
        validation.PublicationReady.Should().BeTrue("{0}", string.Join(", ", validation.Diagnostics));
        (await _fixture.JournalAsync(Token)).HasPendingRecordSizeIncrease.Should().BeFalse();
        _fixture
            .Infrastructure.ConnectCalls.Should()
            .NotContain(nameof(ICdcConnectTransport.DeleteOffsetsAsync));
        _fixture.ProviderModes.Skip(1).Should().OnlyContain(m => m == CdcProviderSetupMode.ValidateOnly);
        (await _fixture.Infrastructure.Bindings.ExactMatchBindingAsync(_request.Binding, Token))
            .State!.Incident.Should()
            .BeNull();
        _evidence.Add(
            new
            {
                Phase = "size-recreation",
                ContainerReplaced = true,
                ClusterRetained = true,
                TopicsRetained = true,
                SourceOffsetRetainedOrAdvanced = true,
                SentinelRetained = true,
                increase.Ready,
                FreshValidationReady = validation.PublicationReady,
                Brokers = afterBrokers.Brokers,
            }
        );
    }

    private async Task<Retained> CaptureAsync(CdcDeploymentRequest request)
    {
        var topics = CdcDeploymentKafkaPolicy
            .Build(request)
            .BindingTopics.Select(t => t.Name)
            .Concat(
                new[]
                {
                    Resources.ControllerProject + ".connect.configs",
                    Resources.ControllerProject + ".connect.offsets",
                    Resources.ControllerProject + ".connect.status",
                }
            );
        var described = await _admin
            .DescribeTopicsAsync(
                TopicCollection.OfTopicNames(topics),
                new() { RequestTimeout = TimeSpan.FromSeconds(10) }
            )
            .WaitAsync(Token);
        string ids = string.Join(
            ",",
            described
                .TopicDescriptions.OrderBy(t => t.Name, StringComparer.Ordinal)
                .Select(t => t.TopicId.ToString())
        );
        var cluster = await _admin
            .DescribeClusterAsync(new() { RequestTimeout = TimeSpan.FromSeconds(10) })
            .WaitAsync(Token);
        var offsets = Observed(await _fixture.Infrastructure.Connect.ReadOffsetsAsync(request, Token));
        offsets.GetProperty("offsets").GetArrayLength().Should().Be(1);
        var offsetEvidence = Observed(
            await _fixture.Infrastructure.Connect.ReadOffsetEvidenceAsync(request, Token)
        );
        offsetEvidence.State.Should().Be(CdcConnectOffsetState.Streaming);
        return new(
            cluster.ClusterId,
            ids,
            offsets.GetProperty("offsets")[0].GetProperty("offset").GetRawText(),
            offsetEvidence.SourcePartitionHash,
            Observed(await _fixture.Infrastructure.Connect.ReadConfigurationAsync(request, Token))
        );
    }

    private void AssertSentinel()
    {
        using var consumer = new ConsumerBuilder<string, string>(
            new ConsumerConfig
            {
                BootstrapServers = Resources.ControllerKafkaBootstrapServers,
                GroupId = "compose-persistence-readback",
                EnableAutoCommit = false,
            }
        ).SetLogHandler((_, _) => { }).SetErrorHandler((_, _) => { }).Build();
        consumer.Assign(_sentinelPosition);
        var record = consumer.Consume(TimeSpan.FromSeconds(30));
        record.Should().NotBeNull();
        record!.Message.Key.Should().Be(_sentinel);
        record.Message.Value.Should().Be(_sentinel);
        record.TopicPartitionOffset.Should().Be(_sentinelPosition);
    }

    private sealed record Retained(
        string Cluster,
        string TopicIds,
        string Offset,
        string PartitionHash,
        IReadOnlyDictionary<string, string> Configuration
    );
}
