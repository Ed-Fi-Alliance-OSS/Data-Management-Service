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

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration;

[TestFixture]
[Parallelizable]
[Category("ExitCode")]
[Category("RepresentationRestamp")]
public sealed class Given_RepresentationRestampCommand
{
    [Test]
    public async Task It_returns_incomplete_retryable_after_a_committed_page_then_cancellation()
    {
        DocumentCacheAdministrativeCommandResult result = Result(
            DocumentCacheAdministrativeCommandStatus.IncompleteRetryable,
            DocumentCacheAdministrativeCommandClassification.CancellationAfterMutation,
            mutated: true,
            DocumentCacheRepresentationRestampOperationState.Incomplete,
            committedDocumentCount: 2,
            remainingEligibleDocumentCount: 3
        );

        (int exitCode, JsonObject output) = await ExecuteWithDispatcherAsync(result);

        exitCode.Should().Be(DocumentCacheAdminExitCodes.IncompleteRetryable);
        output["result"]!["state"]!.GetValue<string>().Should().Be("incomplete");
        output["result"]!["claimLevel"]!.GetValue<string>().Should().Be("incomplete");
    }

    [Test]
    public async Task It_maps_retry_exhaustion_to_incomplete_retryable()
    {
        DocumentCacheAdministrativeCommandResult result = Result(
            DocumentCacheAdministrativeCommandStatus.IncompleteRetryable,
            DocumentCacheAdministrativeCommandClassification.ProviderConcurrencyRetryExhausted,
            mutated: true,
            DocumentCacheRepresentationRestampOperationState.Incomplete,
            committedDocumentCount: 1,
            remainingEligibleDocumentCount: 4
        );

        (int exitCode, JsonObject output) = await ExecuteWithDispatcherAsync(result);

        exitCode.Should().Be(DocumentCacheAdminExitCodes.IncompleteRetryable);
        output["classification"]!.GetValue<string>().Should().Be("providerConcurrencyRetryExhausted");
        output["result"]!["claimLevel"]!.GetValue<string>().Should().Be("incomplete");
    }

    [Test]
    public async Task It_maps_count_reconciliation_failure_to_incomplete_retryable()
    {
        DocumentCacheAdministrativeCommandResult result = Result(
            DocumentCacheAdministrativeCommandStatus.IncompleteRetryable,
            DocumentCacheAdministrativeCommandClassification.RepresentationRestampCountReconciliationFailure,
            mutated: true,
            DocumentCacheRepresentationRestampOperationState.Incomplete,
            committedDocumentCount: 2,
            remainingEligibleDocumentCount: 1
        );

        (int exitCode, JsonObject output) = await ExecuteWithDispatcherAsync(result);

        exitCode.Should().Be(DocumentCacheAdminExitCodes.IncompleteRetryable);
        output["classification"]!
            .GetValue<string>()
            .Should()
            .Be("representationRestampCountReconciliationFailure");
        output["mutated"]!.GetValue<bool>().Should().BeTrue();
    }

    [Test]
    public async Task It_maps_invalid_scope_to_failed_no_mutation()
    {
        DocumentCacheAdministrativeCommandResult result = Result(
            DocumentCacheAdministrativeCommandStatus.FailedNoMutation,
            DocumentCacheAdministrativeCommandClassification.InvalidRepresentationRestampScope,
            mutated: false,
            DocumentCacheRepresentationRestampOperationState.Draft,
            committedDocumentCount: 0,
            remainingEligibleDocumentCount: null
        );

        (int exitCode, JsonObject output) = await ExecuteWithDispatcherAsync(result);

        exitCode.Should().Be(DocumentCacheAdminExitCodes.FailedNoMutation);
        output["status"]!.GetValue<string>().Should().Be("failedNoMutation");
        output["mutated"]!.GetValue<bool>().Should().BeFalse();
    }

    [Test]
    public async Task It_maps_target_unavailable_to_rejected_no_mutation()
    {
        DocumentCacheAdministrativeCommandResult result = Result(
            DocumentCacheAdministrativeCommandStatus.RejectedNoMutation,
            DocumentCacheAdministrativeCommandClassification.TargetUnresolved,
            mutated: false,
            DocumentCacheRepresentationRestampOperationState.Draft,
            committedDocumentCount: 0,
            remainingEligibleDocumentCount: null
        );

        (int exitCode, JsonObject output) = await ExecuteWithDispatcherAsync(result);

        exitCode.Should().Be(DocumentCacheAdminExitCodes.RejectedNoMutation);
        output["classification"]!.GetValue<string>().Should().Be("targetUnresolved");
        output["mutated"]!.GetValue<bool>().Should().BeFalse();
    }

