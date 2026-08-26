// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[Category("DocumentCacheAdministration")]
public class Given_A_Postgresql_DocumentCacheAdministration_Workflow
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/minimal";

    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FirstEnqueuedAt = ObservedAt.AddMinutes(-5);
    private static readonly QualifiedResourceName PersonResource = new("Ed-Fi", "Person");
    private static readonly DocumentCacheTargetKey TargetKey = DocumentCacheTargetKey.Create("TenantA", 1);
    private static readonly DocumentCacheAdministrativeTargetKey AdministrativeTargetKey =
        DocumentCacheAdministrativeTargetKey.FromTargetKey(TargetKey);
    private static readonly DocumentCachePhysicalSourceFingerprint Fingerprint = new(
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    );
    private static readonly DocumentCacheOfflineWriterAdmission OfflineActivationAdmission = new(
        confirmed: true,
        confirmation: DocumentCacheOfflineWriterAdmissionConfirmation.OfflineActivationWritersClosedAndDrained
    );
    private static readonly DocumentCacheOfflineWriterAdmission OfflineDeactivationAdmission = new(
        confirmed: true,
        confirmation: DocumentCacheOfflineWriterAdmissionConfirmation.OfflineDeactivationWritersClosedAndDrained
    );
    private static readonly DocumentCacheOfflineWriterAdmission CacheAheadRecoveryAdmission = new(
        confirmed: true,
        confirmation: DocumentCacheOfflineWriterAdmissionConfirmation.InternalOnlyCacheAheadRecoveryWritersClosedAndDrained
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private PostgresqlGeneratedDdlBaselineDatabase _baseline = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private NpgsqlDataSourceCache _dataSourceCache = null!;
    private Dictionary<long, SourceDocument> _sourcesByDocumentId = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _baseline = await PostgresqlGeneratedDdlBaselineDatabase.CreateAsync(
            $"{nameof(Given_A_Postgresql_DocumentCacheAdministration_Workflow)}:{_fixture.MappingSet.Key.EffectiveSchemaHash}",
            _fixture.GeneratedDdl
        );
    }

    [SetUp]
    public async Task SetUp()
    {
        _database = await _baseline.CreateIsolatedDatabaseAsync();
        _dataSourceCache = new NpgsqlDataSourceCache(NullLogger<NpgsqlDataSourceCache>.Instance);
        _sourcesByDocumentId = [];
    }

    [TearDown]
    public async Task TearDown()
    {
        _dataSourceCache?.Dispose();

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_baseline is not null)
        {
            await _baseline.DisposeAsync();
        }
    }

    [Test]
    public async Task It_classifies_workflow_timeout_before_first_durable_mutation_as_no_mutation()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking, cacheAheadRecoveryRequired: false);
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false),
            workflowTimeout: TimeSpan.FromMilliseconds(250)
        );
        var workflow = new DelegatingWorkflow(
            preflight: static (context, _) => Task.FromResult(context.EligiblePreflightResult()),
            execute: async (context, cancellationToken) =>
            {
                context.EnterPhase(DocumentCacheAdministrativeCommandPhase.ClearCache);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                return context.Completed();
            }
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(
            RunnerRequest(),
            workflow
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.FailedNoMutation);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.WorkflowTimeout);
        result.Mutated.Should().BeFalse();
        result.Lifecycle.Should().Be(DocumentCacheLifecycleState.Tracking);
        result
            .PhaseDiagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.CurrentPhase == DocumentCacheAdministrativeCommandPhase.ClearCache
                && diagnostic.DiagnosticCategory
                    == DocumentCacheAdministrativeDiagnosticCategory.WorkflowTimeout
                && !diagnostic.Retryable
            );
        (await ReadLifecycleAsync())
            .Should()
            .Be(new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false));
    }

    [Test]
    public async Task It_classifies_workflow_timeout_after_durable_mutation_as_incomplete_retryable()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking, cacheAheadRecoveryRequired: false);
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false),
            workflowTimeout: TimeSpan.FromMilliseconds(250)
        );
        var workflow = new DelegatingWorkflow(
            preflight: static (context, _) => Task.FromResult(context.EligiblePreflightResult()),
            execute: async (context, cancellationToken) =>
            {
                await CommitLifecycleTransitionAsync(
                        context,
                        DocumentCacheAdministrativeCommandPhase.EnterResetting,
                        DocumentCacheLifecycleState.Tracking,
                        DocumentCacheLifecycleState.Resetting,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                return context.Completed();
            }
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(
            RunnerRequest(),
            workflow
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.IncompleteRetryable);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.WorkflowTimeout);
        result.Mutated.Should().BeTrue();
        result.Lifecycle.Should().Be(DocumentCacheLifecycleState.Resetting);
        result
            .PhaseDiagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.CurrentPhase == DocumentCacheAdministrativeCommandPhase.EnterResetting
                && diagnostic.LastCompletedPhase == DocumentCacheAdministrativeCommandPhase.EnterResetting
                && diagnostic.DiagnosticCategory
                    == DocumentCacheAdministrativeDiagnosticCategory.WorkflowTimeout
                && diagnostic.Retryable
            );
        (await ReadLifecycleAsync())
            .Should()
            .Be(new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Resetting, false));
    }

    [Test]
    public async Task It_reports_baseline_high_water_backpressure_when_workflow_timeout_fires_during_rebuild_seed()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking, cacheAheadRecoveryRequired: false);
        IReadOnlyList<SourceDocument> sources = await InsertProjectedRowsAsync(documentCount: 1);
        DocumentCacheOnlineCacheRebuildCommand command = CreateOnlineCacheRebuildCommand(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false),
            workflowTimeout: TimeSpan.FromMilliseconds(250),
            projectorBaselineHighWaterMark: 1,
            baselineSeedDelay: new CancellationOnlyBaselineSeedDelay(),
            baselineSeedDrainer: NoBackpressureReliefDrainer.Instance
        );

        DocumentCacheAdministrativeCommandResult result = await command.ExecuteAsync(
            new DocumentCacheOnlineCacheRebuildRequest(
                AdministrativeTargetKey,
                Fingerprint,
                DocumentCacheAdministrativeCommandConfirmation.OnlineCacheRebuild
            )
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.IncompleteRetryable);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.WorkflowTimeout);
        result.Mutated.Should().BeTrue();
        result.Lifecycle.Should().Be(DocumentCacheLifecycleState.Rebuilding);
        result
            .PhaseDiagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.CurrentPhase == DocumentCacheAdministrativeCommandPhase.SeedBaseline
                && diagnostic.DiagnosticCategory
                    == DocumentCacheAdministrativeDiagnosticCategory.BaselineHighWaterBackpressure
                && diagnostic.Retryable
                && diagnostic.AffectedDocumentIds.SequenceEqual(new[] { sources.Single().DocumentId })
            );
        result
            .PhaseDiagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.CurrentPhase == DocumentCacheAdministrativeCommandPhase.SeedBaseline
                && diagnostic.DiagnosticCategory
                    == DocumentCacheAdministrativeDiagnosticCategory.WorkflowTimeout
                && diagnostic.Retryable
            );
        result
            .PhaseDiagnostics.Should()
            .NotContain(diagnostic =>
                diagnostic.DiagnosticCategory
                == DocumentCacheAdministrativeDiagnosticCategory.PersistentPoison
            );
    }

    [Test]
    public async Task It_rolls_back_a_started_short_transaction_when_workflow_timeout_fires_before_commit()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking, cacheAheadRecoveryRequired: false);
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false),
            workflowTimeout: TimeSpan.FromMilliseconds(250)
        );
        var workflow = new DelegatingWorkflow(
            preflight: static (context, _) => Task.FromResult(context.EligiblePreflightResult()),
            execute: async (context, cancellationToken) =>
            {
                context.EnterPhase(DocumentCacheAdministrativeCommandPhase.EnterResetting);
                await using IRelationalWriteSession session = await context
                    .MutexLease.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                    .ConfigureAwait(false);

                try
                {
                    DocumentCacheAdministrativeLifecycleTransitionResult transition = await context
                        .Primitives.TryTransitionLifecycleAsync(
                            session,
                            new DocumentCacheAdministrativeLifecycleTransitionRequest(
                                DocumentCacheLifecycleState.Tracking,
                                expectedCacheAheadRecoveryRequired: false,
                                DocumentCacheLifecycleState.Resetting,
                                nextCacheAheadRecoveryRequired: false
                            ),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    transition.Mutated.Should().BeTrue();

                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                    await session.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return context.Completed();
                }
                catch
                {
                    await session.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            }
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(
            RunnerRequest(),
            workflow
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.FailedNoMutation);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.WorkflowTimeout);
        result.Mutated.Should().BeFalse();
        (await ReadLifecycleAsync())
            .Should()
            .Be(new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false));
    }

    [Test]
    public async Task It_classifies_caller_cancellation_after_durable_mutation_as_incomplete_retryable()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking, cacheAheadRecoveryRequired: false);
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false),
            workflowTimeout: TimeSpan.FromSeconds(30)
        );
        TaskCompletionSource mutationCommitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cancellationSource = new();
        var workflow = new DelegatingWorkflow(
            preflight: static (context, _) => Task.FromResult(context.EligiblePreflightResult()),
            execute: async (context, cancellationToken) =>
            {
                await CommitLifecycleTransitionAsync(
                        context,
                        DocumentCacheAdministrativeCommandPhase.EnterResetting,
                        DocumentCacheLifecycleState.Tracking,
                        DocumentCacheLifecycleState.Resetting,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                mutationCommitted.SetResult();
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                return context.Completed();
            }
        );

        Task<DocumentCacheAdministrativeCommandResult> resultTask = runner.ExecuteAsync(
            RunnerRequest(),
            workflow,
            cancellationSource.Token
        );
        await mutationCommitted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await cancellationSource.CancelAsync().ConfigureAwait(false);
        DocumentCacheAdministrativeCommandResult result = await resultTask.ConfigureAwait(false);

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.IncompleteRetryable);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.CancellationAfterMutation);
        result.Mutated.Should().BeTrue();
        (await ReadLifecycleAsync())
            .Should()
            .Be(new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Resetting, false));
    }

    [Test]
    public async Task It_aborts_without_later_mutation_when_the_mutex_session_is_lost_after_mutation()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking, cacheAheadRecoveryRequired: false);
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false)
        );
        var workflow = new DelegatingWorkflow(
            preflight: static (context, _) => Task.FromResult(context.EligiblePreflightResult()),
            execute: async (context, cancellationToken) =>
            {
                await CommitLifecycleTransitionAsync(
                        context,
                        DocumentCacheAdministrativeCommandPhase.EnterResetting,
                        DocumentCacheLifecycleState.Tracking,
                        DocumentCacheLifecycleState.Resetting,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                await TerminateMutexSessionAsync(context.MutexLease.Connection, cancellationToken)
                    .ConfigureAwait(false);
                context.EnterPhase(DocumentCacheAdministrativeCommandPhase.EnterRebuilding);
                await context
                    .MutexLease.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                    .ConfigureAwait(false);

                return context.Completed();
            }
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(
            RunnerRequest(),
            workflow
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.IncompleteRetryable);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.SessionLossAfterMutation);
        result.Mutated.Should().BeTrue();
        result.Lifecycle.Should().Be(DocumentCacheLifecycleState.Resetting);
        (await ReadLifecycleAsync())
            .Should()
            .Be(new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Resetting, false));
    }

    [TestCase(StaleAdministrativeMutation.Lifecycle)]
    [TestCase(StaleAdministrativeMutation.ClearDocumentCache)]
    [TestCase(StaleAdministrativeMutation.ClearDocumentProjectionWork)]
    [TestCase(StaleAdministrativeMutation.SeedBaseline)]
    [TestCase(StaleAdministrativeMutation.Scrub)]
    public async Task It_fences_lost_session_mutations_after_a_replacement_owner_acquires_the_mutex(
        StaleAdministrativeMutation mutation
    )
    {
        StaleAdministrativeMutationExpectation expectation = await ArrangeStaleAdministrativeMutationAsync(
            mutation
        );
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false)
        );
        var workflow = new DelegatingWorkflow(
            preflight: static (context, _) => Task.FromResult(context.EligiblePreflightResult()),
            execute: async (context, cancellationToken) =>
            {
                await CommitLifecycleTransitionAsync(
                        context,
                        DocumentCacheAdministrativeCommandPhase.EnterResetting,
                        DocumentCacheLifecycleState.Tracking,
                        DocumentCacheLifecycleState.Resetting,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                await TerminateMutexSessionAsync(context.MutexLease.Connection, cancellationToken)
                    .ConfigureAwait(false);
                await CommitReplacementLifecycleTransitionAsync(
                        context,
                        DocumentCacheLifecycleState.Resetting,
                        DocumentCacheLifecycleState.Rebuilding,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                await AttemptStaleAdministrativeMutationAsync(
                        context,
                        mutation,
                        expectation.BoundaryDocumentId,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                return context.Completed();
            }
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(
            RunnerRequest(),
            workflow
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.IncompleteRetryable);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.SessionLossAfterMutation);
        result.Mutated.Should().BeTrue();
        result
            .PhaseDiagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.CurrentPhase == PhaseFor(mutation)
                && diagnostic.DiagnosticCategory == DocumentCacheAdministrativeDiagnosticCategory.SessionLoss
                && diagnostic.Retryable
            );
        (await ReadLifecycleAsync())
            .Should()
            .Be(new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Rebuilding, false));
        (await ReadCountAsync("DocumentCache")).Should().Be(expectation.DocumentCacheRows);
        (await ReadCountAsync("DocumentProjectionWork")).Should().Be(expectation.DocumentProjectionWorkRows);
    }

    [Test]
    public async Task It_reports_noncurrent_pinned_generation_diagnostics_for_a_running_command()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking, cacheAheadRecoveryRequired: false);
        DocumentCacheTargetExecutionContext firstGeneration = ExecutionContext(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false),
            generation: 1
        );
        DocumentCacheTargetExecutionContext replacementGeneration = ExecutionContext(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false),
            generation: 2
        );
        DocumentCacheProjectionObservationStore observationStore = new(new FixedTimeProvider(ObservedAt));
        observationStore.ObserveTarget(TargetHealth(firstGeneration));
        var registry = new MutableTargetRegistry(
            new DocumentCacheTargetRegistrySnapshot([EligibleObservation(firstGeneration)], ObservedAt),
            new DocumentCacheTargetRuntimeSnapshot([firstGeneration], ObservedAt)
        );
        TaskCompletionSource commandStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseCommand = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = new DelegatingWorkflow(
            preflight: static (context, _) => Task.FromResult(context.EligiblePreflightResult()),
            execute: async (context, cancellationToken) =>
            {
                context.EnterPhase(DocumentCacheAdministrativeCommandPhase.DrainWork);
                commandStarted.SetResult();
                await releaseCommand.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return context.Completed();
            }
        );
        var runner = new DocumentCacheAdministrativeCommandRunner(
            new StubProjectionSupervisor([RuntimeContext(firstGeneration, CreateWriter())]),
            registry,
            new PostgresqlDocumentCacheAdministrativeMutex(
                _dataSourceCache,
                NullLogger<PostgresqlDocumentCacheAdministrativeMutex>.Instance
            ),
            DocumentCacheAdministrativePrimitives.ForPostgresql(
                new PostgresqlDocumentCacheProviderCommandTimeoutClassifier()
            ),
            observationStore,
            new FixedTimeProvider(ObservedAt),
            new PostgresqlDocumentCacheProviderCommandTimeoutClassifier(),
            NullLogger<DocumentCacheAdministrativeCommandRunner>.Instance
        );

        Task<DocumentCacheAdministrativeCommandResult> resultTask = runner.ExecuteAsync(
            RunnerRequest(),
            workflow
        );
        await commandStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        registry.CurrentRuntimeSnapshot = new DocumentCacheTargetRuntimeSnapshot(
            [replacementGeneration],
            ObservedAt
        );
        observationStore.ObserveTarget(TargetHealth(replacementGeneration));

        DocumentCacheAdministrativeCommandObservationSnapshot activeCommand = observationStore
            .CurrentSnapshot.ActiveAdministrativeCommands.Values.Should()
            .ContainSingle()
            .Subject;
        activeCommand.IsCurrentGeneration.Should().BeFalse();
        activeCommand.CurrentTargetGeneration!.Value.Should().Be(2);

        releaseCommand.SetResult();
        DocumentCacheAdministrativeCommandResult result = await resultTask.ConfigureAwait(false);

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        result
            .PhaseDiagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.DiagnosticCategory == DocumentCacheAdministrativeDiagnosticCategory.TargetReplaced
            );
    }

    [Test]
    public async Task It_enforces_operation_specific_command_boundaries()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Disabled, cacheAheadRecoveryRequired: false);
        DocumentCacheGuardedNewEmptyActivationCommand guardedActivation = CreateGuardedActivationCommand(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Disabled, false)
        );

        DocumentCacheAdministrativeCommandResult activationResult = await guardedActivation.ExecuteAsync(
            new DocumentCacheGuardedNewEmptyActivationRequest(
                AdministrativeTargetKey,
                Fingerprint,
                DocumentCacheAdministrativeCommandConfirmation.NewEmptyActivation
            )
        );

        activationResult.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        activationResult.Lifecycle.Should().Be(DocumentCacheLifecycleState.Tracking);

        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking, cacheAheadRecoveryRequired: true);
        IReadOnlyList<SourceDocument> latchedSources = await InsertProjectedRowsAsync(documentCount: 2);
        DocumentCacheOnlineCacheRebuildCommand onlineRebuild = CreateOnlineCacheRebuildCommand(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, true)
        );

        DocumentCacheAdministrativeCommandResult rebuildResult = await onlineRebuild.ExecuteAsync(
            new DocumentCacheOnlineCacheRebuildRequest(
                AdministrativeTargetKey,
                Fingerprint,
                DocumentCacheAdministrativeCommandConfirmation.OnlineCacheRebuild
            )
        );

        rebuildResult.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation);
        rebuildResult
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.CacheAheadLatchSet);
        (await ReadLifecycleAsync())
            .Should()
            .Be(new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, true));
        (await ReadCountAsync("DocumentCache")).Should().Be(latchedSources.Count);
        (await ReadCountAsync("DocumentProjectionWork")).Should().Be(latchedSources.Count);

        await SetLifecycleAsync(DocumentCacheLifecycleState.Disabled, cacheAheadRecoveryRequired: false);
        DocumentCacheOfflineActivationCommand offlineActivation = CreateOfflineActivationCommand(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Disabled, false),
            DocumentCacheDownstreamPublicationStatus.Active
        );

        DocumentCacheAdministrativeCommandResult offlineActivationResult =
            await offlineActivation.ExecuteAsync(
                new DocumentCacheOfflineActivationRequest(
                    AdministrativeTargetKey,
                    OfflineActivationAdmission,
                    Fingerprint,
                    DocumentCacheAdministrativeCommandConfirmation.OfflineActivation
                )
            );

        offlineActivationResult
            .Status.Should()
            .Be(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation);
        offlineActivationResult
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.DownstreamHistoryPresentOrUnknown);
        DocumentCacheAdministrativeCommandResult invalidOfflineActivationAdmission =
            await offlineActivation.ExecuteAsync(
                new DocumentCacheOfflineActivationRequest(
                    AdministrativeTargetKey,
                    OfflineDeactivationAdmission,
                    Fingerprint,
                    DocumentCacheAdministrativeCommandConfirmation.OfflineActivation
                )
            );

        invalidOfflineActivationAdmission
            .Status.Should()
            .Be(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation);
        invalidOfflineActivationAdmission
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.MismatchedOfflineWriterAdmission);

        await SetLifecycleAsync(DocumentCacheLifecycleState.Resetting, cacheAheadRecoveryRequired: false);
        await InsertProjectedRowsAsync(documentCount: 2);
        DocumentCacheOfflineDeactivationCommand offlineDeactivation = CreateOfflineDeactivationCommand(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Resetting, false),
            DocumentCacheDownstreamPublicationStatus.InternalOnly
        );

        DocumentCacheAdministrativeCommandResult offlineDeactivationResult =
            await offlineDeactivation.ExecuteAsync(
                new DocumentCacheOfflineDeactivationRequest(
                    AdministrativeTargetKey,
                    OfflineDeactivationAdmission,
                    Fingerprint,
                    DocumentCacheAdministrativeCommandConfirmation.OfflineDeactivation
                )
            );

        offlineDeactivationResult.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        offlineDeactivationResult.Lifecycle.Should().Be(DocumentCacheLifecycleState.Disabled);
        (await ReadCountAsync("DocumentCache")).Should().Be(0);
        (await ReadCountAsync("DocumentProjectionWork")).Should().Be(0);

        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking, cacheAheadRecoveryRequired: true);
        await InsertProjectedRowsAsync(documentCount: 2);
        DocumentCacheInternalOnlyCacheAheadRecoveryCommand recovery = CreateCacheAheadRecoveryCommand(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, true),
            DocumentCacheDownstreamPublicationStatus.Possible
        );

        DocumentCacheAdministrativeCommandResult recoveryResult = await recovery.ExecuteAsync(
            new DocumentCacheInternalOnlyCacheAheadRecoveryRequest(
                AdministrativeTargetKey,
                CacheAheadRecoveryAdmission,
                Fingerprint,
                DocumentCacheAdministrativeCommandConfirmation.InternalCacheAheadRecovery
            )
        );

        recoveryResult.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation);
        recoveryResult
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.DownstreamHistoryPresentOrUnknown);
        recoveryResult.CacheAheadRecoveryRequired.Should().BeTrue();
        (await ReadCountAsync("DocumentCache")).Should().Be(2);
        (await ReadCountAsync("DocumentProjectionWork")).Should().Be(2);
        DocumentCacheAdministrativeCommandResult invalidRecoveryAdmission = await recovery.ExecuteAsync(
            new DocumentCacheInternalOnlyCacheAheadRecoveryRequest(
                AdministrativeTargetKey,
                OfflineActivationAdmission,
                Fingerprint,
                DocumentCacheAdministrativeCommandConfirmation.InternalCacheAheadRecovery
            )
        );

        invalidRecoveryAdmission
            .Status.Should()
            .Be(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation);
        invalidRecoveryAdmission
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.MismatchedOfflineWriterAdmission);
        (await ReadCountAsync("DocumentCache")).Should().Be(2);
        (await ReadCountAsync("DocumentProjectionWork")).Should().Be(2);

        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking, cacheAheadRecoveryRequired: false);
        SourceDocument cacheAhead = await InsertDocumentAsync(contentVersion: 10);
        await ClearProjectionWorkAsync();
        await InsertProjectionWorkAsync(cacheAhead, requiredContentVersion: 5);
        await InsertCacheRowAsync(cacheAhead, cacheContentVersion: 11);
        DocumentCacheExplicitIntegrityScrubCommand scrub = CreateScrubCommand(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false)
        );

        DocumentCacheAdministrativeCommandResult scrubResult = await scrub.ExecuteAsync(
            new DocumentCacheExplicitIntegrityScrubRequest(
                AdministrativeTargetKey,
                Fingerprint,
                DocumentCacheAdministrativeCommandConfirmation.IntegrityScrub
            )
        );

        scrubResult.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        scrubResult.CacheAheadRecoveryRequired.Should().BeTrue();
        (await ReadLifecycleAsync())
            .Should()
            .Be(new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, true));

        DocumentCacheAdministrativeCommandResult latchedScrubResult = await CreateScrubCommand(
                new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, true)
            )
            .ExecuteAsync(
                new DocumentCacheExplicitIntegrityScrubRequest(
                    AdministrativeTargetKey,
                    Fingerprint,
                    DocumentCacheAdministrativeCommandConfirmation.IntegrityScrub
                )
            );

        latchedScrubResult.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation);
        latchedScrubResult.CacheAheadRecoveryRequired.Should().BeTrue();
    }

    private DocumentCacheAdministrativeCommandRunner CreateRunner(
        DocumentCacheLifecycleObservation lifecycle,
        TimeSpan? workflowTimeout = null,
        int projectorBaselineHighWaterMark = 1000
    )
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(
            lifecycle,
            workflowTimeout: workflowTimeout,
            projectorBaselineHighWaterMark: projectorBaselineHighWaterMark
        );
        return CreateRunner(executionContext, new RecordingObservationSink());
    }

    private DocumentCacheAdministrativeCommandRunner CreateRunner(
        DocumentCacheTargetExecutionContext executionContext,
        IDocumentCacheProjectionObservationSink observationSink
    ) =>
        new(
            new StubProjectionSupervisor([RuntimeContext(executionContext, CreateWriter())]),
            new MutableTargetRegistry(
                new DocumentCacheTargetRegistrySnapshot([EligibleObservation(executionContext)], ObservedAt),
                new DocumentCacheTargetRuntimeSnapshot([executionContext], ObservedAt)
            ),
            new PostgresqlDocumentCacheAdministrativeMutex(
                _dataSourceCache,
                NullLogger<PostgresqlDocumentCacheAdministrativeMutex>.Instance
            ),
            DocumentCacheAdministrativePrimitives.ForPostgresql(
                new PostgresqlDocumentCacheProviderCommandTimeoutClassifier()
            ),
            observationSink,
            new FixedTimeProvider(ObservedAt),
            new PostgresqlDocumentCacheProviderCommandTimeoutClassifier(),
            NullLogger<DocumentCacheAdministrativeCommandRunner>.Instance
        );

    private DocumentCacheGuardedNewEmptyActivationCommand CreateGuardedActivationCommand(
        DocumentCacheLifecycleObservation lifecycle
    ) => new(CreateRunner(lifecycle));

    private DocumentCacheOfflineActivationCommand CreateOfflineActivationCommand(
        DocumentCacheLifecycleObservation lifecycle,
        DocumentCacheDownstreamPublicationStatus downstreamPublicationStatus
    ) =>
        CreateBaselineDrainCommand(
            lifecycle,
            downstreamPublicationStatus,
            static (runner, downstreamPublicationHistoryProvider, baselineSeeder, drainer) =>
                new DocumentCacheOfflineActivationCommand(
                    runner,
                    downstreamPublicationHistoryProvider,
                    baselineSeeder,
                    drainer
                )
        );

    private DocumentCacheOfflineDeactivationCommand CreateOfflineDeactivationCommand(
        DocumentCacheLifecycleObservation lifecycle,
        DocumentCacheDownstreamPublicationStatus downstreamPublicationStatus
    ) =>
        new(
            CreateRunner(lifecycle),
            new FixedDownstreamPublicationHistoryProvider(downstreamPublicationStatus)
        );

    private DocumentCacheOnlineCacheRebuildCommand CreateOnlineCacheRebuildCommand(
        DocumentCacheLifecycleObservation lifecycle,
        TimeSpan? workflowTimeout = null,
        int projectorBaselineHighWaterMark = 1000,
        IDocumentCacheBaselineSeedDelay? baselineSeedDelay = null,
        IDocumentCacheAdministrativeDrainer? baselineSeedDrainer = null
    )
    {
        DocumentCacheAdministrativeDrainer drainer = CreateDrainer(new RecordingObservationSink());
        return new(
            CreateRunner(lifecycle, workflowTimeout, projectorBaselineHighWaterMark),
            CreateBaselineSeeder(baselineSeedDelay, baselineSeedDrainer ?? drainer),
            drainer
        );
    }

    private DocumentCacheInternalOnlyCacheAheadRecoveryCommand CreateCacheAheadRecoveryCommand(
        DocumentCacheLifecycleObservation lifecycle,
        DocumentCacheDownstreamPublicationStatus downstreamPublicationStatus
    ) =>
        CreateBaselineDrainCommand(
            lifecycle,
            downstreamPublicationStatus,
            static (runner, downstreamPublicationHistoryProvider, baselineSeeder, drainer) =>
                new DocumentCacheInternalOnlyCacheAheadRecoveryCommand(
                    runner,
                    downstreamPublicationHistoryProvider,
                    baselineSeeder,
                    drainer
                )
        );

    private TCommand CreateBaselineDrainCommand<TCommand>(
        DocumentCacheLifecycleObservation lifecycle,
        DocumentCacheDownstreamPublicationStatus downstreamPublicationStatus,
        Func<
            DocumentCacheAdministrativeCommandRunner,
            IDocumentCacheDownstreamPublicationHistoryProvider,
            IDocumentCacheBaselineSeeder,
            IDocumentCacheAdministrativeDrainer,
            TCommand
        > createCommand
    )
    {
        DocumentCacheAdministrativeDrainer drainer = CreateDrainer(new RecordingObservationSink());
        return createCommand(
            CreateRunner(lifecycle),
            new FixedDownstreamPublicationHistoryProvider(downstreamPublicationStatus),
            CreateBaselineSeeder(drainer: drainer),
            drainer
        );
    }

    private DocumentCacheExplicitIntegrityScrubCommand CreateScrubCommand(
        DocumentCacheLifecycleObservation lifecycle
    ) => new(CreateRunner(lifecycle));

    private PostgresqlDocumentCacheWriter CreateWriter() =>
        new(
            _dataSourceCache,
            new DocumentCacheWriterRetryAdapter(
                new DeadlockRetrySettings
                {
                    MaxRetryAttempts = 0,
                    BaseDelayMilliseconds = 1,
                    UseJitter = false,
                },
                new PostgresqlRelationalWriteExceptionClassifier(),
                NullLogger<DocumentCacheWriterRetryAdapter>.Instance
            ),
            NullLogger<PostgresqlDocumentCacheWriter>.Instance,
            new PostgresqlDocumentCacheProviderCommandTimeoutClassifier()
        );

    private static DocumentCacheBaselineSeeder CreateBaselineSeeder(
        IDocumentCacheBaselineSeedDelay? delay = null,
        IDocumentCacheAdministrativeDrainer? drainer = null
    ) =>
        new(
            delay ?? new DocumentCacheBaselineSeedDelay(),
            new FixedTimeProvider(ObservedAt),
            NullLogger<DocumentCacheBaselineSeeder>.Instance,
            drainer
        );

    private DocumentCacheAdministrativeDrainer CreateDrainer(
        IDocumentCacheProjectionObservationSink observationSink
    )
    {
        DocumentCacheOptions options = new()
        {
            Projector = new DocumentCacheProjectorOptions { MaxConcurrentTargets = 1, PageSize = 3 },
        };
        var scheduler = new DocumentCacheProjectionScheduler(
            Options.Create(options),
            new DocumentCacheProjectionDrainPageProcessor(
                new PostgresqlDocumentProjectionWorkPager(
                    _dataSourceCache,
                    NullLogger<PostgresqlDocumentProjectionWorkPager>.Instance
                ),
                new DocumentCacheProjectionItemProcessor(
                    new FixedTimeProvider(ObservedAt),
                    NullLogger<DocumentCacheProjectionItemProcessor>.Instance
                ),
                NullLogger<DocumentCacheProjectionDrainPageProcessor>.Instance,
                new FixedTimeProvider(ObservedAt)
            ),
            observationSink,
            new FixedTimeProvider(ObservedAt),
            NullLogger<DocumentCacheProjectionScheduler>.Instance
        );

        return new(
            scheduler,
            new DocumentCacheAdministrativeDrainDelay(),
            new FixedTimeProvider(ObservedAt),
            NullLogger<DocumentCacheAdministrativeDrainer>.Instance
        );
    }

    private DocumentCacheTargetExecutionContext ExecutionContext(
        DocumentCacheLifecycleObservation lifecycle,
        long generation = 1,
        TimeSpan? workflowTimeout = null,
        int projectorBaselineHighWaterMark = 1000
    ) =>
        new(
            TargetKey,
            new DocumentCacheTargetContextGeneration(generation),
            EffectiveSettings(workflowTimeout ?? TimeSpan.FromSeconds(30), projectorBaselineHighWaterMark),
            new DocumentCacheTargetDataStoreMetadata(TargetKey.DataStoreId, "postgresql"),
            new DocumentCacheTargetConnectionInput(
                RelationalProviderToken.Postgresql,
                _database.ConnectionString
            ),
            Fingerprint,
            lifecycle,
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

    private static DocumentCacheTargetEffectiveSettings EffectiveSettings(
        TimeSpan workflowTimeout,
        int projectorBaselineHighWaterMark = 1000
    ) =>
        new(
            readAccelerationEnabled: true,
            directFillTimeout: TimeSpan.FromMilliseconds(250),
            projectorPollInterval: TimeSpan.FromMilliseconds(10),
            projectorPageSize: 3,
            projectorMaxConcurrentTargets: 1,
            projectorFailureBackoff: TimeSpan.FromSeconds(1),
            projectorBaselineHighWaterMark: projectorBaselineHighWaterMark,
            administrationWorkflowTimeout: workflowTimeout
        );

    private static DocumentCacheTargetObservation EligibleObservation(
        DocumentCacheTargetExecutionContext executionContext
    ) =>
        DocumentCacheTargetObservation.ResolvedEligible(
            executionContext.TargetKey,
            executionContext.EffectiveSettings,
            executionContext.Generation,
            executionContext.ProviderToken,
            executionContext.PhysicalSourceFingerprint,
            executionContext.Lifecycle,
            executionContext.Inventory,
            executionContext.EnqueueTrigger,
            executionContext.SqlServerPrerequisites
        );

    private DocumentCacheProjectionTargetRuntimeContext RuntimeContext(
        DocumentCacheTargetExecutionContext executionContext,
        PostgresqlDocumentCacheWriter writer
    ) =>
        new(
            executionContext,
            new DocumentCacheProjectionTargetProviderAdapters(
                RelationalProviderToken.Postgresql,
                new DocumentCacheMaterializationTargetContext(
                    new DocumentCacheProjectionTargetKey(
                        TargetKey.TenantKey,
                        new DataStoreId(TargetKey.DataStoreId)
                    ),
                    _fixture.MappingSet,
                    DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated,
                    _database.ConnectionString
                ),
                new CandidateMaterializer(_fixture.MappingSet, _sourcesByDocumentId),
                writer
            ),
            new DocumentCacheProjectionObservationStore(new FixedTimeProvider(ObservedAt)),
            writer
        );

    private static DocumentCacheProjectionTargetHealthSnapshot TargetHealth(
        DocumentCacheTargetExecutionContext executionContext
    ) =>
        new(
            executionContext.TargetKey,
            executionContext.Generation,
            executionContext.EffectiveSettings.ProjectorPageSize,
            ObservedAt,
            executionContext.ProviderToken,
            executionContext.PhysicalSourceFingerprint
        );

    private static DocumentCacheAdministrativeCommandRunnerRequest RunnerRequest() =>
        new(
            DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
            AdministrativeTargetKey,
            expectedPhysicalSourceFingerprint: Fingerprint,
            confirmation: DocumentCacheAdministrativeCommandConfirmation.OnlineCacheRebuild
        );

    private static async Task CommitLifecycleTransitionAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        DocumentCacheAdministrativeCommandPhase phase,
        DocumentCacheLifecycleState expectedLifecycle,
        DocumentCacheLifecycleState nextLifecycle,
        CancellationToken cancellationToken
    )
    {
        context.EnterPhase(phase);

        await using IRelationalWriteSession session = await context
            .MutexLease.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            DocumentCacheAdministrativeLifecycleTransitionResult transition = await context
                .Primitives.TryTransitionLifecycleAsync(
                    session,
                    new DocumentCacheAdministrativeLifecycleTransitionRequest(
                        expectedLifecycle,
                        expectedCacheAheadRecoveryRequired: false,
                        nextLifecycle,
                        nextCacheAheadRecoveryRequired: false
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
            transition.Mutated.Should().BeTrue();

            context.MarkMutated(transition.LifecycleReadResult.Lifecycle);
            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            context.CompletePhase(phase);
        }
        catch
        {
            await session.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task CommitReplacementLifecycleTransitionAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        DocumentCacheLifecycleState expectedLifecycle,
        DocumentCacheLifecycleState nextLifecycle,
        CancellationToken cancellationToken
    )
    {
        var replacementMutex = new PostgresqlDocumentCacheAdministrativeMutex(
            _dataSourceCache,
            NullLogger<PostgresqlDocumentCacheAdministrativeMutex>.Instance
        );
        await using IDocumentCacheAdministrativeMutexLease replacementLease = await replacementMutex
            .AcquireAsync(context.TargetContext.TargetExecutionContext.ConnectionInput, cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);
        await using IRelationalWriteSession replacementSession = await replacementLease
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        DocumentCacheAdministrativeLifecycleTransitionResult transition = await context
            .Primitives.TryTransitionLifecycleAsync(
                replacementSession,
                new DocumentCacheAdministrativeLifecycleTransitionRequest(
                    expectedLifecycle,
                    expectedCacheAheadRecoveryRequired: false,
                    nextLifecycle,
                    nextCacheAheadRecoveryRequired: false
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        transition.Mutated.Should().BeTrue();

        await replacementSession.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task TerminateMutexSessionAsync(
        DbConnection connection,
        CancellationToken cancellationToken
    )
    {
        int backendPid = await ExecuteConnectionScalarAsync<int>(
                connection,
                "SELECT pg_backend_pid();",
                cancellationToken
            )
            .ConfigureAwait(false);
        bool terminated = await _database
            .ExecuteScalarAsync<bool>(
                "SELECT pg_terminate_backend(@backendPid);",
                new NpgsqlParameter("backendPid", NpgsqlDbType.Integer) { Value = backendPid }
            )
            .ConfigureAwait(false);

        terminated.Should().BeTrue();
    }

    private async Task<StaleAdministrativeMutationExpectation> ArrangeStaleAdministrativeMutationAsync(
        StaleAdministrativeMutation mutation
    )
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Disabled, cacheAheadRecoveryRequired: false);
        SourceDocument source = await InsertDocumentAsync(contentVersion: 10);
        await ClearDocumentCacheAsync();
        await ClearProjectionWorkAsync();
        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking, cacheAheadRecoveryRequired: false);

        switch (mutation)
        {
            case StaleAdministrativeMutation.ClearDocumentCache:
                await InsertCacheRowAsync(source, cacheContentVersion: source.ContentVersion);
                return new(source.DocumentId, DocumentCacheRows: 1, DocumentProjectionWorkRows: 0);
            case StaleAdministrativeMutation.ClearDocumentProjectionWork:
                await InsertProjectionWorkAsync(source, requiredContentVersion: source.ContentVersion);
                return new(source.DocumentId, DocumentCacheRows: 0, DocumentProjectionWorkRows: 1);
            case StaleAdministrativeMutation.Lifecycle:
            case StaleAdministrativeMutation.SeedBaseline:
            case StaleAdministrativeMutation.Scrub:
                return new(source.DocumentId, DocumentCacheRows: 0, DocumentProjectionWorkRows: 0);
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unsupported mutation.");
        }
    }

    private static async Task AttemptStaleAdministrativeMutationAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        StaleAdministrativeMutation mutation,
        long boundaryDocumentId,
        CancellationToken cancellationToken
    )
    {
        context.EnterPhase(PhaseFor(mutation));

        await using IRelationalWriteSession staleSession = await context
            .MutexLease.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        switch (mutation)
        {
            case StaleAdministrativeMutation.Lifecycle:
                await context
                    .Primitives.TryTransitionLifecycleAsync(
                        staleSession,
                        new DocumentCacheAdministrativeLifecycleTransitionRequest(
                            DocumentCacheLifecycleState.Rebuilding,
                            expectedCacheAheadRecoveryRequired: false,
                            DocumentCacheLifecycleState.Tracking,
                            nextCacheAheadRecoveryRequired: false
                        ),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                break;
            case StaleAdministrativeMutation.ClearDocumentCache:
                await context
                    .Primitives.ClearDocumentCacheBatchAsync(
                        staleSession,
                        new DocumentCacheAdministrativeClearBatchRequest(pageSize: 1),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                break;
            case StaleAdministrativeMutation.ClearDocumentProjectionWork:
                await context
                    .Primitives.ClearDocumentProjectionWorkBatchAsync(
                        staleSession,
                        new DocumentCacheAdministrativeClearBatchRequest(pageSize: 1),
                        StaleClearance(),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                break;
            case StaleAdministrativeMutation.SeedBaseline:
                await context
                    .Primitives.SeedBaselinePageAsync(
                        staleSession,
                        new DocumentCacheAdministrativeBaselineSeedPageRequest(
                            boundaryDocumentId,
                            afterDocumentId: 0,
                            pageSize: 1
                        ),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                break;
            case StaleAdministrativeMutation.Scrub:
                await context
                    .Primitives.ScrubPageAsync(
                        staleSession,
                        new DocumentCacheAdministrativeScrubPageRequest(
                            boundaryDocumentId,
                            afterDocumentId: 0,
                            pageSize: 1
                        ),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unsupported mutation.");
        }

        await staleSession.CommitAsync(cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException("Stale administrative mutation unexpectedly committed.");
    }

    private static DocumentCacheAdministrativeCommandPhase PhaseFor(StaleAdministrativeMutation mutation) =>
        mutation switch
        {
            StaleAdministrativeMutation.Lifecycle => DocumentCacheAdministrativeCommandPhase.EnterTracking,
            StaleAdministrativeMutation.ClearDocumentCache =>
                DocumentCacheAdministrativeCommandPhase.ClearCache,
            StaleAdministrativeMutation.ClearDocumentProjectionWork =>
                DocumentCacheAdministrativeCommandPhase.ClearWork,
            StaleAdministrativeMutation.SeedBaseline => DocumentCacheAdministrativeCommandPhase.SeedBaseline,
            StaleAdministrativeMutation.Scrub => DocumentCacheAdministrativeCommandPhase.ScrubScan,
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unsupported mutation."),
        };

    private static DocumentCacheAdministrativeWorkClearance StaleClearance() =>
        DocumentCacheAdministrativeWorkClearance.Require(
            DocumentCacheAdministrativeCommand.InternalOnlyCacheAheadRecovery,
            DocumentCacheDownstreamPublicationStatus.InternalOnly,
            DocumentCacheOfflineWriterAdmissionConfirmation.InternalOnlyCacheAheadRecoveryWritersClosedAndDrained
        );

    private static async Task<T> ExecuteConnectionScalarAsync<T>(
        DbConnection connection,
        string commandText,
        CancellationToken cancellationToken
    )
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return (T)Convert.ChangeType(result!, typeof(T));
    }

    private async Task<IReadOnlyList<SourceDocument>> InsertProjectedRowsAsync(int documentCount)
    {
        await ClearDocumentCacheAsync();
        await ClearProjectionWorkAsync();

        List<SourceDocument> sources = [];
        for (var index = 0; index < documentCount; index++)
        {
            SourceDocument source = await InsertDocumentAsync(contentVersion: 10 + index);
            sources.Add(source);
            await InsertProjectionWorkAsync(source, requiredContentVersion: source.ContentVersion);
            await InsertCacheRowAsync(source, cacheContentVersion: source.ContentVersion);
        }

        return sources;
    }

    private async Task<SourceDocument> InsertDocumentAsync(long contentVersion)
    {
        short resourceKeyId = _fixture.MappingSet.ResourceKeyIdByResource[PersonResource];
        Guid documentUuid = Guid.NewGuid();
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await _database.QueryRowsAsync(
            """
            INSERT INTO "dms"."Document" (
                "DocumentUuid",
                "ResourceKeyId",
                "ContentVersion",
                "ContentLastModifiedAt"
            )
            VALUES (
                @documentUuid,
                @resourceKeyId,
                @contentVersion,
                @lastModifiedAt
            )
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", NpgsqlDbType.Uuid) { Value = documentUuid },
            new NpgsqlParameter("resourceKeyId", NpgsqlDbType.Smallint) { Value = resourceKeyId },
            new NpgsqlParameter("contentVersion", NpgsqlDbType.Bigint) { Value = contentVersion },
            new NpgsqlParameter("lastModifiedAt", NpgsqlDbType.TimestampTz) { Value = ObservedAt }
        );

        var source = new SourceDocument(
            Convert.ToInt64(rows.Single()["DocumentId"]),
            documentUuid,
            contentVersion
        );
        _sourcesByDocumentId[source.DocumentId] = source;
        return source;
    }

    private Task SetLifecycleAsync(DocumentCacheLifecycleState lifecycle, bool cacheAheadRecoveryRequired) =>
        _database.ExecuteNonQueryAsync(
            """
            UPDATE "dms"."DocumentCacheState"
            SET
                "ProjectionLifecycleState" = @lifecycle,
                "CacheAheadRecoveryRequired" = @cacheAheadRecoveryRequired
            WHERE "StateId" = 1;
            """,
            new NpgsqlParameter("lifecycle", NpgsqlDbType.Varchar) { Value = lifecycle.ToString() },
            new NpgsqlParameter("cacheAheadRecoveryRequired", NpgsqlDbType.Boolean)
            {
                Value = cacheAheadRecoveryRequired,
            }
        );

    private Task ClearDocumentCacheAsync() =>
        _database.ExecuteNonQueryAsync("""DELETE FROM "dms"."DocumentCache";""");

    private Task ClearProjectionWorkAsync() =>
        _database.ExecuteNonQueryAsync("""DELETE FROM "dms"."DocumentProjectionWork";""");

    private Task InsertProjectionWorkAsync(SourceDocument source, long requiredContentVersion) =>
        _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."DocumentProjectionWork" (
                "DocumentId",
                "RequiredContentVersion",
                "FirstEnqueuedAt",
                "LastEnqueuedAt"
            )
            VALUES (
                @documentId,
                @requiredContentVersion,
                @firstEnqueuedAt,
                @lastEnqueuedAt
            )
            ON CONFLICT ("DocumentId") DO UPDATE
            SET "RequiredContentVersion" = EXCLUDED."RequiredContentVersion",
                "LastEnqueuedAt" = EXCLUDED."LastEnqueuedAt";
            """,
            new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = source.DocumentId },
            new NpgsqlParameter("requiredContentVersion", NpgsqlDbType.Bigint)
            {
                Value = requiredContentVersion,
            },
            new NpgsqlParameter("firstEnqueuedAt", NpgsqlDbType.TimestampTz) { Value = FirstEnqueuedAt },
            new NpgsqlParameter("lastEnqueuedAt", NpgsqlDbType.TimestampTz) { Value = ObservedAt }
        );

    private async Task InsertCacheRowAsync(SourceDocument source, long cacheContentVersion)
    {
        ResourceKeyEntry resourceKey = _fixture.MappingSet.ResourceKeyById[
            _fixture.MappingSet.ResourceKeyIdByResource[PersonResource]
        ];

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."DocumentCache" (
                "DocumentId",
                "DocumentUuid",
                "ProjectName",
                "ResourceName",
                "ResourceVersion",
                "ContentVersion",
                "StreamEtag",
                "LastModifiedAt",
                "DocumentJson",
                "ComputedAt"
            )
            VALUES (
                @documentId,
                @documentUuid,
                @projectName,
                @resourceName,
                @resourceVersion,
                @contentVersion,
                @streamEtag,
                @lastModifiedAt,
                @documentJson,
                @computedAt
            )
            ON CONFLICT ("DocumentId") DO UPDATE
            SET "ContentVersion" = EXCLUDED."ContentVersion",
                "StreamEtag" = EXCLUDED."StreamEtag",
                "DocumentJson" = EXCLUDED."DocumentJson",
                "ComputedAt" = EXCLUDED."ComputedAt";
            """,
            new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = source.DocumentId },
            new NpgsqlParameter("documentUuid", NpgsqlDbType.Uuid) { Value = source.DocumentUuid },
            new NpgsqlParameter("projectName", NpgsqlDbType.Varchar)
            {
                Value = resourceKey.Resource.ProjectName,
            },
            new NpgsqlParameter("resourceName", NpgsqlDbType.Varchar)
            {
                Value = resourceKey.Resource.ResourceName,
            },
            new NpgsqlParameter("resourceVersion", NpgsqlDbType.Varchar)
            {
                Value = resourceKey.ResourceVersion,
            },
            new NpgsqlParameter("contentVersion", NpgsqlDbType.Bigint) { Value = cacheContentVersion },
            new NpgsqlParameter("streamEtag", NpgsqlDbType.Varchar) { Value = $"etag-{cacheContentVersion}" },
            new NpgsqlParameter("lastModifiedAt", NpgsqlDbType.TimestampTz) { Value = ObservedAt },
            new NpgsqlParameter("documentJson", NpgsqlDbType.Jsonb)
            {
                Value = new JsonObject { ["value"] = $"cache-{source.DocumentId}" }.ToJsonString(),
            },
            new NpgsqlParameter("computedAt", NpgsqlDbType.TimestampTz) { Value = ObservedAt.AddMinutes(1) }
        );
    }

    private async Task<DocumentCacheLifecycleObservation> ReadLifecycleAsync()
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await _database.QueryRowsAsync(
            """
            SELECT "ProjectionLifecycleState", "CacheAheadRecoveryRequired"
            FROM "dms"."DocumentCacheState"
            WHERE "StateId" = 1;
            """
        );

        IReadOnlyDictionary<string, object?> row = rows.Single();
        return new(
            Enum.Parse<DocumentCacheLifecycleState>((string)row["ProjectionLifecycleState"]!),
            Convert.ToBoolean(row["CacheAheadRecoveryRequired"])
        );
    }

    private Task<long> ReadCountAsync(string tableName) =>
        _database.ExecuteScalarAsync<long>($$"""SELECT COUNT(*) FROM "dms"."{{tableName}}";""");

    public enum StaleAdministrativeMutation
    {
        Lifecycle,
        ClearDocumentCache,
        ClearDocumentProjectionWork,
        SeedBaseline,
        Scrub,
    }

    private sealed record StaleAdministrativeMutationExpectation(
        long BoundaryDocumentId,
        long DocumentCacheRows,
        long DocumentProjectionWorkRows
    );

    private sealed class DelegatingWorkflow(
        Func<
            DocumentCacheAdministrativeCommandExecutionContext,
            CancellationToken,
            Task<DocumentCacheAdministrativeCommandResult>
        > preflight,
        Func<
            DocumentCacheAdministrativeCommandExecutionContext,
            CancellationToken,
            Task<DocumentCacheAdministrativeCommandResult>
        > execute
    ) : IDocumentCacheAdministrativeCommandWorkflow
    {
        public Task<DocumentCacheAdministrativeCommandResult> RunPreflightAsync(
            DocumentCacheAdministrativeCommandExecutionContext context,
            CancellationToken cancellationToken
        ) => preflight(context, cancellationToken);

        public Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
            DocumentCacheAdministrativeCommandExecutionContext context,
            CancellationToken cancellationToken
        ) => execute(context, cancellationToken);
    }

    private sealed class CancellationOnlyBaselineSeedDelay : IDocumentCacheBaselineSeedDelay
    {
        public Task DelayAsync(
            TimeSpan delay,
            TimeProvider timeProvider,
            CancellationToken cancellationToken
        ) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class NoBackpressureReliefDrainer : IDocumentCacheAdministrativeDrainer
    {
        public static NoBackpressureReliefDrainer Instance { get; } = new();

        public Task<DocumentCacheAdministrativeDrainSliceResult> DrainBackpressureReliefSliceAsync(
            DocumentCacheAdministrativeCommandExecutionContext context,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                DocumentCacheAdministrativeDrainSliceResult.Succeeded(
                    DocumentCacheProjectionDrainPageResult.NoEligibleWork
                )
            );
        }

        public Task<DocumentCacheAdministrativeDrainToEmptyResult> DrainToEmptyAsync(
            DocumentCacheAdministrativeCommandExecutionContext context,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }

    private sealed class StubProjectionSupervisor(
        IEnumerable<DocumentCacheProjectionTargetRuntimeContext> contexts
    ) : IDocumentCacheProjectionSupervisor
    {
        public ImmutableArray<DocumentCacheProjectionTargetRuntimeContext> CurrentTargetContexts { get; } =
            contexts.ToImmutableArray();

        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }

    private sealed class MutableTargetRegistry(
        DocumentCacheTargetRegistrySnapshot currentSnapshot,
        DocumentCacheTargetRuntimeSnapshot currentRuntimeSnapshot
    ) : IDocumentCacheTargetRegistry
    {
        public DocumentCacheTargetRegistrySnapshot CurrentSnapshot { get; } = currentSnapshot;

        public DocumentCacheTargetRuntimeSnapshot CurrentRuntimeSnapshot { get; set; } =
            currentRuntimeSnapshot;

        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(CurrentSnapshot);
    }

    private sealed class RecordingObservationSink : IDocumentCacheProjectionObservationSink
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

    private sealed class FixedDownstreamPublicationHistoryProvider(
        DocumentCacheDownstreamPublicationStatus status
    ) : IDocumentCacheDownstreamPublicationHistoryProvider
    {
        public Task<DocumentCacheDownstreamPublicationHistoryObservation> ObserveAsync(
            DocumentCacheTargetKey targetKey,
            DocumentCachePhysicalSourceFingerprint? currentPhysicalSourceFingerprint,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(
                new DocumentCacheDownstreamPublicationHistoryObservation(
                    targetKey,
                    currentPhysicalSourceFingerprint,
                    status,
                    evidenceSource: "postgresql-document-cache-administration-test",
                    evidenceGenerationIdentifier: null,
                    ObservedAt,
                    "PostgreSQL DocumentCache administration test downstream publication proof."
                )
            );
        }
    }

    private sealed class CandidateMaterializer(
        MappingSet mappingSet,
        IReadOnlyDictionary<long, SourceDocument> sourcesByDocumentId
    ) : IDocumentCacheMaterializer
    {
        public Task<DocumentCacheMaterializationResult> MaterializeAsync(
            DocumentCacheMaterializationRequest request
        )
        {
            SourceDocument source = sourcesByDocumentId[request.DocumentId];
            ResourceKeyEntry resourceKey = mappingSet.ResourceKeyById[
                mappingSet.ResourceKeyIdByResource[PersonResource]
            ];

            return Task.FromResult<DocumentCacheMaterializationResult>(
                new DocumentCacheMaterializationResult.Success(
                    new DocumentCacheMaterializationCandidate(
                        source.DocumentId,
                        new DocumentUuid(source.DocumentUuid),
                        resourceKey.Resource.ProjectName,
                        resourceKey.Resource.ResourceName,
                        resourceKey.ResourceVersion,
                        source.ContentVersion,
                        ObservedAt,
                        $"etag-{source.ContentVersion}",
                        new JsonObject { ["value"] = $"rebuilt-{source.DocumentId}" }
                    )
                )
            );
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed record SourceDocument(long DocumentId, Guid DocumentUuid, long ContentVersion);
}
