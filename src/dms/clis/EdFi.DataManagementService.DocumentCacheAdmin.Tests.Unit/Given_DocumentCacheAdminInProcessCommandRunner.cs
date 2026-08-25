// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.CommandLine;
using System.Data;
using System.Data.Common;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.DocumentCacheAdmin;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("InProcess")]
public sealed class Given_DocumentCacheAdminInProcessCommandRunner
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 21, 9, 0, 0, TimeSpan.Zero);
    private static readonly DocumentCacheTargetKey TargetKey = DocumentCacheTargetKey.Create("TenantA", 7);
    private static readonly DocumentCachePhysicalSourceFingerprint Fingerprint = new(
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    );

    [TestCaseSource(nameof(MutatingCommandCases))]
    public async Task It_routes_every_mutating_cli_command_to_the_shared_command_runner(
        MutatingCommandCase commandCase
    )
    {
        RecordingAdministrativeCommandRunner runner = new();
        await using ServiceProvider serviceProvider = CreateDispatcherServiceProvider(runner);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = await DocumentCacheAdminCommandExecutor.ExecuteAsync(
            ParseCommand(commandCase.CommandName, commandCase.CommandArgs),
            InvocationTarget(),
            serviceProvider,
            stdout,
            stderr
        );

        exitCode.Should().Be(DocumentCacheAdminExitCodes.Success);
        DocumentCacheAdministrativeCommandRunnerRequest request = runner
            .Requests.Should()
            .ContainSingle()
            .Subject;
        request.Command.Should().Be(commandCase.ExpectedCommand);
        request.TargetKey.TargetKey.Should().Be(TargetKey);
        request.ExpectedPhysicalSourceFingerprint.Should().Be(Fingerprint);
        request
            .AcceptedOfflineWriterAdmissionConfirmation.Should()
            .Be(commandCase.ExpectedOfflineWriterAdmission);
        runner.Workflows.Should().ContainSingle().Which.Should().Be(commandCase.ExpectedWorkflowType);

        JsonObject result = JsonNode.Parse(stdout.ToString())!.AsObject();
        result["command"]!.GetValue<string>().Should().Be(commandCase.ExpectedJsonCommand);
        result["status"]!.GetValue<string>().Should().Be("completed");
        stderr.ToString().Should().BeEmpty();
    }

    [TestCaseSource(nameof(DownstreamGuardCases))]
    [Category("Downstream")]
    public async Task It_rejects_the_production_default_unknown_Downstream_history_before_mutation(
        DownstreamCommandCase commandCase
    )
    {
        InProcessCommandHarness harness = InProcessCommandHarness.Create(
            commandCase.InitialLifecycle,
            new DocumentCacheUnknownDownstreamPublicationHistoryProvider(new FixedTimeProvider(ObservedAt))
        );
        await using ServiceProvider serviceProvider = harness.BuildServiceProvider();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = await DocumentCacheAdminCommandExecutor.ExecuteAsync(
            ParseCommand(commandCase.CommandName, commandCase.CommandArgs),
            InvocationTarget(),
            serviceProvider,
            stdout,
            stderr
        );

        exitCode.Should().Be(DocumentCacheAdminExitCodes.RejectedNoMutation);
        harness.Primitives.TransitionRequests.Should().BeEmpty();
        JsonObject result = JsonNode.Parse(stdout.ToString())!.AsObject();
        result["command"]!.GetValue<string>().Should().Be(commandCase.ExpectedJsonCommand);
        result["status"]!.GetValue<string>().Should().Be("rejectedNoMutation");
        result["classification"]!.GetValue<string>().Should().Be("downstreamHistoryPresentOrUnknown");
        result["mutated"]!.GetValue<bool>().Should().BeFalse();
        stderr.ToString().Should().BeEmpty();
    }

    [TestCaseSource(nameof(DownstreamGuardCases))]
    [Category("Downstream")]
    public async Task It_admits_offline_and_cache_ahead_commands_with_explicit_fake_internal_only_Downstream_history(
        DownstreamCommandCase commandCase
    )
    {
        RecordingDownstreamPublicationHistoryProvider downstreamHistoryProvider = new(
            DocumentCacheDownstreamPublicationStatus.InternalOnly
        );
        InProcessCommandHarness harness = InProcessCommandHarness.Create(
            commandCase.InitialLifecycle,
            downstreamHistoryProvider
        );
        await using ServiceProvider serviceProvider = harness.BuildServiceProvider();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = await DocumentCacheAdminCommandExecutor.ExecuteAsync(
            ParseCommand(commandCase.CommandName, commandCase.CommandArgs),
            InvocationTarget(),
            serviceProvider,
            stdout,
            stderr
        );

        exitCode.Should().Be(DocumentCacheAdminExitCodes.Success);
        downstreamHistoryProvider
            .Observations.Should()
            .ContainSingle()
            .Which.Should()
            .Be((TargetKey, Fingerprint));
        harness.Primitives.TransitionRequests.Should().NotBeEmpty();
        JsonObject result = JsonNode.Parse(stdout.ToString())!.AsObject();
        result["command"]!.GetValue<string>().Should().Be(commandCase.ExpectedJsonCommand);
        result["status"]!.GetValue<string>().Should().Be("completed");
        result["classification"]!.GetValue<string>().Should().Be("succeeded");
        result["mutated"]!.GetValue<bool>().Should().BeTrue();
        stderr.ToString().Should().BeEmpty();
    }

    [Test]
    [Category("Downstream")]
    public async Task It_keeps_cache_ahead_recovery_latch_set_until_rebuilding_transition_with_fake_internal_only_history()
    {
        RecordingDownstreamPublicationHistoryProvider downstreamHistoryProvider = new(
            DocumentCacheDownstreamPublicationStatus.InternalOnly
        );
        InProcessCommandHarness harness = InProcessCommandHarness.Create(
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, true),
            downstreamHistoryProvider
        );
        await using ServiceProvider serviceProvider = harness.BuildServiceProvider();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = await DocumentCacheAdminCommandExecutor.ExecuteAsync(
            ParseCommand(
                DocumentCacheAdminCommandSurface.RecoverCacheAheadCommandName,
                Args("internalCacheAheadRecovery", offlineWriterAdmission: true)
            ),
            InvocationTarget(),
            serviceProvider,
            stdout,
            stderr
        );

        exitCode.Should().Be(DocumentCacheAdminExitCodes.Success);
        harness
            .Primitives.Events.Should()
            .Equal(
                "transition:Tracking:True->Resetting:True",
                "clear:DocumentCache",
                "clear:DocumentProjectionWork",
                "transition:Resetting:True->Rebuilding:False",
                "transition:Rebuilding:False->Tracking:False"
            );
        JsonObject result = JsonNode.Parse(stdout.ToString())!.AsObject();
        result["command"]!.GetValue<string>().Should().Be("internalOnlyCacheAheadRecovery");
        result["status"]!.GetValue<string>().Should().Be("completed");
        result["cacheAheadRecoveryRequired"]!.GetValue<bool>().Should().BeFalse();
        stderr.ToString().Should().BeEmpty();
    }

    [Test]
    public void It_does_not_expose_a_packaged_downstream_history_override_option()
    {
        RootCommand rootCommand = DocumentCacheAdminCommandSurface.CreateRootCommand();

        AllOptionNames(rootCommand)
            .Should()
            .NotContain(name =>
                name.Contains("downstream", StringComparison.OrdinalIgnoreCase)
                || name.Contains("history", StringComparison.OrdinalIgnoreCase)
                || name.Contains("internal-only", StringComparison.OrdinalIgnoreCase)
                || name.Contains("internalOnly", StringComparison.Ordinal)
                || name.Contains("proof", StringComparison.OrdinalIgnoreCase)
            );
    }

    [Test]
    public async Task It_uses_the_shared_status_dto_without_fabricating_process_local_command_observations()
    {
        RecordingStatusService statusService = new();
        await using ServiceProvider serviceProvider = new ServiceCollection()
            .AddSingleton<IDocumentCacheStatusService>(statusService)
            .BuildServiceProvider();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = await DocumentCacheAdminCommandExecutor.ExecuteAsync(
            ParseCommand(
                DocumentCacheAdminCommandSurface.StatusCommandName,
                DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                TargetKey.DataStoreId.ToString(),
                DocumentCacheAdminCommandSurface.JsonOptionName
            ),
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

        JsonObject target = JsonNode.Parse(stdout.ToString())!["targets"]![0]!.AsObject();
        target["activeCommand"].Should().BeNull();
        target["lastEndedDiagnostic"].Should().BeNull();
        stderr.ToString().Should().BeEmpty();
    }

    private static IEnumerable<TestCaseData> MutatingCommandCases()
    {
        yield return new TestCaseData(
            new MutatingCommandCase(
                DocumentCacheAdminCommandSurface.ActivateNewEmptyCommandName,
                Args("newEmptyActivation"),
                DocumentCacheAdministrativeCommand.GuardedNewEmptyActivation,
                "guardedNewEmptyActivation",
                typeof(DocumentCacheGuardedNewEmptyActivationCommand)
            )
        ).SetName("activate-new-empty");

        yield return new TestCaseData(
            new MutatingCommandCase(
                DocumentCacheAdminCommandSurface.ActivateOfflineCommandName,
                Args("offlineActivation", offlineWriterAdmission: true),
                DocumentCacheAdministrativeCommand.OfflineActivation,
                "offlineActivation",
                typeof(DocumentCacheOfflineActivationCommand),
                DocumentCacheOfflineWriterAdmissionConfirmation.OfflineActivationWritersClosedAndDrained
            )
        ).SetName("activate-offline");

        yield return new TestCaseData(
            new MutatingCommandCase(
                DocumentCacheAdminCommandSurface.DeactivateOfflineCommandName,
                Args("offlineDeactivation", offlineWriterAdmission: true),
                DocumentCacheAdministrativeCommand.OfflineDeactivation,
                "offlineDeactivation",
                typeof(DocumentCacheOfflineDeactivationCommand),
                DocumentCacheOfflineWriterAdmissionConfirmation.OfflineDeactivationWritersClosedAndDrained
            )
        ).SetName("deactivate-offline");

        yield return new TestCaseData(
            new MutatingCommandCase(
                DocumentCacheAdminCommandSurface.RebuildOnlineCommandName,
                Args("onlineCacheRebuild"),
                DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
                "onlineCacheRebuild",
                typeof(DocumentCacheOnlineCacheRebuildCommand)
            )
        ).SetName("rebuild-online");

        yield return new TestCaseData(
            new MutatingCommandCase(
                DocumentCacheAdminCommandSurface.ScrubCommandName,
                Args("integrityScrub"),
                DocumentCacheAdministrativeCommand.ExplicitIntegrityScrub,
                "explicitIntegrityScrub",
                typeof(DocumentCacheExplicitIntegrityScrubCommand)
            )
        ).SetName("scrub");

        yield return new TestCaseData(
            new MutatingCommandCase(
                DocumentCacheAdminCommandSurface.RecoverCacheAheadCommandName,
                Args("internalCacheAheadRecovery", offlineWriterAdmission: true),
                DocumentCacheAdministrativeCommand.InternalOnlyCacheAheadRecovery,
                "internalOnlyCacheAheadRecovery",
                typeof(DocumentCacheInternalOnlyCacheAheadRecoveryCommand),
                DocumentCacheOfflineWriterAdmissionConfirmation.InternalOnlyCacheAheadRecoveryWritersClosedAndDrained
            )
        ).SetName("recover-cache-ahead");
    }

    private static IEnumerable<TestCaseData> DownstreamGuardCases()
    {
        yield return new TestCaseData(
            new DownstreamCommandCase(
                DocumentCacheAdminCommandSurface.ActivateOfflineCommandName,
                Args("offlineActivation", offlineWriterAdmission: true),
                new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Disabled, false),
                "offlineActivation"
            )
        ).SetName("activate-offline");

        yield return new TestCaseData(
            new DownstreamCommandCase(
                DocumentCacheAdminCommandSurface.DeactivateOfflineCommandName,
                Args("offlineDeactivation", offlineWriterAdmission: true),
                new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false),
                "offlineDeactivation"
            )
        ).SetName("deactivate-offline");

        yield return new TestCaseData(
            new DownstreamCommandCase(
                DocumentCacheAdminCommandSurface.RecoverCacheAheadCommandName,
                Args("internalCacheAheadRecovery", offlineWriterAdmission: true),
                new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, true),
                "internalOnlyCacheAheadRecovery"
            )
        ).SetName("recover-cache-ahead");
    }

    private static string[] Args(string confirmation, bool offlineWriterAdmission = false)
    {
        List<string> args =
        [
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            TargetKey.DataStoreId.ToString(),
            DocumentCacheAdminCommandSurface.TenantKeyOptionName,
            TargetKey.TenantKey,
            DocumentCacheAdminCommandSurface.ConfirmOptionName,
            confirmation,
            DocumentCacheAdminCommandSurface.ExpectedPhysicalSourceFingerprintOptionName,
            Fingerprint.Value,
            DocumentCacheAdminCommandSurface.JsonOptionName,
        ];

        if (offlineWriterAdmission)
        {
            args.Add(DocumentCacheAdminCommandSurface.OfflineWriterAdmissionOptionName);
            args.Add(DocumentCacheAdminCommandSurface.OfflineWriterAdmissionClosedAndDrainedOptionValue);
        }

        return [.. args];
    }

    private static ParseResult ParseCommand(string commandName, params string[] args) =>
        DocumentCacheAdminCommandSurface.CreateRootCommand().Parse([commandName, .. args]);

    private static DocumentCacheAdminInvocationTarget InvocationTarget() =>
        new(TargetKey, DocumentCacheAdminInvocationTargetSource.Options);

    private static ServiceProvider CreateDispatcherServiceProvider(
        IDocumentCacheAdministrativeCommandRunner commandRunner
    )
    {
        ServiceCollection services = new();
        services.AddSingleton(commandRunner);
        services.AddSingleton<
            IDocumentCacheGuardedNewEmptyActivationCommand,
            DocumentCacheGuardedNewEmptyActivationCommand
        >();
        services.AddSingleton<
            IDocumentCacheOfflineActivationCommand,
            DocumentCacheOfflineActivationCommand
        >();
        services.AddSingleton<
            IDocumentCacheOfflineDeactivationCommand,
            DocumentCacheOfflineDeactivationCommand
        >();
        services.AddSingleton<
            IDocumentCacheOnlineCacheRebuildCommand,
            DocumentCacheOnlineCacheRebuildCommand
        >();
        services.AddSingleton<
            IDocumentCacheExplicitIntegrityScrubCommand,
            DocumentCacheExplicitIntegrityScrubCommand
        >();
        services.AddSingleton<
            IDocumentCacheInternalOnlyCacheAheadRecoveryCommand,
            DocumentCacheInternalOnlyCacheAheadRecoveryCommand
        >();
        services.AddSingleton<
            IDocumentCacheDownstreamPublicationHistoryProvider,
            ThrowingDownstreamPublicationHistoryProvider
        >();
        services.AddSingleton<IDocumentCacheBaselineSeeder, ThrowingBaselineSeeder>();
        services.AddSingleton<IDocumentCacheAdministrativeDrainer, ThrowingAdministrativeDrainer>();
        services.AddSingleton<
            IDocumentCacheAdminMutatingCommandDispatcher,
            DocumentCacheAdminMutatingCommandDispatcher
        >();
        return services.BuildServiceProvider();
    }

    private static IEnumerable<string> AllOptionNames(Command command)
    {
        foreach (Option option in command.Options)
        {
            yield return option.Name;
        }

        foreach (Command subcommand in command.Subcommands)
        {
            foreach (string optionName in AllOptionNames(subcommand))
            {
                yield return optionName;
            }
        }
    }

    public sealed record MutatingCommandCase(
        string CommandName,
        string[] CommandArgs,
        DocumentCacheAdministrativeCommand ExpectedCommand,
        string ExpectedJsonCommand,
        Type ExpectedWorkflowType,
        DocumentCacheOfflineWriterAdmissionConfirmation? ExpectedOfflineWriterAdmission = null
    )
    {
        public override string ToString() => CommandName;
    }

    public sealed record DownstreamCommandCase(
        string CommandName,
        string[] CommandArgs,
        DocumentCacheLifecycleObservation InitialLifecycle,
        string ExpectedJsonCommand
    )
    {
        public override string ToString() => CommandName;
    }

    private sealed class RecordingAdministrativeCommandRunner : IDocumentCacheAdministrativeCommandRunner
    {
        private readonly List<DocumentCacheAdministrativeCommandRunnerRequest> _requests = [];
        private readonly List<Type> _workflows = [];

        public ImmutableArray<DocumentCacheAdministrativeCommandRunnerRequest> Requests => [.. _requests];

        public ImmutableArray<Type> Workflows => [.. _workflows];

        public Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
            DocumentCacheAdministrativeCommandRunnerRequest request,
            IDocumentCacheAdministrativeCommandWorkflow workflow,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            _requests.Add(request);
            _workflows.Add(workflow.GetType());

            return Task.FromResult(
                new DocumentCacheAdministrativeCommandResult(
                    request.Command,
                    request.TargetKey,
                    DocumentCacheAdministrativeCommandStatus.Completed,
                    DocumentCacheAdministrativeCommandClassification.Succeeded,
                    mutated: false,
                    targetGeneration: 3,
                    physicalSourceFingerprint: Fingerprint,
                    lifecycle: DocumentCacheLifecycleState.Tracking,
                    cacheAheadRecoveryRequired: false,
                    phaseDiagnostics: [],
                    request.AcceptedOfflineWriterAdmissionConfirmation,
                    elapsedCommandTime: TimeSpan.FromMilliseconds(1)
                )
            );
        }
    }

    private sealed class InProcessCommandHarness
    {
        private readonly DocumentCacheTargetExecutionContext _executionContext;
        private readonly IDocumentCacheDownstreamPublicationHistoryProvider _downstreamHistoryProvider;

        private InProcessCommandHarness(
            DocumentCacheTargetExecutionContext executionContext,
            ScriptedAdministrativePrimitives primitives,
            IDocumentCacheDownstreamPublicationHistoryProvider downstreamHistoryProvider
        )
        {
            _executionContext = executionContext;
            Primitives = primitives;
            _downstreamHistoryProvider = downstreamHistoryProvider;
        }

        public ScriptedAdministrativePrimitives Primitives { get; }

        public static InProcessCommandHarness Create(
            DocumentCacheLifecycleObservation lifecycle,
            IDocumentCacheDownstreamPublicationHistoryProvider downstreamHistoryProvider
        )
        {
            DocumentCacheTargetExecutionContext executionContext = CreateExecutionContext(lifecycle);
            return new(
                executionContext,
                new ScriptedAdministrativePrimitives(lifecycle),
                downstreamHistoryProvider
            );
        }

        public ServiceProvider BuildServiceProvider()
        {
            DocumentCacheProjectionObservationStore observationStore = new(new FixedTimeProvider(ObservedAt));
            DocumentCacheAdministrativeCommandRunner runner = new(
                new StubProjectionSupervisor([CreateRuntimeContext(_executionContext, observationStore)]),
                new StubTargetRegistry(_executionContext),
                new RecordingAdministrativeMutex(),
                Primitives,
                observationStore,
                new FixedTimeProvider(ObservedAt),
                NoOpDocumentCacheProviderCommandTimeoutClassifier.Instance,
                NullLogger<DocumentCacheAdministrativeCommandRunner>.Instance
            );

            ServiceCollection services = new();
            services.AddSingleton<IDocumentCacheAdministrativeCommandRunner>(runner);
            services.AddSingleton<IDocumentCacheDownstreamPublicationHistoryProvider>(
                _downstreamHistoryProvider
            );
            services.AddSingleton<IDocumentCacheBaselineSeeder, SucceedingBaselineSeeder>();
            services.AddSingleton<IDocumentCacheAdministrativeDrainer, SucceedingAdministrativeDrainer>();
            services.AddSingleton<
                IDocumentCacheGuardedNewEmptyActivationCommand,
                DocumentCacheGuardedNewEmptyActivationCommand
            >();
            services.AddSingleton<
                IDocumentCacheOfflineActivationCommand,
                DocumentCacheOfflineActivationCommand
            >();
            services.AddSingleton<
                IDocumentCacheOfflineDeactivationCommand,
                DocumentCacheOfflineDeactivationCommand
            >();
            services.AddSingleton<
                IDocumentCacheOnlineCacheRebuildCommand,
                DocumentCacheOnlineCacheRebuildCommand
            >();
            services.AddSingleton<
                IDocumentCacheExplicitIntegrityScrubCommand,
                DocumentCacheExplicitIntegrityScrubCommand
            >();
            services.AddSingleton<
                IDocumentCacheInternalOnlyCacheAheadRecoveryCommand,
                DocumentCacheInternalOnlyCacheAheadRecoveryCommand
            >();
            services.AddSingleton<
                IDocumentCacheAdminMutatingCommandDispatcher,
                DocumentCacheAdminMutatingCommandDispatcher
            >();
            return services.BuildServiceProvider();
        }

        private static DocumentCacheTargetExecutionContext CreateExecutionContext(
            DocumentCacheLifecycleObservation lifecycle
        ) =>
            new(
                TargetKey,
                new DocumentCacheTargetContextGeneration(1),
                EffectiveSettings(),
                new DocumentCacheTargetDataStoreMetadata(
                    TargetKey.DataStoreId,
                    RelationalProviderToken.Postgresql.Value
                ),
                new DocumentCacheTargetConnectionInput(RelationalProviderToken.Postgresql, "connection"),
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

        private static DocumentCacheProjectionTargetRuntimeContext CreateRuntimeContext(
            DocumentCacheTargetExecutionContext executionContext,
            IDocumentCacheProjectionObservationSink observationSink
        ) =>
            new(
                executionContext,
                new DocumentCacheProjectionTargetProviderAdapters(
                    executionContext.ProviderToken,
                    MaterializationTargetContext(),
                    new ThrowingDocumentCacheMaterializer(),
                    new ThrowingDocumentCacheWriter()
                ),
                observationSink
            );

        private static DocumentCacheMaterializationTargetContext MaterializationTargetContext() =>
            new(
                new DocumentCacheProjectionTargetKey(
                    TargetKey.TenantKey,
                    new DataStoreId(TargetKey.DataStoreId)
                ),
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

            return new(
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
    }

    private sealed class StubTargetRegistry(DocumentCacheTargetExecutionContext executionContext)
        : IDocumentCacheTargetRegistry
    {
        public DocumentCacheTargetRegistrySnapshot CurrentSnapshot { get; } =
            new([CreateEligibleObservation(executionContext)], ObservedAt);

        public DocumentCacheTargetRuntimeSnapshot CurrentRuntimeSnapshot { get; } =
            new([executionContext], ObservedAt);

        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CurrentSnapshot);
        }

        private static DocumentCacheTargetObservation CreateEligibleObservation(
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
    }

    private sealed class StubProjectionSupervisor(
        IEnumerable<DocumentCacheProjectionTargetRuntimeContext> contexts
    ) : IDocumentCacheProjectionSupervisor, IDocumentCacheProjectionRetainedTargetContextReleaser
    {
        public ImmutableArray<DocumentCacheProjectionTargetRuntimeContext> CurrentTargetContexts { get; } =
            contexts.ToImmutableArray();

        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task ReleaseRetainedCommandOwnedTargetContextAsync(
            DocumentCacheProjectionTargetRuntimeContext targetContext,
            CancellationToken cancellationToken = default
        )
        {
            _ = targetContext;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAdministrativeMutex : IDocumentCacheAdministrativeMutex
    {
        public RelationalProviderToken ProviderToken => RelationalProviderToken.Postgresql;

        public Task<IDocumentCacheAdministrativeMutexLease> AcquireAsync(
            DocumentCacheTargetConnectionInput connectionInput,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            connectionInput.ProviderToken.Should().Be(RelationalProviderToken.Postgresql);
            return Task.FromResult<IDocumentCacheAdministrativeMutexLease>(new RecordingMutexLease());
        }
    }

    private sealed class RecordingMutexLease : IDocumentCacheAdministrativeMutexLease
    {
        public RelationalProviderToken ProviderToken => RelationalProviderToken.Postgresql;

        public DbConnection Connection => throw new NotSupportedException();

        public bool IsSessionOpen => true;

        public Task<IRelationalWriteSession> BeginTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IRelationalWriteSession>(new RecordingWriteSession());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingWriteSession : IRelationalWriteSession
    {
        public DbConnection Connection => throw new NotSupportedException();

        public DbTransaction Transaction => throw new NotSupportedException();

        public DbCommand CreateCommand(RelationalCommand command) => throw new NotSupportedException();

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            return cancellationToken.IsCancellationRequested
                ? Task.FromCanceled(cancellationToken)
                : Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScriptedAdministrativePrimitives(DocumentCacheLifecycleObservation lifecycle)
        : IDocumentCacheAdministrativePrimitives
    {
        private DocumentCacheLifecycleObservation _lifecycle = lifecycle;
        private readonly List<DocumentCacheAdministrativeLifecycleTransitionRequest> _transitionRequests = [];
        private readonly List<string> _events = [];

        public RelationalProviderToken ProviderToken => RelationalProviderToken.Postgresql;

        public ImmutableArray<DocumentCacheAdministrativeLifecycleTransitionRequest> TransitionRequests =>
            [.. _transitionRequests];

        public ImmutableArray<string> Events => [.. _events];

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
            return Task.FromResult(DocumentCacheLifecycleReadResult.Success(_lifecycle));
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
                new DocumentCacheGuardedNewEmptyActivationState(
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
            return Task.FromResult(
                DocumentCacheProviderPrerequisiteValidationResult.ActivationPreflight(
                    DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
                )
            );
        }

        public Task<DocumentCacheAdministrativeLifecycleTransitionResult> TryTransitionLifecycleAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeLifecycleTransitionRequest request,
            CancellationToken cancellationToken = default
        )
        {
            _ = mutexSession;
            cancellationToken.ThrowIfCancellationRequested();
            _transitionRequests.Add(request);
            _events.Add(
                $"transition:{request.ExpectedLifecycle}:{request.ExpectedCacheAheadRecoveryRequired}->{request.NextLifecycle}:{request.NextCacheAheadRecoveryRequired}"
            );

            if (
                _lifecycle.State != request.ExpectedLifecycle
                || _lifecycle.CacheAheadRecoveryRequired != request.ExpectedCacheAheadRecoveryRequired
            )
            {
                return Task.FromResult(
                    DocumentCacheAdministrativeLifecycleTransitionResult.NotTransitioned(
                        DocumentCacheLifecycleReadResult.Success(_lifecycle)
                    )
                );
            }

            _lifecycle = new(request.NextLifecycle, request.NextCacheAheadRecoveryRequired);
            return Task.FromResult(
                DocumentCacheAdministrativeLifecycleTransitionResult.Transitioned(_lifecycle)
            );
        }

        public Task<DocumentCacheAdministrativeClearBatchResult> ClearDocumentCacheBatchAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeClearBatchRequest request,
            CancellationToken cancellationToken = default
        )
        {
            _ = mutexSession;
            cancellationToken.ThrowIfCancellationRequested();
            _events.Add($"clear:{DocumentCacheAdministrativeClearTarget.DocumentCache}");
            return Task.FromResult(EmptyClearBatch(DocumentCacheAdministrativeClearTarget.DocumentCache));
        }

        public Task<DocumentCacheAdministrativeClearBatchResult> ClearDocumentProjectionWorkBatchAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeClearBatchRequest request,
            DocumentCacheAdministrativeWorkClearance clearance,
            CancellationToken cancellationToken = default
        )
        {
            _ = mutexSession;
            _ = clearance;
            cancellationToken.ThrowIfCancellationRequested();
            _events.Add($"clear:{DocumentCacheAdministrativeClearTarget.DocumentProjectionWork}");
            return Task.FromResult(
                EmptyClearBatch(DocumentCacheAdministrativeClearTarget.DocumentProjectionWork)
            );
        }

        public Task<DocumentCacheAdministrativeProjectedStateEmptinessResult> ReadProjectedStateEmptinessAsync(
            IRelationalWriteSession mutexSession,
            CancellationToken cancellationToken = default
        )
        {
            _ = mutexSession;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                new DocumentCacheAdministrativeProjectedStateEmptinessResult(
                    documentCacheEmpty: true,
                    documentProjectionWorkEmpty: true,
                    "Projected state is empty."
                )
            );
        }

        public Task<DocumentCacheAdministrativeBaselineBoundaryResult> CaptureBaselineBoundaryAsync(
            IRelationalWriteSession mutexSession,
            CancellationToken cancellationToken = default
        )
        {
            _ = mutexSession;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                new DocumentCacheAdministrativeBaselineBoundaryResult(
                    boundaryDocumentId: null,
                    "No boundary."
                )
            );
        }

        public Task<DocumentCacheAdministrativeWorkHighWaterObservationResult> ObserveWorkHighWaterAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeWorkHighWaterObservationRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<DocumentCacheAdministrativeBaselineSeedPageResult> SeedBaselinePageAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeBaselineSeedPageRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<DocumentCacheAdministrativeScrubPageResult> ScrubPageAsync(
            IRelationalWriteSession mutexSession,
            DocumentCacheAdministrativeScrubPageRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        private static DocumentCacheAdministrativeClearBatchResult EmptyClearBatch(
            DocumentCacheAdministrativeClearTarget target
        ) => new(target, pageSize: 3, clearedDocumentIds: [], "No rows to clear.");
    }

    private sealed class RecordingDownstreamPublicationHistoryProvider(
        DocumentCacheDownstreamPublicationStatus status
    ) : IDocumentCacheDownstreamPublicationHistoryProvider
    {
        private readonly List<(
            DocumentCacheTargetKey TargetKey,
            DocumentCachePhysicalSourceFingerprint? Fingerprint
        )> _observations = [];

        public ImmutableArray<(
            DocumentCacheTargetKey TargetKey,
            DocumentCachePhysicalSourceFingerprint? Fingerprint
        )> Observations => [.. _observations];

        public Task<DocumentCacheDownstreamPublicationHistoryObservation> ObserveAsync(
            DocumentCacheTargetKey targetKey,
            DocumentCachePhysicalSourceFingerprint? currentPhysicalSourceFingerprint,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            _observations.Add((targetKey, currentPhysicalSourceFingerprint));

            return Task.FromResult(
                new DocumentCacheDownstreamPublicationHistoryObservation(
                    targetKey,
                    currentPhysicalSourceFingerprint,
                    status,
                    evidenceSource: "document-cache-cli-unit-test",
                    evidenceGenerationIdentifier: "test-generation-1",
                    ObservedAt,
                    "Fake trusted downstream-publication-history evidence for in-process CLI tests."
                )
            );
        }
    }

    private sealed class SucceedingBaselineSeeder : IDocumentCacheBaselineSeeder
    {
        public Task<DocumentCacheBaselineSeedingResult> SeedAsync(
            DocumentCacheAdministrativeCommandExecutionContext context,
            CancellationToken cancellationToken = default
        )
        {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
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
        public Task<DocumentCacheAdministrativeDrainToEmptyResult> DrainToEmptyAsync(
            DocumentCacheAdministrativeCommandExecutionContext context,
            CancellationToken cancellationToken = default
        )
        {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                DocumentCacheAdministrativeDrainToEmptyResult.Succeeded(
                    new DocumentCacheAdministrativeDrainStats()
                )
            );
        }

        public Task<DocumentCacheAdministrativeDrainSliceResult> DrainBackpressureReliefSliceAsync(
            DocumentCacheAdministrativeCommandExecutionContext context,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }

    private sealed class ThrowingDownstreamPublicationHistoryProvider
        : IDocumentCacheDownstreamPublicationHistoryProvider
    {
        public Task<DocumentCacheDownstreamPublicationHistoryObservation> ObserveAsync(
            DocumentCacheTargetKey targetKey,
            DocumentCachePhysicalSourceFingerprint? currentPhysicalSourceFingerprint,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }

    private sealed class ThrowingBaselineSeeder : IDocumentCacheBaselineSeeder
    {
        public Task<DocumentCacheBaselineSeedingResult> SeedAsync(
            DocumentCacheAdministrativeCommandExecutionContext context,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }

    private sealed class ThrowingAdministrativeDrainer : IDocumentCacheAdministrativeDrainer
    {
        public Task<DocumentCacheAdministrativeDrainToEmptyResult> DrainToEmptyAsync(
            DocumentCacheAdministrativeCommandExecutionContext context,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<DocumentCacheAdministrativeDrainSliceResult> DrainBackpressureReliefSliceAsync(
            DocumentCacheAdministrativeCommandExecutionContext context,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }

    private sealed class RecordingStatusService : IDocumentCacheStatusService
    {
        private readonly List<DocumentCacheStatusEvaluationMode> _evaluationModes = [];

        public ImmutableArray<DocumentCacheStatusEvaluationMode> EvaluationModes => [.. _evaluationModes];

        public Task<DocumentCacheStatusResponse> GetStatusAsync(
            CancellationToken cancellationToken = default,
            DocumentCacheStatusEvaluationMode evaluationMode =
                DocumentCacheStatusEvaluationMode.RuntimeEndpoint
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            _evaluationModes.Add(evaluationMode);
            return Task.FromResult(CreateStatusResponse());
        }

        private static DocumentCacheStatusResponse CreateStatusResponse() =>
            new(
                ObservedAt,
                [
                    new DocumentCacheStatusTarget(
                        DocumentCacheStatusTargetKey.FromTargetKey(TargetKey),
                        targetGeneration: 1,
                        ObservedAt,
                        durableObservedAt: ObservedAt,
                        provider: "postgresql",
                        physicalSourceFingerprint: Fingerprint.Value,
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
                        DocumentCacheStatusEffectiveSettings.FromEffectiveSettings(EffectiveSettings()),
                        new DocumentCacheStatusEnqueueFailures()
                    ),
                ]
            );

        private static DocumentCacheStatusInventoryComponent ValidInventory() =>
            new(
                DocumentCacheStatusInventoryStatus.Valid,
                DocumentCacheStatusInventoryReason.None,
                message: null
            );
    }

    private static DocumentCacheTargetEffectiveSettings EffectiveSettings() =>
        new(
            readAccelerationEnabled: true,
            directFillTimeout: TimeSpan.FromMilliseconds(250),
            projectorPollInterval: TimeSpan.FromSeconds(5),
            projectorPageSize: 3,
            projectorMaxConcurrentTargets: 1,
            projectorFailureBackoff: TimeSpan.FromSeconds(10),
            projectorBaselineHighWaterMark: 1000,
            administrationWorkflowTimeout: TimeSpan.FromHours(24)
        );

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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
