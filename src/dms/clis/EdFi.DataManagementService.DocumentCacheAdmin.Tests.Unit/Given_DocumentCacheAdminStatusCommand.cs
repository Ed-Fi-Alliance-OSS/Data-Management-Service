// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.CommandLine;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
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
    public async Task It_does_not_resolve_mapping_services_for_status()
    {
        int runtimeCompilerResolveCount = 0;
        int mappingProviderResolveCount = 0;
        ScriptedDocumentCacheStatusService statusService = new(_ => Task.FromResult(StatusResponse()));
        await using ServiceProvider serviceProvider = new ServiceCollection()
            .AddSingleton<IRuntimeMappingSetCompiler>(_ =>
            {
                runtimeCompilerResolveCount++;
                return new FixedRuntimeMappingSetCompiler(SqlDialect.Pgsql);
            })
            .AddSingleton<IMappingSetProvider>(_ =>
            {
                mappingProviderResolveCount++;
                return new ThrowingMappingSetProvider("Status command must not request a mapping set.");
            })
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
        runtimeCompilerResolveCount.Should().Be(0);
        mappingProviderResolveCount.Should().Be(0);
        statusService
            .EvaluationModes.Should()
            .ContainSingle()
            .Which.Should()
            .Be(DocumentCacheStatusEvaluationMode.StandaloneDirectObservation);
        stderr.ToString().Should().BeEmpty();
    }

    [Test]
    public async Task It_maps_status_pipeline_failures_to_failed_no_mutation_exit_code()
    {
        ScriptedDocumentCacheStatusService statusService = new(
            (_, _) => throw new InvalidOperationException("status pipeline failed")
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

    [Test]
    [Category("Timeout")]
    public async Task It_applies_status_timeout_to_status_evaluation()
    {
        ScriptedDocumentCacheStatusService statusService = new(
            async (_, cancellationToken) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                return StatusResponse();
            }
        );
        await using ServiceProvider serviceProvider = new ServiceCollection()
            .AddSingleton<IDocumentCacheStatusService>(statusService)
            .BuildServiceProvider();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = await DocumentCacheAdminCommandExecutor.ExecuteAsync(
            ParseStatusCommand(
                DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                "1",
                DocumentCacheAdminCommandSurface.StatusTimeoutSecondsOptionName,
                "0.001",
                DocumentCacheAdminCommandSurface.JsonOptionName
            ),
            InvocationTarget(),
            serviceProvider,
            stdout,
            stderr
        );

        exitCode.Should().Be(DocumentCacheAdminExitCodes.FailedNoMutation);
        stdout.ToString().Should().BeEmpty();
        stderr.ToString().Should().Contain("status timed out");
        statusService
            .EvaluationModes.Should()
            .ContainSingle()
            .Which.Should()
            .Be(DocumentCacheStatusEvaluationMode.StandaloneDirectObservation);
    }

    [Test]
    [Category("Timeout")]
    public async Task It_applies_status_timeout_before_target_resolution_completes()
    {
        ScriptedDocumentCacheStatusService statusService = new(
            (_, _) =>
                throw new AssertionException(
                    "Status pipeline must not run after target resolution times out."
                )
        );
        await using ServiceProvider serviceProvider = new ServiceCollection()
            .AddSingleton<IDocumentCacheAdminTargetResolver>(new DelayingTargetResolver())
            .AddSingleton<IDocumentCacheStatusService>(statusService)
            .BuildServiceProvider();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = await DocumentCacheAdminCommandExecutor.ExecuteAsync(
            ParseStatusCommand(
                DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                "1",
                DocumentCacheAdminCommandSurface.StatusTimeoutSecondsOptionName,
                "0.001",
                DocumentCacheAdminCommandSurface.JsonOptionName
            ),
            InvocationTarget(),
            serviceProvider,
            stdout,
            stderr
        );

        exitCode.Should().Be(DocumentCacheAdminExitCodes.FailedNoMutation);
        stdout.ToString().Should().BeEmpty();
        stderr.ToString().Should().Contain("status timed out");
        statusService.EvaluationModes.Should().BeEmpty();
    }

    [TestCase(UnexpectedMembershipSnapshot.Empty)]
    [TestCase(UnexpectedMembershipSnapshot.WrongTarget)]
    [TestCase(UnexpectedMembershipSnapshot.MultipleTargets)]
    public async Task It_treats_unexpected_target_membership_as_status_pipeline_failure(
        UnexpectedMembershipSnapshot snapshotKind
    )
    {
        SnapshotTargetResolver targetResolver = new(UnexpectedRegistrySnapshot(snapshotKind));
        ScriptedDocumentCacheStatusService statusService = new(
            (_, _) =>
                throw new AssertionException("Status pipeline must not run after target resolution fails.")
        );
        await using ServiceProvider serviceProvider = new ServiceCollection()
            .AddSingleton<IDocumentCacheAdminTargetResolver>(targetResolver)
            .AddSingleton<IDocumentCacheStatusService>(statusService)
            .BuildServiceProvider();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = await DocumentCacheAdminCommandExecutor.ExecuteAsync(
            ParseStatusCommand(
                DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                "1",
                DocumentCacheAdminCommandSurface.JsonOptionName
            ),
            InvocationTarget(),
            serviceProvider,
            stdout,
            stderr
        );

        exitCode.Should().Be(DocumentCacheAdminExitCodes.FailedNoMutation);
        targetResolver.ResolveCount.Should().Be(1);
        statusService.EvaluationModes.Should().BeEmpty();
        stdout.ToString().Should().BeEmpty();
        stderr.ToString().Should().Contain("status failed before a complete status document");
        stderr.ToString().Should().Contain("exactly the invocation target");
    }

    [Test]
    public async Task It_serializes_unresolved_status_data_from_a_matching_one_target_registry()
    {
        DocumentCacheTargetKey targetKey = DocumentCacheTargetKey.Create("", 1);
        SnapshotTargetResolver targetResolver = new(
            new DocumentCacheTargetRegistrySnapshot(
                [
                    DocumentCacheTargetObservation.Unresolved(
                        targetKey,
                        DocumentCacheTargetEffectiveSettings.FromOptions(new DocumentCacheOptions()),
                        retryState: null,
                        diagnostics: []
                    ),
                ],
                ObservedAt
            )
        );
        ScriptedDocumentCacheStatusService statusService = new(_ =>
            Task.FromResult(UnresolvedStatusResponse())
        );
        await using ServiceProvider serviceProvider = new ServiceCollection()
            .AddSingleton<IDocumentCacheAdminTargetResolver>(targetResolver)
            .AddSingleton<IDocumentCacheStatusService>(statusService)
            .BuildServiceProvider();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = await DocumentCacheAdminCommandExecutor.ExecuteAsync(
            ParseStatusCommand(
                DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                "1",
                DocumentCacheAdminCommandSurface.JsonOptionName
            ),
            InvocationTarget(),
            serviceProvider,
            stdout,
            stderr
        );

        exitCode.Should().Be(DocumentCacheAdminExitCodes.Success);
        targetResolver.ResolveCount.Should().Be(1);
        statusService
            .EvaluationModes.Should()
            .ContainSingle()
            .Which.Should()
            .Be(DocumentCacheStatusEvaluationMode.StandaloneDirectObservation);
        JsonObject root = JsonNode.Parse(stdout.ToString())!.AsObject();
        JsonObject target = root["targets"]![0]!.AsObject();
        target["targetKey"]!["dataStoreId"]!.GetValue<long>().Should().Be(1);
        target["resolution"]!["status"]!.GetValue<string>().Should().Be("unresolved");
        target["resolution"]!["reason"]!.GetValue<string>().Should().Be("targetNotFound");
        target["targetDiagnostics"]!["recentEvents"]![0]!["category"]!
            .GetValue<string>()
            .Should()
            .Be("targetResolution");
        stderr.ToString().Should().BeEmpty();
    }

    private static ParseResult ParseStatusCommand(params string[] args) =>
        DocumentCacheAdminCommandSurface
            .CreateRootCommand()
            .Parse([DocumentCacheAdminCommandSurface.StatusCommandName, .. args]);

    private static DocumentCacheAdminInvocationTarget InvocationTarget() =>
        new(DocumentCacheTargetKey.Create("", 1));

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

    private static DocumentCacheStatusResponse UnresolvedStatusResponse()
    {
        string message = "DocumentCache target was not resolved by the shared registry.";
        var notObservedInventory = new DocumentCacheStatusInventoryComponent(
            DocumentCacheStatusInventoryStatus.NotObserved,
            DocumentCacheStatusInventoryReason.None,
            message: null
        );
        var unknownProviderPrerequisite = new DocumentCacheStatusProviderPrerequisiteComponent(
            DocumentCacheStatusProviderPrerequisiteStatus.Unknown,
            DocumentCacheStatusProviderPrerequisiteReason.None,
            message: null
        );

        return new(
            ObservedAt,
            [
                new DocumentCacheStatusTarget(
                    DocumentCacheStatusTargetKey.FromTargetKey(DocumentCacheTargetKey.Create("", 1)),
                    targetGeneration: null,
                    ObservedAt,
                    durableObservedAt: null,
                    provider: null,
                    physicalSourceFingerprint: null,
                    new DocumentCacheStatusResolutionComponent(
                        DocumentCacheStatusResolutionStatus.Unresolved,
                        DocumentCacheStatusResolutionReason.TargetNotFound,
                        ObservedAt,
                        message
                    ),
                    new DocumentCacheStatusEligibilityComponent(
                        DocumentCacheStatusEligibilityStatus.Unknown,
                        DocumentCacheStatusReason.UnresolvedTarget,
                        message
                    ),
                    new DocumentCacheStatusInventoryComponentGroup(
                        observedAt: null,
                        notObservedInventory,
                        notObservedInventory,
                        notObservedInventory,
                        notObservedInventory,
                        new DocumentCacheStatusEnqueueTriggerComponent(
                            DocumentCacheStatusEnqueueTriggerStatus.NotObserved,
                            DocumentCacheStatusInventoryReason.None,
                            message: null
                        )
                    ),
                    new DocumentCacheStatusProviderPrerequisitesComponent(
                        DocumentCacheStatusProviderPrerequisiteStatus.Unknown,
                        DocumentCacheStatusProviderPrerequisiteReason.None,
                        observedAt: null,
                        unknownProviderPrerequisite,
                        unknownProviderPrerequisite
                    ),
                    new DocumentCacheStatusLifecycleComponent(
                        DocumentCacheStatusLifecycleState.Unknown,
                        DocumentCacheStatusAvailability.Unknown,
                        message
                    ),
                    new DocumentCacheStatusCacheAheadComponent(
                        DocumentCacheStatusCacheAheadState.Unknown,
                        recoveryRequired: null,
                        message
                    ),
                    new DocumentCacheOperationalHealthComponent(
                        DocumentCacheOperationalHealthStatus.Unknown,
                        DocumentCacheStatusReason.UnresolvedTarget,
                        message
                    ),
                    new DocumentCacheCaughtUpComponent(
                        DocumentCacheCaughtUpStatus.Unknown,
                        DocumentCacheStatusReason.UnresolvedTarget,
                        message
                    ),
                    new DocumentCacheStatusQueueSummary(
                        DocumentCacheStatusQueuePresence.Unavailable,
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
                        message
                    ),
                    activeCommand: null,
                    lastEndedDiagnostic: null,
                    new DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusTargetDiagnosticEvent>([
                        new DocumentCacheStatusTargetDiagnosticEvent(
                            ObservedAt,
                            DocumentCacheStatusTargetDiagnosticCategory.TargetResolution,
                            message
                        ),
                    ]),
                    new DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusDocumentDiagnosticEvent>(),
                    new DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusPoisonTraversalDiagnosticEvent>(),
                    DocumentCacheStatusEffectiveSettings.FromEffectiveSettings(
                        DocumentCacheTargetEffectiveSettings.FromOptions(new DocumentCacheOptions())
                    ),
                    new DocumentCacheStatusEnqueueFailures()
                ),
            ]
        );
    }

    private static DocumentCacheStatusInventoryComponent ValidInventory() =>
        new(DocumentCacheStatusInventoryStatus.Valid, DocumentCacheStatusInventoryReason.None, message: null);

    private static DocumentCacheTargetRegistrySnapshot UnexpectedRegistrySnapshot(
        UnexpectedMembershipSnapshot snapshotKind
    )
    {
        DocumentCacheTargetObservation unexpectedTarget = DocumentCacheTargetObservation.Configured(
            DocumentCacheTargetKey.Create("TenantB", 2),
            DocumentCacheTargetEffectiveSettings.FromOptions(new DocumentCacheOptions())
        );

        ImmutableArray<DocumentCacheTargetObservation> targets = snapshotKind switch
        {
            UnexpectedMembershipSnapshot.Empty => [],
            UnexpectedMembershipSnapshot.WrongTarget => [unexpectedTarget],
            UnexpectedMembershipSnapshot.MultipleTargets =>
            [
                DocumentCacheTargetObservation.Configured(
                    DocumentCacheTargetKey.Create("", 1),
                    DocumentCacheTargetEffectiveSettings.FromOptions(new DocumentCacheOptions())
                ),
                unexpectedTarget,
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(snapshotKind), snapshotKind, null),
        };

        return new(targets, ObservedAt);
    }

    private sealed class ScriptedDocumentCacheStatusService(
        Func<
            DocumentCacheStatusEvaluationMode,
            CancellationToken,
            Task<DocumentCacheStatusResponse>
        > getStatusAsync
    ) : IDocumentCacheStatusService
    {
        public ScriptedDocumentCacheStatusService(
            Func<DocumentCacheStatusEvaluationMode, Task<DocumentCacheStatusResponse>> getStatusAsync
        )
            : this((evaluationMode, _) => getStatusAsync(evaluationMode)) { }

        private readonly List<DocumentCacheStatusEvaluationMode> _evaluationModes = [];

        public ImmutableArray<DocumentCacheStatusEvaluationMode> EvaluationModes => [.. _evaluationModes];

        public async Task<DocumentCacheStatusResponse> GetStatusAsync(
            CancellationToken cancellationToken = default,
            DocumentCacheStatusEvaluationMode evaluationMode =
                DocumentCacheStatusEvaluationMode.RuntimeEndpoint
        )
        {
            _evaluationModes.Add(evaluationMode);
            cancellationToken.ThrowIfCancellationRequested();
            return await getStatusAsync(evaluationMode, cancellationToken).ConfigureAwait(false);
        }
    }

    public enum UnexpectedMembershipSnapshot
    {
        Empty,
        WrongTarget,
        MultipleTargets,
    }

    private sealed class SnapshotTargetResolver(DocumentCacheTargetRegistrySnapshot registrySnapshot)
        : IDocumentCacheAdminTargetResolver
    {
        public int ResolveCount { get; private set; }

        public Task<DocumentCacheAdminTargetResolutionResult> ResolveAsync(
            DocumentCacheTargetKey targetKey,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolveCount++;

            return Task.FromResult(
                DocumentCacheAdminTargetResolutionResult.FromSnapshot(targetKey, registrySnapshot)
            );
        }
    }

    private sealed class DelayingTargetResolver : IDocumentCacheAdminTargetResolver
    {
        public async Task<DocumentCacheAdminTargetResolutionResult> ResolveAsync(
            DocumentCacheTargetKey targetKey,
            CancellationToken cancellationToken = default
        )
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            throw new AssertionException("Target resolution should be cancelled by the status timeout.");
        }
    }
}
