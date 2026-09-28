// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using EdFi.DataManagementService.Tests.E2E.Cdc;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace EdFi.DataManagementService.Tests.Unit;

[TestFixture(CdcProvider.Postgresql)]
[TestFixture(CdcProvider.SqlServer)]
public class Given_CdcAttachmentReadiness(CdcProvider provider)
{
    private CdcProjectionCorrelationObservation _projection = null!;
    private CdcControllerTargetStatus _target = null!;
    private ICdcProjectionRuntime _runtime = null!;
    private readonly List<string> _events = [];

    [SetUp]
    public void Setup()
    {
        _events.Clear();
        _runtime = A.Fake<ICdcProjectionRuntime>();
        A.CallTo(() => _runtime.StartProcessingAsync(A<CancellationToken>._))
            .Invokes(() => _events.Add("start"))
            .Returns(Task.CompletedTask);
        _projection = Projection(DocumentCacheStatusExecutionState.NotObserved);
        var satisfied = CdcComponent.Satisfied(_projection.ObservedAt);
        // Production projection classification is preserved; the remaining already-admitted
        // controller prerequisites are supplied independently, as in the other scenario tests.
        var status = new CdcTargetStatus(
            _projection.TargetIdentity,
            CdcReadiness.NotReady,
            CdcBlockingCategory.StatusObservationUnavailable,
            satisfied,
            ClassifyProjection(_projection),
            satisfied,
            CdcComponent.NotApplicable(_projection.ObservedAt),
            CdcSourceHistoryComponent.FromComponent(satisfied, CdcSourceHistoryContinuity.Healthy, false),
            satisfied,
            satisfied,
            satisfied,
            satisfied,
            satisfied,
            []
        );
        status = WithLocalKafkaPolicy(status);
        _target = new(
            _projection.ObservedAt,
            status,
            new(
                DocumentCacheStatusQueuePresence.Empty,
                0,
                100,
                0,
                0,
                0,
                CdcProviderArtifactContinuityState.ExactMatch,
                CdcProviderRetainedRangeState.CoversCommittedOffset,
                provider == CdcProvider.Postgresql
                    ? CdcSqlServerSchemaHistoryState.NotApplicable
                    : CdcSqlServerSchemaHistoryState.Valid,
                null,
                new("", "", "", null, "", "", [])
            ),
            false,
            false,
            CdcIncidentPersistenceState.NotRequired,
            CdcConnectorContainmentState.NotRequired,
            [new(CdcDeploymentComponent.Projection, CdcDeploymentFailure.Unavailable)]
        );
    }

    [TearDown]
    public async Task Teardown() => await _runtime.DisposeAsync();

