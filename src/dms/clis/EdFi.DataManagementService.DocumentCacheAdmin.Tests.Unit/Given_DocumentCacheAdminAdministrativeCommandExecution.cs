// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.CommandLine;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.DocumentCacheAdmin;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("AdministrativeCommandExecution")]
public sealed class Given_DocumentCacheAdminAdministrativeCommandExecution
{
    private const string Fingerprint =
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Test]
    public async Task It_invokes_the_mutating_dispatcher_with_the_shared_request_and_writes_json_result()
    {
        RecordingMutatingCommandDispatcher dispatcher = new(_ => CompletedResult());
        await using ServiceProvider serviceProvider = new ServiceCollection()
            .AddSingleton<IDocumentCacheAdminMutatingCommandDispatcher>(dispatcher)
            .BuildServiceProvider();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = await DocumentCacheAdminCommandExecutor.ExecuteAsync(
            ParseCommand(
                DocumentCacheAdminCommandSurface.RebuildOnlineCommandName,
                DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                "1",
                DocumentCacheAdminCommandSurface.ConfirmOptionName,
                "onlineCacheRebuild",
                DocumentCacheAdminCommandSurface.JsonOptionName
            ),
            InvocationTarget(),
            serviceProvider,
            stdout,
            stderr
        );

        exitCode.Should().Be(DocumentCacheAdminExitCodes.Success);
        DocumentCacheAdminMutatingCommandRequest commandRequest = dispatcher
            .Requests.Should()
            .ContainSingle()
            .Subject;
        commandRequest.CommandName.Should().Be(DocumentCacheAdminCommandSurface.RebuildOnlineCommandName);
        commandRequest.Request.Should().BeOfType<DocumentCacheOnlineCacheRebuildRequest>();

        string json = stdout.ToString();
        json.TrimEnd().Should().NotContain("\n");
        JsonObject root = JsonNode.Parse(json)!.AsObject();
        root["command"]!.GetValue<string>().Should().Be("onlineCacheRebuild");
        root["status"]!.GetValue<string>().Should().Be("completed");
        root["classification"]!.GetValue<string>().Should().Be("succeeded");
        root.Should().NotContainKey("elapsedCommandTime");
        root["elapsedCommandTimeSeconds"]!.GetValue<double>().Should().Be(1.25);
        root.Should().NotContainKey("result");
        stderr.ToString().Should().BeEmpty();
    }

    [Test]
    public async Task It_gets_the_mapping_set_only_when_mutating_dispatch_creates_a_target_runtime_context()
    {
        ThrowingMappingSetProvider mappingSetProvider = new(
            "Mutating target-context creation requested a mapping set."
        );
        FixedRuntimeMappingSetCompiler runtimeCompiler = new(SqlDialect.Pgsql);
        await using ServiceProvider serviceProvider = new ServiceCollection()
            .AddSingleton(A.Fake<IDocumentCacheMaterializer>())
            .AddSingleton(A.Fake<IDocumentCacheWriter>())
            .AddSingleton(A.Fake<IDocumentCacheProjectionObservationSink>())
            .AddSingleton<IMappingSetProvider>(mappingSetProvider)
            .AddSingleton<IRuntimeMappingSetCompiler>(runtimeCompiler)
            .AddSingleton<
                IDocumentCacheProjectionTargetRuntimeContextFactory,
                DocumentCacheProjectionTargetRuntimeContextFactory
            >()
            .AddSingleton<IDocumentCacheAdminMutatingCommandDispatcher, TargetContextCreatingDispatcher>()
            .BuildServiceProvider();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = await DocumentCacheAdminCommandExecutor.ExecuteAsync(
            ParseCommand(
                DocumentCacheAdminCommandSurface.RebuildOnlineCommandName,
                DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                "1",
                DocumentCacheAdminCommandSurface.ConfirmOptionName,
                "onlineCacheRebuild"
            ),
            InvocationTarget(),
            serviceProvider,
            stdout,
            stderr
        );

        exitCode.Should().Be(DocumentCacheAdminExitCodes.UnexpectedFailure);
        runtimeCompiler.GetCurrentKeyCount.Should().Be(1);
        runtimeCompiler.CompileCount.Should().Be(0);
        mappingSetProvider.GetOrCreateCount.Should().Be(1);
        stdout.ToString().Should().BeEmpty();
        stderr.ToString().Should().Contain("failed before a shared result could be produced");
    }

    [TestCase(
        DocumentCacheAdministrativeCommandStatus.Completed,
        DocumentCacheAdministrativeCommandClassification.Succeeded,
        true,
        DocumentCacheAdminExitCodes.Success
    )]
    [TestCase(
        DocumentCacheAdministrativeCommandStatus.RejectedNoMutation,
        DocumentCacheAdministrativeCommandClassification.CacheAheadLatchSet,
        false,
        DocumentCacheAdminExitCodes.RejectedNoMutation
    )]
    [TestCase(
        DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
        DocumentCacheAdministrativeCommandClassification.MutexAcquisitionFailed,
        false,
        DocumentCacheAdminExitCodes.FailedNoMutation
    )]
    [TestCase(
        DocumentCacheAdministrativeCommandStatus.IncompleteRetryable,
        DocumentCacheAdministrativeCommandClassification.WorkflowTimeout,
        true,
        DocumentCacheAdminExitCodes.IncompleteRetryable
    )]
    public async Task It_maps_shared_result_status_to_exit_code(
        DocumentCacheAdministrativeCommandStatus status,
        DocumentCacheAdministrativeCommandClassification classification,
        bool mutated,
        int expectedExitCode
    )
    {
        RecordingMutatingCommandDispatcher dispatcher = new(_ => Result(status, classification, mutated));
        await using ServiceProvider serviceProvider = new ServiceCollection()
            .AddSingleton<IDocumentCacheAdminMutatingCommandDispatcher>(dispatcher)
            .BuildServiceProvider();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = await DocumentCacheAdminCommandExecutor.ExecuteAsync(
            ParseCommand(
                DocumentCacheAdminCommandSurface.ScrubCommandName,
                DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                "1",
                DocumentCacheAdminCommandSurface.ConfirmOptionName,
                "integrityScrub"
            ),
            InvocationTarget(),
            serviceProvider,
            stdout,
            stderr
        );

        exitCode.Should().Be(expectedExitCode);
        stdout.ToString().Should().Contain($"status={status} classification={classification}");
        stderr.ToString().Should().BeEmpty();
    }

    [Test]
    public async Task It_returns_configuration_error_when_the_mutating_runtime_is_not_configured()
    {
        await using ServiceProvider serviceProvider = new ServiceCollection().BuildServiceProvider();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = await DocumentCacheAdminCommandExecutor.ExecuteAsync(
            ParseCommand(
                DocumentCacheAdminCommandSurface.ScrubCommandName,
                DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                "1",
                DocumentCacheAdminCommandSurface.ConfirmOptionName,
                "integrityScrub"
            ),
            InvocationTarget(),
            serviceProvider,
            stdout,
            stderr
        );

        exitCode.Should().Be(DocumentCacheAdminExitCodes.ConfigurationError);
        stdout.ToString().Should().BeEmpty();
        stderr.ToString().Should().Contain("runtime services are not configured");
    }

    [Test]
    public async Task It_writes_the_shared_runner_target_resolution_result_returned_by_dispatcher()
    {
        RecordingMutatingCommandDispatcher dispatcher = new(_ => TargetNotConfiguredResult());
        await using ServiceProvider serviceProvider = new ServiceCollection()
            .AddSingleton<IDocumentCacheAdminMutatingCommandDispatcher>(dispatcher)
            .BuildServiceProvider();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = await DocumentCacheAdminCommandExecutor.ExecuteAsync(
            ParseCommand(
                DocumentCacheAdminCommandSurface.RebuildOnlineCommandName,
                DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                "1",
                DocumentCacheAdminCommandSurface.ConfirmOptionName,
                "onlineCacheRebuild",
                DocumentCacheAdminCommandSurface.JsonOptionName
            ),
            InvocationTarget(),
            serviceProvider,
            stdout,
            stderr
        );

        exitCode.Should().Be(DocumentCacheAdminExitCodes.RejectedNoMutation);
        dispatcher.Requests.Should().ContainSingle();

        JsonObject result = JsonNode.Parse(stdout.ToString())!.AsObject();
        result["command"]!.GetValue<string>().Should().Be("onlineCacheRebuild");
        result["status"]!.GetValue<string>().Should().Be("rejectedNoMutation");
        result["classification"]!.GetValue<string>().Should().Be("targetNotConfigured");
        result["mutated"]!.GetValue<bool>().Should().BeFalse();
        result["phaseDiagnostics"]![0]!["currentPhase"]!.GetValue<string>().Should().Be("resolveTarget");
        stderr.ToString().Should().BeEmpty();
    }

    [Test]
    [Category("Timeout")]
    public async Task It_passes_command_timeout_to_mutating_dispatcher_and_writes_shared_timeout_result()
    {
        TimeoutAwareMutatingCommandDispatcher dispatcher = new();
        await using ServiceProvider serviceProvider = new ServiceCollection()
            .AddSingleton<IDocumentCacheAdminMutatingCommandDispatcher>(dispatcher)
            .BuildServiceProvider();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = await DocumentCacheAdminCommandExecutor.ExecuteAsync(
            ParseCommand(
                DocumentCacheAdminCommandSurface.RebuildOnlineCommandName,
                DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                "1",
                DocumentCacheAdminCommandSurface.ConfirmOptionName,
                "onlineCacheRebuild",
                DocumentCacheAdminCommandSurface.CommandTimeoutSecondsOptionName,
                "0.001",
                DocumentCacheAdminCommandSurface.JsonOptionName
            ),
            InvocationTarget(),
            serviceProvider,
            stdout,
            stderr
        );

        exitCode.Should().Be(DocumentCacheAdminExitCodes.FailedNoMutation);
        dispatcher.CancellationObserved.Should().BeTrue();
        string json = stdout.ToString();
        json.Should().Contain("\"classification\":\"workflowTimeout\"");
        json.Should().Contain("\"currentPhase\":\"resolveTarget\"");
        stderr.ToString().Should().BeEmpty();
    }

    private static ParseResult ParseCommand(string commandName, params string[] args) =>
        DocumentCacheAdminCommandSurface.CreateRootCommand().Parse([commandName, .. args]);

    private static DocumentCacheAdminInvocationTarget InvocationTarget() =>
        new(DocumentCacheTargetKey.Create("", 1));

    private static DocumentCacheAdministrativeCommandResult CompletedResult() =>
        Result(
            DocumentCacheAdministrativeCommandStatus.Completed,
            DocumentCacheAdministrativeCommandClassification.Succeeded,
            mutated: true
        );

    private static DocumentCacheAdministrativeCommandResult Result(
        DocumentCacheAdministrativeCommandStatus status,
        DocumentCacheAdministrativeCommandClassification classification,
        bool mutated
    ) =>
        new(
            DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
            new DocumentCacheAdministrativeTargetKey("", 1),
            status,
            classification,
            mutated,
            targetGeneration: 7,
            physicalSourceFingerprint: new DocumentCachePhysicalSourceFingerprint(Fingerprint),
            lifecycle: DocumentCacheLifecycleState.Tracking,
            cacheAheadRecoveryRequired: false,
            phaseDiagnostics:
            [
                new DocumentCacheAdministrativePhaseDiagnostic(
                    DocumentCacheAdministrativeCommandPhase.Preflight,
                    DocumentCacheAdministrativeCommandPhase.ResolveTarget,
                    retryable: status == DocumentCacheAdministrativeCommandStatus.IncompleteRetryable,
                    DocumentCacheAdministrativeDiagnosticCategory.CacheAheadLatchSet,
                    ImmutableArray<long>.Empty,
                    "diagnostic"
                ),
            ],
            elapsedCommandTime: TimeSpan.FromSeconds(1.25)
        );

    private static DocumentCacheAdministrativeCommandResult TargetNotConfiguredResult() =>
        new(
            DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
            new DocumentCacheAdministrativeTargetKey("", 1),
            DocumentCacheAdministrativeCommandStatus.RejectedNoMutation,
            DocumentCacheAdministrativeCommandClassification.TargetNotConfigured,
            mutated: false,
            phaseDiagnostics:
            [
                new DocumentCacheAdministrativePhaseDiagnostic(
                    DocumentCacheAdministrativeCommandPhase.ResolveTarget,
                    lastCompletedPhase: null,
                    retryable: false,
                    DocumentCacheAdministrativeDiagnosticCategory.TargetNotConfigured,
                    ImmutableArray<long>.Empty,
                    "DocumentCache target registry did not contain exactly the invocation target."
                ),
            ]
        );

    private sealed class TargetContextCreatingDispatcher(
        IDocumentCacheProjectionTargetRuntimeContextFactory targetContextFactory
    ) : IDocumentCacheAdminMutatingCommandDispatcher
    {
        public async Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
            DocumentCacheAdminMutatingCommandRequest commandRequest,
            CancellationToken cancellationToken = default
        )
        {
            await using DocumentCacheProjectionTargetRuntimeContext targetContext = await targetContextFactory
                .CreateAsync(TargetExecutionContext(commandRequest.TargetKey), cancellationToken)
                .ConfigureAwait(false);

            throw new AssertionException("Throwing mapping provider should prevent target-context creation.");
        }

        private static DocumentCacheTargetExecutionContext TargetExecutionContext(
            DocumentCacheTargetKey targetKey
        ) =>
            new(
                targetKey,
                new DocumentCacheTargetContextGeneration(1),
                DocumentCacheTargetEffectiveSettings.FromOptions(new DocumentCacheOptions()),
                new DocumentCacheTargetDataStoreMetadata(
                    targetKey.DataStoreId,
                    RelationalProviderToken.PostgresqlValue
                ),
                new DocumentCacheTargetConnectionInput(RelationalProviderToken.Postgresql, "connection"),
                new DocumentCachePhysicalSourceFingerprint(Fingerprint),
                new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false),
                new DocumentCacheInventoryValidationResult(
                    DocumentCacheInventoryStatus.Satisfied,
                    "Inventory."
                ),
                new DocumentCacheEnqueueTriggerValidationResult(
                    DocumentCacheEnqueueTriggerStatus.Satisfied,
                    "Trigger."
                ),
                DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
            );
    }

    private sealed class RecordingMutatingCommandDispatcher(
        Func<DocumentCacheAdminMutatingCommandRequest, DocumentCacheAdministrativeCommandResult> execute
    ) : IDocumentCacheAdminMutatingCommandDispatcher
    {
        private readonly List<DocumentCacheAdminMutatingCommandRequest> _requests = [];

        public ImmutableArray<DocumentCacheAdminMutatingCommandRequest> Requests => [.. _requests];

        public Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
            DocumentCacheAdminMutatingCommandRequest commandRequest,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            _requests.Add(commandRequest);
            return Task.FromResult(execute(commandRequest));
        }
    }

    private sealed class TimeoutAwareMutatingCommandDispatcher : IDocumentCacheAdminMutatingCommandDispatcher
    {
        public bool CancellationObserved { get; private set; }

        public async Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
            DocumentCacheAdminMutatingCommandRequest commandRequest,
            CancellationToken cancellationToken = default
        )
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                throw new AssertionException("Dispatcher should observe the command timeout.");
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                return WorkflowTimeoutResult();
            }
        }

        private static DocumentCacheAdministrativeCommandResult WorkflowTimeoutResult() =>
            new(
                DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
                new DocumentCacheAdministrativeTargetKey("", 1),
                DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
                DocumentCacheAdministrativeCommandClassification.WorkflowTimeout,
                mutated: false,
                phaseDiagnostics:
                [
                    new DocumentCacheAdministrativePhaseDiagnostic(
                        DocumentCacheAdministrativeCommandPhase.ResolveTarget,
                        lastCompletedPhase: null,
                        retryable: false,
                        DocumentCacheAdministrativeDiagnosticCategory.WorkflowTimeout,
                        ImmutableArray<long>.Empty,
                        "Administrative workflow timeout expired before durable mutation."
                    ),
                ]
            );
    }
}
