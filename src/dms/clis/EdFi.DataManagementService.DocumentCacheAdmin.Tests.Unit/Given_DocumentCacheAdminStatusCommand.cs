// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.CommandLine;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.DocumentCacheAdmin;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("StatusCommand")]
public sealed class Given_DocumentCacheAdminStatusCommand
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 20, 12, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DurableObservedAt = ObservedAt.AddSeconds(1);

    [Test]
    public async Task It_requests_standalone_status_mode_and_writes_human_output_by_default()
    {
        ScriptedDocumentCacheStatusService statusService = new(_ => Task.FromResult(StatusResponse()));
        await using ServiceProvider serviceProvider = new ServiceCollection()
            .AddSingleton<IDocumentCacheStatusService>(statusService)
            .BuildServiceProvider();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = await DocumentCacheAdminCommandExecutor.ExecuteAsync(
            ParseStatusCommand(DocumentCacheAdminCommandSurface.DataStoreIdOptionName, "1"),
            InvocationTarget(),
            serviceProvider,
            stdout,
            stderr
        );

        exitCode.Should().Be(DocumentCacheAdminExitCodes.Success);
        statusService
            .EvaluationModes.Should()
            .ContainSingle()
            .Which.Should()
            .Be(DocumentCacheStatusEvaluationMode.StandaloneDirectObservation);
        stdout.ToString().Should().Contain("DocumentCache status observedAt=2026-08-20T12:30:00.0000000Z");
        stdout.ToString().Should().Contain("lifecycle=Tracking availability=Available");
        stdout.ToString().Should().Contain("operationalHealth=Unknown reason=RuntimeNotObserved");
        stderr.ToString().Should().BeEmpty();
    }

    [Test]
    public async Task It_maps_status_pipeline_failures_to_failed_no_mutation_exit_code()
    {
        ScriptedDocumentCacheStatusService statusService = new(_ =>
            throw new InvalidOperationException("status pipeline failed")
        );
        await using ServiceProvider serviceProvider = new ServiceCollection()
            .AddSingleton<IDocumentCacheStatusService>(statusService)
            .BuildServiceProvider();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = await DocumentCacheAdminCommandExecutor.ExecuteAsync(
            ParseStatusCommand(DocumentCacheAdminCommandSurface.DataStoreIdOptionName, "1"),
            InvocationTarget(),
            serviceProvider,
            stdout,
            stderr
        );

        exitCode.Should().Be(DocumentCacheAdminExitCodes.FailedNoMutation);
        stdout.ToString().Should().BeEmpty();
        stderr.ToString().Should().Contain("status failed before a complete status document");
        statusService
            .EvaluationModes.Should()
            .ContainSingle()
            .Which.Should()
            .Be(DocumentCacheStatusEvaluationMode.StandaloneDirectObservation);
    }

    private static ParseResult ParseStatusCommand(params string[] args) =>
        DocumentCacheAdminCommandSurface
            .CreateRootCommand()
            .Parse([DocumentCacheAdminCommandSurface.StatusCommandName, .. args]);

    private static DocumentCacheAdminInvocationTarget InvocationTarget() =>
        new(DocumentCacheTargetKey.Create("", 1), DocumentCacheAdminInvocationTargetSource.Options);

    private static DocumentCacheStatusResponse StatusResponse() =>
        new(
            ObservedAt,
            [
                new DocumentCacheStatusTarget(
                    DocumentCacheStatusTargetKey.FromTargetKey(DocumentCacheTargetKey.Create("", 1)),
                    targetGeneration: 3,
                    ObservedAt,
                    DurableObservedAt,
                    provider: "postgresql",
                    physicalSourceFingerprint: "sha256:0000000000000000000000000000000000000000000000000000000000000001",
                    new DocumentCacheStatusResolutionComponent(
                        DocumentCacheStatusResolutionStatus.Resolved,
                        DocumentCacheStatusResolutionReason.None,
                        ObservedAt,
                        message: null
                    ),
                    new DocumentCacheStatusEligibilityComponent(
                        DocumentCacheStatusEligibilityStatus.Unknown,
                        DocumentCacheStatusReason.RuntimeNotObserved,
                        "Current-generation DocumentCache projection runtime has not been observed."
                    ),
                    new DocumentCacheStatusInventoryComponentGroup(
                        ObservedAt,
                        ValidInventory(),
                        ValidInventory(),
                        ValidInventory(),
                        ValidInventory(),
                        new DocumentCacheStatusEnqueueTriggerComponent(
                            DocumentCacheStatusEnqueueTriggerStatus.Enabled,
                            DocumentCacheStatusInventoryReason.None,
                            message: null
                        )
                    ),
                    new DocumentCacheStatusProviderPrerequisitesComponent(
                        DocumentCacheStatusProviderPrerequisiteStatus.NotApplicable,
                        DocumentCacheStatusProviderPrerequisiteReason.None,
                        observedAt: null,
                        new DocumentCacheStatusProviderPrerequisiteComponent(
                            DocumentCacheStatusProviderPrerequisiteStatus.NotApplicable,
                            DocumentCacheStatusProviderPrerequisiteReason.None,
                            message: null
                        ),
                        new DocumentCacheStatusProviderPrerequisiteComponent(
                            DocumentCacheStatusProviderPrerequisiteStatus.NotApplicable,
                            DocumentCacheStatusProviderPrerequisiteReason.None,
                            message: null
                        )
                    ),
                    new DocumentCacheStatusLifecycleComponent(
                        DocumentCacheStatusLifecycleState.Tracking,
                        DocumentCacheStatusAvailability.Available,
                        message: null
                    ),
                    new DocumentCacheStatusCacheAheadComponent(
                        DocumentCacheStatusCacheAheadState.Clear,
                        recoveryRequired: false,
                        message: null
                    ),
                    new DocumentCacheOperationalHealthComponent(
                        DocumentCacheOperationalHealthStatus.Unknown,
                        DocumentCacheStatusReason.RuntimeNotObserved,
                        "Current-generation DocumentCache projection runtime has not been observed."
                    ),
                    new DocumentCacheCaughtUpComponent(
                        DocumentCacheCaughtUpStatus.Unknown,
                        DocumentCacheStatusReason.RuntimeNotObserved,
                        "Current-generation DocumentCache projection runtime has not been observed."
                    ),
                    new DocumentCacheStatusQueueSummary(
                        DocumentCacheStatusQueuePresence.Empty,
                        oldestWorkFirstEnqueuedAt: null,
                        oldestWorkAgeSeconds: null,
                        DocumentCacheStatusBacklogEstimate.Unavailable
                    ),
                    new DocumentCacheStatusExecutionStateComponent(
                        DocumentCacheStatusExecutionState.NotObserved,
                        observedAt: null,
                        activeWorkers: null,
                        concurrencySlotsUsed: null,
                        targetBackoffUntil: null,
                        lastSuccessfulWorkAt: null,
                        lastFailureAt: null,
                        message: null
                    ),
                    activeCommand: null,
                    lastEndedDiagnostic: null,
                    new DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusTargetDiagnosticEvent>(),
                    new DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusDocumentDiagnosticEvent>(),
                    new DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusPoisonTraversalDiagnosticEvent>(),
                    DocumentCacheStatusEffectiveSettings.FromEffectiveSettings(
                        DocumentCacheTargetEffectiveSettings.FromOptions(new DocumentCacheOptions())
                    ),
                    new DocumentCacheStatusEnqueueFailures()
                ),
            ]
        );

    private static DocumentCacheStatusInventoryComponent ValidInventory() =>
        new(DocumentCacheStatusInventoryStatus.Valid, DocumentCacheStatusInventoryReason.None, message: null);

    private sealed class ScriptedDocumentCacheStatusService(
        Func<DocumentCacheStatusEvaluationMode, Task<DocumentCacheStatusResponse>> getStatusAsync
    ) : IDocumentCacheStatusService
    {
        private readonly List<DocumentCacheStatusEvaluationMode> _evaluationModes = [];

        public ImmutableArray<DocumentCacheStatusEvaluationMode> EvaluationModes => [.. _evaluationModes];

        public async Task<DocumentCacheStatusResponse> GetStatusAsync(
            CancellationToken cancellationToken = default,
            DocumentCacheStatusEvaluationMode evaluationMode =
                DocumentCacheStatusEvaluationMode.RuntimeEndpoint
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            _evaluationModes.Add(evaluationMode);
            return await getStatusAsync(evaluationMode).ConfigureAwait(false);
        }
    }
}
