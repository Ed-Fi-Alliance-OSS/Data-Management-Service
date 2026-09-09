// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.DocumentCacheAdmin;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration;

/// <summary>
/// Covers process-level failure behavior for the representation-restamp commands specifically: an
/// exception escaping the shared dispatcher before it can produce a
/// <c>DocumentCacheAdministrativeCommandResult</c>. These complement
/// <see cref="Given_DocumentCacheAdminRepresentationRestampExitCodes"/>, which covers the case where the
/// dispatcher successfully returns a typed result.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("ExitCode")]
[Category("RepresentationRestamp")]
public sealed class Given_DocumentCacheAdminProcessFailures
{
    [Test]
    public async Task It_returns_unexpected_failure_when_restamp_preview_dispatch_fails_without_a_shared_result()
    {
        ThrowingMutatingCommandDispatcher dispatcher = new(new InvalidOperationException("boom"));
        await using ServiceProvider serviceProvider = new ServiceCollection()
            .AddSingleton<IDocumentCacheAdminMutatingCommandDispatcher>(dispatcher)
            .BuildServiceProvider();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = await DocumentCacheAdminCommandExecutor.ExecuteAsync(
            ParseRestampPreviewCommand(),
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
    public async Task It_returns_retryable_shared_result_when_restamp_execute_cancellation_escapes_in_json_mode()
    {
        ThrowingMutatingCommandDispatcher dispatcher = new(new OperationCanceledException("cancelled"));
        await using ServiceProvider serviceProvider = new ServiceCollection()
            .AddSingleton<IDocumentCacheAdminMutatingCommandDispatcher>(dispatcher)
            .BuildServiceProvider();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = await DocumentCacheAdminCommandExecutor.ExecuteAsync(
            ParseRestampExecuteCommand(),
            InvocationTarget(),
            serviceProvider,
            stdout,
            stderr
        );

        exitCode.Should().Be(DocumentCacheAdminExitCodes.IncompleteRetryable);
        JsonObject result = ParseSingleJsonResult(stdout);
        result["command"]!.GetValue<string>().Should().Be("representationRestamp");
        result["status"]!.GetValue<string>().Should().Be("incompleteRetryable");
        result["classification"]!.GetValue<string>().Should().Be("cancellationAfterMutation");
        result["mutated"]!.GetValue<bool>().Should().BeTrue();
        result["phaseDiagnostics"]![0]!["diagnosticCategory"]!.GetValue<string>().Should().Be("cancellation");
        result["phaseDiagnostics"]![0]!["retryable"]!.GetValue<bool>().Should().BeTrue();
        result.Should().NotContainKey("result");
        stderr.ToString().Should().BeEmpty();
    }

    [Test]
    public async Task It_renders_the_retryable_restamp_execute_cancellation_result_in_human_mode()
    {
        ThrowingMutatingCommandDispatcher dispatcher = new(new OperationCanceledException("cancelled"));
        await using ServiceProvider serviceProvider = new ServiceCollection()
            .AddSingleton<IDocumentCacheAdminMutatingCommandDispatcher>(dispatcher)
            .BuildServiceProvider();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = await DocumentCacheAdminCommandExecutor.ExecuteAsync(
            ParseRestampExecuteCommand(includeJsonOption: false),
            InvocationTarget(),
            serviceProvider,
            stdout,
            stderr
        );

        exitCode.Should().Be(DocumentCacheAdminExitCodes.IncompleteRetryable);
        stdout
            .ToString()
            .Should()
            .Contain(
                "DocumentCache command=RepresentationRestamp status=IncompleteRetryable classification=CancellationAfterMutation mutated=true"
            )
            .And.Contain("diagnostic phase=Complete category=Cancellation retryable=true");
        stderr.ToString().Should().BeEmpty();
    }

    private static ParseResult ParseRestampPreviewCommand() =>
        DocumentCacheAdminCommandSurface
            .CreateRootCommand()
            .Parse([
                DocumentCacheAdminCommandSurface.RestampPreviewCommandName,
                DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                "1",
                DocumentCacheAdminCommandSurface.ModeOptionName,
                "tracking",
                DocumentCacheAdminCommandSurface.ReasonOptionName,
                "process failure coverage",
                DocumentCacheAdminCommandSurface.ProjectNameOptionName,
                "Ed-Fi",
                DocumentCacheAdminCommandSurface.ResourceNameOptionName,
                "SchoolTypeDescriptor",
                DocumentCacheAdminCommandSurface.OfflineWriterAdmissionOptionName,
                "closedAndDrained",
            ]);

    private static ParseResult ParseRestampExecuteCommand(bool includeJsonOption = true)
    {
        List<string> arguments =
        [
            DocumentCacheAdminCommandSurface.RestampExecuteCommandName,
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            "1",
            DocumentCacheAdminCommandSurface.OperationIdOptionName,
            Guid.NewGuid().ToString(),
            DocumentCacheAdminCommandSurface.ConfirmOptionName,
            "representationRestamp",
            DocumentCacheAdminCommandSurface.OfflineWriterAdmissionOptionName,
            "closedAndDrained",
        ];
        if (includeJsonOption)
        {
            arguments.Add(DocumentCacheAdminCommandSurface.JsonOptionName);
        }

        return DocumentCacheAdminCommandSurface.CreateRootCommand().Parse(arguments.ToArray());
    }

    private static DocumentCacheAdminInvocationTarget InvocationTarget() =>
        new(DocumentCacheTargetKey.Create("", 1));

    private static JsonObject ParseSingleJsonResult(StringWriter stdout)
    {
        string json = stdout.ToString();
        json.TrimEnd().Should().NotContain("\n");
        return JsonNode.Parse(json)!.AsObject();
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