    private static async Task<(int ExitCode, JsonObject Output)> ExecuteWithDispatcherAsync(
        DocumentCacheAdministrativeCommandResult result
    )
    {
        await using ServiceProvider services = new ServiceCollection()
            .AddSingleton<IDocumentCacheAdminMutatingCommandDispatcher>(
                new ReturningRestampDispatcher(result)
            )
            .BuildServiceProvider();
        using StringWriter stdout = new();
        using StringWriter stderr = new();
        ParseResult parseResult = DocumentCacheAdminCommandSurface
            .CreateRootCommand()
            .Parse([
                DocumentCacheAdminCommandSurface.RestampExecuteCommandName,
                DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                "1",
                DocumentCacheAdminCommandSurface.OperationIdOptionName,
                Guid.NewGuid().ToString(),
                DocumentCacheAdminCommandSurface.ConfirmOptionName,
                "representationRestamp",
                DocumentCacheAdminCommandSurface.OfflineWriterAdmissionOptionName,
                "closedAndDrained",
                DocumentCacheAdminCommandSurface.JsonOptionName,
            ]);

        int exitCode = await DocumentCacheAdminCommandExecutor.ExecuteAsync(
            parseResult,
            new DocumentCacheAdminInvocationTarget(DocumentCacheTargetKey.Create("", 1)),
            services,
            stdout,
            stderr
        );
        stderr.ToString().Should().BeEmpty();
        return (exitCode, JsonNode.Parse(stdout.ToString())!.AsObject());
    }

    private static DocumentCacheAdministrativeCommandResult Result(
        DocumentCacheAdministrativeCommandStatus status,
        DocumentCacheAdministrativeCommandClassification classification,
        bool mutated,
        DocumentCacheRepresentationRestampOperationState state,
        long committedDocumentCount,
        long? remainingEligibleDocumentCount
    ) =>
        new(
            DocumentCacheAdministrativeCommand.RepresentationRestamp,
            new DocumentCacheAdministrativeTargetKey("", 1),
            status,
            classification,
            mutated,
            phaseDiagnostics:
            [
                new DocumentCacheAdministrativePhaseDiagnostic(
                    DocumentCacheAdministrativeCommandPhase.StampDocuments,
                    DocumentCacheAdministrativeCommandPhase.SelectDocuments,
                    retryable: status is DocumentCacheAdministrativeCommandStatus.IncompleteRetryable,
                    DocumentCacheAdministrativeDiagnosticCategory.ProviderCommandTimeout,
                    ImmutableArray<long>.Empty,
                    "typed restamp diagnostic"
                ),
            ],
            representationRestampResult: new DocumentCacheRepresentationRestampResult(
                Guid.NewGuid(),
                state,
                10,
                5,
                committedDocumentCount,
                remainingEligibleDocumentCount,
                DocumentCacheRepresentationRestampMode.Tracking,
                new DocumentCachePhysicalSourceFingerprint($"sha256:{new string('a', 64)}"),
                state is DocumentCacheRepresentationRestampOperationState.Completed
                    ? DocumentCacheRepresentationRestampClaimLevel.ProjectionWorkQueued
                    : DocumentCacheRepresentationRestampClaimLevel.Incomplete
            )
        );

    private sealed class ReturningRestampDispatcher(DocumentCacheAdministrativeCommandResult result)
        : IDocumentCacheAdminMutatingCommandDispatcher
    {
        public Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
            DocumentCacheAdminMutatingCommandRequest commandRequest,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(result);
    }
}

[TestFixture]
[NonParallelizable]
[Category("MssqlIntegration")]
[Category("MssqlRepresentationRestamp")]
public sealed class Given_RepresentationRestampMssqlCommand
{
    [Test]
    public async Task It_reports_projection_work_queued_without_claiming_kafka_delivery()
    {
        await using DocumentCacheAdminCliTarget target =
            await Given_DocumentCacheAdminMssqlRebuildOnline.CreateReadyMssqlTargetAsync();
        await Given_DocumentCacheAdminMssqlStatus.WithNestedTriggersAsync(
            target,
            true,
            async () =>
            {
                DocumentCacheAdminCliSeededDocument first =
                    await target.State.InsertMssqlDescriptorDocumentAsync("RestampTrackingA", 10);
                DocumentCacheAdminCliSeededDocument second =
                    await target.State.InsertMssqlDescriptorDocumentAsync("RestampTrackingB", 11);
                await target.State.AdvanceMssqlChangeVersionSequencePastAsync(11);
                await target.State.SetLifecycleAsync("Tracking", false);

                DocumentCacheAdminCliProcessResult preview = await RunPreviewAsync(
                    target,
                    "tracking",
                    first.DocumentUuid,
                    second.DocumentUuid
                );
                JsonObject previewResult = AssertCommandResult(
                    preview,
                    target,
                    DocumentCacheAdminExitCodes.Success
                );
                Guid operationId = previewResult["result"]!["operationId"]!.GetValue<Guid>();
                DocumentCacheAdminCliRestampManifest manifest =
                    await target.State.ReadMssqlRestampManifestAsync(operationId);
                manifest.PreviewDocumentCount.Should().Be(2);
                manifest.CommittedDocumentCount.Should().Be(0);
                manifest.State.Should().Be("Draft");
                DocumentCacheAdminCliProcessResult execute = await RunExecuteAsync(target, operationId);
                DocumentCacheAdminCliRestampManifest manifestAfterExecute =
                    await target.State.ReadMssqlRestampManifestAsync(operationId);
                JsonObject commandResult = AssertCommandResult(
                    execute,
                    target,
                    DocumentCacheAdminExitCodes.Success
                );
                manifestAfterExecute.State.Should().Be("Completed");
                JsonObject restampResult = commandResult["result"]!.AsObject();
                commandResult.ToJsonString().ToLowerInvariant().Should().NotContain("kafka");
                restampResult["state"]!.GetValue<string>().Should().Be("completed");
                restampResult["claimLevel"]!.GetValue<string>().Should().Be("projectionWorkQueued");
                restampResult["committedDocumentCount"]!.GetValue<long>().Should().Be(2);
                restampResult["remainingEligibleDocumentCount"]!.GetValue<long>().Should().Be(0);
                (await target.State.ReadMutableCountsAsync()).WorkRows.Should().BeGreaterThanOrEqualTo(2);
            }
        );
    }

