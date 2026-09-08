// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Ddl = EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture(Ddl.CdcProvider.Postgresql)]
[TestFixture(Ddl.CdcProvider.SqlServer)]
[Platform(Exclude = "Win", Reason = "Local CDC state requires Unix owner-only permissions.")]
internal class Given_CdcRecordSizeIncrease(Ddl.CdcProvider provider) : CdcReadinessTestBase(provider)
{
    private const int Ceiling = 134_217_728;
    private CdcRecordSizeIncrease _increase = null!;
    private CdcEstablishedValidation _validation = null!;
    private ICdcKafkaRecordSizeAdministration _sizes = null!;
    private CdcRecordSizeIncreaseScope _scope = null!;
    private int _brokerLimit;
    private int _topicLimit;
    private bool _stopped;
    private bool _failedTask;
    private int _confirmations;
    private List<string> _effects = null!;
    private CdcRecordSizeIncreaseConfirmation _confirmation = null!;
    private Func<CdcRecordSizeIncreaseConfirmation, CdcRecordSizeIncreaseConfirmation> _confirmChange = null!;

    [SetUp]
    public void SetupIncrease()
    {
        _brokerLimit = _topicLimit = _request.ConnectorPolicy.MaxRecordBytes;
        _stopped = _failedTask = false;
        _confirmations = 0;
        _effects = [];
        _confirmChange = c => c;
        _scope = new(Guid.NewGuid(), _request.Binding.ToCompleteBindingIdentity(), _topicLimit, Ceiling);
        _sizes = A.Fake<ICdcKafkaRecordSizeAdministration>();
        A.CallTo(() => _sizes.IncreaseBrokerLimitsAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                Effect("broker-before");
                _brokerLimit = Ceiling;
                Effect("broker-after");
                return Observed(Brokers());
            });
        A.CallTo(() =>
                _sizes.IncreasePublicTopicLimitAsync(
                    A<CdcDeploymentRequest>._,
                    A<int>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(() =>
            {
                Effect("topic-before");
                _brokerLimit.Should().Be(Ceiling);
                _topicLimit = Ceiling;
                Effect("topic-after");
                return Observed(Topic(_request.Binding.TopicName));
            });
        A.CallTo(() => _kafka.InspectBrokersAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                Trace("brokers");
                return Observed(Brokers());
            });
        A.CallTo(() =>
                _kafka.InspectTopicAsync(A<CdcDeploymentRequest>._, A<string>._, A<CancellationToken>._)
            )
            .ReturnsLazily(
                (CdcDeploymentRequest _, string name, CancellationToken _) =>
                {
                    Trace("topic");
                    return Observed(Topic(name));
                }
            );
        A.CallTo(() => _connect.StopAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                Effect("stop-before");
                _stopped = true;
                Effect("stop-after");
                return Observed(new CdcTransportAcknowledgement());
            });
        A.CallTo(() => _connect.ResumeAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                Effect("resume-before");
                _topicLimit.Should().Be(Ceiling);
                _live["producer.override.max.request.size"]
                    .Should()
                    .Be(Ceiling.ToString(CultureInfo.InvariantCulture));
                _stopped = false;
                Effect("resume-after");
                return Observed(new CdcTransportAcknowledgement());
            });
        A.CallTo(() => _connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                Trace("status");
                var status = Status();
                if (_failedTask && !_stopped)
                {
                    status = new(
                        status.Runtime with
                        {
                            SoleTaskState = CdcConnectorRuntimeState.Failed,
                            LastErrorCategory = "connect-runtime-failed",
                            RunningTaskCount = 0,
                        },
                        status.WorkerId,
                        [new(0, CdcConnectorRuntimeState.Failed, status.WorkerId)]
                    );
                }
                return Observed(
                    !_stopped
                        ? status
                        : new CdcConnectStatus(
                            status.Runtime with
                            {
                                ConnectorState = CdcConnectorRuntimeState.Stopped,
                                SoleTaskState = CdcConnectorRuntimeState.Stopped,
                                TaskCount = 0,
                                RunningTaskCount = 0,
                            },
                            status.WorkerId,
                            []
                        )
                );
            });
        A.CallTo(() =>
                _connect.UpdateConfigurationForRecordSizeIncreaseAsync(
                    A<CdcDeploymentRequest>._,
                    A<CdcKafkaConnectRegistrationPayload>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                (CdcDeploymentRequest _, CdcKafkaConnectRegistrationPayload payload, CancellationToken _) =>
                {
                    _stopped.Should().BeTrue();
                    _topicLimit.Should().Be(Ceiling);
                    var changed = payload
                        .Config.Where(p => _live[p.Key] != p.Value)
                        .Select(p => p.Key)
                        .ToArray();
                    changed.Should().ContainSingle();
                    string step =
                        changed.Single() == "producer.override.buffer.memory" ? "buffer" : "request";
                    Effect(step + "-before");
                    _live = new(payload.Config);
                    Effect(step + "-after");
                    return Observed(new CdcTransportAcknowledgement());
                }
            );
        ResetIncrease();
        _trace.Clear();
        Fake.ClearRecordedCalls(_connect);
    }

    private void ResetIncrease()
    {
        var bindings = _services.GetRequiredService<ICdcBindingLifecycleService>();
        _validation = new(
            _store,
            bindings,
            _provider,
            _templates,
            _kafka,
            _connect,
            _worker,
            _metrics,
            _positions,
            TimeProvider.System
        );
        _increase = new(_store, bindings, _kafka, _sizes, _connect, _validation, TimeProvider.System);
    }

    private CdcKafkaBrokerEvidence Brokers() => new(true, [new(0, _brokerLimit, _brokerLimit, _brokerLimit)]);

    private CdcKafkaTopicEvidence Topic(string name)
    {
        var plan = CdcDeploymentKafkaPolicy.Build(_request);
        var intent = plan.BindingTopics.Append(plan.OffsetStore).Single(t => t.Name == name);
        var config = intent.Configuration.ToDictionary(
            p => p.Key,
            p => new CdcKafkaConfigurationValue(p.Value, true)
        );
        if (name == _request.Binding.TopicName)
        {
            config["max.message.bytes"] = new(_topicLimit.ToString(CultureInfo.InvariantCulture), true);
        }
        return new(
            name,
            Enumerable.Range(0, intent.PartitionCount).ToDictionary(i => i, _ => (IReadOnlyList<int>)[0]),
            config
        );
    }

    private void Effect(string step)
    {
        var pending = ReadJournal().Operations.Single(o => o.OperationId == _scope.OperationId);
        pending.Completions.Should().BeEmpty();
        pending.RecordSizeIncrease.Single().Acknowledgements.Should().HaveCount(_confirmations);
        _effects.Add(step);
        Trace(step);
    }

    private Task<CdcRecordSizeIncreaseResult> Execute(CancellationToken token = default) =>
        _increase.IncreaseAsync(
            new(_request, _runtime, 1000),
            _scope,
            Ceiling,
            (invocation, _) =>
            {
                _confirmations++;
                _confirmation = _confirmChange(
                    new(
                        _scope,
                        new(invocation.InvocationId, "operator", DateTimeOffset.UtcNow, true, []),
                        true
                    )
                );
                return Task.FromResult(_confirmation);
            },
            token
        );

    [Test]
    public async Task It_orders_confirmed_effects_and_restores_only_fresh_readiness()
    {
        var result = await Execute();
        result
            .Should()
            .BeEquivalentTo(
                new CdcRecordSizeIncreaseResult(true, true, _scope.OperationId, []),
                because: string.Join(',', _trace)
            );
        _effects
            .Should()
            .Equal(
                "stop-before",
                "stop-after",
                "broker-before",
                "broker-after",
                "topic-before",
                "topic-after",
                "buffer-before",
                "buffer-after",
                "request-before",
                "request-after",
                "resume-before",
                "resume-after"
            );
        ReadJournal().HasPendingRecordSizeIncrease.Should().BeFalse();
        _trace.Count(s => s == "metrics").Should().BeGreaterThanOrEqualTo(3);
    }

    [TestCase("stop-before")]
    [TestCase("stop-after")]
    [TestCase("broker-before")]
    [TestCase("broker-after")]
    [TestCase("topic-before")]
    [TestCase("topic-after")]
    [TestCase("buffer-before")]
    [TestCase("buffer-after")]
    [TestCase("request-before")]
    [TestCase("request-after")]
    [TestCase("resume-before")]
    [TestCase("resume-after")]
    [TestCase("metrics")]
    public async Task It_requires_renewed_confirmation_after_interruption(string boundary)
    {
        using var cancel = new CancellationTokenSource();
        _onCall = step =>
        {
            if (step == boundary)
            {
                cancel.Cancel();
                cancel.Token.ThrowIfCancellationRequested();
            }
        };
        await FluentActions
            .Awaiting(() => Execute(cancel.Token))
            .Should()
            .ThrowAsync<OperationCanceledException>();
        ReadJournal().HasPendingRecordSizeIncrease.Should().BeTrue();
        _onCall = _ => { };
        ResetIncrease();
        var saved = _confirmation;
        _confirmChange = _ => saved;
        (await Execute()).Succeeded.Should().BeFalse();
        _confirmations--; // Rejected replay never reached durable acknowledgement.
        _confirmChange = c => c;
        var result = await Execute();
        result
            .Succeeded.Should()
            .BeTrue(because: JsonSerializer.Serialize(result) + string.Join(',', _trace));
        ReadJournal()
            .Operations.Single(o => o.OperationId == _scope.OperationId)
            .RecordSizeIncrease.Single()
            .Acknowledgements.Should()
            .HaveCount(2);
    }

    [TestCase("broker-after")]
    [TestCase("topic-after")]
    [TestCase("buffer-after")]
    [TestCase("request-after")]
    [TestCase("resume-after")]
    public async Task It_reconciles_lost_mutation_responses_from_live_state(string boundary)
    {
        _onCall = step =>
        {
            if (step == boundary)
            {
                throw new IOException("secret-password-private-host-document-body");
            }
        };
        (await Execute()).Succeeded.Should().BeTrue(because: string.Join(',', _trace));
        _effects.Count(s => s == boundary).Should().Be(1);
    }

    [TestCase("request-first")]
    [TestCase("buffer-first")]
    [TestCase("topic-before-brokers")]
    [TestCase("unknown-buffer")]
    [TestCase("lower-request")]
    public async Task It_rejects_out_of_order_or_unknown_limits_without_effects(string scenario)
    {
        // Retained intent is necessary even to consider partially changed limits.
        _onWrite = boundary =>
        {
            if (boundary == CdcWorkflowWriteBoundary.AfterAtomicReplacement)
            {
                throw new IOException("interrupted acknowledgement");
            }
        };
        (await Execute()).Succeeded.Should().BeFalse();
        _onWrite = _ => { };
        switch (scenario)
        {
            case "request-first":
                _live["producer.override.max.request.size"] = Ceiling.ToString(CultureInfo.InvariantCulture);
                break;
            case "buffer-first":
                _live["producer.override.buffer.memory"] = Ceiling.ToString(CultureInfo.InvariantCulture);
                break;
            case "topic-before-brokers":
                _topicLimit = Ceiling;
                break;
            case "unknown-buffer":
                _live["producer.override.buffer.memory"] = "40000000";
                break;
            case "lower-request":
                _live["producer.override.max.request.size"] = "1";
                break;
        }
        (await Execute()).Succeeded.Should().BeFalse();
        _effects.Should().BeEmpty();
        ReadJournal().HasPendingRecordSizeIncrease.Should().BeTrue();
    }

    [TestCase("omitted-inventory")]
    [TestCase("unconfirmed")]
    [TestCase("wrong-scope")]
    [TestCase("old-time")]
    public async Task It_rejects_invalid_confirmation_before_effects(string scenario)
    {
        _confirmChange = c =>
            scenario switch
            {
                "omitted-inventory" => c with
                {
                    Acknowledgement = c.Acknowledgement with { NoConsumers = false },
                },
                "unconfirmed" => c with { CompleteInventoryAndCapacityConfirmed = false },
                "wrong-scope" => c with { Scope = c.Scope with { OperationId = Guid.NewGuid() } },
                _ => c with
                {
                    Acknowledgement = c.Acknowledgement with
                    {
                        ConfirmedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                    },
                },
            };
        (await Execute()).Succeeded.Should().BeFalse();
        _effects.Should().BeEmpty();
        ReadJournal().HasPendingRecordSizeIncrease.Should().BeFalse();
    }

    [Test]
    public async Task It_keeps_fully_aligned_interrupted_rollout_blocking_ordinary_commands()
    {
        using var cancel = new CancellationTokenSource();
        _onCall = step =>
        {
            if (step == "metrics")
            {
                cancel.Cancel();
                cancel.Token.ThrowIfCancellationRequested();
            }
        };
        await FluentActions
            .Awaiting(() => Execute(cancel.Token))
            .Should()
            .ThrowAsync<OperationCanceledException>();
        _onCall = _ => { };
        var desired = CdcRecordSizeRollout.WithPolicy(_request, Ceiling, Ceiling);
        var before = JsonSerializer.Serialize(ReadJournal());
        var observed = (
            await _validation.ValidateAsync(
                desired,
                _runtime,
                CdcEstablishedValidationMode.RunningPublication,
                1000
            )
        )
            .Should()
            .BeOfType<CdcTransportResult<CdcEstablishedValidationObservation>.Observed>()
            .Subject.Value;
        observed.PublicationReady.Should().BeFalse();
        observed.PreStartEligible.Should().BeFalse();
        observed.HasPendingRecordSizeIncrease.Should().BeTrue();
        var bindings = _services.GetRequiredService<ICdcBindingLifecycleService>();
        CdcControllerStatus status = new(_store, bindings, _connect, _ => _validation, TimeProvider.System);
        var target = new CdcControllerStatusTarget(desired, _runtime, 1000);
        (await status.ObserveTargetAsync(target, CancellationToken.None))
            .Status.Readiness.Should()
            .NotBe(CdcReadiness.Ready);
        CdcManagedLifecycle managed = new(_store, bindings, _connect, _worker, status, TimeProvider.System);
        (await managed.ExecuteAsync(target, CdcManagedLifecycleOperation.Restart))
            .Succeeded.Should()
            .BeFalse();
        JsonSerializer.Serialize(ReadJournal()).Should().Be(before);
        _scope = _scope with { OperationId = Guid.NewGuid() };
        (await Execute()).Succeeded.Should().BeFalse();
        JsonSerializer.Serialize(ReadJournal()).Should().Be(before);
    }

    [Test]
    public async Task It_preserves_pending_state_across_every_journal_write_boundary(
        [Values(1, 2, 3, 4, 5, 6)] int writeNumber,
        [Values] CdcWorkflowWriteBoundary boundary
    )
    {
        int writes = 0;
        _onWrite = current =>
        {
            if (current == boundary && ++writes == writeNumber)
            {
                throw new IOException("secret-password-private-host-document-body");
            }
        };
        var result = await Execute();
        result.Succeeded.Should().BeFalse();
        writes.Should().Be(writeNumber);
        JsonSerializer.Serialize(result).Should().NotContain("secret-password");
        _onWrite = _ => { };
        bool completed = writeNumber == 6 && boundary == CdcWorkflowWriteBoundary.AfterAtomicReplacement;
        ReadJournal()
            .HasPendingRecordSizeIncrease.Should()
            .Be(
                !completed && (writeNumber > 1 || boundary == CdcWorkflowWriteBoundary.AfterAtomicReplacement)
            );
        if (!completed)
        {
            if (!ReadJournal().HasPendingRecordSizeIncrease)
            {
                _confirmations--;
            }
            ResetIncrease();
            var resumed = await Execute();
            resumed
                .Succeeded.Should()
                .BeTrue(because: JsonSerializer.Serialize(resumed) + string.Join(',', _trace));
        }
    }

    [TestCase("provider")]
    [TestCase("offset")]
    [TestCase("worker")]
    [TestCase("lag")]
    [TestCase("projection")]
    public async Task It_rejects_missing_fresh_evidence_without_completing_the_increase(string scenario)
    {
        _onCall = step =>
        {
            if (step == "resume-after")
            {
                switch (scenario)
                {
                    case "provider":
                        _identity = new string('b', 64);
                        break;
                    case "offset":
                        _offsetState = CdcConnectOffsetState.Null;
                        break;
                    case "worker":
                        _workerEvidence = new(
                            "replacement",
                            _workerEvidence.MetricsEndpoint,
                            _workerEvidence.EffectiveConfiguration,
                            _workerEvidence.ImageDigest,
                            _workerEvidence.HeapBytes,
                            _workerEvidence.ConnectWorkerId
                        );
                        break;
                    case "lag":
                        _lag = 2000;
                        break;
                    case "projection":
                        _backlog = true;
                        break;
                }
            }
            if (step == "metrics" && scenario is "lag" or "projection")
            {
                // Prevent a long polling wait after the first rejecting evidence pass.
                _onWrite = _ => throw new IOException("unexpected completion");
            }
        };
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        CdcRecordSizeIncreaseResult result;
        try
        {
            result = await Execute(cancel.Token);
        }
        catch (OperationCanceledException)
        {
            result = new(false, false, _scope.OperationId, []);
        }
        result.Ready.Should().BeFalse();
        ReadJournal().HasPendingRecordSizeIncrease.Should().BeTrue();
        _onWrite = _ => { };
    }

    [Test]
    public async Task It_holds_the_controller_lock_through_size_changes_and_readiness()
    {
        int contested = 0;
        _onCall = step =>
        {
            if (step is "buffer-after" or "metrics")
            {
                Action contender = () =>
                    _store
                        .AcquireAsync(
                            TimeSpan.FromMilliseconds(20),
                            TimeSpan.FromMilliseconds(1),
                            CancellationToken.None
                        )
                        .GetAwaiter()
                        .GetResult();
                contender
                    .Should()
                    .Throw<CdcWorkflowStateException>()
                    .Which.Failure.Should()
                    .Be(CdcWorkflowStateFailure.LockTimeout);
                contested++;
            }
        };
        (await Execute()).Succeeded.Should().BeTrue();
        contested.Should().BeGreaterThanOrEqualTo(4);
    }

    [Test]
    public async Task It_resumes_an_over_budget_failed_task_without_changing_offsets()
    {
        _failedTask = true;
        _onCall = step =>
        {
            if (step == "resume-after")
            {
                _failedTask = false;
            }
        };
        var offsets = JsonSerializer.Serialize(Offsets().SourcePartition);
        (await Execute()).Succeeded.Should().BeTrue();
        JsonSerializer.Serialize(Offsets().SourcePartition).Should().Be(offsets);
    }

    [TearDown]
    public void It_preserves_binding_offsets_and_projection_ownership()
    {
        A.CallTo(() => _connect.DeleteOffsetsAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => _connect.DeleteAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => _runtime.StartProcessingAsync(A<CancellationToken>._)).MustNotHaveHappened();
        _posts.Should().Be(1);
    }
}
