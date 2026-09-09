// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
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
public sealed partial class Given_Cdc_Controller_Record_Size_Increase(CdcProvider provider)
{
    private const int Ceiling = 2_000_000;
    private const int Buffer = 67_108_864;
    private CdcProviderAdmissionFixture _fixture = null!;
    private CdcKafkaAdminAdapter _sizes = null!;
    private CancellationTokenSource _timeout = null!;
    private CancellationToken Token => _timeout.Token;
    private CdcRecordSizeIncreaseScope _scope = null!;
    private CdcRecordSizeIncreaseConfirmation _confirmation = null!;
    private readonly List<object> _evidence = [];
    private readonly List<string> _effects = [];
    private int _confirmations;
    private string _baselineOffset = "";
    private string _topicIds = "";
    private string _baselinePartitionHash = "";
    private IReadOnlyDictionary<string, string> _baselineConfiguration = null!;
    private CdcDeploymentRequest Desired =>
        CdcRecordSizeRollout.WithPolicy(_fixture.Request, Ceiling, Buffer);

    private CdcControllerStatusTarget Target(CdcDeploymentRequest request) =>
        new(request, _fixture.Runtime, 60_000);

    [SetUp]
    public async Task Setup()
    {
        _fixture = null!;
        _sizes = null!;
        _evidence.Clear();
        _effects.Clear();
        _confirmations = 0;
        _timeout = new(TimeSpan.FromMinutes(10));
        _fixture = await CdcProviderAdmissionFixture.StartAsync(provider, Token);
        DateTimeOffset projectionStarted = default;
        _fixture.BeforeRuntimeCall = name =>
        {
            if (name == nameof(ICdcProjectionRuntime.ObserveAsync))
            {
                projectionStarted = DateTimeOffset.UtcNow;
            }
        };
        _fixture.AfterRuntimeCall = name =>
        {
            if (name == nameof(ICdcProjectionRuntime.ObserveAsync))
            {
                var returned = DateTimeOffset.UtcNow;
                var target = _fixture.ProjectionObservations[^1].Targets.Single();
                _evidence.Add(
                    new
                    {
                        ProjectionStarted = projectionStarted,
                        ProjectionReturned = returned,
                        target.DurableObservedAt,
                        target.Lifecycle.State,
                        target.CacheAhead.RecoveryRequired,
                        target.Provider,
                        SourceMatches = target.PhysicalSourceFingerprint
                            == _fixture.Request.Binding.PhysicalSourceFingerprint,
                    }
                );
            }
        };
        await _fixture.RegisterAsync(Token);
        Observed(
            await _fixture.Controllers.Admission.PreparePublicationAsync(
                _fixture.Request,
                _fixture.Runtime,
                60_000,
                Token
            )
        );
        await _fixture.ReopenRuntimeAsync(Token);
        await _fixture.Runtime.StartProcessingAsync(Token);
        _scope = new(
            Guid.NewGuid(),
            _fixture.Request.Binding.ToCompleteBindingIdentity(),
            _fixture.Request.ConnectorPolicy.MaxRecordBytes,
            Ceiling
        );
        string servers = _fixture.Infrastructure.Resources.ControllerKafkaBootstrapServers;
        _sizes = Observed(
            CdcKafkaAdminAdapter.Create(
                new AdminClientConfig { BootstrapServers = servers },
                new CdcComposeKafkaAuthorizationInspection(
                    _fixture.Infrastructure.Resources.ControllerProject,
                    servers
                ),
                new BrokerDeployment(_fixture.Infrastructure.Resources)
            )
        );
        _fixture.Infrastructure.BeforeConnectCall = name =>
        {
            if (
                name
                is nameof(ICdcConnectTransport.StopAsync)
                    or nameof(ICdcConnectTransport.ResumeAsync)
                    or nameof(ICdcConnectTransport.UpdateConfigurationForRecordSizeIncreaseAsync)
            )
            {
                RequireDurableAcknowledgement();
                _effects.Add(name);
            }
        };
        var offsets = Observed(
            await _fixture.Infrastructure.Connect.ReadOffsetsAsync(_fixture.Request, Token)
        );
        _baselineOffset = offsets.GetProperty("offsets")[0].GetProperty("offset").GetRawText();
        _baselinePartitionHash = Observed(
            await _fixture.Infrastructure.Connect.ReadOffsetEvidenceAsync(_fixture.Request, Token)
        ).SourcePartitionHash;
        _baselineConfiguration = Observed(
            await _fixture.Infrastructure.Connect.ReadConfigurationAsync(_fixture.Request, Token)
        );
        _topicIds = await TopicIdsAsync();
        await CaptureLimitsAsync("baseline");
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_fixture is not null)
        {
            _fixture.Hooks.OnBoundary = _ => { };
            _fixture.Infrastructure.BeforeConnectCall = _ => { };
            string path = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "record-size-" + Guid.NewGuid().ToString("N") + ".json"
            );
            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(
                    new
                    {
                        Test = TestContext.CurrentContext.Test.FullName,
                        Provider = provider.ToString(),
                        Evidence = _evidence,
                        Effects = _effects,
                    }
                )
            );
            TestContext.AddTestAttachment(path, "Sanitized rollout limits and acknowledgement observations");
            try
            {
                _sizes?.Dispose();
            }
            finally
            {
                await _fixture.DisposeAsync();
            }
        }
        _timeout.Dispose();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_orders_a_confirmed_increase_and_preserves_identity(bool consumers)
    {
        var identity = _fixture.Request.Binding;
        var before = await CaptureLimitsAsync("before");
        var result = await IncreaseAsync(consumers: consumers);
        _evidence.Add(result);
        result.Succeeded.Should().BeTrue("{0}", JsonSerializer.Serialize(result));
        result.Ready.Should().BeTrue();
        before.Brokers.Brokers.Any(b => b.ReplicaFetchMaxBytes < Ceiling).Should().BeTrue();
        var after = await CaptureLimitsAsync("complete");
        after.Topic.Should().Be(Ceiling);
        after.ProducerBuffer.Should().Be(Buffer);
        after.Request.Should().Be(Ceiling);
        after
            .Brokers.Brokers.Should()
            .OnlyContain(b =>
                b.SocketRequestMaxBytes >= Ceiling
                && b.ReplicaFetchMaxBytes >= Ceiling
                && b.ReplicaFetchResponseMaxBytes >= Ceiling
            );
        after.Partitions.Should().Be(before.Partitions);
        (await _fixture.Infrastructure.Bindings.ExactMatchBindingAsync(identity, Token))
            .Status.Should()
            .Be(CoreCdc.CdcControlPlaneOperationStatus.Succeeded);
        (await _fixture.JournalAsync(Token)).HasPendingRecordSizeIncrease.Should().BeFalse();
        _effects
            .Should()
            .Equal(
                nameof(ICdcConnectTransport.StopAsync),
                "broker",
                "topic",
                nameof(ICdcConnectTransport.UpdateConfigurationForRecordSizeIncreaseAsync),
                nameof(ICdcConnectTransport.UpdateConfigurationForRecordSizeIncreaseAsync),
                nameof(ICdcConnectTransport.ResumeAsync)
            );
        AssertNoOffsetReset();
    }

    [TestCase("omitted")]
    [TestCase("inventory")]
    [TestCase("outdated")]
    [TestCase("scope")]
    [TestCase("ceiling")]
    [TestCase("deployment")]
    public async Task It_rejects_incomplete_or_mismatched_confirmation_without_mutation(string scenario)
    {
        var before = await CaptureLimitsAsync("before-rejection");
        var result = await IncreaseAsync(c =>
            scenario switch
            {
                "omitted" => c with { CompleteInventoryAndCapacityConfirmed = false },
                "inventory" => c with { Acknowledgement = c.Acknowledgement with { NoConsumers = false } },
                "outdated" => c with
                {
                    Acknowledgement = c.Acknowledgement with
                    {
                        ConfirmedAt = DateTimeOffset.UtcNow.AddHours(-1),
                    },
                },
                "scope" => c with { Scope = c.Scope with { OperationId = Guid.NewGuid() } },
                "ceiling" => c with { Scope = c.Scope with { RequestedMaxRecordBytes = Ceiling + 1 } },
                "deployment" => c with
                {
                    Acknowledgement = c.Acknowledgement with
                    {
                        NoConsumers = false,
                        Consumers = [new("consumer-a", "", "owner", "old-evidence")],
                    },
                },
                _ => throw new InvalidOperationException("Unknown scenario."),
            }
        );
        _evidence.Add(result);
        result.Succeeded.Should().BeFalse();
        result.Ready.Should().BeFalse();
        _effects.Should().BeEmpty();
        (await CaptureLimitsAsync("rejected")).Should().BeEquivalentTo(before);
        (await _fixture.JournalAsync(Token))
            .Operations.Should()
            .NotContain(o => o.Effect == CdcWorkflowEffect.IncreaseRecordSize);
        AssertNoOffsetReset();
    }

    [TestCase(0, false)]
    [TestCase(1, false)]
    [TestCase(1, true)]
    [TestCase(2, true)]
    [TestCase(3, true)]
    [TestCase(4, true)]
    [TestCase(5, true)]
    public async Task It_requires_renewed_confirmation_at_every_interrupted_boundary(int step, bool after)
    {
        // The shared hook cancels this invocation at an effect boundary. The resumed invocation
        // reads the durable journal and real services; this does not model killing the test process.
        using var interrupted = CancellationTokenSource.CreateLinkedTokenSource(Token);
        bool reached = false;
        int beforeCount = 0;
        int afterCount = 0;
        bool resuming = false;
        var originalBefore = _fixture.Infrastructure.BeforeConnectCall;
        _fixture.Infrastructure.BeforeConnectCall = name =>
        {
            originalBefore(name);
            if (step == 0 && name == nameof(ICdcConnectTransport.StopAsync))
            {
                Interrupt();
            }
            if (name == nameof(ICdcConnectTransport.ResumeAsync))
            {
                resuming = true;
            }
        };
        _fixture.BeforeMetricsCall = () =>
        {
            if (step == 5 && resuming)
            {
                Interrupt();
            }
        };
        _fixture.Hooks.OnBoundary = e =>
        {
            if (e.Boundary != CdcControllerBoundary.Rollout)
            {
                return;
            }
            if (e.Edge == CdcControllerEdge.Before && ++beforeCount == step && !after)
            {
                Interrupt();
            }
            if (e.Edge == CdcControllerEdge.After && ++afterCount == step && after)
            {
                Interrupt();
            }
        };
        await FluentActions
            .Awaiting(() => IncreaseAsync(token: interrupted.Token, consumers: true))
            .Should()
            .ThrowAsync<OperationCanceledException>();
        reached.Should().BeTrue();
        _fixture.Hooks.OnBoundary = _ => { };
        _fixture.BeforeMetricsCall = () => { };
        _fixture.Infrastructure.BeforeConnectCall = originalBefore;
        (await _fixture.JournalAsync(Token)).HasPendingRecordSizeIncrease.Should().BeTrue();
        var partial = await CaptureLimitsAsync("interrupted-" + step);
        if (step >= 1 && after)
        {
            partial.Brokers.Brokers.Should().OnlyContain(b => b.ReplicaFetchMaxBytes >= Ceiling);
        }
        partial.Topic.Should().Be(step >= 2 ? Ceiling : _scope.PreviousMaxRecordBytes);
        partial
            .ProducerBuffer.Should()
            .Be(step >= 3 ? Buffer : _fixture.Request.ConnectorPolicy.EffectiveProducerBufferBytes);
        partial.Request.Should().Be(step >= 4 ? Ceiling : _scope.PreviousMaxRecordBytes);
        var saved = _confirmation;
        int effects = _effects.Count;
        var replay = await IncreaseAsync(_ => saved, consumers: true);
        replay.Succeeded.Should().BeFalse();
        _confirmations--; // Rejected replay never creates a durable acknowledgement.
        _effects.Should().HaveCount(effects);
        (await CaptureLimitsAsync("replayed-old-confirmation")).Should().BeEquivalentTo(partial);
        var missing = await IncreaseAsync(c => c with { CompleteInventoryAndCapacityConfirmed = false });
        missing.Succeeded.Should().BeFalse();
        missing.Ready.Should().BeFalse();
        _confirmations--;
        var wrongCeiling = await IncreaseAsync(c =>
            c with
            {
                Scope = c.Scope with { RequestedMaxRecordBytes = Ceiling + 1 },
            }
        );
        wrongCeiling.Succeeded.Should().BeFalse();
        _confirmations--;
        _effects.Should().HaveCount(effects);
        (await CaptureLimitsAsync("missing-or-mismatched-renewal")).Should().BeEquivalentTo(partial);
        await RequirePendingAcrossOrdinaryCommandsAsync();
        var completed = await IncreaseAsync(
            c =>
                c with
                {
                    Acknowledgement = c.Acknowledgement with
                    {
                        Consumers =
                        [
                            new("consumer-a", "revision-2", "consumer-owner", "capacity-evidence-2"),
                        ],
                    },
                },
            consumers: true
        );
        _evidence.Add(completed);
        completed.Succeeded.Should().BeTrue("{0}", JsonSerializer.Serialize(completed));
        completed.Ready.Should().BeTrue();
        var journal = await _fixture.JournalAsync(Token);
        journal.HasPendingRecordSizeIncrease.Should().BeFalse();
        var acknowledgements = journal
            .Operations.Single(o => o.OperationId == _scope.OperationId)
            .RecordSizeIncrease.Single()
            .Acknowledgements;
        _evidence.Add(new { Acknowledgements = acknowledgements });
        acknowledgements.Should().HaveCount(2);
        acknowledgements.Select(a => a.InvocationId).Distinct().Should().HaveCount(2);
        acknowledgements[1].Consumers.Single().Revision.Should().Be("revision-2");
        acknowledgements[1].Consumers.Single().EvidenceReference.Should().Be("capacity-evidence-2");
        await CaptureLimitsAsync("resumed-complete");
        AssertNoOffsetReset();

        void Interrupt()
        {
            reached = true;
            interrupted.Cancel();
            interrupted.Token.ThrowIfCancellationRequested();
        }
    }

    private async Task RequirePendingAcrossOrdinaryCommandsAsync()
    {
        int effects = _effects.Count;
        // Fresh objects: no in-memory rollout authorization or readiness can leak to another invocation.
        foreach (var request in new[] { _fixture.Request, Desired })
        {
            var validation = await _fixture.Controllers.Validation.ValidateAsync(
                request,
                _fixture.Runtime,
                CdcEstablishedValidationMode.RunningPublication,
                60_000,
                cancellationToken: Token
            );
            (
                validation is CdcTransportResult<CdcEstablishedValidationObservation>.Observed live
                && live.Value.PublicationReady
            )
                .Should()
                .BeFalse();
            await foreach (
                var pass in _fixture
                    .CreateControllers()
                    .Status.WatchAsync([Target(request)], 2, TimeSpan.FromMilliseconds(100), Token)
            )
            {
                pass.Targets.Single().HasPendingRecordSizeIncrease.Should().BeTrue();
                pass.Targets.Single().Status.Readiness.Should().Be(CoreCdc.CdcReadiness.NotReady);
                _evidence.Add(pass);
            }
            var restart = await _fixture
                .CreateControllers()
                .Lifecycle.ExecuteAsync(Target(request), CdcManagedLifecycleOperation.Restart, Token);
            restart.Succeeded.Should().BeFalse();
            restart.Ready.Should().BeFalse();
        }
        _effects.Should().HaveCount(effects);
        (await _fixture.JournalAsync(Token)).HasPendingRecordSizeIncrease.Should().BeTrue();
    }

    private Task<CdcRecordSizeIncreaseResult> IncreaseAsync(
        Func<CdcRecordSizeIncreaseConfirmation, CdcRecordSizeIncreaseConfirmation> change = null!,
        CancellationToken token = default,
        bool consumers = false
    )
    {
        return _fixture
            .Controllers.RecordSize(
                new ObservedSizes(
                    _sizes,
                    step =>
                    {
                        RequireDurableAcknowledgement();
                        _effects.Add(step);
                    }
                )
            )
            .IncreaseAsync(
                Target(_fixture.Request),
                _scope,
                Buffer,
                (invocation, _) =>
                {
                    _confirmation = new(
                        _scope,
                        new(
                            invocation.InvocationId,
                            "rollout-operator",
                            DateTimeOffset.UtcNow,
                            !consumers,
                            consumers
                                ? [new("consumer-a", "revision-1", "consumer-owner", "capacity-evidence-1")]
                                : []
                        ),
                        true
                    );
                    var candidate = change is null ? _confirmation : change(_confirmation);
                    // Count only successful durable writes in RequireDurableAcknowledgement; invalid inputs do not mutate.
                    _confirmations++;
                    return Task.FromResult(candidate);
                },
                token == default ? Token : token
            );
    }

    private JsonElement ReadDurableOperation()
    {
        string file = Directory
            .GetFiles(
                Path.Combine(_fixture.Infrastructure.StateRoot, "workflows"),
                "*.json",
                SearchOption.AllDirectories
            )
            .Single();
        using var journal = JsonDocument.Parse(File.ReadAllText(file));
        return journal
            .RootElement.GetProperty("operations")
            .EnumerateArray()
            .Single(o => o.GetProperty("operationId").GetGuid() == _scope.OperationId)
            .Clone();
    }

    private void RequireDurableAcknowledgement()
    {
        var operation = ReadDurableOperation();
        operation.GetProperty("completions").GetArrayLength().Should().Be(0);
        var acknowledgements = operation.GetProperty("recordSizeIncrease")[0].GetProperty("acknowledgements");
        acknowledgements.GetArrayLength().Should().Be(_confirmations);
        acknowledgements[acknowledgements.GetArrayLength() - 1]
            .GetProperty("invocationId")
            .GetGuid()
            .Should()
            .Be(_confirmation.Acknowledgement.InvocationId);
        _evidence.Add(
            new
            {
                DurableBeforeEffect = true,
                Confirmations = acknowledgements.GetArrayLength(),
                At = DateTimeOffset.UtcNow,
            }
        );
    }

    private async Task<Limits> CaptureLimitsAsync(string phase)
    {
        var request = _fixture.Request;
        (await TopicIdsAsync()).Should().Be(_topicIds, "public and internal Kafka topic IDs are retained");
        var config = Observed(await _fixture.Infrastructure.Connect.ReadConfigurationAsync(request, Token));
        var topic = Observed(
            await _fixture.Kafka.InspectTopicAsync(request, request.Binding.TopicName, Token)
        );
        var brokers = Observed(await _fixture.Kafka.InspectBrokersAsync(request, Token));
        var worker = Observed(await _fixture.Infrastructure.Worker.InspectAsync(request, Token));
        worker.ImageDigest.Should().Be(CdcQualifiedWorkerImage.Digest);
        worker.HeapBytes.Should().BeGreaterThan(Buffer);
        config
            .Where(p =>
                p.Key is not "producer.override.max.request.size" and not "producer.override.buffer.memory"
            )
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .SequenceEqual(
                _baselineConfiguration
                    .Where(p =>
                        p.Key
                            is not "producer.override.max.request.size"
                                and not "producer.override.buffer.memory"
                    )
                    .OrderBy(p => p.Key, StringComparer.Ordinal)
            )
            .Should()
            .BeTrue("all non-size configuration remains identical (values redacted)");
        var offsets = Observed(await _fixture.Infrastructure.Connect.ReadOffsetsAsync(request, Token));
        CdcConnectorTemplatePinnedImageFixture
            .CommittedSourceOffsetRetainsOrAdvances(
                provider,
                _baselineOffset,
                offsets.GetProperty("offsets")[0].GetProperty("offset").GetRawText()
            )
            .Should()
            .BeTrue();
        Observed(await _fixture.Infrastructure.Connect.ReadOffsetEvidenceAsync(request, Token))
            .SourcePartitionHash.Should()
            .Be(_baselinePartitionHash);
        var result = new Limits(
            int.Parse(topic.Configuration["max.message.bytes"].Value, CultureInfo.InvariantCulture),
            int.Parse(config["producer.override.buffer.memory"], CultureInfo.InvariantCulture),
            int.Parse(config["producer.override.max.request.size"], CultureInfo.InvariantCulture),
            JsonSerializer.Serialize(topic.PartitionReplicas.OrderBy(p => p.Key)),
            brokers
        );
        _evidence.Add(
            new
            {
                Phase = phase,
                At = DateTimeOffset.UtcNow,
                Limits = result,
                TopicIdsRetained = true,
                BrokerCapacity = brokers.Brokers,
                worker.ImageDigest,
                worker.HeapBytes,
                Offset = Position(
                    Observed(await _fixture.Infrastructure.Connect.ReadOffsetEvidenceAsync(request, Token))
                ),
            }
        );
        return result;
    }

    private async Task<string> TopicIdsAsync()
    {
        using var admin = new AdminClientBuilder(
            new AdminClientConfig
            {
                BootstrapServers = _fixture.Infrastructure.Resources.ControllerKafkaBootstrapServers,
            }
        )
            .SetLogHandler((_, _) => { })
            .SetErrorHandler((_, _) => { })
            .Build();
        var topics = CdcDeploymentKafkaPolicy
            .Build(_fixture.Request)
            .BindingTopics.Select(t => t.Name)
            .Append(_fixture.Request.WorkerPolicy.OffsetStorageTopic.Value)
            .Order(StringComparer.Ordinal);
        var described = await admin
            .DescribeTopicsAsync(
                TopicCollection.OfTopicNames(topics),
                new() { RequestTimeout = TimeSpan.FromSeconds(10) }
            )
            .WaitAsync(Token);
        return string.Join(
            ",",
            described
                .TopicDescriptions.OrderBy(t => t.Name, StringComparer.Ordinal)
                .Select(t => t.TopicId.ToString())
        );
    }

    private void AssertNoOffsetReset() =>
        _fixture
            .Infrastructure.ConnectCalls.Should()
            .NotContain(nameof(ICdcConnectTransport.DeleteOffsetsAsync));

    private sealed record Limits(
        int Topic,
        int ProducerBuffer,
        int Request,
        string Partitions,
        CdcKafkaBrokerEvidence Brokers
    );

    private sealed class BrokerDeployment(CdcConnectorTemplatePinnedImageFixture resources)
        : ICdcKafkaBrokerSizeDeployment
    {
        public Task ApplyAsync(
            CdcDeploymentRequest request,
            IReadOnlyList<CdcKafkaBrokerCapacity> limits,
            CancellationToken cancellationToken
        ) => resources.ApplyControllerBrokerSizesAsync(limits, cancellationToken);
    }

    private sealed class ObservedSizes(ICdcKafkaRecordSizeAdministration inner, Action<string> before)
        : ICdcKafkaRecordSizeAdministration
    {
        public Task<CdcTransportResult<CdcKafkaBrokerEvidence>> IncreaseBrokerLimitsAsync(
            CdcDeploymentRequest desired,
            CancellationToken cancellationToken
        )
        {
            before("broker");
            return inner.IncreaseBrokerLimitsAsync(desired, cancellationToken);
        }

        public Task<CdcTransportResult<CdcKafkaTopicEvidence>> IncreasePublicTopicLimitAsync(
            CdcDeploymentRequest desired,
            int previousMaxRecordBytes,
            CancellationToken cancellationToken
        )
        {
            before("topic");
            return inner.IncreasePublicTopicLimitAsync(desired, previousMaxRecordBytes, cancellationToken);
        }
    }
}
