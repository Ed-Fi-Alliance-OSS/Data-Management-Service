// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Tests.E2E.Cdc;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace EdFi.DataManagementService.Tests.Unit;

[TestFixture]
public partial class Given_CdcProjectionGate
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly DocumentCacheTargetKey Target = DocumentCacheTargetKey.Create("Tenant-A", 1);
    private static readonly DocumentCachePhysicalSourceFingerprint Fingerprint = new(
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    );
    private static readonly DocumentCacheLifecycleObservation TrackingLifecycle = new(
        DocumentCacheLifecycleState.Tracking,
        CacheAheadRecoveryRequired: false
    );
    private CdcProjectionGate _gate = null!;
    private FakeTimeProvider _clock = null!;
    private CancellationTokenSource _phase = null!;
    private ServiceProvider _provider = null!;
    private RecordingWriter _writer = null!;
    private RecordingMaterializer _materializer = null!;
    private DocumentCacheProjectionTargetRuntimeContext _context = null!;
    private IDocumentCacheProjectionItemProcessor _processor = null!;
    private CancellationTokenSource _cancellation = null!;
    private Task<DocumentCacheProjectionItemProcessResult> _processing = null!;
    private CdcProjectionPauseObservation _observation = null!;

    [SetUp]
    public async Task Setup()
    {
        _clock = new();
        _phase = new(TimeSpan.FromMinutes(10), _clock);
        _gate = new(Target, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30), _clock);
        _writer = new();
        _materializer = new();
        _cancellation = new();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDocumentCacheProjectionItemProcessor, DocumentCacheProjectionItemProcessor>();
        services.AddScoped<IDocumentCacheWriter>(_ => _writer);
        _gate.ConfigureServices(services);
        _provider = services.BuildServiceProvider();
        _context = RuntimeContext(
            _materializer,
            _provider.GetRequiredService<IDocumentCacheWriter>(),
            DocumentCacheTargetKey.Create("Tenant-A", 1)
        );
        _processor = _provider.GetRequiredService<IDocumentCacheProjectionItemProcessor>();
        _gate.Pause(_phase.Token, 101);
        _processing = _processor.ProcessItemAsync(Request(_context, 101), _cancellation.Token);
        _observation = await _gate.WaitUntilPausedAsync();
    }

    [TearDown]
    public async Task Teardown()
    {
        await _gate.DisposeAsync();
        try
        {
            await _processing;
        }
        catch (OperationCanceledException)
        { /* Expected when teardown cancels held work. */
        }
        catch (TimeoutException)
        { /* The timeout test observes this failure itself. */
        }
        await _context.DisposeAsync();
        await _provider.DisposeAsync();
        _processing.Dispose();
        _cancellation.Dispose();
        _phase.Dispose();
    }

    [Test]
    public void It_holds_before_even_the_writer_fast_path()
    {
        _processing.IsCompleted.Should().BeFalse();
        _writer.Calls.Should().BeEmpty();
        _materializer.Calls.Should().BeEmpty();
        _observation.Should().Be(new CdcProjectionPauseObservation(101, 11));
    }

    [Test]
    public async Task It_releases_through_the_real_processor_materialization_and_writer_path()
    {
        _gate.Release();
        DocumentCacheProjectionItemProcessResult result = await _processing;
        result.Should().BeSameAs(DocumentCacheProjectionItemProcessResult.AcknowledgedOrRemoved);
        _materializer
            .Calls.Should()
            .ContainSingle()
            .Which.TargetContext.Should()
            .BeSameAs(_context.MaterializationTargetContext);
        _writer.Calls.Should().HaveCount(2);
        _writer.Calls[0].Candidate.Should().BeNull();
        _writer.Calls[1].Candidate.Should().BeSameAs(_materializer.Candidate);
        _writer.Calls[1].SelectedRequiredContentVersion.Should().Be(11);
    }

    [Test]
    public async Task It_waits_for_the_processor_to_return_before_rearming()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _writer.BeforeWriteAsync = async _ =>
        {
            entered.TrySetResult();
            await finish.Task.WaitAsync(TimeSpan.FromSeconds(2));
        };
        _gate.Release();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task idle = _gate.WaitUntilIdleAsync(CancellationToken.None);
        try
        {
            idle.IsCompleted.Should().BeFalse();
        }
        finally
        {
            finish.TrySetResult();
        }
        await idle;
        await _processing;
        _gate.Pause(_phase.Token, 102);
        await _gate.WaitUntilIdleAsync(CancellationToken.None);
    }

    [Test]
    public async Task It_cancels_an_idle_wait_without_releasing_held_processing()
    {
        using var cancellation = new CancellationTokenSource();
        Task idle = _gate.WaitUntilIdleAsync(cancellation.Token);
        await cancellation.CancelAsync();
        Func<Task> act = () => idle;
        await act.Should().ThrowAsync<OperationCanceledException>();
        _processing.IsCompleted.Should().BeFalse();
    }

    [TestCase("Tenant-A")]
    [TestCase("tenant-a")]
    public async Task It_can_pause_all_target_work_again_for_a_new_document(string runtimeTenant)
    {
        _gate.Release();
        await _processing;
        _gate.Pause(_phase.Token);
        await using var context = RuntimeContext(
            _materializer,
            _provider.GetRequiredService<IDocumentCacheWriter>(),
            DocumentCacheTargetKey.Create(runtimeTenant, 1)
        );
        _processing = _processor.ProcessItemAsync(Request(context, 102));
        (await _gate.WaitUntilPausedAsync()).DocumentId.Should().Be(102);
        _writer.Calls.Should().HaveCount(2);
        _processing.IsCompleted.Should().BeFalse();
        _gate.Release();
        (await _processing).AcknowledgedOrRemovedDurableWork.Should().BeTrue();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_delegates_unselected_documents_and_targets(bool otherTarget)
    {
        await using DocumentCacheProjectionTargetRuntimeContext context = RuntimeContext(
            _materializer,
            _writer,
            DocumentCacheTargetKey.Create(otherTarget ? "Tenant-B" : "Tenant-A", 1)
        );
        DocumentCacheProjectionItemProcessResult result = await _processor.ProcessItemAsync(
            Request(context, otherTarget ? 101 : 102)
        );
        result.AcknowledgedOrRemovedDurableWork.Should().BeTrue();
        _processing.IsCompleted.Should().BeFalse();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_cancels_held_work_without_delegating(bool targetCancellation)
    {
        if (targetCancellation)
        {
            _context.Cancel();
        }
        else
        {
            await _cancellation.CancelAsync();
        }
        Func<Task> act = () => _processing;
        await act.Should().ThrowAsync<OperationCanceledException>();
        _writer.Calls.Should().BeEmpty();
    }

    [Test]
    public async Task It_expires_held_work_at_the_owning_phase_deadline_without_delegating()
    {
        _clock.Advance(TimeSpan.FromMinutes(10));
        Func<Task> act = () => _processing;
        await act.Should().ThrowAsync<OperationCanceledException>();
        _writer.Calls.Should().BeEmpty();
    }

    [Test]
    public async Task It_disposes_while_held_and_awaits_the_operation()
    {
        await _gate.DisposeAsync();
        Func<Task> act = () => _processing;
        await act.Should().ThrowAsync<OperationCanceledException>();
        _writer.Calls.Should().BeEmpty();
        await _gate.DisposeAsync();
    }

    [Test]
    public async Task It_requires_an_owned_hold_lifetime()
    {
        await using var gate = new CdcProjectionGate(Target, TimeSpan.FromSeconds(30));
        Action pause = () => gate.Pause(CancellationToken.None);
        pause.Should().Throw<ArgumentException>();
    }

    [Test]
    public async Task It_bounds_disposal_but_retains_cleanup_until_noncooperative_work_exits()
    {
        var clock = new FakeTimeProvider();
        var gate = new CdcProjectionGate(Target, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30), clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processing = gate.ProcessAsync(
            new PendingProcessor(async token =>
            {
                using var registration = token.Register(() => cancelled.TrySetResult());
                entered.TrySetResult();
                await exit.Task;
                token.ThrowIfCancellationRequested();
                return DocumentCacheProjectionItemProcessResult.AcknowledgedOrRemoved;
            }),
            Request(_context, 102),
            CancellationToken.None
        );
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var disposal = gate.DisposeAsync().AsTask();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30));
            clock.Advance(TimeSpan.FromSeconds(30));
            Func<Task> dispose = () => disposal;
            await dispose.Should().ThrowAsync<TimeoutException>();
            gate.CleanupCompletion.IsCompleted.Should().BeFalse();
            processing.IsCompleted.Should().BeFalse();
            exit.SetResult();
            await gate.CleanupCompletion.WaitAsync(TimeSpan.FromSeconds(30));
            Func<Task> process = () => processing;
            await process.Should().ThrowAsync<OperationCanceledException>();
            await dispose.Should().ThrowAsync<TimeoutException>();
        }
        finally
        {
            exit.TrySetResult();
            await ((Task)processing).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await gate.CleanupCompletion;
        }
    }

    private sealed class PendingProcessor(
        Func<CancellationToken, Task<DocumentCacheProjectionItemProcessResult>> process
    ) : IDocumentCacheProjectionItemProcessor
    {
        public Task<DocumentCacheProjectionItemProcessResult> ProcessItemAsync(
            DocumentCacheProjectionItemProcessRequest request,
            CancellationToken cancellationToken = default
        ) => process(cancellationToken);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_retains_the_runtime_and_attachment_after_a_failed_projection_drain(
        bool callbackFails
    )
    {
        var clock = new FakeTimeProvider();
        var gate = new CdcProjectionGate(Target, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30), clock);
        var runtime = A.Fake<ICdcProjectionRuntime>();
        List<string> disposed = [];
        A.CallTo(() => runtime.DisposeAsync())
            .ReturnsLazily(() =>
            {
                disposed.Add("runtime");
                return ValueTask.CompletedTask;
            });
        var owner = new CdcRuntimeOwner(_ => Task.FromResult((runtime, gate)));
        await owner.InPhaseAsync(
            async token =>
            {
                await owner.OpenAsync(token);
                return true;
            },
            CancellationToken.None
        );
        var resources = new CdcAttachmentResources();
        resources.Add(new AttachmentProbe(() => disposed.Add("transport")));
        resources.Add(owner);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processing = gate.ProcessAsync(
            new PendingProcessor(async token =>
            {
                using var registration = token.Register(() =>
                {
                    cancelled.TrySetResult();
                    if (callbackFails)
                    {
                        throw new InvalidOperationException("Cancellation callback failed.");
                    }
                });
                await exit.Task;
                token.ThrowIfCancellationRequested();
                return DocumentCacheProjectionItemProcessResult.AcknowledgedOrRemoved;
            }),
            Request(_context, 102),
            CancellationToken.None
        );
        try
        {
            var disposal = resources.DisposeAsync().AsTask();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30));
            clock.Advance(TimeSpan.FromSeconds(30));
            Func<Task> dispose = () => disposal;
            await dispose
                .Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("CDC_API_ATTACHMENT_DISPOSAL");
            disposed.Should().BeEmpty();
            exit.SetResult();
            await resources.CleanupCompletion.WaitAsync(TimeSpan.FromSeconds(30));
            disposed.Should().Equal("runtime", "transport");
            Func<Task> disposeOwner = () => owner.DisposeAsync().AsTask();
            await disposeOwner.Should().ThrowAsync<CdcPendingCleanupException>();
        }
        finally
        {
            exit.TrySetResult();
            await ((Task)processing).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await resources.CleanupCompletion;
        }
    }

    private sealed class AttachmentProbe(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    [Test]
    public async Task It_awaits_delegated_operation_cleanup_during_disposal()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _writer.BeforeWriteAsync = async request =>
        {
            entered.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, request.CancellationToken);
            }
            finally
            {
                cancelled.SetResult();
                await cleanup.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
        };
        _gate.Release();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task disposal = _gate.DisposeAsync().AsTask();
        try
        {
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            disposal.IsCompleted.Should().BeFalse();
        }
        finally
        {
            cleanup.SetResult();
        }
        await disposal;
        Func<Task> act = () => _processing;
        await act.Should().ThrowAsync<OperationCanceledException>();
        _materializer.Calls.Should().BeEmpty();
    }

    [Test]
    public async Task It_bounds_waiting_for_work_that_never_arrives()
    {
        await using CdcProjectionGate gate = new(Target, TimeSpan.FromSeconds(30), clock: _clock);
        gate.Pause(_phase.Token);
        var arrival = gate.WaitUntilPausedAsync();
        _clock.Advance(TimeSpan.FromSeconds(30));
        Func<Task> act = () => arrival;
        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Test]
    public async Task It_cancels_an_arrival_wait_on_disposal()
    {
        CdcProjectionGate gate = new(Target, TimeSpan.FromSeconds(2));
        gate.Pause(_phase.Token);
        Task arrival = gate.WaitUntilPausedAsync();
        await gate.DisposeAsync();
        Func<Task> act = () => arrival;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task It_honors_arrival_wait_cancellation()
    {
        await using CdcProjectionGate gate = new(Target, TimeSpan.FromSeconds(2));
        gate.Pause(_phase.Token);
        using CancellationTokenSource cancellation = new();
        Task arrival = gate.WaitUntilPausedAsync(cancellation.Token);
        await cancellation.CancelAsync();
        Func<Task> act = () => arrival;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(301)]
    public void It_rejects_nonfinite_or_excessive_deadlines(int seconds)
    {
        Action act = () => _ = new CdcProjectionGate(Target, TimeSpan.FromSeconds(seconds));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static DocumentCacheProjectionItemProcessRequest Request(
        DocumentCacheProjectionTargetRuntimeContext context,
        long documentId
    ) =>
        new(
            context,
            new(documentId, 11, ObservedAt, ObservedAt),
            DocumentCacheProjectionDrainInvocationKind.Ordinary
        );

    private sealed class RecordingWriter : IDocumentCacheWriter
    {
        public List<DocumentCacheWriterRequest> Calls { get; } = [];

        public Func<DocumentCacheWriterRequest, Task> BeforeWriteAsync { get; set; } =
            _ => Task.CompletedTask;

        public bool SuppressCandidate { get; set; }

        public async Task<DocumentCacheWriterResult> WriteAsync(DocumentCacheWriterRequest request)
        {
            Calls.Add(request);
            await BeforeWriteAsync(request);
            if (request.Candidate is null)
            {
                return new DocumentCacheWriterResult.NeedsMaterialization(11);
            }
            return SuppressCandidate
                ? new DocumentCacheWriterResult.StaleCandidateSuppressed(12, request.Candidate.ContentVersion)
                : new DocumentCacheWriterResult.CandidateWrittenAcknowledged(request.Candidate, 11);
        }
    }

    private sealed class RecordingMaterializer : IDocumentCacheMaterializer
    {
        public List<DocumentCacheMaterializationRequest> Calls { get; } = [];
        public DocumentCacheMaterializationCandidate Candidate { get; private set; } = null!;

        public Task<DocumentCacheMaterializationResult> MaterializeAsync(
            DocumentCacheMaterializationRequest request
        )
        {
            Calls.Add(request);
            Candidate = new(
                request.DocumentId,
                new DocumentUuid(Guid.NewGuid()),
                "Ed-Fi",
                "School",
                "5.2.0",
                11,
                ObservedAt,
                new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero),
                "etag-11",
                new JsonObject { ["id"] = request.DocumentId }
            );
            return Task.FromResult<DocumentCacheMaterializationResult>(
                new DocumentCacheMaterializationResult.Success(Candidate)
            );
        }
    }

    private static DocumentCacheProjectionTargetRuntimeContext RuntimeContext(
        IDocumentCacheMaterializer materializer,
        IDocumentCacheWriter writer,
        DocumentCacheTargetKey targetKey
    )
    {
        DocumentCacheTargetExecutionContext executionContext = new(
            targetKey,
            new DocumentCacheTargetContextGeneration(1),
            new DocumentCacheTargetEffectiveSettings(
                readAccelerationEnabled: true,
                directFillTimeout: TimeSpan.FromMilliseconds(250),
                projectorPollInterval: TimeSpan.FromSeconds(5),
                projectorPageSize: 3,
                projectorMaxConcurrentTargets: 2,
                projectorFailureBackoff: TimeSpan.FromSeconds(10),
                projectorBaselineHighWaterMark: 1000,
                administrationWorkflowTimeout: TimeSpan.FromHours(24)
            ),
            new DocumentCacheTargetDataStoreMetadata(targetKey.DataStoreId, "postgresql"),
            new DocumentCacheTargetConnectionInput(RelationalProviderToken.Postgresql, "connection"),
            Fingerprint,
            TrackingLifecycle,
            new DocumentCacheInventoryValidationResult(
                DocumentCacheInventoryStatus.Satisfied,
                "Inventory satisfied."
            ),
            new DocumentCacheEnqueueTriggerValidationResult(
                DocumentCacheEnqueueTriggerStatus.Satisfied,
                "Enqueue trigger satisfied."
            ),
            DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
        );

        return new DocumentCacheProjectionTargetRuntimeContext(
            executionContext,
            new DocumentCacheProjectionTargetProviderAdapters(
                RelationalProviderToken.Postgresql,
                MaterializationTargetContext(targetKey),
                materializer,
                writer
            ),
            new NoOpObservationSink(),
            disposeScopeAsync: null
        );
    }

    private static DocumentCacheMaterializationTargetContext MaterializationTargetContext(
        DocumentCacheTargetKey targetKey
    ) =>
        new(
            new DocumentCacheProjectionTargetKey(targetKey.TenantKey, new DataStoreId(targetKey.DataStoreId)),
            MappingSet(),
            DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated,
            "connection"
        );

    private static MappingSet MappingSet()
    {
        EffectiveSchemaInfo effectiveSchema = new(
            ApiSchemaFormatVersion: "5.2.0",
            RelationalMappingVersion: "v2",
            EffectiveSchemaHash: "schema-hash",
            ResourceKeyCount: 0,
            ResourceKeySeedHash: new byte[32],
            SchemaComponentsInEndpointOrder: [],
            ResourceKeysInIdOrder: []
        );

        return new MappingSet(
            new MappingSetKey(
                effectiveSchema.EffectiveSchemaHash,
                SqlDialect.Pgsql,
                effectiveSchema.RelationalMappingVersion
            ),
            new DerivedRelationalModelSet(effectiveSchema, SqlDialect.Pgsql, [], [], [], [], [], []),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>(),
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>(),
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private sealed class NoOpObservationSink : IDocumentCacheProjectionObservationSink
    {
        public void ObserveTarget(DocumentCacheProjectionTargetHealthSnapshot snapshot) => _ = snapshot;

        public void EndTargetContext(
            DocumentCacheProjectionTargetContextKey contextKey,
            DocumentCacheProjectionTargetEndReason endReason,
            DateTimeOffset? endedAt = null
        ) => _ = (contextKey, endReason, endedAt);

        public void ObserveAdministrativeCommand(
            DocumentCacheAdministrativeCommandObservationSnapshot snapshot
        ) => _ = snapshot;

        public void EndAdministrativeCommand(DocumentCacheAdministrativeCommandExecutionId executionId) =>
            _ = executionId;
    }
}
