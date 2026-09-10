// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;
using Ddl = EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture(Ddl.CdcProvider.Postgresql)]
[TestFixture(Ddl.CdcProvider.SqlServer)]
[Platform(Exclude = "Win", Reason = "Local CDC state requires Unix owner-only permissions.")]
internal class Given_CdcConnectorRegistration(Ddl.CdcProvider provider) : CdcRegistrationTestBase(provider)
{
    [Test]
    public async Task It_establishes_only_after_policy_live_configuration_running_and_streaming_offsets()
    {
        var bindingPath = Directory
            .GetFiles(Path.Combine(_root, "bindings"), "*.json", SearchOption.AllDirectories)
            .Single();
        var before = await File.ReadAllBytesAsync(bindingPath);
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        _trace.IndexOf("preflight").Should().BeLessThan(_trace.IndexOf("post-before"));
        _trace.IndexOf("acls").Should().BeLessThan(_trace.IndexOf("post-before"));
        _trace.IndexOf("status").Should().BeLessThan(_trace.IndexOf("offset"));
        _posts.Should().Be(1);
        ReadJournal()
            .Operations.Single(o => o.Effect == CdcWorkflowEffect.EstablishConnector)
            .Completions.Single()
            .Evidence.Should()
            .Be(new CdcWorkflowCompletion.Connector(Offsets().SourcePartitionHash));
        _calls.Should().OnlyContain(c => c.Mode == Ddl.CdcProviderSetupMode.ValidateOnly);
        _calls[0].RequireUnconsumedInitialSlot.Should().BeTrue();
        _calls[^1].RequireUnconsumedInitialSlot.Should().BeFalse();
        (await File.ReadAllBytesAsync(bindingPath)).Should().Equal(before);
        ReadJournal().WriterPublicationAuthorized.Should().BeFalse();
    }

    [TestCase(CdcConnectorRuntimeState.Unassigned, 0, 1)]
    [TestCase(CdcConnectorRuntimeState.Running, 0, 1)]
    [TestCase(CdcConnectorRuntimeState.Running, 1, 1)]
    [TestCase(CdcConnectorRuntimeState.Running, 0, 2)]
    [TestCase(CdcConnectorRuntimeState.Unassigned, 0, 3)]
    [TestCase(CdcConnectorRuntimeState.Running, 1, 2)]
    public async Task It_waits_for_task_assignment_before_reading_offsets(
        CdcConnectorRuntimeState connectorState,
        int taskCount,
        int unassignedRead
    )
    {
        int reads = 0;
        A.CallTo(() => _connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                var status = Status();
                if (++reads == unassignedRead)
                {
                    _offsetReads.Should().Be(unassignedRead == 1 ? 0 : 1);
                    status = new(
                        status.Runtime with
                        {
                            ConnectorState = connectorState,
                            TaskCount = taskCount,
                            RunningTaskCount = 0,
                            SoleTaskState =
                                taskCount == 0
                                    ? CdcConnectorRuntimeState.Unknown
                                    : CdcConnectorRuntimeState.Unassigned,
                        },
                        status.WorkerId,
                        taskCount == 0 ? [] : [new(0, CdcConnectorRuntimeState.Unassigned, "worker:8083")]
                    );
                }
                return Observed(status);
            });
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        reads.Should().BeGreaterThan(unassignedRead);
        if (unassignedRead > 1)
        {
            _offsetReads
                .Should()
                .BeGreaterThan(1, "a late unassigned task discards the earlier offset observation");
        }
        _offsetReads.Should().BeGreaterThan(0);
        _posts.Should().Be(1);
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public async Task It_waits_for_initial_status_store_publication_without_recreating_the_connector(
        int absentRead
    )
    {
        int reads = 0;
        A.CallTo(() => _connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
                ++reads == absentRead ? new CdcTransportResult<CdcConnectStatus>.Absent() : Observed(Status())
            );
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        _posts.Should().Be(1);
        reads.Should().BeGreaterThan(absentRead);
        _offsetReads.Should().Be(absentRead == 1 ? 1 : 2);
    }

