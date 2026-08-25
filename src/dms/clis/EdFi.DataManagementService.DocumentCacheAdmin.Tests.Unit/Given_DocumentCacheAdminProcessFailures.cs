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
[Category("ExitCode")]
public sealed class Given_DocumentCacheAdminProcessFailures
{
    [Test]
    public async Task It_preserves_a_failed_no_mutation_cancellation_result_from_the_shared_runner()
    {
        ReturningMutatingCommandDispatcher dispatcher = new(
            Result(
                DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
                DocumentCacheAdministrativeCommandClassification.CancellationBeforeMutation,
                mutated: false
            )
        );
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

        exitCode.Should().Be(DocumentCacheAdminExitCodes.FailedNoMutation);
        JsonObject result = ParseSingleJsonResult(stdout);
        result["status"]!.GetValue<string>().Should().Be("failedNoMutation");
        result["classification"]!.GetValue<string>().Should().Be("cancellationBeforeMutation");
        result["mutated"]!.GetValue<bool>().Should().BeFalse();
        result["phaseDiagnostics"]![0]!["currentPhase"]!.GetValue<string>().Should().Be("clearCache");
        result["phaseDiagnostics"]![0]!["diagnosticCategory"]!.GetValue<string>().Should().Be("cancellation");
        stderr.ToString().Should().BeEmpty();
    }

    [Test]
    public async Task It_preserves_a_failed_no_mutation_unexpected_provider_result_from_the_shared_runner()
    {
        ReturningMutatingCommandDispatcher dispatcher = new(
            Result(
                DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
                DocumentCacheAdministrativeCommandClassification.UnexpectedProviderFailure,
                mutated: false
            )
        );
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

        exitCode.Should().Be(DocumentCacheAdminExitCodes.FailedNoMutation);
        JsonObject result = ParseSingleJsonResult(stdout);
        result["status"]!.GetValue<string>().Should().Be("failedNoMutation");
        result["classification"]!.GetValue<string>().Should().Be("unexpectedProviderFailure");
        result["mutated"]!.GetValue<bool>().Should().BeFalse();
        result["phaseDiagnostics"]![0]!["diagnosticCategory"]!
            .GetValue<string>()
            .Should()
            .Be("unexpectedProviderFailure");
        stderr.ToString().Should().BeEmpty();
    }

    [Test]
    public async Task It_returns_unexpected_failure_when_mutating_runtime_fails_without_a_shared_result()
    {
        ThrowingMutatingCommandDispatcher dispatcher = new(new InvalidOperationException("boom"));
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
                "onlineCacheRebuild"
            ),
            InvocationTarget(),
            serviceProvider,
            stdout,
            stderr
        );

        exitCode.Should().Be(DocumentCacheAdminExitCodes.UnexpectedFailure);
        stdout.ToString().Should().BeEmpty();
        stderr.ToString().Should().Contain("failed before a shared result could be produced");
    }

    [Test]
    public async Task It_preserves_a_retryable_cancellation_result_from_the_shared_runner()
    {
        ReturningMutatingCommandDispatcher dispatcher = new(
            Result(
                DocumentCacheAdministrativeCommandStatus.IncompleteRetryable,
                DocumentCacheAdministrativeCommandClassification.CancellationAfterMutation,
                mutated: true
            )
        );
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

        exitCode.Should().Be(DocumentCacheAdminExitCodes.IncompleteRetryable);
        stdout.ToString().Should().Contain("\"classification\":\"cancellationAfterMutation\"");
        stderr.ToString().Should().BeEmpty();
    }

    [Test]
    public async Task It_preserves_a_retryable_session_loss_result_from_the_shared_runner()
    {
        ReturningMutatingCommandDispatcher dispatcher = new(
            Result(
                DocumentCacheAdministrativeCommandStatus.IncompleteRetryable,
                DocumentCacheAdministrativeCommandClassification.SessionLossAfterMutation,
                mutated: true
            )
        );
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

        exitCode.Should().Be(DocumentCacheAdminExitCodes.IncompleteRetryable);
        stdout.ToString().Should().Contain("\"classification\":\"sessionLossAfterMutation\"");
        stderr.ToString().Should().BeEmpty();
    }

    private static ParseResult ParseCommand(string commandName, params string[] args) =>
        DocumentCacheAdminCommandSurface.CreateRootCommand().Parse([commandName, .. args]);

    private static DocumentCacheAdminInvocationTarget InvocationTarget() =>
        new(DocumentCacheTargetKey.Create("", 1));

    private static JsonObject ParseSingleJsonResult(StringWriter stdout)
    {
        string json = stdout.ToString();
        json.TrimEnd().Should().NotContain("\n");
        return JsonNode.Parse(json)!.AsObject();
    }

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
                    classification switch
                    {
                        DocumentCacheAdministrativeCommandClassification.SessionLossAfterMutation
                        or DocumentCacheAdministrativeCommandClassification.SessionLossNoMutation =>
                            DocumentCacheAdministrativeDiagnosticCategory.SessionLoss,
                        DocumentCacheAdministrativeCommandClassification.UnexpectedProviderFailure =>
                            DocumentCacheAdministrativeDiagnosticCategory.UnexpectedProviderFailure,
                        DocumentCacheAdministrativeCommandClassification.WorkflowTimeout =>
                            DocumentCacheAdministrativeDiagnosticCategory.WorkflowTimeout,
                        _ => DocumentCacheAdministrativeDiagnosticCategory.Cancellation,
                    },
                    ImmutableArray<long>.Empty,
                    "typed diagnostic"
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

    private sealed class ThrowingMutatingCommandDispatcher(Exception exception)
        : IDocumentCacheAdminMutatingCommandDispatcher
    {
        public Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
            DocumentCacheAdminMutatingCommandRequest commandRequest,
            CancellationToken cancellationToken = default
        ) => Task.FromException<DocumentCacheAdministrativeCommandResult>(exception);
    }
}