    [Test]
    public async Task It_reports_canonical_only_complete_without_invoking_a_drainer()
    {
        await using DocumentCacheAdminCliTarget target =
            await Given_DocumentCacheAdminMssqlRebuildOnline.CreateReadyMssqlTargetAsync();
        await Given_DocumentCacheAdminMssqlStatus.WithNestedTriggersAsync(
            target,
            true,
            async () =>
            {
                DocumentCacheAdminCliSeededDocument document =
                    await target.State.InsertMssqlDescriptorDocumentAsync("RestampDisabled", 10);
                await target.State.AdvanceMssqlChangeVersionSequencePastAsync(10);
                await target.State.SetLifecycleAsync("Disabled", false);
                DocumentCacheAdminCliProcessResult preview = await RunPreviewAsync(
                    target,
                    "disabled",
                    document.DocumentUuid
                );
                JsonObject previewResult = AssertCommandResult(
                    preview,
                    target,
                    DocumentCacheAdminExitCodes.Success
                );
                Guid operationId = previewResult["result"]!["operationId"]!.GetValue<Guid>();
                JsonObject commandResult = AssertCommandResult(
                    await RunExecuteAsync(target, operationId),
                    target,
                    DocumentCacheAdminExitCodes.Success
                );
                commandResult["result"]!["claimLevel"]!
                    .GetValue<string>()
                    .Should()
                    .Be("canonicalOnlyComplete");
                (await target.State.ReadMutableCountsAsync()).WorkRows.Should().Be(0);
            }
        );
    }

    private static async Task<DocumentCacheAdminCliProcessResult> RunPreviewAsync(
        DocumentCacheAdminCliTarget target,
        string mode,
        params Guid[] documentUuids
    )
    {
        await using DocumentCacheAdminCliProcessHarness harness =
            await DocumentCacheAdminCliProcessHarness.CreateAsync(target);
        return await harness.RunAsync([
            DocumentCacheAdminCommandSurface.RestampPreviewCommandName,
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            target.DataStoreId.ToString(),
            DocumentCacheAdminCommandSurface.ModeOptionName,
            mode,
            DocumentCacheAdminCommandSurface.ReasonOptionName,
            "representation restamp integration test",
            DocumentCacheAdminCommandSurface.DocumentUuidOptionName,
            .. documentUuids.Select(uuid => uuid.ToString()),
            DocumentCacheAdminCommandSurface.OfflineWriterAdmissionOptionName,
            "closedAndDrained",
            DocumentCacheAdminCommandSurface.JsonOptionName,
        ]);
    }

    private static async Task<DocumentCacheAdminCliProcessResult> RunExecuteAsync(
        DocumentCacheAdminCliTarget target,
        Guid operationId
    )
    {
        await using DocumentCacheAdminCliProcessHarness harness =
            await DocumentCacheAdminCliProcessHarness.CreateAsync(target);
        return await harness.RunAsync(
            DocumentCacheAdminCommandSurface.RestampExecuteCommandName,
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            target.DataStoreId.ToString(),
            DocumentCacheAdminCommandSurface.OperationIdOptionName,
            operationId.ToString(),
            DocumentCacheAdminCommandSurface.ConfirmOptionName,
            "representationRestamp",
            DocumentCacheAdminCommandSurface.OfflineWriterAdmissionOptionName,
            "closedAndDrained",
            DocumentCacheAdminCommandSurface.JsonOptionName
        );
    }

    private static JsonObject AssertCommandResult(
        DocumentCacheAdminCliProcessResult result,
        DocumentCacheAdminCliTarget target,
        int expectedExitCode
    ) => DocumentCacheAdminCliCommandResultAssertions.AssertCommandResult(result, target, expectedExitCode);
}