    [Test]
    public async Task It_rejects_missing_status_after_durable_establishment()
    {
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        A.CallTo(() => _connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Returns(new CdcTransportResult<CdcConnectStatus>.Absent());
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _posts.Should().Be(1);
    }

    [TestCase(CdcConnectorRuntimeState.Failed, 2)]
    [TestCase(CdcConnectorRuntimeState.Stopped, 2)]
    [TestCase(CdcConnectorRuntimeState.Paused, 3)]
    public async Task It_rejects_late_failed_stopped_or_paused_tasks_without_establishment(
        CdcConnectorRuntimeState state,
        int changedRead
    )
    {
        int reads = 0;
        A.CallTo(() => _connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                if (++reads == changedRead)
                {
                    _runtimeState = state;
                }
                return Observed(Status());
            });
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _offsetReads.Should().Be(1);
        ReadJournal()
            .Operations.Single(o => o.Effect == CdcWorkflowEffect.EstablishConnector)
            .Completions.Should()
            .BeEmpty();
        A.CallTo(() => _connect.ResumeAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => _connect.RestartAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_reconciles_an_existing_exact_connector_without_creation_or_rewriting_establishment()
    {
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        var before = JsonSerializer.Serialize(ReadJournal());
        _calls.Clear();
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        _posts.Should().Be(1);
        _calls
            .Should()
            .OnlyContain(c =>
                c.Mode == Ddl.CdcProviderSetupMode.ValidateOnly && !c.RequireUnconsumedInitialSlot
            );
        JsonSerializer.Serialize(ReadJournal()).Should().Be(before);
    }

    [TestCase("post-before")]
    [TestCase("post-after")]
    public async Task It_reconciles_an_uncertain_post_by_independent_readback(string stage)
    {
        _onCall = name =>
        {
            if (name == stage)
            {
                throw new HttpRequestException("private-secret");
            }
        };
        var result = await RunAsync();
        result
            .State.Should()
            .Be(
                stage == "post-after"
                    ? CdcTransportEvidenceState.Observed
                    : CdcTransportEvidenceState.Unavailable
            );
        _onCall = _ => { };
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        _posts.Should().Be(stage == "post-after" ? 1 : 2);
    }

    [TestCase("post-before")]
    [TestCase("post-after")]
    [TestCase("status")]
    [TestCase("offset")]
    public async Task It_preserves_cancellation_and_reconciles_on_a_new_invocation(string stage)
    {
        using var cancellation = new CancellationTokenSource();
        _onCall = name =>
        {
            if (name == stage)
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
        };
        Func<Task> run = () => RunAsync(cancellation.Token);
        await run.Should().ThrowAsync<OperationCanceledException>();
        _onCall = _ => { };
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        _posts.Should().Be(stage == "post-before" ? 2 : 1);
    }

    [TestCase(CdcConnectOffsetState.Missing)]
    [TestCase(CdcConnectOffsetState.AwaitingStreaming)]
    public async Task It_retains_initial_awaiting_offset_state_and_resumes_without_recreation(
        CdcConnectOffsetState state
    )
    {
        ShortTiming();
        _offsetState = state;
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        ReadJournal()
            .Operations.Single(o => o.Effect == CdcWorkflowEffect.RegisterConnector)
            .Completions.Should()
            .ContainSingle();
        ReadJournal()
            .Operations.Single(o => o.Effect == CdcWorkflowEffect.EstablishConnector)
            .Completions.Should()
            .BeEmpty();
        _offsetState = CdcConnectOffsetState.Streaming;
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        _posts.Should().Be(1);
    }

    [TestCase(CdcConnectOffsetState.Missing)]
    [TestCase(CdcConnectOffsetState.AwaitingStreaming)]
    public async Task It_rejects_established_offset_loss_without_wait_or_repair(CdcConnectOffsetState state)
    {
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        _offsetState = state;
        _offsetReads = 0;
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _offsetReads.Should().Be(1);
        _posts.Should().Be(1);
    }

    [TestCase(CdcConnectOffsetState.Snapshot)]
    [TestCase(CdcConnectOffsetState.Null)]
    [TestCase(CdcConnectOffsetState.Multiple)]
    [TestCase(CdcConnectOffsetState.Malformed)]
    [TestCase(CdcConnectOffsetState.SourcePartitionMismatch)]
    public async Task It_rejects_non_streaming_offset_evidence(CdcConnectOffsetState state)
    {
        ShortTiming();
        _offsetState = state;
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        ReadJournal()
            .Operations.Single(o => o.Effect == CdcWorkflowEffect.EstablishConnector)
            .Completions.Should()
            .BeEmpty();
    }

    [TestCase("tasks.max", "2")]
    [TestCase("table.include.list", "dms.Document")]
    [TestCase("snapshot.mode", "always")]
    [TestCase("heartbeat.interval.ms", "0")]
    [TestCase("transforms", "foreign")]
    [TestCase("producer.override.max.request.size", "1")]
    [TestCase("database.hostname", "other-source")]
    [TestCase("unrecognized.reserved.key", "secret")]
    public async Task It_rejects_live_drift_before_waiting_or_mutating(string key, string value)
    {
        await RegisterIntentAsync();
        _connectorExists = true;
        _live[key] = value;
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _posts.Should().Be(0);
        _offsetReads.Should().Be(0);
    }

    [Test]
    public async Task It_accepts_masked_live_credentials_using_existing_template_rules()
    {
        await RegisterIntentAsync();
        _connectorExists = true;
        _live["database.password"] = "********";
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        _posts.Should().Be(0);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_refuses_changed_payload_on_retry_even_if_live_configuration_matches(
        bool connectorPresent
    )
    {
        await RegisterIntentAsync();
        _connectorExists = connectorPresent;
        _request = CdcDeploymentRequestTestData.Request(
            Provider,
            worker: _request.WorkerPolicy,
            password: "${env:OTHER_PASSWORD}"
        );
        _live["database.password"] = "${env:OTHER_PASSWORD}";
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _posts.Should().Be(0);
    }

    [Test]
    public async Task It_does_not_adopt_an_unjournaled_connector()
    {
        _connectorExists = true;
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        ReadJournal().Operations.Should().NotContain(o => o.Effect == CdcWorkflowEffect.RegisterConnector);
    }

    [TestCase("workflows")]
    [TestCase("source-history")]
    [TestCase("bindings")]
    public async Task It_rejects_missing_provenance_before_connect_calls(string folder)
    {
        Directory.Delete(Path.Combine(_root, folder), true);
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _trace.Should().NotContain("preflight");
        _posts.Should().Be(0);
    }

    [TestCase(CdcConnectorRuntimeState.Failed)]
    [TestCase(CdcConnectorRuntimeState.Stopped)]
    [TestCase(CdcConnectorRuntimeState.Paused)]
    public async Task It_does_not_read_offsets_or_resume_failed_or_stopped_tasks(
        CdcConnectorRuntimeState state
    )
    {
        _runtimeState = state;
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _offsetReads.Should().Be(0);
        A.CallTo(() => _connect.ResumeAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => _connect.RestartAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [TestCase("")]
    [TestCase("private-other-broker:9092")]
    public async Task It_rejects_missing_or_different_worker_broker_before_registration(string endpoint)
    {
        ChangeWorkerBootstrap(endpoint);
        var result = await RunAsync();
        result.State.Should().Be(CdcTransportEvidenceState.Unavailable);
        result
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Component.Should()
            .Be(CdcDeploymentComponent.Worker);
        _posts.Should().Be(0);
        _trace.Should().NotContain("preflight");
        JsonSerializer.Serialize(result).Should().NotContain("private-other-broker");
    }

    [TestCase("image")]
    [TestCase("heap")]
    [TestCase("bootstrap.servers")]
    [TestCase("group.id")]
    [TestCase("offset.storage.topic")]
    [TestCase("connector.client.config.override.policy")]
    public async Task It_rejects_worker_drift_before_creation(string field)
    {
        var config = new Dictionary<string, string>(_workerEvidence.EffectiveConfiguration);
        if (field is not ("image" or "heap"))
        {
            config[field] = "wrong";
        }
        _workerEvidence = new(
            _workerEvidence.ProcessIdentity,
            _workerEvidence.MetricsEndpoint,
            config,
            field == "image" ? "sha256:" + new string('b', 64) : _workerEvidence.ImageDigest,
            field == "heap" ? 1 : _workerEvidence.HeapBytes,
            _workerEvidence.ConnectWorkerId
        );
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _posts.Should().Be(0);
    }

    [TestCase("offset")]
    [TestCase("post-before")]
    public async Task It_rejects_worker_replacement_during_registration(string stage)
    {
        _onCall = name =>
        {
            if (name == stage)
            {
                _workerEvidence = new(
                    "jvm-replaced",
                    _workerEvidence.MetricsEndpoint,
                    _workerEvidence.EffectiveConfiguration,
                    _workerEvidence.ImageDigest,
                    _workerEvidence.HeapBytes,
                    _workerEvidence.ConnectWorkerId
                );
            }
        };
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        ReadJournal()
            .Operations.Single(o => o.Effect == CdcWorkflowEffect.EstablishConnector)
            .Completions.Should()
            .BeEmpty();
    }

    [TestCase("topic")]
    [TestCase("brokers")]
    [TestCase("acls")]
    [TestCase("preflight")]
    [TestCase("worker")]
    public async Task It_sanitizes_unavailable_prerequisites_and_prevents_creation(string stage)
    {
        _onCall = name =>
        {
            if (name == stage)
            {
                throw new IOException("private-secret-source");
            }
        };
        var result = await RunAsync();
        result.State.Should().Be(CdcTransportEvidenceState.Unavailable);
        JsonSerializer.Serialize(result).Should().NotContain("private-secret-source");
        _posts.Should().Be(0);
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    public async Task It_reconciles_each_atomic_journal_boundary_after_a_crash(int write)
    {
        foreach (var boundary in Enum.GetValues<CdcWorkflowWriteBoundary>())
        {
            // Each iteration needs its own original managed workflow.
            if (boundary != Enum.GetValues<CdcWorkflowWriteBoundary>()[0])
            {
                await Teardown();
                await Setup();
            }
            int count = 0;
            _onWrite = current =>
            {
                if (current == boundary && ++count == write)
                {
                    throw new IOException("crash-secret");
                }
            };
            (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
            _onWrite = _ => { };
            ResetController();
            (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
            _posts.Should().Be(1);
            ReadJournal()
                .Operations.Single(o => o.Effect == CdcWorkflowEffect.EstablishConnector)
                .Completions.Should()
                .ContainSingle();
        }
    }

    [Test]
    public async Task It_does_not_treat_active_history_before_establishment_replacement_as_awaiting_first_offset()
    {
        int writes = 0;
        _onWrite = b =>
        {
            if (b == CdcWorkflowWriteBoundary.AfterAtomicReplacement && ++writes == 4)
            {
                throw new IOException();
            }
        };
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _onWrite = _ => { };
        _offsetState = CdcConnectOffsetState.Missing;
        _offsetReads = 0;
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _offsetReads.Should().Be(1);
    }

    [Test]
    public async Task It_bounds_unresponsive_calls_and_releases_the_controller_lock()
    {
        ShortTiming();
        A.CallTo(() => _connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() => new TaskCompletionSource<CdcTransportResult<CdcConnectStatus>>().Task);
        var result = await RunAsync();
        result.Diagnostics.Should().ContainSingle().Which.Failure.Should().Be(CdcDeploymentFailure.Timeout);
        await using var session = await _store.AcquireAsync(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(5),
            CancellationToken.None
        );
    }

    [Test]
    public async Task It_holds_the_controller_lock_across_external_effects()
    {
        A.CallTo(() =>
                _connect.ValidateConfigurationAsync(
                    A<CdcDeploymentRequest>._,
                    A<CdcKafkaConnectRegistrationPayload>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                async (
                    CdcDeploymentRequest _,
                    CdcKafkaConnectRegistrationPayload payload,
                    CancellationToken _
                ) =>
                {
                    Func<Task> compete = async () =>
                    {
                        await using var session = await new LocalCdcWorkflowJournalStore(_root).AcquireAsync(
                            TimeSpan.FromMilliseconds(25),
                            TimeSpan.FromMilliseconds(5),
                            CancellationToken.None
                        );
                    };
                    await compete
                        .Should()
                        .ThrowAsync<CdcWorkflowStateException>()
                        .Where(e => e.Failure == CdcWorkflowStateFailure.LockTimeout);
                    return Observed<IReadOnlyDictionary<string, string>>(payload.Config);
                }
            );
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
    }

    [TestCase(CdcConnectOffsetState.Missing)]
    [TestCase(CdcConnectOffsetState.Snapshot)]
    public async Task It_waits_for_initial_streaming_offsets_without_resetting(CdcConnectOffsetState initial)
    {
        ShortTiming(500);
        _offsetState = initial;
        _onCall = stage =>
        {
            if (stage == "offset" && _offsetReads == 2)
            {
                _offsetState = CdcConnectOffsetState.Streaming;
            }
        };
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        _offsetReads.Should().Be(2);
        A.CallTo(() => _connect.DeleteOffsetsAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [TestCase(CdcWorkflowEffect.StopConnector)]
    [TestCase(CdcWorkflowEffect.ResumeConnector)]
    [TestCase(CdcWorkflowEffect.Retire)]
    [TestCase(CdcWorkflowEffect.AuthorizeWriterPublication)]
    public async Task It_does_not_reenter_initial_registration_after_lifecycle_or_publication(
        CdcWorkflowEffect effect
    )
    {
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        await CompleteAsync(effect);
        _trace.Clear();
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _trace.Should().BeEmpty();
    }

    [Test]
    public async Task It_rejects_a_missing_previously_registered_connector_without_recreating()
    {
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        _connectorExists = false;
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _posts.Should().Be(1);
    }

    [Test]
    public void It_keeps_full_live_template_validation_requiring_actual_partition_evidence()
    {
        var config = new CdcConnectorTemplateEffectiveConfigValidationRequest(
            _handoff.TemplateRequest,
            _live,
            _handoff.TemplateRequest.ProviderSetupEvidence
        );
        _templates
            .ValidateLiveConfigurationReadBack(config)
            .Outcome.Should()
            .Be(CdcConnectorTemplateOutcome.Rendered);
        _templates
            .ValidateLiveReadBack(config)
            .Outcome.Should()
            .Be(CdcConnectorTemplateOutcome.ValidationFailed);
    }

    [Test]
    public async Task It_rejects_legacy_registration_intent_without_payload_identity()
    {
        await using (
            var session = await _store.AcquireAsync(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(5),
                CancellationToken.None
            )
        )
        {
            var journal = await session.ReadAsync(Target, CancellationToken.None);
            await session.RecordIntentAsync(
                Target,
                journal.WorkflowId,
                Guid.NewGuid(),
                CdcWorkflowEffect.RegisterConnector,
                [],
                CancellationToken.None
            );
        }
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _posts.Should().Be(0);
    }

    [TestCase("topic")]
    [TestCase("brokers")]
    [TestCase("acls")]
    [TestCase("preflight")]
    public async Task It_requires_affirmative_live_policy_before_post(string component)
    {
        if (component == "topic")
        {
            A.CallTo(() =>
                    _kafka.InspectTopicAsync(A<CdcDeploymentRequest>._, A<string>._, A<CancellationToken>._)
                )
                .Returns(
                    Observed(
                        new CdcKafkaTopicEvidence(
                            "wrong",
                            new Dictionary<int, IReadOnlyList<int>>(),
                            new Dictionary<string, CdcKafkaConfigurationValue>()
                        )
                    )
                );
        }
        if (component == "brokers")
        {
            A.CallTo(() => _kafka.InspectBrokersAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
                .Returns(Observed(new CdcKafkaBrokerEvidence(true, [new(0, 1, 1, 1)])));
        }
        if (component == "acls")
        {
            A.CallTo(() => _kafka.InspectAclsAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
                .Returns(Observed(new CdcKafkaAclEvidence(true, true, false, [], [])));
        }
        if (component == "preflight")
        {
            A.CallTo(() =>
                    _connect.ValidateConfigurationAsync(
                        A<CdcDeploymentRequest>._,
                        A<CdcKafkaConnectRegistrationPayload>._,
                        A<CancellationToken>._
                    )
                )
                .Returns(
                    Observed<IReadOnlyDictionary<string, string>>(
                        new Dictionary<string, string>(_live) { ["producer.override.max.request.size"] = "1" }
                    )
                );
        }
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _posts.Should().Be(0);
        ReadJournal().Operations.Should().NotContain(o => o.Effect == CdcWorkflowEffect.RegisterConnector);
    }

    [Test]
    public async Task It_reconciles_create_conflicts_but_rejects_changed_readback()
    {
        A.CallTo(() =>
                _connect.CreateAsync(
                    A<CdcDeploymentRequest>._,
                    A<CdcKafkaConnectRegistrationPayload>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(() =>
            {
                _connectorExists = true;
                _live["tasks.max"] = "2";
                return new CdcTransportResult<CdcTransportAcknowledgement>.Unavailable(
                    new(CdcDeploymentComponent.Connect, CdcDeploymentFailure.Conflict)
                );
            });
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        ReadJournal()
            .Operations.Single(o => o.Effect == CdcWorkflowEffect.RegisterConnector)
            .Completions.Should()
            .BeEmpty();
        _offsetReads.Should().Be(0);
    }

    [Test]
    public async Task It_requires_actual_readback_after_a_create_acknowledgement()
    {
        A.CallTo(() =>
                _connect.CreateAsync(
                    A<CdcDeploymentRequest>._,
                    A<CdcKafkaConnectRegistrationPayload>._,
                    A<CancellationToken>._
                )
            )
            .Returns(Observed(new CdcTransportAcknowledgement()));
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        ReadJournal()
            .Operations.Single(o => o.Effect == CdcWorkflowEffect.RegisterConnector)
            .Completions.Should()
            .BeEmpty();
        _offsetReads.Should().Be(0);
    }

    [Test]
    public async Task It_rejects_changed_provider_identity_after_the_running_wait()
    {
        _onCall = name =>
        {
            if (name == "offset")
            {
                _identity = new('b', 64);
            }
        };
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        ReadJournal()
            .Operations.Single(o => o.Effect == CdcWorkflowEffect.EstablishConnector)
            .Completions.Should()
            .BeEmpty();
    }

    [Test]
    public async Task It_does_not_persist_payload_source_identifiers_offsets_or_readiness()
    {
        var result = await RunAsync();
        result.State.Should().Be(CdcTransportEvidenceState.Observed);
        string journal = JsonSerializer.Serialize(ReadJournal());
        journal
            .Should()
            .NotContain("private-source-host")
            .And.NotContain("DATABASE_PASSWORD")
            .And.NotContain("database.password")
            .And.NotContain("lsn_proc")
            .And.NotContain("commit_lsn")
            .And.NotContain("Ready");
        JsonSerializer
            .Serialize(result)
            .Should()
            .NotContain("private-source-host")
            .And.NotContain("database.password");
    }
}
