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

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration;

[TestFixture]
[Category("Cancellation")]
public sealed class Given_DocumentCacheAdminCancellationExitCodes
{
    [Test]
    public async Task It_returns_incomplete_retryable_when_the_shared_runner_reports_cancellation_after_mutation()
    {
        (int exitCode, string jsonOutput) = await ExecuteWithResultAsync(
            DocumentCacheAdministrativeCommandStatus.IncompleteRetryable,
            DocumentCacheAdministrativeCommandClassification.CancellationAfterMutation,
            mutated: true
        );

        exitCode.Should().Be(DocumentCacheAdminExitCodes.IncompleteRetryable);
        jsonOutput.Should().Contain("\"status\":\"incompleteRetryable\"");
        jsonOutput.Should().Contain("\"classification\":\"cancellationAfterMutation\"");
        jsonOutput.Should().Contain("\"mutated\":true");
    }

    [Test]
    public async Task It_returns_failed_no_mutation_when_the_shared_runner_reports_cancellation_before_mutation()
    {
        (int exitCode, string jsonOutput) = await ExecuteWithResultAsync(
            DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
            DocumentCacheAdministrativeCommandClassification.CancellationBeforeMutation,
            mutated: false
        );

        exitCode.Should().Be(DocumentCacheAdminExitCodes.FailedNoMutation);
        jsonOutput.Should().Contain("\"status\":\"failedNoMutation\"");
        jsonOutput.Should().Contain("\"classification\":\"cancellationBeforeMutation\"");
        jsonOutput.Should().Contain("\"mutated\":false");
    }

    private static async Task<(int ExitCode, string JsonOutput)> ExecuteWithResultAsync(
        DocumentCacheAdministrativeCommandStatus status,
        DocumentCacheAdministrativeCommandClassification classification,
        bool mutated
    )
    {
        ReturningMutatingCommandDispatcher dispatcher = new(Result(status, classification, mutated));
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

        stderr.ToString().Should().BeEmpty();
        return (exitCode, stdout.ToString());
    }

    private static ParseResult ParseCommand(string commandName, params string[] args) =>
        DocumentCacheAdminCommandSurface.CreateRootCommand().Parse([commandName, .. args]);

    private static DocumentCacheAdminInvocationTarget InvocationTarget() =>
        new(DocumentCacheTargetKey.Create("", 1), DocumentCacheAdminInvocationTargetSource.Options);

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
            phaseDiagnostics:
            [
                new DocumentCacheAdministrativePhaseDiagnostic(
                    DocumentCacheAdministrativeCommandPhase.ClearCache,
                    DocumentCacheAdministrativeCommandPhase.EnterResetting,
                    retryable: status == DocumentCacheAdministrativeCommandStatus.IncompleteRetryable,
                    DocumentCacheAdministrativeDiagnosticCategory.Cancellation,
                    ImmutableArray<long>.Empty,
                    "typed cancellation diagnostic"
                ),
            ]
        );

    private sealed class ReturningMutatingCommandDispatcher(DocumentCacheAdministrativeCommandResult result)
        : IDocumentCacheAdminMutatingCommandDispatcher
    {
        public Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
            DocumentCacheAdminMutatingCommandRequest commandRequest,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }
}

[TestFixture]
[Category("SessionLoss")]
public sealed class Given_DocumentCacheAdminSessionLossExitCodes
{
    [Test]
    public async Task It_returns_incomplete_retryable_when_the_shared_runner_reports_session_loss_after_mutation()
    {
        (int exitCode, string jsonOutput) = await ExecuteWithResultAsync(
            DocumentCacheAdministrativeCommandStatus.IncompleteRetryable,
            DocumentCacheAdministrativeCommandClassification.SessionLossAfterMutation,
            mutated: true
        );

        exitCode.Should().Be(DocumentCacheAdminExitCodes.IncompleteRetryable);
        jsonOutput.Should().Contain("\"status\":\"incompleteRetryable\"");
        jsonOutput.Should().Contain("\"classification\":\"sessionLossAfterMutation\"");
        jsonOutput.Should().Contain("\"mutated\":true");
    }

    [Test]
    public async Task It_returns_failed_no_mutation_when_the_shared_runner_reports_session_loss_before_mutation()
    {
        (int exitCode, string jsonOutput) = await ExecuteWithResultAsync(
            DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
            DocumentCacheAdministrativeCommandClassification.SessionLossNoMutation,
            mutated: false
        );

        exitCode.Should().Be(DocumentCacheAdminExitCodes.FailedNoMutation);
        jsonOutput.Should().Contain("\"status\":\"failedNoMutation\"");
        jsonOutput.Should().Contain("\"classification\":\"sessionLossNoMutation\"");
        jsonOutput.Should().Contain("\"mutated\":false");
    }

    private static async Task<(int ExitCode, string JsonOutput)> ExecuteWithResultAsync(
        DocumentCacheAdministrativeCommandStatus status,
        DocumentCacheAdministrativeCommandClassification classification,
        bool mutated
    )
    {
        ReturningMutatingCommandDispatcher dispatcher = new(Result(status, classification, mutated));
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
                "integrityScrub",
                DocumentCacheAdminCommandSurface.JsonOptionName
            ),
            InvocationTarget(),
            serviceProvider,
            stdout,
            stderr
        );

        stderr.ToString().Should().BeEmpty();
        return (exitCode, stdout.ToString());
    }

    private static ParseResult ParseCommand(string commandName, params string[] args) =>
        DocumentCacheAdminCommandSurface.CreateRootCommand().Parse([commandName, .. args]);

    private static DocumentCacheAdminInvocationTarget InvocationTarget() =>
        new(DocumentCacheTargetKey.Create("", 1), DocumentCacheAdminInvocationTargetSource.Options);

    private static DocumentCacheAdministrativeCommandResult Result(
        DocumentCacheAdministrativeCommandStatus status,
        DocumentCacheAdministrativeCommandClassification classification,
        bool mutated
    ) =>
        new(
            DocumentCacheAdministrativeCommand.ExplicitIntegrityScrub,
            new DocumentCacheAdministrativeTargetKey("", 1),
            status,
            classification,
            mutated,
            phaseDiagnostics:
            [
                new DocumentCacheAdministrativePhaseDiagnostic(
                    DocumentCacheAdministrativeCommandPhase.ScrubScan,
                    DocumentCacheAdministrativeCommandPhase.Preflight,
                    retryable: status == DocumentCacheAdministrativeCommandStatus.IncompleteRetryable,
                    DocumentCacheAdministrativeDiagnosticCategory.SessionLoss,
                    ImmutableArray<long>.Empty,
                    "typed session-loss diagnostic"
                ),
            ]
        );

    private sealed class ReturningMutatingCommandDispatcher(DocumentCacheAdministrativeCommandResult result)
        : IDocumentCacheAdminMutatingCommandDispatcher
    {
        public Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
            DocumentCacheAdminMutatingCommandRequest commandRequest,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }
}
