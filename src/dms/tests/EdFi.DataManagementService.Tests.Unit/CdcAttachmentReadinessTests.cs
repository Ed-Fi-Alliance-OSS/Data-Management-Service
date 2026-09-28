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
