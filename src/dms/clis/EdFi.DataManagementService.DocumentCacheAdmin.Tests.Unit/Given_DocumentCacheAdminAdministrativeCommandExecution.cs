// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.CommandLine;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.DocumentCacheAdmin;
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
    [Category("Timeout")]
    public async Task It_applies_command_timeout_before_mutating_target_resolution_completes()
    {
        ThrowingMutatingCommandDispatcher dispatcher = new(
            new AssertionException("Dispatcher must not run after target resolution times out.")
        );
        await using ServiceProvider serviceProvider = new ServiceCollection()
            .AddSingleton<IDocumentCacheAdminTargetResolver>(new DelayingTargetResolver())
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
        string json = stdout.ToString();
        json.Should().Contain("\"classification\":\"workflowTimeout\"");
        json.Should().Contain("\"currentPhase\":\"resolveTarget\"");
        stderr.ToString().Should().BeEmpty();
    }

    private static ParseResult ParseCommand(string commandName, params string[] args) =>
        DocumentCacheAdminCommandSurface.CreateRootCommand().Parse([commandName, .. args]);

    private static DocumentCacheAdminInvocationTarget InvocationTarget() =>
        new(DocumentCacheTargetKey.Create("", 1), DocumentCacheAdminInvocationTargetSource.Options);

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

    private sealed class DelayingTargetResolver : IDocumentCacheAdminTargetResolver
    {
        public async Task<DocumentCacheAdminTargetResolutionResult> ResolveAsync(
            DocumentCacheTargetKey targetKey,
            CancellationToken cancellationToken = default
        )
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            throw new AssertionException("Target resolution should be cancelled by the command timeout.");
        }
    }

    private sealed class ThrowingMutatingCommandDispatcher(Exception exception)
        : IDocumentCacheAdminMutatingCommandDispatcher
    {
        public Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
            DocumentCacheAdminMutatingCommandRequest commandRequest,
            CancellationToken cancellationToken = default
        ) => Task.FromException<DocumentCacheAdministrativeCommandResult>(exception);
    }
}
