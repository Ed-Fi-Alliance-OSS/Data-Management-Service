// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Net;
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
    private Func<CdcConnectStatus, CdcConnectStatus> _statusChange = null!;

    [SetUp]
    public void SetupIncrease()
    {
        _brokerLimit = _topicLimit = _request.ConnectorPolicy.MaxRecordBytes;
        _stopped = _failedTask = false;
        _confirmations = 0;
        _effects = [];
        _confirmChange = c => c;
        _statusChange = status => status;
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
                    _statusChange(
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

    private Task<CdcRecordSizeIncreaseResult> Execute(
        CancellationToken token = default,
        CancellationToken operationDeadline = default
    ) =>
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
            token,
            operationDeadline
        );

    private Dictionary<string, string> ConfigureMaskedCredentials(string mask = "********")
    {
        Dictionary<string, string> security = new()
        {
            ["security.protocol"] = "SASL_SSL",
            ["sasl.mechanism"] = "PLAIN",
            ["sasl.jaas.config"] = "${env:CDC_KAFKA_JAAS}",
            ["ssl.truststore.password"] = "${file:/run/secrets/kafka.properties:truststore-password}",
            ["ssl.keystore.password"] = "${env:CDC_KAFKA_KEYSTORE_PASSWORD}",
            ["ssl.key.password"] = "${env:CDC_KAFKA_KEY_PASSWORD}",
            ["ssl.keystore.key"] = "${file:/run/secrets/kafka.properties:keystore-key}",
        };
        SetSecurity(security);
        var rendered = _templates.Render(
            _request.CreateTemplateRequest(_handoff.TemplateRequest.ProviderSetupEvidence)
        );
        rendered.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        _live = new(rendered.Config);
        var credentials = rendered
            .Config.Where(p => CdcConnectorTemplateInputValidator.IsSecretBearingRenderedProperty(p.Key))
            .ToDictionary();
        foreach (string key in credentials.Keys)
        {
            _live[key] = mask;
        }
        return credentials;
    }

    private void SetSecurity(IReadOnlyDictionary<string, string> security) =>
        _request = new(
            _request.Binding,
            _request.DmsSettings,
            _request.ProviderSetup,
            _request.ConnectEndpoint,
            _request.WorkerMetricsEndpoint,
            _request.ConnectorPolicy,
            _request.WorkerPolicy,
            _request.ProviderConnectionProperties,
            new(security),
            _request.Timing
        );

    [TestCase("********")]
    [TestCase("[hidden]")]
    public async Task It_restores_rendered_credentials_for_both_size_updates_through_production_Connect(
        string mask
    )
    {
        var credentials = ConfigureMaskedCredentials(mask);
        string[] prefixes =
            Provider == Ddl.CdcProvider.Postgresql
                ? ["producer.override."]
                :
                [
                    "producer.override.",
                    "schema.history.internal.producer.",
                    "schema.history.internal.consumer.",
                ];
        credentials.Should().HaveCount(1 + 5 * prefixes.Length);
        foreach (string prefix in prefixes)
        {
            credentials.Should().Contain(prefix + "sasl.jaas.config", "${env:CDC_KAFKA_JAAS}");
        }
        credentials.Should().ContainKey("database.password");
        Dictionary<string, string> original = new(_live);
        using var http = new CdcConnectHttpFixture(Provider);
        A.CallTo(() => _connect.ReadConfigurationAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(
                (CdcDeploymentRequest request, CancellationToken ct) =>
                {
                    http.Respond(body: JsonSerializer.Serialize(_live));
                    return http.Adapter.ReadConfigurationAsync(request, ct);
                }
            );
        A.CallTo(() =>
                _connect.ValidateConfigurationAsync(
                    A<CdcDeploymentRequest>._,
                    A<CdcKafkaConnectRegistrationPayload>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                (
                    CdcDeploymentRequest request,
                    CdcKafkaConnectRegistrationPayload payload,
                    CancellationToken ct
                ) =>
                {
                    http.Respond(
                        body: """{"error_count":0,"configs":[{"value":{"name":"connector.class","errors":[]}}]}"""
                    );
                    return http.Adapter.ValidateConfigurationAsync(request, payload, ct);
                }
            );
        A.CallTo(() =>
                _connect.UpdateConfigurationForRecordSizeIncreaseAsync(
                    A<CdcDeploymentRequest>._,
                    A<CdcKafkaConnectRegistrationPayload>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                async (
                    CdcDeploymentRequest request,
                    CdcKafkaConnectRegistrationPayload payload,
                    CancellationToken ct
                ) =>
                {
                    _stopped.Should().BeTrue();
                    _topicLimit.Should().Be(Ceiling);
                    http.Respond(body: JsonSerializer.Serialize(_live));
                    http.Respond(
                        body: JsonSerializer.Serialize(
                            new
                            {
                                name = request.Binding.ConnectorName,
                                connector = new
                                {
                                    state = "STOPPED",
                                    worker_id = _workerEvidence.ConnectWorkerId,
                                },
                                tasks = Array.Empty<object>(),
                            }
                        )
                    );
                    var changed = payload.Config.Single(p =>
                        !credentials.ContainsKey(p.Key) && _live[p.Key] != p.Value
                    );
                    string step = changed.Key == "producer.override.buffer.memory" ? "buffer" : "request";
                    http.Http.Responses.Enqueue(_ =>
                    {
                        Effect(step + "-before");
                        _live = new(payload.Config);
                        foreach (string key in credentials.Keys)
                        {
                            _live[key] = mask;
                        }
                        Effect(step + "-after");
                        return Task.FromResult(
                            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }
                        );
                    });
                    // Reconciliation also sees masked credentials; the controller must reconcile live state.
                    Dictionary<string, string> after = new(_live) { [changed.Key] = changed.Value };
                    http.Respond(body: JsonSerializer.Serialize(after));
                    return await http.Adapter.UpdateConfigurationForRecordSizeIncreaseAsync(
                        request,
                        payload,
                        ct
                    );
                }
            );

        var result = await Execute();

        result.Succeeded.Should().BeTrue(because: string.Join(',', _trace));
        result.Ready.Should().BeTrue();
        result.Diagnostics.Should().BeEmpty();
        _confirmations.Should().Be(1);
        _effects
            .Where(e => e is "buffer-after" or "request-after" or "resume-after")
            .Should()
            .Equal("buffer-after", "request-after", "resume-after");
        ReadJournal().HasPendingRecordSizeIncrease.Should().BeFalse();
        http.Http.Responses.Should().BeEmpty();
        var puts = http.Http.Calls.Where(c => c.Method == HttpMethod.Put).ToArray();
        puts.Should().HaveCount(4);
        for (int i = 0; i < puts.Length; i++)
        {
            puts[i].Path.Should().EndWith(i % 2 == 0 ? "/config/validate" : "/config");
            var payload = JsonSerializer.Deserialize<Dictionary<string, string>>(puts[i].Body)!;
            payload.Should().Contain(credentials);
            payload.Values.Should().NotContain(mask);
            payload
                .Where(p =>
                    !credentials.ContainsKey(p.Key)
                    && p.Key
                        is not ("producer.override.buffer.memory" or "producer.override.max.request.size")
                )
                .Should()
                .BeEquivalentTo(
                    original.Where(p =>
                        !credentials.ContainsKey(p.Key)
                        && p.Key
                            is not ("producer.override.buffer.memory" or "producer.override.max.request.size")
                    )
                );
            payload["producer.override.buffer.memory"]
                .Should()
                .Be(Ceiling.ToString(CultureInfo.InvariantCulture));
            payload["producer.override.max.request.size"]
                .Should()
                .Be(
                    i < 2
                        ? original["producer.override.max.request.size"]
                        : Ceiling.ToString(CultureInfo.InvariantCulture)
                );
        }
        string serialized = JsonSerializer.Serialize(result) + JsonSerializer.Serialize(ReadJournal());
        foreach (string reference in credentials.Values)
        {
            serialized.Should().NotContain(reference);
        }
        serialized.Should().NotContain(mask);
    }

    [TestCase("missing-reference")]
    [TestCase("masked-reference")]
    [TestCase("raw-reference")]
    [TestCase("unrelated-drift")]
    public async Task It_rejects_invalid_credentials_or_drift_before_overlay(string defect)
    {
        ConfigureMaskedCredentials();
        Dictionary<string, string> security = new(_request.KafkaClientSecurityProperties.Properties);
        switch (defect)
        {
            case "missing-reference":
                security.Remove("sasl.jaas.config");
                break;
            case "masked-reference":
                security["sasl.jaas.config"] = "********";
                break;
            case "raw-reference":
                security["sasl.jaas.config"] = "private-raw-credential";
                break;
            case "unrelated-drift":
                _live["producer.override.security.protocol"] = "PLAINTEXT";
                break;
        }
        if (defect is "masked-reference" or "raw-reference")
        {
            Action configure = () => SetSecurity(security);
            configure
                .Should()
                .Throw<ArgumentException>()
                .Which.Message.Should()
                .NotContain("private-raw-credential")
                .And.NotContain("********");
            _effects.Should().BeEmpty();
            return;
        }
        SetSecurity(security);

        var result = await Execute();

        result.Succeeded.Should().BeFalse();
        result.Ready.Should().BeFalse();
        _effects.Should().BeEmpty();
        A.CallTo(() =>
                _connect.UpdateConfigurationForRecordSizeIncreaseAsync(
                    A<CdcDeploymentRequest>._,
                    A<CdcKafkaConnectRegistrationPayload>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        JsonSerializer
            .Serialize(result)
            .Should()
            .NotContain("private-raw-credential")
            .And.NotContain("********");
    }

    [TestCase("unavailable")]
    [TestCase("malformed")]
    [TestCase("out-of-order")]
    [TestCase("failed-shutdown")]
    [TestCase("uncertain-stop")]
    public async Task It_contains_retained_loss_before_pending_rollout_configuration(string failure)
    {
        await using (
            var session = await _store.AcquireAsync(
                _request.Timing.CallTimeout,
                _request.Timing.PollInterval,
                default
            )
        )
        {
            var journal = await session.ReadAsync(_request.TargetIdentity, default);
            var invocation = session.BeginRecordSizeAcknowledgement(journal.WorkflowId, _scope);
            await invocation.ConfirmAndRunAsync(
                new(_scope, new(invocation.InvocationId, "operator", DateTimeOffset.UtcNow, true, []), true),
                _ => Task.FromResult(true),
                default
            );
        }
        _confirmations = 1;
        var bindings = _services.GetRequiredService<ICdcBindingLifecycleService>();
        var incident = new CdcIncident(
            CdcJsonContract.CurrentContractVersion,
            CdcIncidentType.SourceHistoryContinuityLost,
            DateTimeOffset.UtcNow,
            _request.Binding.ToCompleteBindingIdentity(),
            CdcIncidentFailureCategory.ConnectOffsetMissing,
            new(
                _request.Binding.ConnectorName,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                [CdcIncidentUnavailableFact.ConnectOffset]
            )
        );
        (await bindings.LatchSourceHistoryLossAsync(incident))
            .Status.Should()
            .Be(CdcControlPlaneOperationStatus.Succeeded);
        string before = JsonSerializer.Serialize(ReadJournal());
        _live["producer.override.max.request.size"] =
            failure == "malformed" ? "private-invalid-size" : Ceiling.ToString(CultureInfo.InvariantCulture);
        A.CallTo(() => _connect.ReadConfigurationAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                Trace("config");
                return failure is "unavailable" or "failed-shutdown" or "uncertain-stop"
                    ? new CdcTransportResult<IReadOnlyDictionary<string, string>>.Unavailable(
                        new(CdcDeploymentComponent.Connect, CdcDeploymentFailure.Unavailable)
                    )
                    : Observed<IReadOnlyDictionary<string, string>>(_live);
            });
        A.CallTo(() => _connect.StopAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                Trace("stop");
                _stopped.Should().BeFalse();
                _stopped = failure != "failed-shutdown";
                if (failure is "failed-shutdown" or "uncertain-stop")
                {
                    throw new HttpRequestException("private-stop-sentinel");
                }
                return Observed(new CdcTransportAcknowledgement());
            });
        if (failure == "failed-shutdown")
        {
            A.CallTo(() => _connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
                .ReturnsLazily(() =>
                {
                    Trace("status");
                    return new CdcTransportResult<CdcConnectStatus>.Unavailable(
                        new(CdcDeploymentComponent.Connect, CdcDeploymentFailure.Unavailable)
                    );
                });
        }
        _trace.Clear();

        var result = await Execute();

        result.Succeeded.Should().BeFalse();
        result.Ready.Should().BeFalse();
        result.Observation.Should().NotBeNull();
        result.Observation.Status.SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
        result.Observation.IncidentPersistence.Should().Be(CdcIncidentPersistenceState.Persisted);
        result
            .Observation.Containment.Should()
            .Be(
                failure == "failed-shutdown"
                    ? CdcConnectorContainmentState.Failed
                    : CdcConnectorContainmentState.Stopped
            );
        _trace.Should().Equal("stop", "status");
        _confirmations.Should().Be(1);
        _effects.Should().BeEmpty();
        JsonSerializer.Serialize(ReadJournal()).Should().Be(before);
        (await bindings.ExactMatchBindingAsync(_request.Binding))
            .State!.Incident.Should()
            .BeEquivalentTo(incident);
        if (failure is "failed-shutdown" or "uncertain-stop")
        {
            result
                .Diagnostics.Should()
                .Contain(d =>
                    d.Component == CdcDeploymentComponent.Connect
                    && d.Failure == CdcDeploymentFailure.Unavailable
                );
        }
        JsonSerializer
            .Serialize(result)
            .Should()
            .NotContain("private-stop-sentinel")
            .And.NotContain("private-invalid-size");
        await using var released = await _store.AcquireAsync(
            _request.Timing.CallTimeout,
            _request.Timing.PollInterval,
            default
        );
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_contains_terminal_loss_during_rollout_and_after_final_completion(bool final)
    {
        ShortTiming(1000);
        using var deadline = new CancellationTokenSource();
        _onCall = name =>
        {
            if (!final && name == "broker-after")
            {
                _identity = new('b', 64);
            }
        };
        A.CallTo(() => _provider.SetupAsync(A<Ddl.CdcProviderSetupRequest>._, A<CancellationToken>._))
            .Invokes(() =>
            {
                if (final && _effects.Contains("resume-after") && !ReadJournal().HasPendingRecordSizeIncrease)
                {
                    _identity = new('b', 64);
                }
            })
            .ReturnsLazily(
                (Ddl.CdcProviderSetupRequest request, CancellationToken _) => ProviderResult(request)
            );
        int stops = 0;
        A.CallTo(() => _connect.StopAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(
                async (CdcDeploymentRequest _, CancellationToken ct) =>
                {
                    _stopped = true;
                    if (++stops == 2)
                    {
                        await deadline.CancelAsync();
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    }
                    return Observed(new CdcTransportAcknowledgement());
                }
            );
        var result = await Execute(operationDeadline: deadline.Token);
        result.Succeeded.Should().BeFalse();
        result.Ready.Should().BeFalse();
        result.Observation.Status.SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
        result.Observation.IncidentPersistence.Should().Be(CdcIncidentPersistenceState.Persisted);
        result.Observation.Containment.Should().Be(CdcConnectorContainmentState.Stopped);
        result
            .Diagnostics.Should()
            .Contain(d =>
                d.Component == CdcDeploymentComponent.Connect && d.Failure == CdcDeploymentFailure.Timeout
            );
        stops.Should().Be(2);
        ReadJournal().HasPendingRecordSizeIncrease.Should().Be(!final);
        _effects.Count(e => e == "resume-before").Should().Be(final ? 1 : 0);
    }

    [TestCase("operation-wait", false)]
    [TestCase("latch", false)]
    [TestCase("stop", false)]
    [TestCase("read-back", false)]
    [TestCase("latch", true)]
    [TestCase("stop", true)]
    [TestCase("read-back", true)]
    public async Task It_preserves_terminal_containment_budgets_and_honors_only_caller_cancellation(
        string boundary,
        bool cancelCaller
    )
    {
        ShortTiming(100);
        _identity = new('b', 64);
        if (boundary == "operation-wait")
        {
            // Equal call/wait budgets guarantee the operation expires during the latch, after
            // terminal evidence is captured, without depending on a narrow scheduling window.
            _request = new(
                _request.Binding,
                _request.DmsSettings,
                _request.ProviderSetup,
                _request.ConnectEndpoint,
                _request.WorkerMetricsEndpoint,
                _request.ConnectorPolicy,
                _request.WorkerPolicy,
                _request.ProviderConnectionProperties,
                _request.KafkaClientSecurityProperties,
                new(_request.Timing.WaitTimeout, _request.Timing.WaitTimeout, _request.Timing.PollInterval)
            );
        }
        using var caller = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource();
        var real = _services.GetRequiredService<ICdcBindingLifecycleService>();
        var bindings = A.Fake<ICdcBindingLifecycleService>();
        A.CallTo(() => bindings.ExactMatchBindingAsync(A<CdcBinding>._, A<CancellationToken>._))
            .ReturnsLazily(
                (CdcBinding binding, CancellationToken ct) => real.ExactMatchBindingAsync(binding, ct)
            );
        A.CallTo(() => bindings.LatchSourceHistoryLossAsync(A<CdcIncident>._, A<CancellationToken>._))
            .ReturnsLazily(
                async (CdcIncident incident, CancellationToken ct) =>
                {
                    if (boundary is "latch" or "operation-wait")
                    {
                        if (boundary == "latch")
                        {
                            Expire();
                        }
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    }
                    return await real.LatchSourceHistoryLossAsync(incident, ct);
                }
            );
        int stops = 0;
        int readBacks = 0;
        A.CallTo(() => _connect.StopAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(
                async (CdcDeploymentRequest _, CancellationToken ct) =>
                {
                    stops++;
                    _stopped = true;
                    if (boundary == "operation-wait")
                    {
                        ct.IsCancellationRequested.Should().BeFalse();
                    }
                    if (boundary == "stop")
                    {
                        Expire();
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    }
                    return Observed(new CdcTransportAcknowledgement());
                }
            );
        _onCall = name =>
        {
            if (name == "status" && _stopped)
            {
                readBacks++;
                if (boundary == "read-back")
                {
                    Expire();
                }
            }
        };
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
        async Task Run()
        {
            var result = await _increase.IncreaseAsync(
                new(_request, _runtime, 1000),
                _scope,
                Ceiling,
                (_, _) => throw new AssertionException("Terminal baseline cannot request confirmation"),
                caller.Token,
                deadline.Token
            );
            result.Succeeded.Should().BeFalse();
            result.Ready.Should().BeFalse();
            var observation = result.Observation;
            observation.Status.SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
            observation.Status.Readiness.Should().Be(CdcReadiness.NotReady);
            observation
                .IncidentPersistence.Should()
                .Be(
                    boundary is "latch" or "operation-wait"
                        ? CdcIncidentPersistenceState.Failed
                        : CdcIncidentPersistenceState.Persisted
                );
            observation.Containment.Should().Be(CdcConnectorContainmentState.Stopped);
            stops.Should().Be(1);
            readBacks.Should().BeGreaterThan(0);
            if (boundary != "read-back")
            {
                result
                    .Diagnostics.Should()
                    .Contain(d =>
                        d.Failure == CdcDeploymentFailure.Timeout
                        && d.Component
                            == (
                                (boundary == "latch" || boundary == "operation-wait")
                                    ? CdcDeploymentComponent.WorkflowState
                                    : CdcDeploymentComponent.Connect
                            )
                    );
            }
            JsonSerializer.Serialize(result).Should().NotContain("private");
            deadline.IsCancellationRequested.Should().Be(boundary != "operation-wait");
        }
        if (cancelCaller)
        {
            await FluentActions.Awaiting(Run).Should().ThrowAsync<OperationCanceledException>();
            stops.Should().Be(boundary == "latch" ? 0 : 1);
        }
        else
        {
            await Run();
        }
        _effects.Should().BeEmpty();
        ReadJournal().HasPendingRecordSizeIncrease.Should().BeFalse();
        void Expire()
        {
            if (cancelCaller)
            {
                caller.Cancel();
            }
            else
            {
                deadline.Cancel();
            }
        }
    }

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

    [TestCase("lag")]
    [TestCase("backlog")]
    [TestCase("both")]
    public async Task It_completes_delayed_catch_up_in_the_same_acknowledged_invocation(string blocker)
    {
        ShortTiming(1000);
        List<DateTimeOffset> observations = [];
        _onCall = step =>
        {
            if (
                !_effects.Contains("resume-after")
                || !step.StartsWith("projection-", StringComparison.Ordinal)
            )
            {
                return;
            }
            observations.Add(DateTimeOffset.UtcNow);
            _lag = observations.Count <= 3 && blocker is "lag" or "both" ? 2000 : 1;
            _backlog = observations.Count <= 3 && blocker is "backlog" or "both";
            if (observations.Count == 3)
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
                ReadJournal().HasPendingRecordSizeIncrease.Should().BeTrue();
            }
        };

        var result = await Execute();

        result.Succeeded.Should().BeTrue(because: JsonSerializer.Serialize(result));
        result.Ready.Should().BeTrue();
        result.Diagnostics.Should().BeEmpty();
        observations.Should().HaveCount(6); // Four catch-up passes, then fresh completion/final passes.
        for (int i = 1; i < 4; i++)
        {
            (observations[i] - observations[i - 1])
                .Should()
                .BeGreaterThanOrEqualTo(_request.Timing.PollInterval - TimeSpan.FromMilliseconds(1));
        }
        AssertSingleRollout();
        var journal = ReadJournal();
        journal.HasPendingRecordSizeIncrease.Should().BeFalse();
        journal
            .Operations.Single(o => o.OperationId == _scope.OperationId)
            .Completions.Should()
            .ContainSingle();
        journal
            .Operations.Single(o => o.Effect == CdcWorkflowEffect.ResumeConnector)
            .Completions.Should()
            .ContainSingle();
    }

    [TestCase("lag", false)]
    [TestCase("backlog", false)]
    [TestCase("lag", true)]
    [TestCase("backlog", true)]
    public async Task It_bounds_persistent_catch_up_and_propagates_caller_cancellation(
        string blocker,
        bool cancel
    )
    {
        ShortTiming(200);
        using var caller = new CancellationTokenSource();
        int observations = 0;
        _onCall = step =>
        {
            if (
                !_effects.Contains("resume-after")
                || !step.StartsWith("projection-", StringComparison.Ordinal)
            )
            {
                return;
            }
            observations++;
            _lag = blocker == "lag" ? 2000 : 1;
            _backlog = blocker == "backlog";
            if (cancel && observations == 3)
            {
                caller.Cancel();
                caller.Token.ThrowIfCancellationRequested();
            }
        };
        var started = DateTimeOffset.UtcNow;

        if (cancel)
        {
            await FluentActions
                .Awaiting(() => Execute(caller.Token))
                .Should()
                .ThrowAsync<OperationCanceledException>();
        }
        else
        {
            var result = await Execute();
            result.Succeeded.Should().BeFalse();
            result.Ready.Should().BeFalse();
            result.Diagnostics.Should().Contain(d => d.Failure == CdcDeploymentFailure.Timeout);
            (DateTimeOffset.UtcNow - started)
                .Should()
                .BeGreaterThanOrEqualTo(_request.Timing.WaitTimeout - TimeSpan.FromMilliseconds(20));
            (DateTimeOffset.UtcNow - started)
                .Should()
                .BeLessThan(_request.Timing.WaitTimeout + TimeSpan.FromSeconds(1));
        }
        observations.Should().BeGreaterThanOrEqualTo(3);
        AssertSingleRollout();
        var journal = ReadJournal();
        journal.HasPendingRecordSizeIncrease.Should().BeTrue();
        journal
            .Operations.Single(o => o.Effect == CdcWorkflowEffect.ResumeConnector)
            .Completions.Should()
            .BeEmpty();
        await using var released = await _store.AcquireAsync(
            _request.Timing.CallTimeout,
            _request.Timing.PollInterval,
            CancellationToken.None
        );
    }

    [TestCase("metrics-unavailable")]
    [TestCase("metrics-invalid")]
    [TestCase("projection-invalid")]
    [TestCase("config")]
    [TestCase("policy")]
    [TestCase("worker")]
    [TestCase("failed-task")]
    [TestCase("terminal")]
    public async Task It_rejects_new_failures_during_catch_up_without_repeating_rollout(string failure)
    {
        ShortTiming(1000);
        using var deadline = new CancellationTokenSource();
        int passes = 0;
        _onCall = step =>
        {
            if (step == "resume-after")
            {
                _backlog = true;
            }
            if (step != "provider" || !_effects.Contains("resume-after") || ++passes != 3)
            {
                return;
            }
            switch (failure)
            {
                case "metrics-unavailable":
                case "metrics-invalid":
                    A.CallTo(() =>
                            _metrics.CollectAsync(
                                A<CdcDeploymentRequest>._,
                                A<CdcTelemetryObservationPass>._,
                                A<CancellationToken>._
                            )
                        )
                        .Returns(
                            new CdcTransportResult<CdcConnectorTelemetryObservation>.Unavailable(
                                new(
                                    CdcDeploymentComponent.Metrics,
                                    failure == "metrics-unavailable"
                                        ? CdcDeploymentFailure.Unavailable
                                        : CdcDeploymentFailure.ValidationFailed
                                )
                            )
                        );
                    break;
                case "projection-invalid":
                    A.CallTo(() => _runtime.ObserveAsync(A<CancellationToken>._))
                        .Throws(
                            new CdcEstablishedValidation.EvidenceException(
                                new(CdcDeploymentComponent.Projection, CdcDeploymentFailure.ValidationFailed)
                            )
                        );
                    break;
                case "config":
                    _live["tasks.max"] = "2";
                    break;
                case "policy":
                    _topicLimit = 1;
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
                case "failed-task":
                    _failedTask = true;
                    break;
                case "terminal":
                    _identity = new('b', 64);
                    // Expire the enclosing deadline only after terminal evidence reaches containment.
                    A.CallTo(() => _connect.StopAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
                        .ReturnsLazily(
                            async (CdcDeploymentRequest _, CancellationToken ct) =>
                            {
                                _stopped = true;
                                await deadline.CancelAsync();
                                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                                return Observed(new CdcTransportAcknowledgement());
                            }
                        );
                    break;
            }
        };

        var result = await Execute(operationDeadline: deadline.Token);

        result.Succeeded.Should().BeFalse();
        result.Ready.Should().BeFalse();
        passes.Should().Be(3);
        AssertSingleRollout();
        ReadJournal().HasPendingRecordSizeIncrease.Should().BeTrue();
        ReadJournal()
            .Operations.Single(o => o.Effect == CdcWorkflowEffect.ResumeConnector)
            .Completions.Should()
            .BeEmpty();
        if (failure == "terminal")
        {
            result.Observation.IncidentPersistence.Should().Be(CdcIncidentPersistenceState.Persisted);
            result.Observation.Containment.Should().Be(CdcConnectorContainmentState.Stopped);
            result
                .Diagnostics.Should()
                .Contain(d =>
                    d.Component == CdcDeploymentComponent.Connect && d.Failure == CdcDeploymentFailure.Timeout
                );
            deadline.IsCancellationRequested.Should().BeTrue();
        }
        else if (failure is "metrics-unavailable" or "metrics-invalid" or "projection-invalid")
        {
            result
                .Diagnostics.Should()
                .Contain(d =>
                    d.Component
                        == (
                            failure == "projection-invalid"
                                ? CdcDeploymentComponent.Projection
                                : CdcDeploymentComponent.Metrics
                        )
                    && d.Failure
                        == (
                            failure == "metrics-unavailable"
                                ? CdcDeploymentFailure.Unavailable
                                : CdcDeploymentFailure.ValidationFailed
                        )
                );
            result.Diagnostics.Should().NotContain(d => d.Failure == CdcDeploymentFailure.Timeout);
        }
        await using var released = await _store.AcquireAsync(
            _request.Timing.CallTimeout,
            _request.Timing.PollInterval,
            CancellationToken.None
        );
    }

    private void AssertSingleRollout()
    {
        _confirmations.Should().Be(1);
        foreach (string effect in new[] { "stop", "broker", "topic", "buffer", "request", "resume" })
        {
            _effects.Count(e => e == effect + "-before").Should().Be(1);
            _effects.Count(e => e == effect + "-after").Should().Be(1);
        }
        ReadJournal()
            .Operations.Single(o => o.OperationId == _scope.OperationId)
            .RecordSizeIncrease.Single()
            .Acknowledgements.Should()
            .ContainSingle();
    }

    [Test]
    public async Task It_waits_for_task_free_unassignment_after_each_configuration_update()
    {
        HashSet<string> observed = [];
        _statusChange = status =>
        {
            string effect = _effects.LastOrDefault() ?? "";
            return effect is "buffer-after" or "request-after" && observed.Add(effect)
                ? new(
                    status.Runtime with
                    {
                        ConnectorState = CdcConnectorRuntimeState.Unassigned,
                    },
                    status.WorkerId,
                    status.Tasks
                )
                : status;
        };

        var result = await Execute();

        result.Succeeded.Should().BeTrue();
        result.Ready.Should().BeTrue();
        observed.Should().BeEquivalentTo("buffer-after", "request-after");
        _effects.Count(effect => effect == "buffer-after").Should().Be(1);
        _effects.Count(effect => effect == "request-after").Should().Be(1);
        ReadJournal().HasPendingRecordSizeIncrease.Should().BeFalse();
    }

    [TestCase("running")]
    [TestCase("failed")]
    [TestCase("unknown")]
    [TestCase("task")]
    [TestCase("runtime-task")]
    [TestCase("running-task-count")]
    [TestCase("worker")]
    [TestCase("stale")]
    [TestCase("future")]
    public async Task It_rejects_unsafe_configuration_transition_evidence_without_advancing(string defect)
    {
        int reads = 0;
        _statusChange = status =>
        {
            if (_effects.LastOrDefault() != "buffer-after")
            {
                return status;
            }
            reads++;
            return new(
                status.Runtime with
                {
                    TaskCount = defect == "runtime-task" ? 1 : status.Runtime.TaskCount,
                    RunningTaskCount = defect == "running-task-count" ? 1 : status.Runtime.RunningTaskCount,
                    ConnectorState = defect switch
                    {
                        "running" => CdcConnectorRuntimeState.Running,
                        "failed" => CdcConnectorRuntimeState.Failed,
                        "unknown" => CdcConnectorRuntimeState.Unknown,
                        _ => CdcConnectorRuntimeState.Unassigned,
                    },
                    ObservedAt = defect switch
                    {
                        "stale" => DateTimeOffset.UtcNow.AddMinutes(-1),
                        "future" => DateTimeOffset.UtcNow.AddMinutes(1),
                        _ => status.Runtime.ObservedAt,
                    },
                },
                defect == "worker" ? "different-worker" : status.WorkerId,
                defect == "task" ? [new(0, CdcConnectorRuntimeState.Running, status.WorkerId)] : []
            );
        };

        var result = await Execute();

        result.Succeeded.Should().BeFalse();
        result.Ready.Should().BeFalse();
        reads.Should().Be(1);
        _effects.Should().NotContain("request-before").And.NotContain("resume-before");
        ReadJournal().HasPendingRecordSizeIncrease.Should().BeTrue();
    }

    [Test]
    public async Task It_cancels_an_unassignment_that_never_returns_to_stopped()
    {
        using var timeout = new CancellationTokenSource();
        bool waiting = false;
        _statusChange = status =>
        {
            if (_effects.LastOrDefault() != "buffer-after")
            {
                return status;
            }
            if (!waiting)
            {
                waiting = true;
                timeout.CancelAfter(TimeSpan.FromMilliseconds(100));
            }
            return new(
                status.Runtime with
                {
                    ConnectorState = CdcConnectorRuntimeState.Unassigned,
                },
                status.WorkerId,
                status.Tasks
            );
        };

        Func<Task> execute = () => Execute(timeout.Token);
        await execute.Should().ThrowAsync<OperationCanceledException>();

        waiting.Should().BeTrue();
        _effects.Should().NotContain("request-before").And.NotContain("resume-before");
        ReadJournal().HasPendingRecordSizeIncrease.Should().BeTrue();
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
