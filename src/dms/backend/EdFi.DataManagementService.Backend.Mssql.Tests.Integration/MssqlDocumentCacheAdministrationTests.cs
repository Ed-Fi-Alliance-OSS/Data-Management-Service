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
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category("DocumentCacheAdministration")]
[Category(MssqlCiShards.Shard4)]
public class Given_A_Mssql_DocumentCacheAdministration_Workflow
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/minimal";

    private static readonly DateTime ObservedAt = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset ObservedAtOffset = new(ObservedAt);
    private static readonly DateTimeOffset FirstEnqueuedAt = ObservedAtOffset.AddMinutes(-5);
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

    private MssqlGeneratedDdlFixture _fixture = null!;
    private IMssqlGeneratedDdlBaselineDatabase _baseline = null!;
    private IMssqlGeneratedDdlBaselineLease _lease = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private Dictionary<long, SourceDocument> _sourcesByDocumentId = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        MssqlConnectionStringGuard.RequireConfiguredForCiOrSkipLocally(
            "SQL Server DocumentCache administration tests require a MssqlAdmin connection string in appsettings.Test.json"
        );

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _baseline = await MssqlGeneratedDdlBaselineDatabaseFactory.CreateAsync(
            $"{nameof(Given_A_Mssql_DocumentCacheAdministration_Workflow)}:{_fixture.MappingSet.Key.EffectiveSchemaHash}",
            _fixture.GeneratedDdl
        );
    }

    [SetUp]
    public async Task SetUp()
    {
        _lease = await _baseline.AcquireRestoredDatabaseAsync();
        _database = _lease.Database;
        _sourcesByDocumentId = [];
        await SetReadCommittedSnapshotAsync(_database.DatabaseName, enabled: true);

        if (!await NestedTriggersEnabledAsync())
        {
            Assert.Ignore(
                "SQL Server DocumentCache administration tests require nested triggers to be enabled."
            );
        }
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_database is not null)
        {
            await SetReadCommittedSnapshotAsync(_database.DatabaseName, enabled: true);
        }

        if (_lease is not null)
        {
            await _lease.DisposeAsync();
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
            new DocumentCacheOnlineCacheRebuildRequest(AdministrativeTargetKey, Fingerprint)
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
            execute: static async (context, cancellationToken) =>
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
        DocumentCacheProjectionObservationStore observationStore = new(
            new FixedTimeProvider(ObservedAtOffset)
        );
        observationStore.ObserveTarget(TargetHealth(firstGeneration));
        var registry = new MutableTargetRegistry(
            new DocumentCacheTargetRegistrySnapshot([EligibleObservation(firstGeneration)], ObservedAtOffset),
            new DocumentCacheTargetRuntimeSnapshot([firstGeneration], ObservedAtOffset)
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
            new MssqlDocumentCacheAdministrativeMutex(
                NullLogger<MssqlDocumentCacheAdministrativeMutex>.Instance
            ),
            DocumentCacheAdministrativePrimitives.ForSqlServer(),
            observationStore,
            new FixedTimeProvider(ObservedAtOffset),
            NullLogger<DocumentCacheAdministrativeCommandRunner>.Instance
        );

        Task<DocumentCacheAdministrativeCommandResult> resultTask = runner.ExecuteAsync(
            RunnerRequest(),
            workflow
        );
        await commandStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        registry.CurrentRuntimeSnapshot = new DocumentCacheTargetRuntimeSnapshot(
            [replacementGeneration],
            ObservedAtOffset
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
            new DocumentCacheGuardedNewEmptyActivationRequest(AdministrativeTargetKey, Fingerprint)
        );

        activationResult.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        activationResult.Lifecycle.Should().Be(DocumentCacheLifecycleState.Tracking);

        await SetLifecycleAsync(DocumentCacheLifecycleState.Tracking, cacheAheadRecoveryRequired: true);
        IReadOnlyList<SourceDocument> latchedSources = await InsertProjectedRowsAsync(documentCount: 2);
        DocumentCacheOnlineCacheRebuildCommand onlineRebuild = CreateOnlineCacheRebuildCommand(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, true)
        );

        DocumentCacheAdministrativeCommandResult rebuildResult = await onlineRebuild.ExecuteAsync(
            new DocumentCacheOnlineCacheRebuildRequest(AdministrativeTargetKey, Fingerprint)
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
                    Fingerprint
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
                    Fingerprint
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
                    Fingerprint
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
                Fingerprint
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
                Fingerprint
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
            new DocumentCacheExplicitIntegrityScrubRequest(AdministrativeTargetKey, Fingerprint)
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
                new DocumentCacheExplicitIntegrityScrubRequest(AdministrativeTargetKey, Fingerprint)
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
                new DocumentCacheTargetRegistrySnapshot(
                    [EligibleObservation(executionContext)],
                    ObservedAtOffset
                ),
                new DocumentCacheTargetRuntimeSnapshot([executionContext], ObservedAtOffset)
            ),
            new MssqlDocumentCacheAdministrativeMutex(
                NullLogger<MssqlDocumentCacheAdministrativeMutex>.Instance
            ),
            DocumentCacheAdministrativePrimitives.ForSqlServer(),
            observationSink,
            new FixedTimeProvider(ObservedAtOffset),
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

    private static MssqlDocumentCacheWriter CreateWriter() =>
        new(
            new DocumentCacheWriterRetryAdapter(
                new DeadlockRetrySettings
                {
                    MaxRetryAttempts = 0,
                    BaseDelayMilliseconds = 1,
                    UseJitter = false,
                },
                new MssqlRelationalWriteExceptionClassifier(),
                NullLogger<DocumentCacheWriterRetryAdapter>.Instance
            ),
            NullLogger<MssqlDocumentCacheWriter>.Instance
        );

    private static DocumentCacheBaselineSeeder CreateBaselineSeeder(
        IDocumentCacheBaselineSeedDelay? delay = null,
        IDocumentCacheAdministrativeDrainer? drainer = null
    ) =>
        new(
            delay ?? new DocumentCacheBaselineSeedDelay(),
            new FixedTimeProvider(ObservedAtOffset),
            NullLogger<DocumentCacheBaselineSeeder>.Instance,
            drainer
        );

    private static DocumentCacheAdministrativeDrainer CreateDrainer(
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
                new MssqlDocumentProjectionWorkPager(NullLogger<MssqlDocumentProjectionWorkPager>.Instance),
                new DocumentCacheProjectionItemProcessor(
                    new FixedTimeProvider(ObservedAtOffset),
                    NullLogger<DocumentCacheProjectionItemProcessor>.Instance
                ),
                NullLogger<DocumentCacheProjectionDrainPageProcessor>.Instance,
                new FixedTimeProvider(ObservedAtOffset)
            ),
            observationSink,
            new FixedTimeProvider(ObservedAtOffset),
            NullLogger<DocumentCacheProjectionScheduler>.Instance
        );

        return new(
            scheduler,
            new DocumentCacheAdministrativeDrainDelay(),
            new FixedTimeProvider(ObservedAtOffset),
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
            new DocumentCacheTargetDataStoreMetadata(TargetKey.DataStoreId, "mssql"),
            new DocumentCacheTargetConnectionInput(
                RelationalProviderToken.SqlServer,
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
            SatisfiedSqlServerPrerequisites()
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

    private static DocumentCacheSqlServerPrerequisiteDetails SatisfiedSqlServerPrerequisites() =>
        new(
            new DocumentCacheProviderPrerequisiteResult(
                DocumentCacheProviderPrerequisiteName.ReadCommittedSnapshot,
                DocumentCacheProviderPrerequisiteStatus.Satisfied,
                "SQL Server READ_COMMITTED_SNAPSHOT is enabled."
            ),
            new DocumentCacheProviderPrerequisiteResult(
                DocumentCacheProviderPrerequisiteName.NestedTriggers,
                DocumentCacheProviderPrerequisiteStatus.Satisfied,
                "SQL Server nested triggers are enabled."
            )
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
        MssqlDocumentCacheWriter writer
    ) =>
        new(
            executionContext,
            new DocumentCacheProjectionTargetProviderAdapters(
                RelationalProviderToken.SqlServer,
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
            new DocumentCacheProjectionObservationStore(new FixedTimeProvider(ObservedAtOffset)),
            writer
        );

    private static DocumentCacheProjectionTargetHealthSnapshot TargetHealth(
        DocumentCacheTargetExecutionContext executionContext
    ) =>
        new(
            executionContext.TargetKey,
            executionContext.Generation,
            executionContext.EffectiveSettings.ProjectorPageSize,
            ObservedAtOffset,
            executionContext.ProviderToken,
            executionContext.PhysicalSourceFingerprint
        );

    private static DocumentCacheAdministrativeCommandRunnerRequest RunnerRequest() =>
        new(
            DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
            AdministrativeTargetKey,
            expectedPhysicalSourceFingerprint: Fingerprint
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

    private static async Task CommitReplacementLifecycleTransitionAsync(
        DocumentCacheAdministrativeCommandExecutionContext context,
        DocumentCacheLifecycleState expectedLifecycle,
        DocumentCacheLifecycleState nextLifecycle,
        CancellationToken cancellationToken
    )
    {
        var replacementMutex = new MssqlDocumentCacheAdministrativeMutex(
            NullLogger<MssqlDocumentCacheAdministrativeMutex>.Instance
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

    private static async Task TerminateMutexSessionAsync(
        DbConnection connection,
        CancellationToken cancellationToken
    )
    {
        int sessionId = await ExecuteConnectionScalarAsync<int>(
                connection,
                "SELECT @@SPID;",
                cancellationToken
            )
            .ConfigureAwait(false);

        await MssqlTestDatabaseHelper.ExecuteAdminNonQueryAsync($"KILL {sessionId};").ConfigureAwait(false);
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
            DECLARE @inserted TABLE ([DocumentId] bigint);

            INSERT INTO [dms].[Document] (
                [DocumentUuid],
                [ResourceKeyId],
                [ContentVersion],
                [ContentLastModifiedAt]
            )
            OUTPUT INSERTED.[DocumentId] INTO @inserted ([DocumentId])
            VALUES (
                @documentUuid,
                @resourceKeyId,
                @contentVersion,
                @lastModifiedAt
            );

            SELECT [DocumentId] FROM @inserted;
            """,
            new SqlParameter("@documentUuid", SqlDbType.UniqueIdentifier) { Value = documentUuid },
            new SqlParameter("@resourceKeyId", SqlDbType.SmallInt) { Value = resourceKeyId },
            new SqlParameter("@contentVersion", SqlDbType.BigInt) { Value = contentVersion },
            new SqlParameter("@lastModifiedAt", SqlDbType.DateTime2) { Value = ObservedAt }
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
            UPDATE [dms].[DocumentCacheState]
            SET
                [ProjectionLifecycleState] = @lifecycle,
                [CacheAheadRecoveryRequired] = @cacheAheadRecoveryRequired
            WHERE [StateId] = 1;
            """,
            new SqlParameter("@lifecycle", SqlDbType.VarChar, 32) { Value = lifecycle.ToString() },
            new SqlParameter("@cacheAheadRecoveryRequired", SqlDbType.Bit)
            {
                Value = cacheAheadRecoveryRequired,
            }
        );

    private Task ClearDocumentCacheAsync() =>
        _database.ExecuteNonQueryAsync("""DELETE FROM [dms].[DocumentCache];""");

    private Task ClearProjectionWorkAsync() =>
        _database.ExecuteNonQueryAsync("""DELETE FROM [dms].[DocumentProjectionWork];""");

    private Task InsertProjectionWorkAsync(SourceDocument source, long requiredContentVersion) =>
        _database.ExecuteNonQueryAsync(
            """
            MERGE [dms].[DocumentProjectionWork] AS target
            USING (
                SELECT
                    @documentId AS [DocumentId],
                    @requiredContentVersion AS [RequiredContentVersion],
                    @firstEnqueuedAt AS [FirstEnqueuedAt],
                    @lastEnqueuedAt AS [LastEnqueuedAt]
            ) AS source
                ON target.[DocumentId] = source.[DocumentId]
            WHEN MATCHED THEN
                UPDATE SET
                    [RequiredContentVersion] = source.[RequiredContentVersion],
                    [LastEnqueuedAt] = source.[LastEnqueuedAt]
            WHEN NOT MATCHED THEN
                INSERT (
                    [DocumentId],
                    [RequiredContentVersion],
                    [FirstEnqueuedAt],
                    [LastEnqueuedAt]
                )
                VALUES (
                    source.[DocumentId],
                    source.[RequiredContentVersion],
                    source.[FirstEnqueuedAt],
                    source.[LastEnqueuedAt]
                );
            """,
            new SqlParameter("@documentId", SqlDbType.BigInt) { Value = source.DocumentId },
            new SqlParameter("@requiredContentVersion", SqlDbType.BigInt) { Value = requiredContentVersion },
            new SqlParameter("@firstEnqueuedAt", SqlDbType.DateTime2) { Value = FirstEnqueuedAt.UtcDateTime },
            new SqlParameter("@lastEnqueuedAt", SqlDbType.DateTime2) { Value = ObservedAt }
        );

    private async Task InsertCacheRowAsync(SourceDocument source, long cacheContentVersion)
    {
        ResourceKeyEntry resourceKey = _fixture.MappingSet.ResourceKeyById[
            _fixture.MappingSet.ResourceKeyIdByResource[PersonResource]
        ];

        await _database.ExecuteNonQueryAsync(
            """
            MERGE [dms].[DocumentCache] AS target
            USING (
                SELECT
                    @documentId AS [DocumentId],
                    @documentUuid AS [DocumentUuid],
                    @projectName AS [ProjectName],
                    @resourceName AS [ResourceName],
                    @resourceVersion AS [ResourceVersion],
                    @contentVersion AS [ContentVersion],
                    @streamEtag AS [StreamEtag],
                    @lastModifiedAt AS [LastModifiedAt],
                    @documentJson AS [DocumentJson],
                    @computedAt AS [ComputedAt]
            ) AS source
                ON target.[DocumentId] = source.[DocumentId]
            WHEN MATCHED THEN
                UPDATE SET
                    [ContentVersion] = source.[ContentVersion],
                    [StreamEtag] = source.[StreamEtag],
                    [DocumentJson] = source.[DocumentJson],
                    [ComputedAt] = source.[ComputedAt]
            WHEN NOT MATCHED THEN
                INSERT (
                    [DocumentId],
                    [DocumentUuid],
                    [ProjectName],
                    [ResourceName],
                    [ResourceVersion],
                    [ContentVersion],
                    [StreamEtag],
                    [LastModifiedAt],
                    [DocumentJson],
                    [ComputedAt]
                )
                VALUES (
                    source.[DocumentId],
                    source.[DocumentUuid],
                    source.[ProjectName],
                    source.[ResourceName],
                    source.[ResourceVersion],
                    source.[ContentVersion],
                    source.[StreamEtag],
                    source.[LastModifiedAt],
                    source.[DocumentJson],
                    source.[ComputedAt]
                );
            """,
            new SqlParameter("@documentId", SqlDbType.BigInt) { Value = source.DocumentId },
            new SqlParameter("@documentUuid", SqlDbType.UniqueIdentifier) { Value = source.DocumentUuid },
            new SqlParameter("@projectName", SqlDbType.VarChar, 256)
            {
                Value = resourceKey.Resource.ProjectName,
            },
            new SqlParameter("@resourceName", SqlDbType.VarChar, 256)
            {
                Value = resourceKey.Resource.ResourceName,
            },
            new SqlParameter("@resourceVersion", SqlDbType.VarChar, 32)
            {
                Value = resourceKey.ResourceVersion,
            },
            new SqlParameter("@contentVersion", SqlDbType.BigInt) { Value = cacheContentVersion },
            new SqlParameter("@streamEtag", SqlDbType.VarChar, 64) { Value = $"etag-{cacheContentVersion}" },
            new SqlParameter("@lastModifiedAt", SqlDbType.DateTime2) { Value = ObservedAt },
            new SqlParameter("@documentJson", SqlDbType.NVarChar, -1)
            {
                Value = new JsonObject { ["value"] = $"cache-{source.DocumentId}" }.ToJsonString(),
            },
            new SqlParameter("@computedAt", SqlDbType.DateTime2) { Value = ObservedAt.AddMinutes(1) }
        );
    }

    private async Task<DocumentCacheLifecycleObservation> ReadLifecycleAsync()
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await _database.QueryRowsAsync(
            """
            SELECT [ProjectionLifecycleState], [CacheAheadRecoveryRequired]
            FROM [dms].[DocumentCacheState]
            WHERE [StateId] = 1;
            """
        );

        IReadOnlyDictionary<string, object?> row = rows.Single();
        return new(
            Enum.Parse<DocumentCacheLifecycleState>((string)row["ProjectionLifecycleState"]!),
            Convert.ToBoolean(row["CacheAheadRecoveryRequired"])
        );
    }

    private Task<long> ReadCountAsync(string tableName) =>
        _database.ExecuteScalarAsync<long>($$"""SELECT COUNT(*) FROM [dms].[{{tableName}}];""");

    private static async Task SetReadCommittedSnapshotAsync(string databaseName, bool enabled)
    {
        SqlConnection.ClearAllPools();

        string quotedDatabaseName = MssqlTestDatabaseHelper.QuoteIdentifier(databaseName);
        string enabledSql = enabled ? "ON" : "OFF";

        await MssqlTestDatabaseHelper.ExecuteAdminNonQueryAsync(
            $"""
            ALTER DATABASE {quotedDatabaseName}
            SET READ_COMMITTED_SNAPSHOT {enabledSql} WITH ROLLBACK IMMEDIATE;
            """
        );

        SqlConnection.ClearAllPools();
    }

    private static async Task<bool> NestedTriggersEnabledAsync()
    {
        await using SqlConnection connection = new(BaselineDatabaseConfiguration.MssqlAdminConnectionString!);
        await connection.OpenAsync();

        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT CONVERT(int, [value_in_use])
            FROM [sys].[configurations]
            WHERE [name] = N'nested triggers';
            """;

        object? value = await command.ExecuteScalarAsync();
        return value is not null && value != DBNull.Value && Convert.ToInt32(value) == 1;
    }

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
                    evidenceSource: "mssql-document-cache-administration-test",
                    evidenceGenerationIdentifier: null,
                    ObservedAtOffset,
                    "SQL Server DocumentCache administration test downstream publication proof."
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
                        ObservedAtOffset,
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
