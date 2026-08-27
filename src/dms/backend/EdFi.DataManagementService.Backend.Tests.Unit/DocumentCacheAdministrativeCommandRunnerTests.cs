// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("DocumentCacheAdministrativeCommandRunner")]
public class Given_DocumentCacheAdministrativeCommandRunner
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DocumentCacheTargetKey TargetKey = DocumentCacheTargetKey.Create("TenantA", 7);
    private static readonly DocumentCacheAdministrativeTargetKey AdministrativeTargetKey =
        DocumentCacheAdministrativeTargetKey.FromTargetKey(TargetKey);
    private static readonly DocumentCachePhysicalSourceFingerprint Fingerprint = new(
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    );
    private static readonly DocumentCachePhysicalSourceFingerprint OtherFingerprint = new(
        "sha256:fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210"
    );
    private static readonly DocumentCacheLifecycleObservation TrackingLifecycle = new(
        DocumentCacheLifecycleState.Tracking,
        CacheAheadRecoveryRequired: false
    );
    private static readonly DocumentCacheLifecycleObservation DisabledLifecycle = new(
        DocumentCacheLifecycleState.Disabled,
        CacheAheadRecoveryRequired: false
    );

    [Test]
    public async Task It_rejects_target_replacement_before_acquiring_the_mutex()
    {
        DocumentCacheTargetExecutionContext firstGeneration = ExecutionContext(generation: 1);
        DocumentCacheTargetExecutionContext replacementGeneration = ExecutionContext(generation: 2);
        DocumentCacheProjectionTargetRuntimeContext runtimeContext = RuntimeContext(firstGeneration);
        var mutex = new RecordingAdministrativeMutex();
        var registry = new MutableTargetRegistry(
            Snapshot([EligibleObservation(firstGeneration)]),
            RuntimeSnapshot([replacementGeneration])
        );
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            registry,
            new StubProjectionSupervisor([runtimeContext]),
            mutex
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(
            Request(),
            SucceedingWorkflow.Instance
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.TargetReplacedBeforeExecution);
        result.Mutated.Should().BeFalse();
        result.ElapsedCommandTime.Should().BeNull();
        result
            .PhaseDiagnostics.Should()
            .ContainSingle()
            .Which.CurrentPhase.Should()
            .Be(DocumentCacheAdministrativeCommandPhase.ResolveTarget);
        mutex.AcquireCount.Should().Be(0);
    }

    [Test]
    public async Task It_rejects_when_administrative_target_retention_fails_before_acquiring_the_mutex()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(generation: 1);
        DocumentCacheProjectionTargetRuntimeContext runtimeContext = RuntimeContext(executionContext);
        var mutex = new RecordingAdministrativeMutex();
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new RejectingAdministrativeTargetRetainerProjectionSupervisor(
                EligibleObservation(executionContext),
                runtimeContext
            ),
            mutex
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(
            Request(),
            SucceedingWorkflow.Instance
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.TargetReplacedBeforeExecution);
        result.Mutated.Should().BeFalse();
        result.ElapsedCommandTime.Should().BeNull();
        mutex.AcquireCount.Should().Be(0);
    }

    [Test]
    public async Task It_rejects_expected_source_mismatch_before_acquiring_the_mutex()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(generation: 1);
        var mutex = new RecordingAdministrativeMutex();
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([RuntimeContext(executionContext)]),
            mutex
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(
            new DocumentCacheAdministrativeCommandRunnerRequest(
                DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
                AdministrativeTargetKey,
                OtherFingerprint,
                confirmation: DocumentCacheAdministrativeCommandConfirmation.OnlineCacheRebuild
            ),
            SucceedingWorkflow.Instance
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.ExpectedSourceMismatch);
        result.Mutated.Should().BeFalse();
        result.ElapsedCommandTime.Should().BeNull();
        mutex.AcquireCount.Should().Be(0);
    }

    [Test]
    public async Task It_bounds_mutex_acquisition_with_the_administration_workflow_timeout()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(
            generation: 1,
            workflowTimeout: TimeSpan.FromMilliseconds(30)
        );
        DocumentCacheProjectionTargetRuntimeContext runtimeContext = RuntimeContext(executionContext);
        var mutex = new RecordingAdministrativeMutex(acquireDelay: TimeSpan.FromMilliseconds(500));
        MutableTargetRegistry registry = RegistryFor(executionContext);
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            registry,
            new StubProjectionSupervisor([runtimeContext]),
            mutex
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(
            Request(),
            SucceedingWorkflow.Instance
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.FailedNoMutation);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.MutexAcquisitionCancelled);
        result.Mutated.Should().BeFalse();
        result.ElapsedCommandTime.Should().BeNull();
        result
            .PhaseDiagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.CurrentPhase == DocumentCacheAdministrativeCommandPhase.AcquireMutex
                && diagnostic.LastCompletedPhase == DocumentCacheAdministrativeCommandPhase.ResolveTarget
                && diagnostic.DiagnosticCategory
                    == DocumentCacheAdministrativeDiagnosticCategory.MutexAcquisitionCancelled
                && !diagnostic.Retryable
            );
        mutex.AcquireCount.Should().Be(1);
    }

    [Test]
    public async Task It_bounds_administrative_target_retention_with_the_administration_workflow_timeout()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(
            generation: 1,
            workflowTimeout: TimeSpan.FromMilliseconds(30)
        );
        DocumentCacheProjectionTargetRuntimeContext runtimeContext = RuntimeContext(executionContext);
        var mutex = new RecordingAdministrativeMutex();
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new DelayingAdministrativeTargetRetainerProjectionSupervisor(
                EligibleObservation(executionContext),
                runtimeContext,
                TimeSpan.FromMilliseconds(500)
            ),
            mutex
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(
            Request(),
            SucceedingWorkflow.Instance
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.FailedNoMutation);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.WorkflowTimeout);
        result.Mutated.Should().BeFalse();
        result
            .PhaseDiagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.CurrentPhase == DocumentCacheAdministrativeCommandPhase.ResolveTarget
                && diagnostic.DiagnosticCategory
                    == DocumentCacheAdministrativeDiagnosticCategory.WorkflowTimeout
            );
        mutex.AcquireCount.Should().Be(0);
    }

    [Test]
    public async Task It_runs_live_preflight_on_the_mutex_session_before_command_work()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(generation: 1);
        var mutex = new RecordingAdministrativeMutex();
        var workflow = new DelegatingWorkflow(
            preflight: (context, _) =>
            {
                DocumentCacheGuardedNewEmptyActivationRequest request = new(
                    AdministrativeTargetKey,
                    Fingerprint
                );
                DocumentCacheAdministrativeCommandResult result =
                    DocumentCachePreflightClassifier.ClassifyGuardedNewEmptyActivation(
                        request,
                        context.LiveTargetObservation,
                        new DocumentCacheGuardedNewEmptyActivationPreflightFacts(
                            executionContext.Generation,
                            DocumentCacheProviderPrerequisiteValidationResult.ActivationPreflight(
                                DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
                            ),
                            new DocumentCacheGuardedNewEmptyActivationState(
                                canonicalDocumentsEmpty: true,
                                documentCacheEmpty: true,
                                documentProjectionWorkEmpty: true
                            )
                        )
                    );

                return Task.FromResult(result);
            },
            execute: static (_, _) => throw new AssertionException("Command work must not run.")
        );
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([RuntimeContext(executionContext)]),
            mutex
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(
            new DocumentCacheAdministrativeCommandRunnerRequest(
                DocumentCacheAdministrativeCommand.GuardedNewEmptyActivation,
                AdministrativeTargetKey,
                Fingerprint,
                confirmation: DocumentCacheAdministrativeCommandConfirmation.NewEmptyActivation
            ),
            workflow
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.LifecycleMismatch);
        result.ElapsedCommandTime.Should().NotBeNull();
        result
            .PhaseDiagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.CurrentPhase == DocumentCacheAdministrativeCommandPhase.Preflight
            );
        mutex.AcquireCount.Should().Be(1);
    }

    [Test]
    public async Task It_does_not_duplicate_context_phase_diagnostics_when_preflight_returns_context_failure()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(generation: 1);
        var workflow = new DelegatingWorkflow(
            preflight: static (context, _) =>
                Task.FromResult(
                    context.Failed(
                        DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
                        DocumentCacheAdministrativeCommandClassification.LifecycleMismatch,
                        DocumentCacheAdministrativeDiagnosticCategory.BaselineHighWaterBackpressure,
                        "Preflight stopped after observing baseline high-water backpressure.",
                        retryable: false,
                        affectedDocumentIds: [10, 11, 12]
                    )
                ),
            execute: static (_, _) => throw new AssertionException("Command work must not run.")
        );
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([RuntimeContext(executionContext)]),
            new RecordingAdministrativeMutex()
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(Request(), workflow);

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.FailedNoMutation);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.LifecycleMismatch);
        DocumentCacheAdministrativePhaseDiagnostic diagnostic = result
            .PhaseDiagnostics.Should()
            .ContainSingle()
            .Subject;
        diagnostic
            .DiagnosticCategory.Should()
            .Be(DocumentCacheAdministrativeDiagnosticCategory.BaselineHighWaterBackpressure);
        diagnostic.AffectedDocumentIds.Should().Equal(10, 11, 12);
    }

    [Test]
    public async Task It_carries_accepted_downstream_publication_status_from_preflight_to_execution()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(generation: 1);
        DocumentCacheDownstreamPublicationStatus? observedStatus = null;
        var workflow = new DelegatingWorkflow(
            preflight: static (context, _) =>
                Task.FromResult(
                    context.EligiblePreflightResult(DocumentCacheDownstreamPublicationStatus.InternalOnly)
                ),
            execute: (context, _) =>
            {
                observedStatus = context.RequireAcceptedDownstreamPublicationStatus();
                return Task.FromResult(context.Completed());
            }
        );
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([RuntimeContext(executionContext)]),
            new RecordingAdministrativeMutex()
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(Request(), workflow);

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
        observedStatus.Should().Be(DocumentCacheDownstreamPublicationStatus.InternalOnly);
    }

    [TestCase(
        DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed,
        DocumentCacheAdministrativeCommandClassification.ProviderPrerequisiteFailed
    )]
    [TestCase(
        DocumentCacheTargetDiagnosticCategory.UnsupportedPrerequisiteIncident,
        DocumentCacheAdministrativeCommandClassification.UnsupportedPrerequisiteIncident
    )]
    public async Task It_rejects_SqlServerDocumentCachePrerequisite_failures_before_acquiring_the_mutex(
        DocumentCacheTargetDiagnosticCategory diagnosticCategory,
        DocumentCacheAdministrativeCommandClassification expectedClassification
    )
    {
        DocumentCacheTargetObservation targetObservation = IneligiblePrerequisiteObservation(
            diagnosticCategory
        );
        var mutex = new RecordingAdministrativeMutex();
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            new MutableTargetRegistry(Snapshot([targetObservation]), RuntimeSnapshot([])),
            new StubProjectionSupervisor([]),
            mutex
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(
            Request(),
            SucceedingWorkflow.Instance
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation);
        result.Classification.Should().Be(expectedClassification);
        result.Mutated.Should().BeFalse();
        result.ElapsedCommandTime.Should().BeNull();
        result
            .PhaseDiagnostics.Should()
            .ContainSingle()
            .Which.CurrentPhase.Should()
            .Be(DocumentCacheAdministrativeCommandPhase.Preflight);
        mutex.AcquireCount.Should().Be(0);
    }

    [Test]
    public async Task It_rejects_unexpected_target_observation_failures_before_acquiring_the_mutex()
    {
        DocumentCacheTargetObservation targetObservation = IneligibleUnexpectedProviderFailureObservation();
        var mutex = new RecordingAdministrativeMutex();
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            new MutableTargetRegistry(Snapshot([targetObservation]), RuntimeSnapshot([])),
            new StubProjectionSupervisor([]),
            mutex
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(
            Request(),
            SucceedingWorkflow.Instance
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.UnexpectedProviderFailure);
        result.Mutated.Should().BeFalse();
        result.ElapsedCommandTime.Should().BeNull();
        result
            .PhaseDiagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.CurrentPhase == DocumentCacheAdministrativeCommandPhase.Preflight
                && diagnostic.DiagnosticCategory
                    == DocumentCacheAdministrativeDiagnosticCategory.UnexpectedProviderFailure
            );
        mutex.AcquireCount.Should().Be(0);
    }

    [TestCaseSource(nameof(InvalidOfflineWriterAdmissionRequests))]
    public async Task It_rejects_invalid_offline_writer_admission_before_acquiring_the_mutex(
        DocumentCacheAdministrativeCommand command,
        DocumentCacheOfflineWriterAdmission? offlineWriterAdmission,
        DocumentCacheAdministrativeCommandClassification expectedClassification,
        DocumentCacheAdministrativeDiagnosticCategory expectedDiagnosticCategory
    )
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(generation: 1);
        var mutex = new RecordingAdministrativeMutex();
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([RuntimeContext(executionContext)]),
            mutex
        );

        DocumentCacheAdministrativeCommandRunnerRequest request = new(
            command,
            AdministrativeTargetKey,
            Fingerprint,
            offlineWriterAdmission,
            DocumentCachePreflightClassifier.ExpectedCommandConfirmation(command)
        );
        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(
            request,
            SucceedingWorkflow.Instance
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation);
        result.Classification.Should().Be(expectedClassification);
        result.Mutated.Should().BeFalse();
        result.ElapsedCommandTime.Should().BeNull();
        result.OfflineWriterAdmission.Should().BeNull();
        result
            .PhaseDiagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.CurrentPhase == DocumentCacheAdministrativeCommandPhase.Preflight
                && diagnostic.DiagnosticCategory == expectedDiagnosticCategory
            );
        mutex.AcquireCount.Should().Be(0);
    }

    [TestCaseSource(nameof(AdministrativeCommands))]
    public async Task It_rejects_missing_command_confirmation_before_acquiring_the_mutex(
        DocumentCacheAdministrativeCommand command
    )
    {
        var mutex = new RecordingAdministrativeMutex();
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(ExecutionContext(generation: 1)),
            new StubProjectionSupervisor([]),
            mutex
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(
            new DocumentCacheAdministrativeCommandRunnerRequest(
                command,
                AdministrativeTargetKey,
                Fingerprint,
                OfflineWriterAdmissionFor(command)
            ),
            ThrowingWorkflow()
        );

        AssertRejectedCommandConfirmation(
            result,
            DocumentCacheAdministrativeCommandClassification.MissingCommandConfirmation,
            DocumentCacheAdministrativeDiagnosticCategory.MissingCommandConfirmation
        );
        mutex.AcquireCount.Should().Be(0);
    }

    [TestCaseSource(nameof(AdministrativeCommands))]
    public async Task It_rejects_wrong_command_confirmation_before_acquiring_the_mutex(
        DocumentCacheAdministrativeCommand command
    )
    {
        var mutex = new RecordingAdministrativeMutex();
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(ExecutionContext(generation: 1)),
            new StubProjectionSupervisor([]),
            mutex
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(
            new DocumentCacheAdministrativeCommandRunnerRequest(
                command,
                AdministrativeTargetKey,
                Fingerprint,
                OfflineWriterAdmissionFor(command),
                WrongConfirmationFor(command)
            ),
            ThrowingWorkflow()
        );

        AssertRejectedCommandConfirmation(
            result,
            DocumentCacheAdministrativeCommandClassification.MismatchedCommandConfirmation,
            DocumentCacheAdministrativeDiagnosticCategory.MismatchedCommandConfirmation
        );
        mutex.AcquireCount.Should().Be(0);
    }

    [Test]
    public async Task It_classifies_workflow_timeout_after_mutation_as_incomplete_retryable()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(
            generation: 1,
            workflowTimeout: TimeSpan.FromMilliseconds(30)
        );
        DocumentCacheProjectionTargetRuntimeContext runtimeContext = RuntimeContext(executionContext);
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([runtimeContext]),
            new RecordingAdministrativeMutex()
        );
        var workflow = new DelegatingWorkflow(
            preflight: static (context, _) => Task.FromResult(context.EligiblePreflightResult()),
            execute: static async (context, cancellationToken) =>
            {
                context.EnterPhase(DocumentCacheAdministrativeCommandPhase.SeedBaseline);
                context.MarkMutated(
                    new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Rebuilding, false)
                );
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                return context.Completed();
            }
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(Request(), workflow);

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.IncompleteRetryable);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.WorkflowTimeout);
        result.Mutated.Should().BeTrue();
        result.Lifecycle.Should().Be(DocumentCacheLifecycleState.Rebuilding);
        result
            .PhaseDiagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.CurrentPhase == DocumentCacheAdministrativeCommandPhase.SeedBaseline
                && diagnostic.LastCompletedPhase == DocumentCacheAdministrativeCommandPhase.Preflight
                && diagnostic.Retryable
                && diagnostic.DiagnosticCategory
                    == DocumentCacheAdministrativeDiagnosticCategory.WorkflowTimeout
            );
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_preserves_completed_result_returned_after_workflow_timeout_token_is_canceled(
        bool transactionMutates
    )
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(
            generation: 1,
            workflowTimeout: TimeSpan.FromMilliseconds(20)
        );
        DocumentCacheProjectionTargetRuntimeContext runtimeContext = RuntimeContext(executionContext);
        List<CancellationToken> transactionTokens = [];
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([runtimeContext]),
            new RecordingAdministrativeMutex()
        );
        var workflow = new DelegatingWorkflow(
            preflight: static (context, _) => Task.FromResult(context.EligiblePreflightResult()),
            execute: async (context, cancellationToken) =>
            {
                context.EnterPhase(DocumentCacheAdministrativeCommandPhase.ClearCache);
                await DocumentCacheAdministrativeWorkflow
                    .ExecuteInTransactionAsync(
                        context,
                        IsolationLevel.ReadCommitted,
                        async (_, transactionCancellationToken) =>
                        {
                            transactionTokens.Add(transactionCancellationToken);
                            await Task.Delay(TimeSpan.FromMilliseconds(80), transactionCancellationToken)
                                .ConfigureAwait(false);

                            if (transactionMutates)
                            {
                                context.MarkMutated(
                                    new DocumentCacheLifecycleObservation(
                                        DocumentCacheLifecycleState.Resetting,
                                        false
                                    )
                                );
                            }

                            return true;
                        },
                        commit: true,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                return context.Completed();
            }
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(Request(), workflow);

        transactionTokens.Should().ContainSingle().Which.Should().Be(CancellationToken.None);
        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
        result.Mutated.Should().Be(transactionMutates);
        result
            .PhaseDiagnostics.Should()
            .NotContain(diagnostic =>
                diagnostic.DiagnosticCategory == DocumentCacheAdministrativeDiagnosticCategory.WorkflowTimeout
            );
    }

    [Test]
    public async Task It_applies_remaining_workflow_budget_to_administrative_transaction_commands()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(
            generation: 1,
            workflowTimeout: TimeSpan.FromSeconds(7.2)
        );
        List<CancellationToken> transactionTokens = [];
        List<RecordingDbCommand> createdCommands = [];
        var commandSession = new RecordingWriteSession(
            RelationalProviderToken.Postgresql,
            connection: new TestNpgsqlConnection(),
            transaction: new TestDbTransaction(),
            createCommand: _ =>
            {
                var command = new RecordingDbCommand(new DataTable().CreateDataReader());
                createdCommands.Add(command);
                return command;
            }
        );
        RecordingMutexLease lease = LeaseWith(NormalSession(), commandSession);
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([RuntimeContext(executionContext)]),
            new RecordingAdministrativeMutex(lease: lease)
        );
        var workflow = new DelegatingWorkflow(
            preflight: static (context, _) => Task.FromResult(context.EligiblePreflightResult()),
            execute: async (context, cancellationToken) =>
            {
                await DocumentCacheAdministrativeWorkflow
                    .ExecuteInTransactionAsync(
                        context,
                        IsolationLevel.ReadCommitted,
                        async (session, transactionCancellationToken) =>
                        {
                            transactionTokens.Add(transactionCancellationToken);
                            await using DbCommand directCommand = session.CreateCommand(
                                new RelationalCommand("select 1")
                            );
                            await session
                                .CreateCommandExecutor()
                                .ExecuteReaderAsync(
                                    new RelationalCommand("select 2"),
                                    static (_, _) => Task.FromResult(true),
                                    transactionCancellationToken
                                )
                                .ConfigureAwait(false);

                            return true;
                        },
                        commit: true,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                return context.Completed();
            }
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(Request(), workflow);

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        transactionTokens.Should().ContainSingle().Which.Should().Be(CancellationToken.None);
        createdCommands.Select(command => command.CommandTimeout).Should().Equal(8, 8);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_preserves_completed_result_returned_after_caller_cancellation_token_is_canceled(
        bool transactionMutates
    )
    {
        using CancellationTokenSource cancellationTokenSource = new();
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(generation: 1);
        DocumentCacheProjectionTargetRuntimeContext runtimeContext = RuntimeContext(executionContext);
        List<CancellationToken> transactionTokens = [];
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([runtimeContext]),
            new RecordingAdministrativeMutex()
        );
        var workflow = new DelegatingWorkflow(
            preflight: static (context, _) => Task.FromResult(context.EligiblePreflightResult()),
            execute: async (context, cancellationToken) =>
            {
                context.EnterPhase(DocumentCacheAdministrativeCommandPhase.ClearWork);
                await DocumentCacheAdministrativeWorkflow
                    .ExecuteInTransactionAsync(
                        context,
                        IsolationLevel.ReadCommitted,
                        (_, transactionCancellationToken) =>
                        {
                            transactionTokens.Add(transactionCancellationToken);
                            cancellationTokenSource.Cancel();

                            if (transactionMutates)
                            {
                                context.MarkMutated(
                                    new DocumentCacheLifecycleObservation(
                                        DocumentCacheLifecycleState.Resetting,
                                        false
                                    )
                                );
                            }

                            return Task.FromResult(true);
                        },
                        commit: true,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                return context.Completed();
            }
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(
            Request(),
            workflow,
            cancellationTokenSource.Token
        );

        transactionTokens.Should().ContainSingle().Which.Should().Be(CancellationToken.None);
        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
        result.Mutated.Should().Be(transactionMutates);
        result
            .PhaseDiagnostics.Should()
            .NotContain(diagnostic =>
                diagnostic.DiagnosticCategory == DocumentCacheAdministrativeDiagnosticCategory.Cancellation
            );
    }

    [TestCase(
        false,
        DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
        DocumentCacheAdministrativeCommandClassification.CancellationBeforeMutation
    )]
    [TestCase(
        true,
        DocumentCacheAdministrativeCommandStatus.IncompleteRetryable,
        DocumentCacheAdministrativeCommandClassification.CancellationAfterMutation
    )]
    public async Task It_classifies_caller_cancellation_observed_before_workflow_returns(
        bool commandMutates,
        DocumentCacheAdministrativeCommandStatus expectedStatus,
        DocumentCacheAdministrativeCommandClassification expectedClassification
    )
    {
        using CancellationTokenSource cancellationTokenSource = new();
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(generation: 1);
        DocumentCacheProjectionTargetRuntimeContext runtimeContext = RuntimeContext(executionContext);
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([runtimeContext]),
            new RecordingAdministrativeMutex()
        );
        var workflow = new DelegatingWorkflow(
            preflight: static (context, _) => Task.FromResult(context.EligiblePreflightResult()),
            execute: async (context, cancellationToken) =>
            {
                context.EnterPhase(DocumentCacheAdministrativeCommandPhase.ClearWork);
                if (commandMutates)
                {
                    context.MarkMutated(
                        new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Resetting, false)
                    );
                }

                await cancellationTokenSource.CancelAsync().ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                return context.Completed();
            }
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(
            Request(),
            workflow,
            cancellationTokenSource.Token
        );

        result.Status.Should().Be(expectedStatus);
        result.Classification.Should().Be(expectedClassification);
        result.Mutated.Should().Be(commandMutates);
        result
            .PhaseDiagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.CurrentPhase == DocumentCacheAdministrativeCommandPhase.ClearWork
                && diagnostic.DiagnosticCategory == DocumentCacheAdministrativeDiagnosticCategory.Cancellation
            );
    }

    [TestCaseSource(nameof(ProviderCommandTimeoutCases))]
    public async Task It_classifies_provider_command_timeouts_escaping_administrative_primitives(
        RelationalProviderToken providerToken,
        Exception providerException,
        bool commandMutated,
        DocumentCacheAdministrativeCommandStatus expectedStatus
    )
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(
            generation: 1,
            providerToken: providerToken
        );
        var primitives = new StubAdministrativePrimitives(
            providerToken,
            projectedStateEmptinessException: providerException
        );
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([RuntimeContext(executionContext)]),
            new RecordingAdministrativeMutex(providerToken: providerToken),
            primitives: primitives
        );
        var workflow = new DelegatingWorkflow(
            preflight: static (context, _) => Task.FromResult(context.EligiblePreflightResult()),
            execute: async (context, cancellationToken) =>
            {
                context.EnterPhase(DocumentCacheAdministrativeCommandPhase.ClearCache);
                if (commandMutated)
                {
                    context.MarkMutated(
                        new DocumentCacheLifecycleObservation(
                            DocumentCacheLifecycleState.Resetting,
                            CacheAheadRecoveryRequired: false
                        )
                    );
                }

                await context
                    .Primitives.ReadProjectedStateEmptinessAsync(
                        new RecordingWriteSession(providerToken),
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                return context.Completed();
            }
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(Request(), workflow);

        result.Status.Should().Be(expectedStatus);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.ProviderCommandTimeout);
        result.Mutated.Should().Be(commandMutated);
        result
            .PhaseDiagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.CurrentPhase == DocumentCacheAdministrativeCommandPhase.ClearCache
                && diagnostic.LastCompletedPhase == DocumentCacheAdministrativeCommandPhase.Preflight
                && diagnostic.Retryable == commandMutated
                && diagnostic.DiagnosticCategory
                    == DocumentCacheAdministrativeDiagnosticCategory.ProviderCommandTimeout
            );
    }

    [Test]
    public async Task It_classifies_lifecycle_read_timeout_during_command_preflight_as_provider_command_timeout()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(
            generation: 1,
            providerToken: RelationalProviderToken.SqlServer
        );
        var primitives = new StubAdministrativePrimitives(
            RelationalProviderToken.SqlServer,
            lifecycleReads: [CreateSqlException(-2, "Execution Timeout Expired.")]
        );
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([RuntimeContext(executionContext)]),
            new RecordingAdministrativeMutex(providerToken: RelationalProviderToken.SqlServer),
            primitives: primitives
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(
            Request(),
            SucceedingWorkflow.Instance
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.FailedNoMutation);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.ProviderCommandTimeout);
        result.Mutated.Should().BeFalse();
        DocumentCacheAdministrativePhaseDiagnostic diagnostic = result
            .PhaseDiagnostics.Should()
            .ContainSingle()
            .Subject;
        diagnostic.CurrentPhase.Should().Be(DocumentCacheAdministrativeCommandPhase.Preflight);
        diagnostic.LastCompletedPhase.Should().BeNull();
        diagnostic
            .DiagnosticCategory.Should()
            .Be(DocumentCacheAdministrativeDiagnosticCategory.ProviderCommandTimeout);
        diagnostic.Retryable.Should().BeFalse();
    }

    [Test]
    public async Task It_classifies_transition_timeout_after_prior_mutation_as_retryable_provider_command_timeout()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(
            generation: 1,
            providerToken: RelationalProviderToken.SqlServer
        );
        var primitives = new StubAdministrativePrimitives(
            RelationalProviderToken.SqlServer,
            transitionLifecycleException: CreateSqlException(-2, "Execution Timeout Expired.")
        );
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([RuntimeContext(executionContext)]),
            new RecordingAdministrativeMutex(providerToken: RelationalProviderToken.SqlServer),
            primitives: primitives
        );
        var workflow = new DelegatingWorkflow(
            preflight: static (context, _) => Task.FromResult(context.EligiblePreflightResult()),
            execute: async (context, cancellationToken) =>
            {
                context.EnterPhase(DocumentCacheAdministrativeCommandPhase.EnterTracking);
                context.MarkMutated(
                    new DocumentCacheLifecycleObservation(
                        DocumentCacheLifecycleState.Rebuilding,
                        CacheAheadRecoveryRequired: false
                    )
                );

                await context
                    .Primitives.TryTransitionLifecycleAsync(
                        new RecordingWriteSession(RelationalProviderToken.SqlServer),
                        new DocumentCacheAdministrativeLifecycleTransitionRequest(
                            DocumentCacheLifecycleState.Rebuilding,
                            expectedCacheAheadRecoveryRequired: false,
                            DocumentCacheLifecycleState.Tracking,
                            nextCacheAheadRecoveryRequired: false
                        ),
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                return context.Completed();
            }
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(Request(), workflow);

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.IncompleteRetryable);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.ProviderCommandTimeout);
        result.Mutated.Should().BeTrue();
        result
            .PhaseDiagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.CurrentPhase == DocumentCacheAdministrativeCommandPhase.EnterTracking
                && diagnostic.LastCompletedPhase == DocumentCacheAdministrativeCommandPhase.Preflight
                && diagnostic.DiagnosticCategory
                    == DocumentCacheAdministrativeDiagnosticCategory.ProviderCommandTimeout
                && diagnostic.Retryable
            );
    }

    [Test]
    public async Task It_classifies_explicit_scrub_page_lifecycle_read_timeout_as_provider_command_timeout()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(generation: 1);
        var primitives = new StubAdministrativePrimitives(
            lifecycleReads:
            [
                DocumentCacheLifecycleReadResult.Success(TrackingLifecycle),
                DocumentCacheLifecycleReadResult.Success(TrackingLifecycle),
                CreatePostgresException("57014"),
            ],
            baselineBoundary: new DocumentCacheAdministrativeBaselineBoundaryResult(5, "boundary")
        );
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([RuntimeContext(executionContext)]),
            new RecordingAdministrativeMutex(),
            primitives: primitives
        );
        var command = new DocumentCacheExplicitIntegrityScrubCommand(runner);

        DocumentCacheAdministrativeCommandResult result = await command.ExecuteAsync(
            new DocumentCacheExplicitIntegrityScrubRequest(
                AdministrativeTargetKey,
                Fingerprint,
                DocumentCacheAdministrativeCommandConfirmation.IntegrityScrub
            )
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.FailedNoMutation);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.ProviderCommandTimeout);
        result.Mutated.Should().BeFalse();
        result
            .PhaseDiagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.CurrentPhase == DocumentCacheAdministrativeCommandPhase.ScrubScan
                && diagnostic.LastCompletedPhase == DocumentCacheAdministrativeCommandPhase.CaptureBoundary
                && diagnostic.DiagnosticCategory
                    == DocumentCacheAdministrativeDiagnosticCategory.ProviderCommandTimeout
                && !diagnostic.Retryable
            );
    }

    [TestCaseSource(nameof(ProviderConcurrencyRetryExhaustionCases))]
    public async Task It_classifies_exhausted_provider_concurrency_retry_from_explicit_scrub_page(
        RelationalProviderToken providerToken,
        DbException providerConcurrencyException,
        IRelationalWriteExceptionClassifier writeExceptionClassifier
    )
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(
            generation: 1,
            providerToken: providerToken
        );
        var primitives = new StubAdministrativePrimitives(
            providerToken,
            lifecycleReads:
            [
                DocumentCacheLifecycleReadResult.Success(TrackingLifecycle),
                DocumentCacheLifecycleReadResult.Success(TrackingLifecycle),
                DocumentCacheLifecycleReadResult.Success(TrackingLifecycle),
                DocumentCacheLifecycleReadResult.Success(TrackingLifecycle),
            ],
            baselineBoundary: new DocumentCacheAdministrativeBaselineBoundaryResult(5, "boundary"),
            scrubPages: [providerConcurrencyException, providerConcurrencyException]
        );
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([RuntimeContext(executionContext)]),
            new RecordingAdministrativeMutex(providerToken: providerToken),
            primitives: primitives,
            providerConcurrencyRetrySettings: ProviderConcurrencyRetrySettings(maxRetryAttempts: 1),
            writeExceptionClassifier: writeExceptionClassifier
        );
        var command = new DocumentCacheExplicitIntegrityScrubCommand(runner);

        DocumentCacheAdministrativeCommandResult result = await command.ExecuteAsync(
            new DocumentCacheExplicitIntegrityScrubRequest(
                AdministrativeTargetKey,
                Fingerprint,
                DocumentCacheAdministrativeCommandConfirmation.IntegrityScrub
            )
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.FailedNoMutation);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.ProviderConcurrencyRetryExhausted);
        result.Mutated.Should().BeFalse();
        primitives.ScrubPageCallCount.Should().Be(2);
        result
            .PhaseDiagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.CurrentPhase == DocumentCacheAdministrativeCommandPhase.ScrubScan
                && diagnostic.LastCompletedPhase == DocumentCacheAdministrativeCommandPhase.CaptureBoundary
                && diagnostic.DiagnosticCategory
                    == DocumentCacheAdministrativeDiagnosticCategory.ProviderConcurrencyRetryExhausted
                && diagnostic.Retryable
            );
    }

    [Test]
    public async Task It_classifies_exhausted_page_invalidation_retry_from_explicit_scrub_page()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(generation: 1);
        var primitives = new StubAdministrativePrimitives(
            baselineBoundary: new DocumentCacheAdministrativeBaselineBoundaryResult(5, "boundary"),
            scrubPages:
            [
                ScrubRetryPage(boundaryDocumentId: 5, afterDocumentId: 0, pageSize: 3),
                ScrubRetryPage(boundaryDocumentId: 5, afterDocumentId: 0, pageSize: 1),
                ScrubRetryPage(boundaryDocumentId: 5, afterDocumentId: 0, pageSize: 1),
            ]
        );
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([RuntimeContext(executionContext)]),
            new RecordingAdministrativeMutex(),
            primitives: primitives,
            providerConcurrencyRetrySettings: ProviderConcurrencyRetrySettings(maxRetryAttempts: 1)
        );
        var command = new DocumentCacheExplicitIntegrityScrubCommand(runner);

        DocumentCacheAdministrativeCommandResult result = await command.ExecuteAsync(
            new DocumentCacheExplicitIntegrityScrubRequest(
                AdministrativeTargetKey,
                Fingerprint,
                DocumentCacheAdministrativeCommandConfirmation.IntegrityScrub
            )
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.FailedNoMutation);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.PageInvalidationRetryExhausted);
        result.Mutated.Should().BeFalse();
        primitives
            .ScrubPageRequests.Select(request => (request.AfterDocumentId, request.PageSize))
            .Should()
            .Equal((0, 3), (0, 1), (0, 1));
        result
            .PhaseDiagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.CurrentPhase == DocumentCacheAdministrativeCommandPhase.ScrubScan
                && diagnostic.LastCompletedPhase == DocumentCacheAdministrativeCommandPhase.CaptureBoundary
                && diagnostic.DiagnosticCategory
                    == DocumentCacheAdministrativeDiagnosticCategory.PageInvalidationRetryExhausted
                && diagnostic.Retryable
                && diagnostic.AffectedDocumentIds.SequenceEqual(new long[] { 1L })
            );
    }

    [Test]
    public async Task It_classifies_sql_server_activation_prerequisite_timeout_as_provider_command_timeout()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(
            generation: 1,
            providerToken: RelationalProviderToken.SqlServer,
            lifecycle: DisabledLifecycle
        );
        var primitives = new StubAdministrativePrimitives(
            RelationalProviderToken.SqlServer,
            lifecycleReads:
            [
                DocumentCacheLifecycleReadResult.Success(DisabledLifecycle),
                DocumentCacheLifecycleReadResult.Success(DisabledLifecycle),
            ],
            activationPrerequisiteException: CreateSqlException(-2, "Execution Timeout Expired.")
        );
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([RuntimeContext(executionContext)]),
            new RecordingAdministrativeMutex(providerToken: RelationalProviderToken.SqlServer),
            primitives: primitives
        );
        var command = new DocumentCacheGuardedNewEmptyActivationCommand(runner);

        DocumentCacheAdministrativeCommandResult result = await command.ExecuteAsync(
            new DocumentCacheGuardedNewEmptyActivationRequest(
                AdministrativeTargetKey,
                Fingerprint,
                DocumentCacheAdministrativeCommandConfirmation.NewEmptyActivation
            )
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.FailedNoMutation);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.ProviderCommandTimeout);
        result.Mutated.Should().BeFalse();
        result
            .PhaseDiagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.DiagnosticCategory
                    == DocumentCacheAdministrativeDiagnosticCategory.ProviderCommandTimeout
                && !diagnostic.Retryable
            );
    }

    [Test]
    public async Task It_keeps_non_timeout_provider_failures_classified_as_unexpected()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(
            generation: 1,
            providerToken: RelationalProviderToken.SqlServer
        );
        var primitives = new StubAdministrativePrimitives(
            RelationalProviderToken.SqlServer,
            projectedStateEmptinessException: CreateSqlException(
                547,
                "The statement conflicted with a foreign key constraint."
            )
        );
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([RuntimeContext(executionContext)]),
            new RecordingAdministrativeMutex(providerToken: RelationalProviderToken.SqlServer),
            primitives: primitives
        );
        var workflow = new DelegatingWorkflow(
            preflight: static (context, _) => Task.FromResult(context.EligiblePreflightResult()),
            execute: async (context, cancellationToken) =>
            {
                context.EnterPhase(DocumentCacheAdministrativeCommandPhase.ClearWork);
                await context
                    .Primitives.ReadProjectedStateEmptinessAsync(
                        new RecordingWriteSession(RelationalProviderToken.SqlServer),
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                return context.Completed();
            }
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(Request(), workflow);

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.FailedNoMutation);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.UnexpectedProviderFailure);
        result.Mutated.Should().BeFalse();
    }

    [Test]
    public async Task It_preserves_baseline_high_water_context_when_workflow_timeout_expires()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(
            generation: 1,
            workflowTimeout: TimeSpan.FromMilliseconds(30)
        );
        DocumentCacheProjectionTargetRuntimeContext runtimeContext = RuntimeContext(executionContext);
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([runtimeContext]),
            new RecordingAdministrativeMutex()
        );
        var workflow = new DelegatingWorkflow(
            preflight: static (context, _) => Task.FromResult(context.EligiblePreflightResult()),
            execute: static async (context, cancellationToken) =>
            {
                context.EnterPhase(DocumentCacheAdministrativeCommandPhase.SeedBaseline);
                context.AddPhaseDiagnostic(
                    DocumentCacheAdministrativeDiagnosticCategory.BaselineHighWaterBackpressure,
                    "DocumentProjectionWork is at or above the baseline high-water mark.",
                    retryable: true,
                    affectedDocumentIds: [10, 11, 12]
                );

                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                return context.Completed();
            }
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(Request(), workflow);

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.FailedNoMutation);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.WorkflowTimeout);
        result.Mutated.Should().BeFalse();
        result
            .PhaseDiagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.CurrentPhase == DocumentCacheAdministrativeCommandPhase.SeedBaseline
                && diagnostic.DiagnosticCategory
                    == DocumentCacheAdministrativeDiagnosticCategory.BaselineHighWaterBackpressure
                && diagnostic.Retryable
                && diagnostic.AffectedDocumentIds.SequenceEqual(new long[] { 10L, 11L, 12L })
            );
        result
            .PhaseDiagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.CurrentPhase == DocumentCacheAdministrativeCommandPhase.SeedBaseline
                && diagnostic.DiagnosticCategory
                    == DocumentCacheAdministrativeDiagnosticCategory.WorkflowTimeout
                && !diagnostic.Retryable
            );
        result
            .PhaseDiagnostics.Should()
            .NotContain(diagnostic =>
                diagnostic.DiagnosticCategory
                == DocumentCacheAdministrativeDiagnosticCategory.PersistentPoison
            );
    }

    [Test]
    public async Task It_preserves_distinct_identical_phase_diagnostic_occurrences()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(generation: 1);
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([RuntimeContext(executionContext)]),
            new RecordingAdministrativeMutex()
        );
        var workflow = new DelegatingWorkflow(
            preflight: static (context, _) => Task.FromResult(context.EligiblePreflightResult()),
            execute: static (context, _) =>
            {
                context.EnterPhase(DocumentCacheAdministrativeCommandPhase.SeedBaseline);
                context.AddPhaseDiagnostic(
                    DocumentCacheAdministrativeDiagnosticCategory.BaselineHighWaterBackpressure,
                    "Backpressure observation repeated.",
                    retryable: true
                );

                ImmutableArray<DocumentCacheAdministrativePhaseDiagnostic> workflowDiagnostics =
                [
                    new(
                        DocumentCacheAdministrativeCommandPhase.SeedBaseline,
                        DocumentCacheAdministrativeCommandPhase.Preflight,
                        retryable: true,
                        DocumentCacheAdministrativeDiagnosticCategory.BaselineHighWaterBackpressure,
                        message: "Backpressure observation repeated."
                    ),
                ];

                return Task.FromResult(
                    new DocumentCacheAdministrativeCommandResult(
                        context.Request.Command,
                        context.Request.TargetKey,
                        DocumentCacheAdministrativeCommandStatus.Completed,
                        DocumentCacheAdministrativeCommandClassification.Succeeded,
                        context.Mutated,
                        context.TargetContext.Generation.Value,
                        context.TargetContext.TargetExecutionContext.PhysicalSourceFingerprint,
                        context.LifecycleObservation?.State,
                        context.LifecycleObservation?.CacheAheadRecoveryRequired,
                        workflowDiagnostics,
                        context.Request.AcceptedOfflineWriterAdmissionConfirmation,
                        context.ElapsedCommandTime
                    )
                );
            }
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(Request(), workflow);

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        result
            .PhaseDiagnostics.Should()
            .HaveCount(2)
            .And.OnlyContain(diagnostic =>
                diagnostic.DiagnosticCategory
                    == DocumentCacheAdministrativeDiagnosticCategory.BaselineHighWaterBackpressure
                && diagnostic.Message == "Backpressure observation repeated."
            );
    }

    [Test]
    public async Task It_bounds_repeated_phase_diagnostics_in_active_observations_and_completed_results()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(generation: 1);
        DocumentCacheProjectionObservationStore observationStore = new(new FixedTimeProvider(ObservedAt));
        DocumentCacheProjectionTargetRuntimeContext runtimeContext = RuntimeContext(
            executionContext,
            observationStore
        );
        observationStore.ObserveTarget(TargetHealth(executionContext));
        TaskCompletionSource diagnosticsAdded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseCommand = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = new DelegatingWorkflow(
            preflight: static (context, _) => Task.FromResult(context.EligiblePreflightResult()),
            execute: async (context, cancellationToken) =>
            {
                context.EnterPhase(DocumentCacheAdministrativeCommandPhase.SeedBaseline);
                AddBackpressureDiagnostics(context, count: 5);
                diagnosticsAdded.SetResult();

                await releaseCommand.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return context.Completed();
            }
        );
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([runtimeContext]),
            new RecordingAdministrativeMutex(),
            observationStore
        );

        Task<DocumentCacheAdministrativeCommandResult> resultTask = runner.ExecuteAsync(Request(), workflow);
        await diagnosticsAdded.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        try
        {
            DocumentCacheAdministrativeCommandObservationSnapshot activeCommand = observationStore
                .CurrentSnapshot.ActiveAdministrativeCommands.Values.Should()
                .ContainSingle()
                .Subject;
            activeCommand
                .PhaseDiagnostics.Should()
                .HaveCount(executionContext.EffectiveSettings.ProjectorPageSize);
            activeCommand
                .PhaseDiagnostics.Select(diagnostic => diagnostic.Message)
                .Should()
                .Equal(
                    "Backpressure observation 3.",
                    "Backpressure observation 4.",
                    "Backpressure observation 5."
                );
            activeCommand
                .PhaseDiagnostics.Should()
                .OnlyContain(diagnostic =>
                    diagnostic.AffectedDocumentIds.Length
                    == executionContext.EffectiveSettings.ProjectorPageSize
                );
        }
        finally
        {
            releaseCommand.SetResult();
        }

        DocumentCacheAdministrativeCommandResult result = await resultTask.ConfigureAwait(false);

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        result.PhaseDiagnostics.Should().HaveCount(executionContext.EffectiveSettings.ProjectorPageSize);
        result
            .PhaseDiagnostics.Select(diagnostic => diagnostic.Message)
            .Should()
            .Equal(
                "Backpressure observation 3.",
                "Backpressure observation 4.",
                "Backpressure observation 5."
            );

        DocumentCacheProjectionObservationSnapshot snapshot = observationStore.CurrentSnapshot;
        snapshot.ActiveAdministrativeCommands.Should().BeEmpty();
        DocumentCacheAdministrativeCommandEndedDiagnosticSnapshot endedDiagnostic =
            snapshot.GetCurrentGenerationEndedAdministrativeCommandDiagnostic(TargetKey)!;
        endedDiagnostic.Should().NotBeNull();
        endedDiagnostic.Outcome.Should().Be(DocumentCacheAdministrativeCommandEndedOutcome.Succeeded);
        endedDiagnostic.Phase.Should().Be(DocumentCacheAdministrativeCommandPhase.Complete);
        endedDiagnostic.Message.Should().Be("Backpressure observation 5.");
    }

    [Test]
    public async Task It_keeps_final_timeout_diagnostic_when_repeated_phase_diagnostics_reach_the_cap()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(
            generation: 1,
            workflowTimeout: TimeSpan.FromMilliseconds(30)
        );
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([RuntimeContext(executionContext)]),
            new RecordingAdministrativeMutex()
        );
        var workflow = new DelegatingWorkflow(
            preflight: static (context, _) => Task.FromResult(context.EligiblePreflightResult()),
            execute: static async (context, cancellationToken) =>
            {
                context.EnterPhase(DocumentCacheAdministrativeCommandPhase.SeedBaseline);
                AddBackpressureDiagnostics(context, count: 5);

                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                return context.Completed();
            }
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(Request(), workflow);

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.FailedNoMutation);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.WorkflowTimeout);
        result.PhaseDiagnostics.Should().HaveCount(executionContext.EffectiveSettings.ProjectorPageSize);
        result
            .PhaseDiagnostics.Select(diagnostic => diagnostic.DiagnosticCategory)
            .Should()
            .Equal(
                DocumentCacheAdministrativeDiagnosticCategory.BaselineHighWaterBackpressure,
                DocumentCacheAdministrativeDiagnosticCategory.BaselineHighWaterBackpressure,
                DocumentCacheAdministrativeDiagnosticCategory.WorkflowTimeout
            );
        result
            .PhaseDiagnostics.Select(diagnostic => diagnostic.Message)
            .Should()
            .Contain("Backpressure observation 4.", "Backpressure observation 5.");
        result
            .PhaseDiagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.DiagnosticCategory == DocumentCacheAdministrativeDiagnosticCategory.WorkflowTimeout
            );
        result
            .PhaseDiagnostics.Where(diagnostic =>
                diagnostic.DiagnosticCategory
                == DocumentCacheAdministrativeDiagnosticCategory.BaselineHighWaterBackpressure
            )
            .Should()
            .OnlyContain(diagnostic =>
                diagnostic.AffectedDocumentIds.Length == executionContext.EffectiveSettings.ProjectorPageSize
            );
    }

    [Test]
    public async Task It_keeps_active_command_observation_for_a_noncurrent_pinned_generation()
    {
        DocumentCacheTargetExecutionContext firstGeneration = ExecutionContext(generation: 1);
        DocumentCacheTargetExecutionContext replacementGeneration = ExecutionContext(generation: 2);
        DocumentCacheProjectionTargetRuntimeContext runtimeContext = RuntimeContext(firstGeneration);
        DocumentCacheProjectionObservationStore observationStore = new(new FixedTimeProvider(ObservedAt));
        observationStore.ObserveTarget(TargetHealth(firstGeneration));
        MutableTargetRegistry registry = RegistryFor(firstGeneration);
        TaskCompletionSource commandStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseCommand = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = new DelegatingWorkflow(
            preflight: static (context, _) => Task.FromResult(context.EligiblePreflightResult()),
            execute: async (context, cancellationToken) =>
            {
                context.EnterPhase(DocumentCacheAdministrativeCommandPhase.DrainWork);
                context.MarkMutated(
                    new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Rebuilding, false)
                );
                commandStarted.SetResult();
                await releaseCommand.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return context.Completed();
            }
        );
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            registry,
            new StubProjectionSupervisor([runtimeContext]),
            new RecordingAdministrativeMutex(),
            observationStore
        );

        Task<DocumentCacheAdministrativeCommandResult> resultTask = runner.ExecuteAsync(Request(), workflow);
        await commandStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        observationStore.CurrentSnapshot.ActiveAdministrativeCommands.Values.Should().ContainSingle();

        registry.CurrentRuntimeSnapshot = RuntimeSnapshot([replacementGeneration]);
        observationStore.ObserveTarget(TargetHealth(replacementGeneration));

        DocumentCacheAdministrativeCommandObservationSnapshot activeCommand = observationStore
            .CurrentSnapshot.ActiveAdministrativeCommands.Values.Should()
            .ContainSingle()
            .Subject;
        activeCommand.TargetGeneration.Value.Should().Be(1);
        activeCommand.IsCurrentGeneration.Should().BeFalse();
        activeCommand.CurrentTargetGeneration!.Value.Should().Be(2);

        releaseCommand.SetResult();
        DocumentCacheAdministrativeCommandResult result = await resultTask.ConfigureAwait(false);

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
        result
            .PhaseDiagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.DiagnosticCategory == DocumentCacheAdministrativeDiagnosticCategory.TargetReplaced
            );
        observationStore.CurrentSnapshot.ActiveAdministrativeCommands.Should().BeEmpty();
    }

    [Test]
    public async Task It_preserves_the_pinned_target_when_replaced_while_waiting_for_mutex()
    {
        DocumentCacheTargetExecutionContext firstGeneration = ExecutionContext(generation: 1);
        DocumentCacheTargetExecutionContext replacementGeneration = ExecutionContext(generation: 2);
        DocumentCacheProjectionObservationStore observationStore = new(new FixedTimeProvider(ObservedAt));
        var targetContextFactory = new RecordingTargetContextFactory(observationStore);
        MutableTargetRegistry registry = RegistryFor(firstGeneration);
        DocumentCacheProjectionSupervisor supervisor = CreateSupervisor(
            registry,
            targetContextFactory,
            observationStore
        );
        await supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);
        DocumentCacheProjectionTargetRuntimeContext oldContext =
            targetContextFactory.CreatedContexts.Single();
        TaskCompletionSource mutexWaitStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseMutex = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutex = new RecordingAdministrativeMutex(beforeAcquireCompletes: async cancellationToken =>
        {
            mutexWaitStarted.SetResult();
            await releaseMutex.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        });
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            registry,
            supervisor,
            mutex,
            observationStore
        );

        Task<DocumentCacheAdministrativeCommandResult> resultTask = runner.ExecuteAsync(
            Request(),
            SucceedingWorkflow.Instance
        );
        await mutexWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        SetCurrentTarget(registry, replacementGeneration);
        await supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.SupervisorTriggered);

        oldContext.CancellationRequested.Should().BeFalse();
        supervisor.CurrentTargetContexts.Should().ContainSingle().Which.Generation.Value.Should().Be(2);

        releaseMutex.SetResult();
        DocumentCacheAdministrativeCommandResult result = await resultTask
            .WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);

        AssertCompletedAgainstPinnedNoncurrentGeneration(result);
        oldContext.CancellationRequested.Should().BeTrue();
        observationStore
            .CurrentSnapshot.LastEndedTargetDiagnostics.Values.Should()
            .Contain(diagnostic =>
                diagnostic.ContextKey.Equals(oldContext.ContextKey)
                && diagnostic.EndReason == DocumentCacheProjectionTargetEndReason.Replaced
            );
    }

    [Test]
    public async Task It_does_not_reject_target_replacement_after_mutex_acquisition_before_workflow_execution()
    {
        DocumentCacheTargetExecutionContext firstGeneration = ExecutionContext(generation: 1);
        DocumentCacheTargetExecutionContext replacementGeneration = ExecutionContext(generation: 2);
        DocumentCacheProjectionObservationStore observationStore = new(new FixedTimeProvider(ObservedAt));
        var targetContextFactory = new RecordingTargetContextFactory(observationStore);
        MutableTargetRegistry registry = RegistryFor(firstGeneration);
        DocumentCacheProjectionSupervisor supervisor = CreateSupervisor(
            registry,
            targetContextFactory,
            observationStore
        );
        await supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);
        DocumentCacheProjectionTargetRuntimeContext oldContext =
            targetContextFactory.CreatedContexts.Single();
        bool workflowRan = false;
        var workflow = new DelegatingWorkflow(
            preflight: (context, _) =>
            {
                workflowRan = true;
                return Task.FromResult(context.EligiblePreflightResult());
            },
            execute: static (context, _) => Task.FromResult(context.Completed())
        );
        var mutex = new RecordingAdministrativeMutex(afterAcquireCompletes: async cancellationToken =>
        {
            SetCurrentTarget(registry, replacementGeneration);
            await supervisor
                .RefreshAsync(DocumentCacheTargetRefreshReason.SupervisorTriggered, cancellationToken)
                .ConfigureAwait(false);
        });
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            registry,
            supervisor,
            mutex,
            observationStore
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(Request(), workflow);

        workflowRan.Should().BeTrue();
        AssertCompletedAgainstPinnedNoncurrentGeneration(result);
        oldContext.CancellationRequested.Should().BeTrue();
        supervisor.CurrentTargetContexts.Should().ContainSingle().Which.Generation.Value.Should().Be(2);
    }

    [Test]
    public async Task It_classifies_mutex_acquisition_failure_before_mutation()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(generation: 1);
        var mutex = new RecordingAdministrativeMutex(
            acquireException: new InvalidOperationException("provider failed")
        );
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([RuntimeContext(executionContext)]),
            mutex
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(
            Request(),
            SucceedingWorkflow.Instance
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.FailedNoMutation);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.MutexAcquisitionFailed);
        result.Mutated.Should().BeFalse();
        result.ElapsedCommandTime.Should().BeNull();
        result
            .PhaseDiagnostics.Should()
            .ContainSingle()
            .Which.CurrentPhase.Should()
            .Be(DocumentCacheAdministrativeCommandPhase.AcquireMutex);
    }

    [Test]
    public async Task It_preserves_completed_result_when_mutex_lease_cleanup_fails_after_workflow_execution()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(generation: 1);
        var cleanupException = new InvalidOperationException("release failed");
        var mutex = new RecordingAdministrativeMutex(disposeException: cleanupException);
        var logger = new CapturingLogger<DocumentCacheAdministrativeCommandRunner>();
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([RuntimeContext(executionContext)]),
            mutex,
            logger: logger
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(
            Request(),
            SucceedingWorkflow.Instance
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
        result.Mutated.Should().BeFalse();
        mutex.LastLease.Should().NotBeNull();
        mutex.LastLease!.DisposeCount.Should().Be(1);
        logger
            .Entries.Should()
            .ContainSingle(entry =>
                entry.Level == LogLevel.Warning
                && entry.Exception == cleanupException
                && entry.Message.Contains("mutex cleanup failed", StringComparison.Ordinal)
            );
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_preserves_command_result_when_observation_sink_end_cleanup_fails(
        bool classifiedFailure
    )
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(generation: 1);
        var cleanupException = new InvalidOperationException("observation end failed");
        var observationSink = new ThrowingEndAdministrativeCommandObservationSink(cleanupException);
        var logger = new CapturingLogger<DocumentCacheAdministrativeCommandRunner>();
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([RuntimeContext(executionContext)]),
            new RecordingAdministrativeMutex(),
            observationSink,
            logger: logger
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(
            Request(),
            CleanupPreservationWorkflow(classifiedFailure)
        );

        AssertCleanupPreservedResult(result, classifiedFailure);
        observationSink.EndAdministrativeCommandCount.Should().Be(1);
        logger
            .Entries.Should()
            .ContainSingle(entry =>
                entry.Level == LogLevel.Warning
                && entry.Exception == cleanupException
                && entry.Message.Contains("observation cleanup failed", StringComparison.Ordinal)
                && entry.Message.Contains("ClassifiedResultPreserved: True", StringComparison.Ordinal)
            );
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_preserves_command_result_when_retained_target_context_release_cleanup_fails(
        bool classifiedFailure
    )
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(generation: 1);
        DocumentCacheProjectionTargetRuntimeContext runtimeContext = RuntimeContext(executionContext);
        var cleanupException = new InvalidOperationException("retained release failed");
        var supervisor = new StubProjectionSupervisor([runtimeContext], cleanupException);
        var logger = new CapturingLogger<DocumentCacheAdministrativeCommandRunner>();
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            supervisor,
            new RecordingAdministrativeMutex(),
            logger: logger
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(
            Request(),
            CleanupPreservationWorkflow(classifiedFailure)
        );

        AssertCleanupPreservedResult(result, classifiedFailure);
        supervisor.ReleaseRetainedCommandOwnedTargetContextCount.Should().Be(1);
        supervisor.LastReleasedRetainedTargetContext.Should().BeSameAs(runtimeContext);
        logger
            .Entries.Should()
            .ContainSingle(entry =>
                entry.Level == LogLevel.Warning
                && entry.Exception == cleanupException
                && entry.Message.Contains("retained target-context cleanup failed", StringComparison.Ordinal)
                && entry.Message.Contains("ClassifiedResultPreserved: True", StringComparison.Ordinal)
            );
    }

    [Test]
    public async Task It_preserves_session_loss_classification_when_mutex_lease_cleanup_also_fails()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(generation: 1);
        var mutex = new RecordingAdministrativeMutex(
            disposeException: new InvalidOperationException("release failed")
        );
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([RuntimeContext(executionContext)]),
            mutex
        );
        var workflow = new DelegatingWorkflow(
            preflight: static (context, _) => Task.FromResult(context.EligiblePreflightResult()),
            execute: static (context, _) =>
            {
                context.EnterPhase(DocumentCacheAdministrativeCommandPhase.DrainWork);
                context.MarkMutated(
                    new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Rebuilding, false)
                );
                throw new DocumentCacheAdministrativeMutexSessionLostException(
                    context.MutexLease.ProviderToken
                );
            }
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(Request(), workflow);

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.IncompleteRetryable);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.SessionLossAfterMutation);
        result.Mutated.Should().BeTrue();
        mutex.LastLease.Should().NotBeNull();
        mutex.LastLease!.DisposeCount.Should().Be(1);
    }

    [Test]
    public async Task It_classifies_bounded_clear_commit_session_loss_after_marking_the_command_mutated()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(generation: 1);
        RecordingMutexLease lease = LeaseWith(NormalSession(), SessionLosingCommit());
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([RuntimeContext(executionContext)]),
            new RecordingAdministrativeMutex(lease: lease)
        );
        var workflow = new DelegatingWorkflow(
            preflight: static (context, _) => Task.FromResult(context.EligiblePreflightResult()),
            execute: async (context, cancellationToken) =>
            {
                context.EnterPhase(DocumentCacheAdministrativeCommandPhase.ClearCache);
                await DocumentCacheAdministrativeWorkflow
                    .ExecuteInTransactionAsync(
                        context,
                        IsolationLevel.ReadCommitted,
                        static (_, _) =>
                            Task.FromResult(
                                new DocumentCacheAdministrativeClearBatchResult(
                                    DocumentCacheAdministrativeClearTarget.DocumentCache,
                                    pageSize: 3,
                                    clearedDocumentIds: [101],
                                    "cleared"
                                )
                            ),
                        commit: true,
                        cancellationToken,
                        beforeCommit: batch =>
                        {
                            if (batch.Mutated)
                            {
                                context.MarkMutated();
                            }
                        }
                    )
                    .ConfigureAwait(false);

                return context.Completed();
            }
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(Request(), workflow);

        AssertSessionLossAfterMutation(result, DocumentCacheAdministrativeCommandPhase.ClearCache);
    }

    [Test]
    public async Task It_continues_online_rebuild_cache_clearing_after_a_cascade_short_mutated_batch()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(generation: 1);
        var primitives = new StubAdministrativePrimitives(
            cacheClearBatches:
            [
                ClearBatch(DocumentCacheAdministrativeClearTarget.DocumentCache, 101),
                ClearBatch(DocumentCacheAdministrativeClearTarget.DocumentCache, 202),
                ClearBatch(DocumentCacheAdministrativeClearTarget.DocumentCache),
            ],
            projectedStateEmptiness: (cacheClearCallCount, _) =>
                new DocumentCacheAdministrativeProjectedStateEmptinessResult(
                    documentCacheEmpty: cacheClearCallCount >= 3,
                    documentProjectionWorkEmpty: true,
                    "Projected state is empty after all clear batches."
                )
        );
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([RuntimeContext(executionContext)]),
            new RecordingAdministrativeMutex(),
            primitives: primitives
        );
        var seeder = new SucceedingBaselineSeeder();
        var drainer = new SucceedingAdministrativeDrainer();
        var command = new DocumentCacheOnlineCacheRebuildCommand(runner, seeder, drainer);

        DocumentCacheAdministrativeCommandResult result = await command.ExecuteAsync(
            new DocumentCacheOnlineCacheRebuildRequest(
                AdministrativeTargetKey,
                Fingerprint,
                DocumentCacheAdministrativeCommandConfirmation.OnlineCacheRebuild
            )
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
        result.Mutated.Should().BeTrue();
        primitives.CacheClearCallCount.Should().Be(3);
        seeder.SeedCallCount.Should().Be(1);
        drainer.DrainToEmptyCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_classifies_read_only_commit_session_loss_before_mutation()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(generation: 1);
        RecordingMutexLease lease = LeaseWith(NormalSession(), SessionLosingCommit());
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([RuntimeContext(executionContext)]),
            new RecordingAdministrativeMutex(lease: lease)
        );
        var workflow = new DelegatingWorkflow(
            preflight: static (context, _) => Task.FromResult(context.EligiblePreflightResult()),
            execute: async (context, cancellationToken) =>
            {
                context.EnterPhase(DocumentCacheAdministrativeCommandPhase.CaptureBoundary);
                await DocumentCacheAdministrativeWorkflow
                    .ExecuteInTransactionAsync(
                        context,
                        IsolationLevel.ReadCommitted,
                        static (_, _) => Task.FromResult(true),
                        commit: true,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                return context.Completed();
            }
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(Request(), workflow);

        AssertSessionLossNoMutation(result, DocumentCacheAdministrativeCommandPhase.CaptureBoundary);
    }

    [Test]
    public async Task It_classifies_baseline_seed_commit_session_loss_after_work_mutation()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(generation: 1);
        var primitives = new StubAdministrativePrimitives(
            baselineBoundary: new DocumentCacheAdministrativeBaselineBoundaryResult(1, "boundary"),
            highWaterObservations: [HighWaterBelow()],
            seedPages:
            [
                BaselineSeedPage(
                    DocumentCacheAdministrativeBaselineSeedPageStatus.PageSeeded,
                    new DocumentCacheAdministrativeBaselineSeededDocument(
                        1,
                        sourceContentVersion: 10,
                        previousRequiredContentVersion: null,
                        DocumentCacheAdministrativeBaselineWorkMutationKind.Inserted
                    )
                ),
            ]
        );
        RecordingMutexLease lease = LeaseWith(
            NormalSession(),
            NormalSession(),
            NormalSession(),
            SessionLosingCommit()
        );
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([RuntimeContext(executionContext)]),
            new RecordingAdministrativeMutex(lease: lease),
            primitives: primitives
        );
        var seeder = new DocumentCacheBaselineSeeder(
            new DocumentCacheBaselineSeedDelay(),
            new FixedTimeProvider(ObservedAt),
            NullLogger<DocumentCacheBaselineSeeder>.Instance
        );
        var workflow = new DelegatingWorkflow(
            preflight: static (context, _) => Task.FromResult(context.EligiblePreflightResult()),
            execute: async (context, cancellationToken) =>
            {
                await seeder.SeedAsync(context, cancellationToken).ConfigureAwait(false);
                return context.Completed();
            }
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(Request(), workflow);

        AssertSessionLossAfterMutation(
            result,
            DocumentCacheAdministrativeCommandPhase.SeedBaseline,
            DocumentCacheAdministrativeCommandPhase.CaptureBoundary
        );
    }

    [Test]
    public async Task It_does_not_mark_seed_retry_rollback_session_loss_as_mutated()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(generation: 1);
        var primitives = new StubAdministrativePrimitives(
            baselineBoundary: new DocumentCacheAdministrativeBaselineBoundaryResult(1, "boundary"),
            highWaterObservations: [HighWaterBelow()],
            seedPages:
            [
                BaselineSeedPage(
                    DocumentCacheAdministrativeBaselineSeedPageStatus.RetryFromLastCommittedKey,
                    new DocumentCacheAdministrativeBaselineSeededDocument(
                        1,
                        sourceContentVersion: 10,
                        previousRequiredContentVersion: 9,
                        DocumentCacheAdministrativeBaselineWorkMutationKind.Retry
                    )
                ),
            ]
        );
        RecordingMutexLease lease = LeaseWith(
            NormalSession(),
            NormalSession(),
            NormalSession(),
            SessionLosingRollback()
        );
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([RuntimeContext(executionContext)]),
            new RecordingAdministrativeMutex(lease: lease),
            primitives: primitives
        );
        var seeder = new DocumentCacheBaselineSeeder(
            new DocumentCacheBaselineSeedDelay(),
            new FixedTimeProvider(ObservedAt),
            NullLogger<DocumentCacheBaselineSeeder>.Instance
        );
        var workflow = new DelegatingWorkflow(
            preflight: static (context, _) => Task.FromResult(context.EligiblePreflightResult()),
            execute: async (context, cancellationToken) =>
            {
                await seeder.SeedAsync(context, cancellationToken).ConfigureAwait(false);
                return context.Completed();
            }
        );

        DocumentCacheAdministrativeCommandResult result = await runner.ExecuteAsync(Request(), workflow);

        AssertSessionLossNoMutation(
            result,
            DocumentCacheAdministrativeCommandPhase.SeedBaseline,
            DocumentCacheAdministrativeCommandPhase.CaptureBoundary
        );
    }

    [Test]
    public async Task It_classifies_completed_explicit_scrub_latch_set_as_cache_ahead_latch_set()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(generation: 1);
        var primitives = new StubAdministrativePrimitives(
            lifecycleReads:
            [
                DocumentCacheLifecycleReadResult.Success(TrackingLifecycle),
                DocumentCacheLifecycleReadResult.Success(TrackingLifecycle),
                DocumentCacheLifecycleReadResult.Success(TrackingLifecycle),
            ],
            baselineBoundary: new DocumentCacheAdministrativeBaselineBoundaryResult(1, "boundary"),
            scrubPages:
            [
                ScrubPage(
                    DocumentCacheAdministrativeScrubPageStatus.CacheAheadLatched,
                    new DocumentCacheAdministrativeScrubbedDocument(
                        1,
                        sourceContentVersion: 10,
                        cacheContentVersion: 8,
                        previousRequiredContentVersion: null,
                        DocumentCacheAdministrativeScrubMutationKind.CacheAheadLatchSet
                    )
                ),
            ]
        );
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([RuntimeContext(executionContext)]),
            new RecordingAdministrativeMutex(),
            primitives: primitives
        );
        var command = new DocumentCacheExplicitIntegrityScrubCommand(runner);

        DocumentCacheAdministrativeCommandResult result = await command.ExecuteAsync(
            new DocumentCacheExplicitIntegrityScrubRequest(
                AdministrativeTargetKey,
                Fingerprint,
                DocumentCacheAdministrativeCommandConfirmation.IntegrityScrub
            )
        );

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.CacheAheadLatchSet);
        result.Mutated.Should().BeTrue();
        result.CacheAheadRecoveryRequired.Should().BeTrue();
        result
            .PhaseDiagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.CurrentPhase == DocumentCacheAdministrativeCommandPhase.SetCacheAheadLatch
                && diagnostic.DiagnosticCategory
                    == DocumentCacheAdministrativeDiagnosticCategory.CacheAheadLatchSet
            );
    }

    [Test]
    public async Task It_classifies_explicit_scrub_latch_commit_session_loss_after_mutation()
    {
        DocumentCacheTargetExecutionContext executionContext = ExecutionContext(generation: 1);
        var primitives = new StubAdministrativePrimitives(
            lifecycleReads:
            [
                DocumentCacheLifecycleReadResult.Success(TrackingLifecycle),
                DocumentCacheLifecycleReadResult.Success(TrackingLifecycle),
                DocumentCacheLifecycleReadResult.Success(TrackingLifecycle),
            ],
            baselineBoundary: new DocumentCacheAdministrativeBaselineBoundaryResult(1, "boundary"),
            scrubPages:
            [
                ScrubPage(
                    DocumentCacheAdministrativeScrubPageStatus.CacheAheadLatched,
                    new DocumentCacheAdministrativeScrubbedDocument(
                        1,
                        sourceContentVersion: 10,
                        cacheContentVersion: 8,
                        previousRequiredContentVersion: null,
                        DocumentCacheAdministrativeScrubMutationKind.CacheAheadLatchSet
                    )
                ),
            ]
        );
        RecordingMutexLease lease = LeaseWith(
            NormalSession(),
            NormalSession(),
            NormalSession(),
            SessionLosingCommit()
        );
        DocumentCacheAdministrativeCommandRunner runner = CreateRunner(
            RegistryFor(executionContext),
            new StubProjectionSupervisor([RuntimeContext(executionContext)]),
            new RecordingAdministrativeMutex(lease: lease),
            primitives: primitives
        );
        var command = new DocumentCacheExplicitIntegrityScrubCommand(runner);

        DocumentCacheAdministrativeCommandResult result = await command.ExecuteAsync(
            new DocumentCacheExplicitIntegrityScrubRequest(
                AdministrativeTargetKey,
                Fingerprint,
                DocumentCacheAdministrativeCommandConfirmation.IntegrityScrub
            )
        );

        AssertSessionLossAfterMutation(
            result,
            DocumentCacheAdministrativeCommandPhase.ScrubScan,
            DocumentCacheAdministrativeCommandPhase.CaptureBoundary
        );
        result.CacheAheadRecoveryRequired.Should().BeTrue();
    }

    private static DocumentCacheAdministrativeCommandRunner CreateRunner(
        IDocumentCacheTargetRegistry registry,
        IDocumentCacheProjectionSupervisor supervisor,
        IDocumentCacheAdministrativeMutex mutex,
        IDocumentCacheProjectionObservationSink? observationSink = null,
        IDocumentCacheAdministrativePrimitives? primitives = null,
        ILogger<DocumentCacheAdministrativeCommandRunner>? logger = null,
        DeadlockRetrySettings? providerConcurrencyRetrySettings = null,
        IRelationalWriteExceptionClassifier? writeExceptionClassifier = null,
        IDocumentCacheProviderCommandTimeoutClassifier? providerCommandTimeoutClassifier = null
    )
    {
        DocumentCacheProjectionObservationStore defaultObservationStore = new(
            new FixedTimeProvider(ObservedAt)
        );
        IDocumentCacheProjectionObservationSink sink = observationSink ?? defaultObservationStore;

        return new(
            supervisor,
            registry,
            mutex,
            primitives ?? new StubAdministrativePrimitives(mutex.ProviderToken),
            sink,
            new FixedTimeProvider(ObservedAt),
            providerCommandTimeoutClassifier ?? ProviderCommandTimeoutClassifierFor(mutex.ProviderToken),
            logger ?? NullLogger<DocumentCacheAdministrativeCommandRunner>.Instance,
            telemetry: null,
            providerConcurrencyRetrySettings: providerConcurrencyRetrySettings,
            writeExceptionClassifier: writeExceptionClassifier
        );
    }

    private static IDocumentCacheProviderCommandTimeoutClassifier ProviderCommandTimeoutClassifierFor(
        RelationalProviderToken providerToken
    ) =>
        providerToken == RelationalProviderToken.SqlServer
            ? new MssqlDocumentCacheProviderCommandTimeoutClassifier()
            : new PostgresqlDocumentCacheProviderCommandTimeoutClassifier();

    private static DeadlockRetrySettings ProviderConcurrencyRetrySettings(int maxRetryAttempts) =>
        new()
        {
            MaxRetryAttempts = maxRetryAttempts,
            BaseDelayMilliseconds = 1,
            UseJitter = false,
        };

    private static DocumentCacheProjectionSupervisor CreateSupervisor(
        IDocumentCacheTargetRegistry registry,
        IDocumentCacheProjectionTargetRuntimeContextFactory targetContextFactory,
        IDocumentCacheProjectionObservationSink observationSink
    ) =>
        new(
            registry,
            targetContextFactory,
            observationSink,
            OptionsFor([TargetKey]),
            new NoOpDocumentCacheProjectionScheduler(),
            new StubDocumentCacheLifecycleReader(),
            new FixedTimeProvider(ObservedAt),
            NullLogger<DocumentCacheProjectionSupervisor>.Instance
        );

    private static void SetCurrentTarget(
        MutableTargetRegistry registry,
        DocumentCacheTargetExecutionContext executionContext
    )
    {
        registry.CurrentSnapshot = Snapshot([EligibleObservation(executionContext)]);
        registry.CurrentRuntimeSnapshot = RuntimeSnapshot([executionContext]);
    }

    private static void AssertCompletedAgainstPinnedNoncurrentGeneration(
        DocumentCacheAdministrativeCommandResult result
    )
    {
        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
        result.Mutated.Should().BeFalse();
        result.TargetGeneration.Should().Be(1);
        result
            .PhaseDiagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.DiagnosticCategory == DocumentCacheAdministrativeDiagnosticCategory.TargetReplaced
            );
    }

    private static IDocumentCacheAdministrativeCommandWorkflow CleanupPreservationWorkflow(
        bool classifiedFailure
    ) =>
        classifiedFailure
            ? new DelegatingWorkflow(
                preflight: static (context, _) => Task.FromResult(context.EligiblePreflightResult()),
                execute: static (context, _) =>
                {
                    context.EnterPhase(DocumentCacheAdministrativeCommandPhase.ClearCache);
                    return Task.FromResult(
                        context.Failed(
                            DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
                            DocumentCacheAdministrativeCommandClassification.LifecycleMismatch,
                            DocumentCacheAdministrativeDiagnosticCategory.LifecycleMismatch,
                            "Classified command failure before cleanup.",
                            retryable: false
                        )
                    );
                }
            )
            : SucceedingWorkflow.Instance;

    private static void AssertCleanupPreservedResult(
        DocumentCacheAdministrativeCommandResult result,
        bool classifiedFailure
    )
    {
        if (classifiedFailure)
        {
            result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.FailedNoMutation);
            result
                .Classification.Should()
                .Be(DocumentCacheAdministrativeCommandClassification.LifecycleMismatch);
            result.Mutated.Should().BeFalse();
            result
                .PhaseDiagnostics.Should()
                .ContainSingle(diagnostic =>
                    diagnostic.DiagnosticCategory
                    == DocumentCacheAdministrativeDiagnosticCategory.LifecycleMismatch
                );
            return;
        }

        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed);
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
        result.Mutated.Should().BeFalse();
    }

    private static DocumentCacheAdministrativeCommandRunnerRequest Request() =>
        new(
            DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
            AdministrativeTargetKey,
            expectedPhysicalSourceFingerprint: Fingerprint,
            confirmation: DocumentCacheAdministrativeCommandConfirmation.OnlineCacheRebuild
        );

    private static DelegatingWorkflow ThrowingWorkflow() =>
        new(
            preflight: static (_, _) => throw new AssertionException("Preflight must not run."),
            execute: static (_, _) => throw new AssertionException("Command work must not run.")
        );

    private static void AssertRejectedCommandConfirmation(
        DocumentCacheAdministrativeCommandResult result,
        DocumentCacheAdministrativeCommandClassification expectedClassification,
        DocumentCacheAdministrativeDiagnosticCategory expectedDiagnosticCategory
    )
    {
        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation);
        result.Classification.Should().Be(expectedClassification);
        result.Mutated.Should().BeFalse();
        result.ElapsedCommandTime.Should().BeNull();
        result.OfflineWriterAdmission.Should().BeNull();
        result
            .PhaseDiagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.CurrentPhase == DocumentCacheAdministrativeCommandPhase.Preflight
                && diagnostic.DiagnosticCategory == expectedDiagnosticCategory
            );
    }

    private static void AssertSessionLossAfterMutation(
        DocumentCacheAdministrativeCommandResult result,
        DocumentCacheAdministrativeCommandPhase expectedPhase,
        DocumentCacheAdministrativeCommandPhase expectedLastCompletedPhase =
            DocumentCacheAdministrativeCommandPhase.Preflight
    )
    {
        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.IncompleteRetryable);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.SessionLossAfterMutation);
        result.Mutated.Should().BeTrue();
        result
            .PhaseDiagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.CurrentPhase == expectedPhase
                && diagnostic.LastCompletedPhase == expectedLastCompletedPhase
                && diagnostic.DiagnosticCategory == DocumentCacheAdministrativeDiagnosticCategory.SessionLoss
                && diagnostic.Retryable
            );
    }

    private static void AssertSessionLossNoMutation(
        DocumentCacheAdministrativeCommandResult result,
        DocumentCacheAdministrativeCommandPhase expectedPhase,
        DocumentCacheAdministrativeCommandPhase expectedLastCompletedPhase =
            DocumentCacheAdministrativeCommandPhase.Preflight
    )
    {
        result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.FailedNoMutation);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.SessionLossNoMutation);
        result.Mutated.Should().BeFalse();
        result
            .PhaseDiagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.CurrentPhase == expectedPhase
                && diagnostic.LastCompletedPhase == expectedLastCompletedPhase
                && diagnostic.DiagnosticCategory == DocumentCacheAdministrativeDiagnosticCategory.SessionLoss
                && !diagnostic.Retryable
            );
    }

    private static RecordingMutexLease LeaseWith(params RecordingWriteSession[] sessions) =>
        new(RelationalProviderToken.Postgresql, null, sessions);

    private static RecordingWriteSession NormalSession() => new(RelationalProviderToken.Postgresql);

    private static RecordingWriteSession SessionLosingCommit() =>
        new(
            RelationalProviderToken.Postgresql,
            commitAsync: static (session, _) =>
            {
                session.LoseMutexSession();
                throw new InvalidOperationException("Administrative mutex session closed during commit.");
            }
        );

    private static RecordingWriteSession SessionLosingRollback() =>
        new(
            RelationalProviderToken.Postgresql,
            rollbackAsync: static (session, _) =>
            {
                session.LoseMutexSession();
                throw new InvalidOperationException("Administrative mutex session closed during rollback.");
            }
        );

    private static DocumentCacheAdministrativeWorkHighWaterObservationResult HighWaterBelow() =>
        new(highWaterMark: 1000, observedWorkRows: 0, diagnosticDocumentIds: [], "below high-water");

    private static DocumentCacheAdministrativeBaselineSeedPageResult BaselineSeedPage(
        DocumentCacheAdministrativeBaselineSeedPageStatus status,
        params DocumentCacheAdministrativeBaselineSeededDocument[] documents
    ) => new(status, boundaryDocumentId: 1, afterDocumentId: 0, pageSize: 3, [.. documents], "seed page");

    private static DocumentCacheAdministrativeScrubPageResult ScrubPage(
        DocumentCacheAdministrativeScrubPageStatus status,
        params DocumentCacheAdministrativeScrubbedDocument[] documents
    ) => new(status, boundaryDocumentId: 1, afterDocumentId: 0, pageSize: 3, [.. documents], "scrub page");

    private static DocumentCacheAdministrativeScrubPageResult ScrubRetryPage(
        long boundaryDocumentId,
        long afterDocumentId,
        int pageSize
    ) =>
        new(
            DocumentCacheAdministrativeScrubPageStatus.RetryFromLastCommittedKey,
            boundaryDocumentId,
            afterDocumentId,
            pageSize,
            [
                new DocumentCacheAdministrativeScrubbedDocument(
                    afterDocumentId + 1,
                    sourceContentVersion: 10,
                    cacheContentVersion: null,
                    previousRequiredContentVersion: 9,
                    DocumentCacheAdministrativeScrubMutationKind.Retry
                ),
            ],
            "retry scrub page"
        );

    private static DocumentCacheAdministrativeClearBatchResult ClearBatch(
        DocumentCacheAdministrativeClearTarget target,
        params long[] clearedDocumentIds
    ) =>
        new(
            target,
            pageSize: 3,
            [.. clearedDocumentIds],
            clearedDocumentIds.Length == 0 ? "empty clear batch" : "short mutated clear batch"
        );

    private static IEnumerable<TestCaseData> InvalidOfflineWriterAdmissionRequests()
    {
        yield return new TestCaseData(
            DocumentCacheAdministrativeCommand.OfflineActivation,
            null,
            DocumentCacheAdministrativeCommandClassification.MissingOfflineWriterAdmission,
            DocumentCacheAdministrativeDiagnosticCategory.MissingOfflineWriterAdmission
        ).SetName("Missing admission");

        yield return new TestCaseData(
            DocumentCacheAdministrativeCommand.OfflineDeactivation,
            new DocumentCacheOfflineWriterAdmission(
                confirmed: false,
                DocumentCacheOfflineWriterAdmissionConfirmation.OfflineDeactivationWritersClosedAndDrained
            ),
            DocumentCacheAdministrativeCommandClassification.UnconfirmedOfflineWriterAdmission,
            DocumentCacheAdministrativeDiagnosticCategory.UnconfirmedOfflineWriterAdmission
        ).SetName("Unconfirmed admission");

        yield return new TestCaseData(
            DocumentCacheAdministrativeCommand.InternalOnlyCacheAheadRecovery,
            new DocumentCacheOfflineWriterAdmission(
                confirmed: true,
                DocumentCacheOfflineWriterAdmissionConfirmation.OfflineActivationWritersClosedAndDrained
            ),
            DocumentCacheAdministrativeCommandClassification.MismatchedOfflineWriterAdmission,
            DocumentCacheAdministrativeDiagnosticCategory.MismatchedOfflineWriterAdmission
        ).SetName("Mismatched admission");
    }

    private static IEnumerable<TestCaseData> AdministrativeCommands()
    {
        return Enum.GetValues<DocumentCacheAdministrativeCommand>()
            .Select(command => new TestCaseData(command).SetName($"Command {command}"));
    }

    private static DocumentCacheOfflineWriterAdmission? OfflineWriterAdmissionFor(
        DocumentCacheAdministrativeCommand command
    ) =>
        command switch
        {
            DocumentCacheAdministrativeCommand.OfflineActivation => new(
                confirmed: true,
                DocumentCacheOfflineWriterAdmissionConfirmation.OfflineActivationWritersClosedAndDrained
            ),
            DocumentCacheAdministrativeCommand.OfflineDeactivation => new(
                confirmed: true,
                DocumentCacheOfflineWriterAdmissionConfirmation.OfflineDeactivationWritersClosedAndDrained
            ),
            DocumentCacheAdministrativeCommand.InternalOnlyCacheAheadRecovery => new(
                confirmed: true,
                DocumentCacheOfflineWriterAdmissionConfirmation.InternalOnlyCacheAheadRecoveryWritersClosedAndDrained
            ),
            _ => null,
        };

    private static DocumentCacheAdministrativeCommandConfirmation WrongConfirmationFor(
        DocumentCacheAdministrativeCommand command
    )
    {
        DocumentCacheAdministrativeCommandConfirmation expectedConfirmation =
            DocumentCachePreflightClassifier.ExpectedCommandConfirmation(command);
        return expectedConfirmation == DocumentCacheAdministrativeCommandConfirmation.NewEmptyActivation
            ? DocumentCacheAdministrativeCommandConfirmation.OnlineCacheRebuild
            : DocumentCacheAdministrativeCommandConfirmation.NewEmptyActivation;
    }

    private static IEnumerable<TestCaseData> ProviderCommandTimeoutCases()
    {
        foreach (bool commandMutated in new[] { false, true })
        {
            DocumentCacheAdministrativeCommandStatus expectedStatus = commandMutated
                ? DocumentCacheAdministrativeCommandStatus.IncompleteRetryable
                : DocumentCacheAdministrativeCommandStatus.FailedNoMutation;

            yield return new TestCaseData(
                RelationalProviderToken.SqlServer,
                CreateSqlException(-2, "Execution Timeout Expired."),
                commandMutated,
                expectedStatus
            ).SetName(
                commandMutated
                    ? "SQL Server command timeout after mutation"
                    : "SQL Server command timeout before mutation"
            );

            yield return new TestCaseData(
                RelationalProviderToken.Postgresql,
                CreatePostgresException("57014"),
                commandMutated,
                expectedStatus
            ).SetName(
                commandMutated
                    ? "PostgreSQL query canceled after mutation"
                    : "PostgreSQL query canceled before mutation"
            );

            yield return new TestCaseData(
                RelationalProviderToken.Postgresql,
                new NpgsqlException(
                    "Exception while reading from stream.",
                    new TimeoutException("The operation has timed out.")
                ),
                commandMutated,
                expectedStatus
            ).SetName(
                commandMutated
                    ? "PostgreSQL timeout wrapper after mutation"
                    : "PostgreSQL timeout wrapper before mutation"
            );
        }
    }

    private static IEnumerable<TestCaseData> ProviderConcurrencyRetryExhaustionCases()
    {
        yield return new TestCaseData(
            RelationalProviderToken.Postgresql,
            CreatePostgresException(PostgresErrorCodes.SerializationFailure),
            new PostgresqlRelationalWriteExceptionClassifier()
        ).SetName("PostgreSQL serialization failure 40001");

        yield return new TestCaseData(
            RelationalProviderToken.SqlServer,
            CreateSqlException(1205, "Transaction was deadlocked on lock resources."),
            new MssqlRelationalWriteExceptionClassifier()
        ).SetName("SQL Server deadlock victim 1205");
    }

    private static MutableTargetRegistry RegistryFor(DocumentCacheTargetExecutionContext executionContext) =>
        new(Snapshot([EligibleObservation(executionContext)]), RuntimeSnapshot([executionContext]));

    private static IOptions<DocumentCacheOptions> OptionsFor(IEnumerable<DocumentCacheTargetKey> targetKeys)
    {
        DocumentCacheOptions options = new()
        {
            Targets = targetKeys
                .Select(targetKey => new DocumentCacheTargetOptions
                {
                    TenantKey = targetKey.TenantKey,
                    DataStoreId = targetKey.DataStoreId,
                })
                .ToList(),
        };

        return Options.Create(options);
    }

    private static DocumentCacheTargetRegistrySnapshot Snapshot(
        IEnumerable<DocumentCacheTargetObservation> observations
    ) => new(observations, ObservedAt);

    private static DocumentCacheTargetRuntimeSnapshot RuntimeSnapshot(
        IEnumerable<DocumentCacheTargetExecutionContext> executionContexts
    ) => new(executionContexts, ObservedAt);

    private static DocumentCacheTargetExecutionContext ExecutionContext(
        long generation,
        TimeSpan? workflowTimeout = null,
        RelationalProviderToken? providerToken = null,
        DocumentCacheLifecycleObservation? lifecycle = null
    ) =>
        new(
            TargetKey,
            new DocumentCacheTargetContextGeneration(generation),
            EffectiveSettings(workflowTimeout ?? TimeSpan.FromHours(24)),
            new DocumentCacheTargetDataStoreMetadata(
                TargetKey.DataStoreId,
                (providerToken ?? RelationalProviderToken.Postgresql).Value
            ),
            new DocumentCacheTargetConnectionInput(
                providerToken ?? RelationalProviderToken.Postgresql,
                "connection"
            ),
            Fingerprint,
            lifecycle ?? TrackingLifecycle,
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

    private static DocumentCacheTargetObservation IneligiblePrerequisiteObservation(
        DocumentCacheTargetDiagnosticCategory diagnosticCategory
    )
    {
        DocumentCacheLifecycleObservation lifecycle =
            diagnosticCategory == DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed
                ? DisabledLifecycle
                : TrackingLifecycle;
        DocumentCacheProviderPrerequisiteValidationResult prerequisiteResult =
            DocumentCacheProviderPrerequisiteValidationResult.Initialization(
                FailedSqlServerPrerequisites(),
                lifecycle
            );
        DocumentCacheTargetContextGeneration generation = new(1);
        DocumentCacheTargetEffectiveSettings settings = EffectiveSettings(TimeSpan.FromHours(24));
        DocumentCacheTargetDiagnostic diagnostic = new(
            TargetKey,
            DocumentCacheTargetResolutionState.Resolved,
            RelationalProviderToken.SqlServer,
            generation,
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
            prerequisiteResult.SqlServerPrerequisites,
            retryState: null,
            prerequisiteResult.FailureCategory!.Value,
            prerequisiteResult.Message
        );

        return DocumentCacheTargetObservation.ResolvedIneligible(
            TargetKey,
            settings,
            generation,
            RelationalProviderToken.SqlServer,
            Fingerprint,
            lifecycle,
            diagnostic.Inventory,
            diagnostic.EnqueueTrigger,
            prerequisiteResult.SqlServerPrerequisites,
            retryState: null,
            [diagnostic]
        );
    }

    private static DocumentCacheTargetObservation IneligibleUnexpectedProviderFailureObservation() =>
        DocumentCacheTargetObservation.ResolvedIneligible(
            TargetKey,
            EffectiveSettings(TimeSpan.FromHours(24)),
            new DocumentCacheTargetContextGeneration(1),
            RelationalProviderToken.Postgresql,
            physicalSourceFingerprint: null,
            lifecycle: null,
            inventory: null,
            enqueueTrigger: null,
            sqlServerPrerequisites: null,
            retryState: null,
            [
                new DocumentCacheTargetDiagnostic(
                    TargetKey,
                    DocumentCacheTargetResolutionState.Resolved,
                    RelationalProviderToken.Postgresql,
                    new DocumentCacheTargetContextGeneration(1),
                    physicalSourceFingerprint: null,
                    lifecycle: null,
                    inventory: null,
                    enqueueTrigger: null,
                    sqlServerPrerequisites: null,
                    retryState: null,
                    DocumentCacheTargetDiagnosticCategory.UnexpectedProviderFailure,
                    "Resolved target failed for an unexpected provider reason."
                ),
            ]
        );

    private static DocumentCacheSqlServerPrerequisiteDetails FailedSqlServerPrerequisites() =>
        new(
            new DocumentCacheProviderPrerequisiteResult(
                DocumentCacheProviderPrerequisiteName.ReadCommittedSnapshot,
                DocumentCacheProviderPrerequisiteStatus.Disabled,
                "SQL Server READ_COMMITTED_SNAPSHOT is disabled."
            ),
            new DocumentCacheProviderPrerequisiteResult(
                DocumentCacheProviderPrerequisiteName.NestedTriggers,
                DocumentCacheProviderPrerequisiteStatus.Satisfied,
                "SQL Server nested triggers are enabled."
            )
        );

    private static DocumentCacheTargetEffectiveSettings EffectiveSettings(TimeSpan workflowTimeout) =>
        new(
            readAccelerationEnabled: true,
            directFillTimeout: TimeSpan.FromMilliseconds(250),
            projectorPollInterval: TimeSpan.FromSeconds(5),
            projectorPageSize: 3,
            projectorMaxConcurrentTargets: 2,
            projectorFailureBackoff: TimeSpan.FromSeconds(10),
            projectorBaselineHighWaterMark: 1000,
            workflowTimeout
        );

    private static void AddBackpressureDiagnostics(
        DocumentCacheAdministrativeCommandExecutionContext context,
        int count
    )
    {
        for (int index = 1; index <= count; index++)
        {
            context.AddPhaseDiagnostic(
                DocumentCacheAdministrativeDiagnosticCategory.BaselineHighWaterBackpressure,
                $"Backpressure observation {index}.",
                retryable: true,
                affectedDocumentIds: [index, index + 10, index + 20, index + 30]
            );
        }
    }

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

    private static DocumentCacheProjectionTargetRuntimeContext RuntimeContext(
        DocumentCacheTargetExecutionContext executionContext,
        IDocumentCacheProjectionObservationSink? observationSink = null
    ) =>
        new(
            executionContext,
            new DocumentCacheProjectionTargetProviderAdapters(
                executionContext.ProviderToken,
                MaterializationTargetContext(executionContext.ProviderToken),
                new ThrowingDocumentCacheMaterializer(),
                new ThrowingDocumentCacheWriter()
            ),
            observationSink ?? new DocumentCacheProjectionObservationStore(new FixedTimeProvider(ObservedAt))
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

    private static DocumentCacheMaterializationTargetContext MaterializationTargetContext(
        RelationalProviderToken providerToken
    ) =>
        new(
            new DocumentCacheProjectionTargetKey(TargetKey.TenantKey, new DataStoreId(TargetKey.DataStoreId)),
            MappingSet(providerToken),
            DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated,
            "connection"
        );

    private static MappingSet MappingSet(RelationalProviderToken providerToken)
    {
        SqlDialect dialect =
            providerToken == RelationalProviderToken.SqlServer ? SqlDialect.Mssql : SqlDialect.Pgsql;
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
                dialect,
                effectiveSchema.RelationalMappingVersion
            ),
            new DerivedRelationalModelSet(effectiveSchema, dialect, [], [], [], [], [], []),
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

    private sealed class SucceedingWorkflow : IDocumentCacheAdministrativeCommandWorkflow
    {
        public static SucceedingWorkflow Instance { get; } = new();

        public Task<DocumentCacheAdministrativeCommandResult> RunPreflightAsync(
            DocumentCacheAdministrativeCommandExecutionContext context,
            CancellationToken cancellationToken
        ) => Task.FromResult(context.EligiblePreflightResult());

        public Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
            DocumentCacheAdministrativeCommandExecutionContext context,
            CancellationToken cancellationToken
        ) => Task.FromResult(context.Completed());
    }

    private sealed class SucceedingBaselineSeeder : IDocumentCacheBaselineSeeder
    {
        public int SeedCallCount { get; private set; }

        public Task<DocumentCacheBaselineSeedingResult> SeedAsync(
            DocumentCacheAdministrativeCommandExecutionContext context,
            CancellationToken cancellationToken = default
        )
        {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
            SeedCallCount++;

            return Task.FromResult(
                new DocumentCacheBaselineSeedingResult(
                    boundaryDocumentId: null,
                    lastCommittedDocumentId: 0,
                    pagesSeeded: 0,
                    documentsVisited: 0,
                    workMutationCount: 0
                )
            );
        }
    }

    private sealed class SucceedingAdministrativeDrainer : IDocumentCacheAdministrativeDrainer
    {
        public int DrainToEmptyCallCount { get; private set; }

        public Task<DocumentCacheAdministrativeDrainToEmptyResult> DrainToEmptyAsync(
            DocumentCacheAdministrativeCommandExecutionContext context,
            CancellationToken cancellationToken = default
        )
        {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
            DrainToEmptyCallCount++;

            return Task.FromResult(
                DocumentCacheAdministrativeDrainToEmptyResult.Succeeded(
                    new DocumentCacheAdministrativeDrainStats()
                )
            );
        }

        public Task<DocumentCacheAdministrativeDrainSliceResult> DrainBackpressureReliefSliceAsync(
            DocumentCacheAdministrativeCommandExecutionContext context,
            CancellationToken cancellationToken = default
        )
        {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotSupportedException();
        }
    }

    private sealed class StubProjectionSupervisor(
        IEnumerable<DocumentCacheProjectionTargetRuntimeContext> contexts,
        Exception? retainedCommandOwnedTargetContextReleaseException = null
    ) : IDocumentCacheProjectionSupervisor, IDocumentCacheProjectionRetainedTargetContextReleaser
    {
        public ImmutableArray<DocumentCacheProjectionTargetRuntimeContext> CurrentTargetContexts { get; } =
            contexts.ToImmutableArray();

        public int ReleaseRetainedCommandOwnedTargetContextCount { get; private set; }

        public DocumentCacheProjectionTargetRuntimeContext? LastReleasedRetainedTargetContext
        {
            get;
            private set;
        }

        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task ReleaseRetainedCommandOwnedTargetContextAsync(
            DocumentCacheProjectionTargetRuntimeContext targetContext,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReleaseRetainedCommandOwnedTargetContextCount++;
            LastReleasedRetainedTargetContext = targetContext;

            if (retainedCommandOwnedTargetContextReleaseException is not null)
            {
                throw retainedCommandOwnedTargetContextReleaseException;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class RejectingAdministrativeTargetRetainerProjectionSupervisor(
        DocumentCacheTargetObservation targetObservation,
        DocumentCacheProjectionTargetRuntimeContext targetContext
    ) : IDocumentCacheProjectionSupervisor, IDocumentCacheProjectionAdministrativeTargetRetainer
    {
        public ImmutableArray<DocumentCacheProjectionTargetRuntimeContext> CurrentTargetContexts { get; } =
            ImmutableArray.Create(targetContext);

        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<DocumentCacheProjectionAdministrativeTargetRetainResult> TryRetainCurrentTargetForAdministrativeCommandAsync(
            DocumentCacheTargetKey targetKey,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            targetKey.Should().Be(targetContext.TargetKey);

            return Task.FromResult(
                new DocumentCacheProjectionAdministrativeTargetRetainResult(
                    targetObservation,
                    targetContext,
                    Retention: null
                )
            );
        }
    }

    private sealed class DelayingAdministrativeTargetRetainerProjectionSupervisor(
        DocumentCacheTargetObservation targetObservation,
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        TimeSpan retainDelay
    ) : IDocumentCacheProjectionSupervisor, IDocumentCacheProjectionAdministrativeTargetRetainer
    {
        public ImmutableArray<DocumentCacheProjectionTargetRuntimeContext> CurrentTargetContexts { get; } =
            ImmutableArray.Create(targetContext);

        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public async Task<DocumentCacheProjectionAdministrativeTargetRetainResult> TryRetainCurrentTargetForAdministrativeCommandAsync(
            DocumentCacheTargetKey targetKey,
            CancellationToken cancellationToken = default
        )
        {
            targetKey.Should().Be(targetContext.TargetKey);
            await Task.Delay(retainDelay, cancellationToken).ConfigureAwait(false);

            return new(targetObservation, targetContext, targetContext.TryRetainForAdministrativeCommand());
        }
    }

    private sealed class ThrowingEndAdministrativeCommandObservationSink(Exception endException)
        : IDocumentCacheProjectionObservationSink
    {
        public int EndAdministrativeCommandCount { get; private set; }

        public void ObserveTarget(DocumentCacheProjectionTargetHealthSnapshot snapshot) => _ = snapshot;

        public void EndTargetContext(
            DocumentCacheProjectionTargetContextKey contextKey,
            DocumentCacheProjectionTargetEndReason endReason,
            DateTimeOffset? endedAt = null
        )
        {
            _ = contextKey;
            _ = endReason;
            _ = endedAt;
        }

        public void ObserveAdministrativeCommand(
            DocumentCacheAdministrativeCommandObservationSnapshot snapshot
        ) => _ = snapshot;

        public void EndAdministrativeCommand(DocumentCacheAdministrativeCommandExecutionId executionId)
        {
            EndAdministrativeCommandCount++;
            throw endException;
        }
    }

    private sealed class RecordingTargetContextFactory(
        IDocumentCacheProjectionObservationSink observationSink
    ) : IDocumentCacheProjectionTargetRuntimeContextFactory
    {
        public List<DocumentCacheProjectionTargetRuntimeContext> CreatedContexts { get; } = [];

        public Task<DocumentCacheProjectionTargetRuntimeContext> CreateAsync(
            DocumentCacheTargetExecutionContext executionContext,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            DocumentCacheProjectionTargetRuntimeContext context = RuntimeContext(
                executionContext,
                observationSink
            );
            CreatedContexts.Add(context);
            return Task.FromResult(context);
        }
    }

    private sealed class MutableTargetRegistry(
        DocumentCacheTargetRegistrySnapshot currentSnapshot,
        DocumentCacheTargetRuntimeSnapshot currentRuntimeSnapshot
    ) : IDocumentCacheTargetRegistry
    {
        public DocumentCacheTargetRegistrySnapshot CurrentSnapshot { get; set; } = currentSnapshot;

        public DocumentCacheTargetRuntimeSnapshot CurrentRuntimeSnapshot { get; set; } =
            currentRuntimeSnapshot;

        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(CurrentSnapshot);
    }

    private sealed class RecordingAdministrativeMutex(
        RelationalProviderToken? providerToken = null,
        TimeSpan? acquireDelay = null,
        Exception? acquireException = null,
        Exception? disposeException = null,
        Func<CancellationToken, Task>? beforeAcquireCompletes = null,
        Func<CancellationToken, Task>? afterAcquireCompletes = null,
        RecordingMutexLease? lease = null
    ) : IDocumentCacheAdministrativeMutex
    {
        public int AcquireCount { get; private set; }

        public RecordingMutexLease? LastLease { get; private set; }

        public RelationalProviderToken ProviderToken { get; } =
            providerToken ?? RelationalProviderToken.Postgresql;

        public async Task<IDocumentCacheAdministrativeMutexLease> AcquireAsync(
            DocumentCacheTargetConnectionInput connectionInput,
            CancellationToken cancellationToken = default
        )
        {
            AcquireCount++;

            if (acquireDelay is not null)
            {
                await Task.Delay(acquireDelay.Value, cancellationToken).ConfigureAwait(false);
            }

            if (beforeAcquireCompletes is not null)
            {
                await beforeAcquireCompletes(cancellationToken).ConfigureAwait(false);
            }

            if (acquireException is not null)
            {
                throw acquireException;
            }

            if (afterAcquireCompletes is not null)
            {
                await afterAcquireCompletes(cancellationToken).ConfigureAwait(false);
            }

            LastLease = lease ?? new RecordingMutexLease(ProviderToken, disposeException);
            return LastLease;
        }
    }

    private sealed class RecordingMutexLease(
        RelationalProviderToken providerToken,
        Exception? disposeException = null,
        params RecordingWriteSession[] sessions
    ) : IDocumentCacheAdministrativeMutexLease
    {
        private readonly Queue<RecordingWriteSession> _sessions = new(sessions);
        private bool _sessionOpen = true;

        public RelationalProviderToken ProviderToken { get; } = providerToken;

        public int DisposeCount { get; private set; }

        public DbConnection Connection => throw new NotSupportedException();

        public bool IsSessionOpen => _sessionOpen;

        public void LoseSession() => _sessionOpen = false;

        public Task<IRelationalWriteSession> BeginTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
            CancellationToken cancellationToken = default
        )
        {
            if (!IsSessionOpen)
            {
                throw new DocumentCacheAdministrativeMutexSessionLostException(ProviderToken);
            }

            RecordingWriteSession session =
                _sessions.Count > 0
                    ? _sessions.Dequeue()
                    : new RecordingWriteSession(ProviderToken, isolationLevel);
            session.Attach(this);
            return Task.FromResult<IRelationalWriteSession>(session);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (disposeException is not null)
            {
                throw disposeException;
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed record CapturedLogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<CapturedLogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            Entries.Add(new CapturedLogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed class RecordingWriteSession(
        RelationalProviderToken providerToken,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        Func<RecordingWriteSession, CancellationToken, Task>? commitAsync = null,
        Func<RecordingWriteSession, CancellationToken, Task>? rollbackAsync = null,
        DbConnection? connection = null,
        DbTransaction? transaction = null,
        Func<RelationalCommand, DbCommand>? createCommand = null
    ) : IRelationalWriteSession
    {
        private RecordingMutexLease? _lease;

        public RelationalProviderToken ProviderToken { get; } = providerToken;

        public IsolationLevel IsolationLevel { get; } = isolationLevel;

        public DbConnection Connection => connection ?? throw new NotSupportedException();

        public DbTransaction Transaction => transaction ?? throw new NotSupportedException();

        public DbCommand CreateCommand(RelationalCommand command) =>
            createCommand?.Invoke(command) ?? throw new NotSupportedException();

        public void Attach(RecordingMutexLease lease) => _lease = lease;

        public void LoseMutexSession() =>
            (
                _lease ?? throw new InvalidOperationException("Session was not attached to a lease.")
            ).LoseSession();

        public Task CommitAsync(CancellationToken cancellationToken = default) =>
            commitAsync?.Invoke(this, cancellationToken) ?? Task.CompletedTask;

        public Task RollbackAsync(CancellationToken cancellationToken = default) =>
            rollbackAsync?.Invoke(this, cancellationToken) ?? Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestNpgsqlConnection : DbConnection
    {
        [AllowNull]
        public override string ConnectionString { get; set; } = "Host=localhost;Database=test";

        public override string Database => "test";

        public override string DataSource => "test";

        public override string ServerVersion => "1.0";

        public override ConnectionState State => ConnectionState.Open;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public override void Close() { }

        public override void Open() { }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            new TestDbTransaction(this, isolationLevel);

        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }

    private sealed class TestDbTransaction(
        DbConnection? connection = null,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted
    ) : DbTransaction
    {
        protected override DbConnection? DbConnection { get; } = connection;

        public override IsolationLevel IsolationLevel { get; } = isolationLevel;

        public override void Commit() { }

        public override void Rollback() { }
    }

    private sealed class StubAdministrativePrimitives(
        RelationalProviderToken? providerToken = null,
        Exception? projectedStateEmptinessException = null,
        Func<int, int, DocumentCacheAdministrativeProjectedStateEmptinessResult>? projectedStateEmptiness =
            null,
        IReadOnlyList<object>? lifecycleReads = null,
        Exception? transitionLifecycleException = null,
        Exception? activationPrerequisiteException = null,
        DocumentCacheAdministrativeBaselineBoundaryResult? baselineBoundary = null,
        DocumentCacheGuardedNewEmptyActivationState? guardedNewEmptyActivationState = null,
        IReadOnlyList<DocumentCacheAdministrativeWorkHighWaterObservationResult>? highWaterObservations =
            null,
        IReadOnlyList<object>? seedPages = null,
        IReadOnlyList<object>? scrubPages = null,
        IReadOnlyList<DocumentCacheAdministrativeClearBatchResult>? cacheClearBatches = null,
        IReadOnlyList<DocumentCacheAdministrativeClearBatchResult>? workClearBatches = null
    ) : IDocumentCacheAdministrativePrimitives
    {
        private readonly Queue<object> _lifecycleReads = new(
            lifecycleReads ?? [DocumentCacheLifecycleReadResult.Success(TrackingLifecycle)]
        );
        private readonly Queue<DocumentCacheAdministrativeWorkHighWaterObservationResult> _highWater = new(
            highWaterObservations ?? []
        );
        private readonly Queue<object> _seedPages = new(seedPages ?? []);
        private readonly Queue<object> _scrubPages = new(scrubPages ?? []);
        private readonly Queue<DocumentCacheAdministrativeClearBatchResult> _cacheClearBatches = new(
            cacheClearBatches ?? []
        );
        private readonly Queue<DocumentCacheAdministrativeClearBatchResult> _workClearBatches = new(
            workClearBatches ?? []
        );

        public RelationalProviderToken ProviderToken { get; } =
            providerToken ?? RelationalProviderToken.Postgresql;

        public int CacheClearCallCount { get; private set; }

        public int WorkClearCallCount { get; private set; }

        public int SeedBaselinePageCallCount { get; private set; }

        public int ScrubPageCallCount { get; private set; }

        public List<DocumentCacheAdministrativeBaselineSeedPageRequest> SeedBaselinePageRequests { get; } =
        [];

        public List<DocumentCacheAdministrativeScrubPageRequest> ScrubPageRequests { get; } = [];

        public Task<DocumentCacheLifecycleReadResult> ReadLifecycleAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeStateLockMode lockMode =
                DocumentCacheAdministrativeStateLockMode.Shared,
            CancellationToken cancellationToken = default
        )
        {
            _ = mutexSession;
            _ = lockMode;
            cancellationToken.ThrowIfCancellationRequested();

            object nextRead =
                _lifecycleReads.Count > 0
                    ? _lifecycleReads.Dequeue()
                    : DocumentCacheLifecycleReadResult.Success(TrackingLifecycle);

            return nextRead switch
            {
                DocumentCacheLifecycleReadResult result => Task.FromResult(result),
                Exception exception => Task.FromException<DocumentCacheLifecycleReadResult>(exception),
                _ => throw new InvalidOperationException("Unsupported lifecycle read stub item."),
            };
        }

        public Task LockCanonicalDocumentsForGuardedActivationAsync(
            IRelationalWriteSession mutexSession,
            CancellationToken cancellationToken = default
        )
        {
            _ = mutexSession;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<DocumentCacheGuardedNewEmptyActivationState> ReadGuardedNewEmptyActivationStateAsync(
            IRelationalWriteSession mutexSession,
            CancellationToken cancellationToken = default
        )
        {
            _ = mutexSession;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                guardedNewEmptyActivationState
                    ?? new DocumentCacheGuardedNewEmptyActivationState(
                        canonicalDocumentsEmpty: true,
                        documentCacheEmpty: true,
                        documentProjectionWorkEmpty: true
                    )
            );
        }

        public Task<DocumentCacheProviderPrerequisiteValidationResult> ValidateActivationPrerequisitesAsync(
            IRelationalWriteSession mutexSession,
            CancellationToken cancellationToken = default
        )
        {
            _ = mutexSession;
            cancellationToken.ThrowIfCancellationRequested();
            return activationPrerequisiteException is null
                ? Task.FromResult(
                    DocumentCacheProviderPrerequisiteValidationResult.ActivationPreflight(
                        DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
                    )
                )
                : Task.FromException<DocumentCacheProviderPrerequisiteValidationResult>(
                    activationPrerequisiteException
                );
        }

        public Task<DocumentCacheAdministrativeLifecycleTransitionResult> TryTransitionLifecycleAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeLifecycleTransitionRequest request,
            CancellationToken cancellationToken = default
        )
        {
            _ = mutexSession;
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return transitionLifecycleException is null
                ? Task.FromResult(
                    DocumentCacheAdministrativeLifecycleTransitionResult.Transitioned(TrackingLifecycle)
                )
                : Task.FromException<DocumentCacheAdministrativeLifecycleTransitionResult>(
                    transitionLifecycleException
                );
        }

        public Task<DocumentCacheAdministrativeClearBatchResult> ClearDocumentCacheBatchAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeClearBatchRequest request,
            CancellationToken cancellationToken = default
        )
        {
            _ = mutexSession;
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            CacheClearCallCount++;

            return _cacheClearBatches.Count > 0
                ? Task.FromResult(_cacheClearBatches.Dequeue())
                : throw new NotSupportedException();
        }

        public Task<DocumentCacheAdministrativeClearBatchResult> ClearDocumentProjectionWorkBatchAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeClearBatchRequest request,
            DocumentCacheAdministrativeWorkClearance clearance,
            CancellationToken cancellationToken = default
        )
        {
            _ = mutexSession;
            _ = request;
            _ = clearance;
            cancellationToken.ThrowIfCancellationRequested();
            WorkClearCallCount++;

            return _workClearBatches.Count > 0
                ? Task.FromResult(_workClearBatches.Dequeue())
                : throw new NotSupportedException();
        }

        public Task<DocumentCacheAdministrativeProjectedStateEmptinessResult> ReadProjectedStateEmptinessAsync(
            IRelationalWriteSession mutexSession,
            CancellationToken cancellationToken = default
        )
        {
            _ = mutexSession;
            cancellationToken.ThrowIfCancellationRequested();

            if (projectedStateEmptinessException is not null)
            {
                return Task.FromException<DocumentCacheAdministrativeProjectedStateEmptinessResult>(
                    projectedStateEmptinessException
                );
            }

            if (projectedStateEmptiness is not null)
            {
                return Task.FromResult(projectedStateEmptiness(CacheClearCallCount, WorkClearCallCount));
            }

            throw new NotSupportedException();
        }

        public Task<DocumentCacheAdministrativeBaselineBoundaryResult> CaptureBaselineBoundaryAsync(
            IRelationalWriteSession mutexSession,
            CancellationToken cancellationToken = default
        )
        {
            _ = mutexSession;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                baselineBoundary
                    ?? new DocumentCacheAdministrativeBaselineBoundaryResult(
                        boundaryDocumentId: null,
                        "No boundary."
                    )
            );
        }

        public Task<DocumentCacheAdministrativeWorkHighWaterObservationResult> ObserveWorkHighWaterAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeWorkHighWaterObservationRequest request,
            CancellationToken cancellationToken = default
        )
        {
            _ = mutexSession;
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_highWater.Dequeue());
        }

        public Task<DocumentCacheAdministrativeBaselineSeedPageResult> SeedBaselinePageAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeBaselineSeedPageRequest request,
            CancellationToken cancellationToken = default
        )
        {
            _ = mutexSession;
            cancellationToken.ThrowIfCancellationRequested();
            SeedBaselinePageCallCount++;
            SeedBaselinePageRequests.Add(request);
            return _seedPages.Dequeue() switch
            {
                DocumentCacheAdministrativeBaselineSeedPageResult page => Task.FromResult(page),
                Exception exception => Task.FromException<DocumentCacheAdministrativeBaselineSeedPageResult>(
                    exception
                ),
                _ => throw new InvalidOperationException("Unsupported baseline seed page stub item."),
            };
        }

        public Task<DocumentCacheAdministrativeScrubPageResult> ScrubPageAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeScrubPageRequest request,
            CancellationToken cancellationToken = default
        )
        {
            _ = mutexSession;
            cancellationToken.ThrowIfCancellationRequested();
            ScrubPageCallCount++;
            ScrubPageRequests.Add(request);
            return _scrubPages.Dequeue() switch
            {
                DocumentCacheAdministrativeScrubPageResult page => Task.FromResult(page),
                Exception exception => Task.FromException<DocumentCacheAdministrativeScrubPageResult>(
                    exception
                ),
                _ => throw new InvalidOperationException("Unsupported scrub page stub item."),
            };
        }
    }

    private sealed class ThrowingDocumentCacheMaterializer : IDocumentCacheMaterializer
    {
        public Task<DocumentCacheMaterializationResult> MaterializeAsync(
            DocumentCacheMaterializationRequest request
        ) => throw new NotSupportedException();
    }

    private sealed class ThrowingDocumentCacheWriter : IDocumentCacheWriter
    {
        public Task<DocumentCacheWriterResult> WriteAsync(DocumentCacheWriterRequest request) =>
            throw new NotSupportedException();
    }

    private sealed class StubDocumentCacheLifecycleReader : IDocumentCacheLifecycleReader
    {
        public RelationalProviderToken ProviderToken => RelationalProviderToken.Postgresql;

        public Task<DocumentCacheLifecycleReadResult> ReadLifecycleAsync(
            string connectionString,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(DocumentCacheLifecycleReadResult.Success(TrackingLifecycle));
        }
    }

    private sealed class NoOpDocumentCacheProjectionScheduler : IDocumentCacheProjectionScheduler
    {
        public Task<ImmutableArray<DocumentCacheProjectionSchedulerDispatchResult>> RunReadyTargetsOnceAsync(
            IEnumerable<DocumentCacheProjectionTargetRuntimeContext> targetContexts,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(ImmutableArray<DocumentCacheProjectionSchedulerDispatchResult>.Empty);

        public Task<DocumentCacheProjectionSchedulerDispatchResult> RunAdministrativeDrainSliceAsync(
            DocumentCacheProjectionTargetRuntimeContext targetContext,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private static SqlException CreateSqlException(int number, string message)
    {
        var sqlError = (SqlError)RuntimeHelpers.GetUninitializedObject(typeof(SqlError));
        typeof(SqlError)
            .GetField("_number", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(sqlError, number);
        typeof(SqlError)
            .GetField("_message", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(sqlError, message);

        var errorList = new List<object> { sqlError };
        var errorCollection = (SqlErrorCollection)
            RuntimeHelpers.GetUninitializedObject(typeof(SqlErrorCollection));
        typeof(SqlErrorCollection)
            .GetField("_errors", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(errorCollection, errorList);

        var sqlException = (SqlException)RuntimeHelpers.GetUninitializedObject(typeof(SqlException));
        typeof(Exception)
            .GetField("_message", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(sqlException, message);
        typeof(SqlException)
            .GetField("_errors", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(sqlException, errorCollection);

        return sqlException;
    }

    private static PostgresException CreatePostgresException(string sqlState) =>
        new(
            messageText: "simulated provider command timeout",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: sqlState,
            detail: string.Empty,
            hint: string.Empty,
            position: 0,
            internalPosition: 0,
            internalQuery: string.Empty,
            where: string.Empty,
            schemaName: "dms",
            tableName: "DocumentCacheState",
            columnName: string.Empty,
            dataTypeName: string.Empty,
            constraintName: string.Empty,
            file: "test.sql",
            line: "1",
            routine: "Execute"
        );
}