    [Test]
    public void It_accepts_only_the_unstarted_projection_exception_without_claiming_readiness()
    {
        var result = Result(_target);
        CdcAttachmentReadiness.RequireUnstarted(result, _projection);
        result.Aggregate.Readiness.Should().Be(CdcReadiness.NotReady);
        _projection.OperationalHealthReason.Should().Be(DocumentCacheStatusReason.RuntimeNotObserved);
        A.CallTo(() => _runtime.StartProcessingAsync(A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Test]
    public void It_accepts_production_local_authorization_notices_without_starting_projection()
    {
        _target
            .Status.Diagnostics.Select(d => (d.Code, d.Component))
            .Should()
            .Equal(
                ("authorizationDisabledLocal", CdcDiagnosticComponent.KafkaPolicy),
                ("authorizationDisabledLocal", CdcDiagnosticComponent.ConnectOffsetStore)
            );
        CdcAttachmentReadiness.RequireUnstarted(Result(_target), _projection);
        A.CallTo(() => _runtime.StartProcessingAsync(A<CancellationToken>._)).MustNotHaveHappened();
    }

    [TestCase("warning")]
    [TestCase("error")]
    [TestCase("category")]
    [TestCase("component")]
    [TestCase("code")]
    [TestCase("retryable")]
    [TestCase("additional-info")]
    public void It_rejects_diagnostics_outside_the_exact_local_authorization_notice(string change)
    {
        var notice = _target.Status.Diagnostics[0];
        var unexpected = change switch
        {
            "warning" => notice with { Severity = CdcDiagnosticSeverity.Warning },
            "error" => notice with { Severity = CdcDiagnosticSeverity.Error },
            "category" => notice with { Category = CdcDiagnosticCategory.KafkaPolicyInvalid },
            "component" => notice with { Component = CdcDiagnosticComponent.Projection },
            "code" or "additional-info" => notice with { Code = "unexpectedNotice" },
            "retryable" => notice with { Retryable = true },
            _ => throw new AssertionException("Unknown test case"),
        };
        AssertRejected(
            _target with
            {
                Status = _target.Status with
                {
                    Diagnostics =
                        change == "additional-info"
                            ? [.. _target.Status.Diagnostics, unexpected]
                            : [unexpected, _target.Status.Diagnostics[1]],
                },
            },
            _projection
        );
    }

    [TestCase("Binding")]
    [TestCase("ProviderSetup")]
    [TestCase("KafkaPolicy")]
    [TestCase("ConnectOffsetStore")]
    [TestCase("ConnectorConfig")]
    [TestCase("ConnectorRuntime")]
    [TestCase("Lag")]
    public void It_rejects_each_other_unsatisfied_prerequisite(string component)
    {
        var invalid = CdcComponent.Unknown(CdcBlockingCategory.StatusObservationUnavailable);
        var status = _target.Status;
        status = component switch
        {
            "Binding" => status with { Binding = invalid },
            "ProviderSetup" => status with { ProviderSetup = invalid },
            "KafkaPolicy" => status with { KafkaPolicy = invalid },
            "ConnectOffsetStore" => status with { ConnectOffsetStore = invalid },
            "ConnectorConfig" => status with { ConnectorConfig = invalid },
            "ConnectorRuntime" => status with { ConnectorRuntime = invalid },
            "Lag" => status with { Lag = invalid },
            _ => throw new AssertionException("Unknown test case"),
        };
        AssertRejected(_target with { Status = status }, _projection);
    }

    [TestCase("continuity")]
    [TestCase("incident")]
    [TestCase("pending-rollout")]
    [TestCase("offset-store")]
    [TestCase("recovery")]
    [TestCase("persistence")]
    [TestCase("containment")]
    [TestCase("transport-failure")]
    public void It_preserves_control_plane_failures(string failure)
    {
        var invalid = failure switch
        {
            "continuity" => _target with
            {
                Status = _target.Status with
                {
                    SourceHistory = _target.Status.SourceHistory with
                    {
                        Continuity = CdcSourceHistoryContinuity.Unknown,
                    },
                },
            },
            "incident" => _target with
            {
                Status = _target.Status with
                {
                    SourceHistory = _target.Status.SourceHistory with { IncidentLatched = true },
                },
            },
            "pending-rollout" => _target with { HasPendingRecordSizeIncrease = true },
            "offset-store" => _target with { HasSharedOffsetStoreIssue = true },
            "recovery" => _target with { Recovery = _target.Recovery with { RequiresFreshPass = true } },
            "persistence" => _target with { IncidentPersistence = CdcIncidentPersistenceState.Persisted },
            "containment" => _target with { Containment = CdcConnectorContainmentState.Stopped },
            "transport-failure" => _target with
            {
                Diagnostics = [new(CdcDeploymentComponent.Projection, CdcDeploymentFailure.Timeout)],
            },
            _ => throw new AssertionException("Unknown test case"),
        };
        AssertRejected(invalid, _projection);
    }

    [TestCase(DocumentCacheStatusExecutionState.Cancelled)]
    [TestCase(DocumentCacheStatusExecutionState.TargetBackoff)]
    [TestCase(DocumentCacheStatusExecutionState.Active)]
    public void It_does_not_treat_other_runtime_states_as_unstarted(DocumentCacheStatusExecutionState state)
    {
        var projection = Projection(state);
        AssertRejected(
            _target with
            {
                Status = _target.Status with { Projection = ClassifyProjection(projection) },
            },
            projection
        );
    }

    [Test]
    public async Task It_starts_the_designated_runtime_and_waits_for_a_fresh_ready_pass_before_writing()
    {
        CdcAttachmentReadiness.RequireUnstarted(Result(_target), _projection);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<CdcControllerStatusResult>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        int passes = 0;
        async Task<CdcControllerStatusResult> Observe(CancellationToken token)
        {
            _events.Add("observe");
            if (++passes == 1)
            {
                return Result(_target);
            }
            observed.SetResult();
            return await release.Task.WaitAsync(token);
        }
        async Task WriteAfterStartup()
        {
            await CdcAttachmentReadiness.StartAndWaitAsync(
                _runtime,
                Observe,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(1),
                CancellationToken.None
            );
            _events.Add("write");
        }
        var projection = ClassifyProjection(Projection(DocumentCacheStatusExecutionState.Active));
        projection.State.Should().Be(CdcComponentState.Satisfied);
        var write = WriteAfterStartup();
        try
        {
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            write.IsCompleted.Should().BeFalse();
            _events.Should().Equal("start", "observe", "observe");
        }
        finally
        {
            release.TrySetResult(
                Result(
                    _target with
                    {
                        Status = _target.Status with
                        {
                            Projection = projection,
                            Readiness = CdcReadiness.Ready,
                            PrimaryBlockingCategory = CdcBlockingCategory.None,
                        },
                        Diagnostics = [],
                    }
                )
            );
        }
        await write;
        _events.Should().Equal("start", "observe", "observe", "write");
        A.CallTo(() => _runtime.StartProcessingAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_prevents_writes_when_readiness_times_out_or_is_cancelled(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observedToken = default;
        Task<CdcControllerStatusResult> Observe(CancellationToken token)
        {
            observedToken = token;
            entered.TrySetResult();
            return Task.FromResult(Result(_target));
        }
        bool wrote = false;
        async Task WriteAfterStartup()
        {
            await CdcAttachmentReadiness.StartAndWaitAsync(
                _runtime,
                Observe,
                cancel ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(5),
                cancellation.Token
            );
            wrote = true;
        }
        var write = WriteAfterStartup();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancel)
        {
            await cancellation.CancelAsync();
        }
        Func<Task> act = () => write;
        await act.Should().ThrowAsync<OperationCanceledException>();
        observedToken.IsCancellationRequested.Should().BeTrue();
        wrote.Should().BeFalse();
    }

    private static CdcControllerStatusResult Result(CdcControllerTargetStatus target) =>
        new(CdcAggregateStatusEvaluator.Evaluate(new(target.ObservedAt, [target.Status])), [target]);

    private CdcTargetStatus WithLocalKafkaPolicy(CdcTargetStatus status)
    {
        // Produce the notices through the same policy mapping and core evaluation as controller
        // status. The other admitted prerequisites remain independently supplied by this fixture.
        var identity = _projection.TargetIdentity;
        var names = CdcArtifactNameGenerator
            .Render(new(identity.DeploymentKey, "edfi", identity.InstanceKey, identity.Generation, provider))
            .Inventory!;
        var binding = new CdcBinding(
            1,
            identity.DeploymentKey,
            identity.TenantKey,
            identity.DataStoreId,
            identity.InstanceKey,
            identity.Generation,
            provider,
            _projection.PhysicalSourceFingerprint!,
            names.ConnectorName,
            names.TopicName,
            1,
            CdcTargetValidator.KafkaMurmur2V1PartitionerAlgorithm,
            1
        );
        var configuration = new ConfigurationBuilder().Build();
        using var configurationLifetime = (IDisposable)configuration;
        var request = CdcDeploymentRequest.CreateDeferred(
            binding,
            configuration,
            () => throw new AssertionException("Policy observation must not prepare provider schema."),
            new("http://connect:8083/"),
            new("http://connect:9404/metrics"),
            new("broker:9092", 1_048_576),
            new(
                new("worker"),
                new("connect-offsets"),
                CdcQualifiedWorkerImage.Digest,
                268_435_456,
                "All",
                CdcKafkaDurabilityProfile.LocalSingleBroker,
                CdcKafkaAuthorizationProfile.AuthorizationDisabledLocal,
                new("worker"),
                new("connector"),
                new("administrator"),
                []
            ),
            new(
                provider == CdcProvider.Postgresql
                    ? EdFi.DataManagementService.Backend.Ddl.CdcProvider.Postgresql
                    : EdFi.DataManagementService.Backend.Ddl.CdcProvider.SqlServer,
                new Dictionary<string, string>()
            ),
            new(new Dictionary<string, string>()),
            new(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1))
        );
        var plan = CdcDeploymentKafkaPolicy.Build(request);
        var topics = plan
            .BindingTopics.Append(plan.OffsetStore)
            .ToDictionary(
                t => t.Name,
                t =>
                    (CdcTransportResult<CdcKafkaTopicEvidence>)
                        new CdcTransportResult<CdcKafkaTopicEvidence>.Observed(
                            new(
                                t.Name,
                                new Dictionary<int, IReadOnlyList<int>> { [0] = new[] { 0 } },
                                t.Configuration.ToDictionary(
                                    c => c.Key,
                                    c => new CdcKafkaConfigurationValue(c.Value, true)
                                )
                            )
                        )
            );
        var evidence = new CdcKafkaDeploymentEvidence(
            topics,
            new CdcTransportResult<CdcKafkaBrokerEvidence>.Observed(
                new(true, [new(0, 16_777_216, plan.MaxRecordBytes, plan.MaxRecordBytes)])
            ),
            new CdcTransportResult<CdcKafkaProducerCapacityEvidence>.Observed(
                new(plan.MaxRecordBytes, plan.ProducerBufferBytes, request.WorkerPolicy.HeapBytes)
            ),
            new CdcTransportResult<CdcKafkaAclEvidence>.Observed(new(false, true, false, [], []))
        );
        var evaluated = CdcTargetStatusEvaluator.EvaluatePostAdmission(
            new(_projection.OperationId, _projection.ObservedAt, identity, binding.PhysicalSourceFingerprint)
            {
                KafkaPolicy = CdcDeploymentKafkaPolicy.ObserveBinding(
                    request,
                    _projection.OperationId,
                    _projection.ObservedAt,
                    evidence
                ),
                ConnectOffsetStore = CdcDeploymentKafkaPolicy.ObserveOffsetStore(
                    request,
                    _projection.OperationId,
                    _projection.ObservedAt,
                    evidence
                ),
            }
        );
        evaluated.KafkaPolicy.State.Should().Be(CdcComponentState.Satisfied);
        evaluated.ConnectOffsetStore.State.Should().Be(CdcComponentState.Satisfied);
        return status with
        {
            KafkaPolicy = evaluated.KafkaPolicy,
            ConnectOffsetStore = evaluated.ConnectOffsetStore,
            Diagnostics = evaluated
                .Diagnostics.Where(d =>
                    d.Component
                        is CdcDiagnosticComponent.KafkaPolicy
                            or CdcDiagnosticComponent.ConnectOffsetStore
                )
                .ToArray(),
        };
    }

    private static void AssertRejected(
        CdcControllerTargetStatus target,
        CdcProjectionCorrelationObservation projection
    )
    {
        Action act = () => CdcAttachmentReadiness.RequireUnstarted(Result(target), projection);
        act.Should().Throw<InvalidOperationException>().WithMessage("CDC_API_ATTACHMENT_PREREQUISITES");
    }

    private static CdcComponent ClassifyProjection(CdcProjectionCorrelationObservation projection) =>
        CdcTargetStatusEvaluator
            .EvaluatePostAdmission(
                new(
                    projection.OperationId,
                    projection.ObservedAt,
                    projection.TargetIdentity,
                    projection.PhysicalSourceFingerprint!
                )
                {
                    Projection = projection,
                }
            )
            .Projection;

    private CdcProjectionCorrelationObservation Projection(DocumentCacheStatusExecutionState state)
    {
        var now = DateTimeOffset.UtcNow;
        var key = DocumentCacheTargetKey.Create("", 7);
        var identity = new CdcTargetIdentity(
            "deployment",
            CdcTargetValidator.DefaultBindingTenantKey,
            "7",
            "instance",
            1,
            provider
        );
        const string fingerprint = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var target = DocumentCacheTargetObservation.ResolvedEligible(
            key,
            new(
                false,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(1),
                2,
                1,
                TimeSpan.FromSeconds(5),
                100,
                TimeSpan.FromMinutes(1)
            ),
            new(1),
            provider == CdcProvider.Postgresql
                ? RelationalProviderToken.Postgresql
                : RelationalProviderToken.SqlServer,
            new(fingerprint),
            new(DocumentCacheLifecycleState.Tracking, false),
            new(DocumentCacheInventoryStatus.Satisfied, ""),
            new(DocumentCacheEnqueueTriggerStatus.Satisfied, ""),
            DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
        );
        var classified = DocumentCacheStatusClassifier.Classify(
            target,
            new(state, now),
            DocumentCacheStatusDurableObservation.Success(
                DocumentCacheLifecycleState.Tracking,
                false,
                DocumentCacheStatusQueuePresence.Empty,
                null,
                null,
                now
            ),
            DocumentCacheStatusEvaluationMode.StandaloneDirectObservation
        );
        return new(
            1,
            "observation",
            now,
            identity,
            provider,
            fingerprint,
            now,
            new("", 7),
            CdcProjectionCorrelationState.Matched,
            classified.OperationalHealth.Status,
            classified.OperationalHealth.Reason,
            classified.CaughtUp.Status,
            classified.CaughtUp.Reason,
            classified.QueueSummary.Presence,
            [],
            []
        );
    }
}
