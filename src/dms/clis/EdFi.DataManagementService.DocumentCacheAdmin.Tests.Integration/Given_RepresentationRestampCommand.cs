// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.CommandLine;
using System.Data.Common;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.DocumentCacheAdmin;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

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
                new DocumentCacheRepresentationRestampResourceScope("Ed-Fi", "Student"),
                "representation correction",
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
                restampResult["scope"]!["scopeType"]!.GetValue<string>().Should().Be("documentUuids");
                restampResult["reason"]!
                    .GetValue<string>()
                    .Should()
                    .Be("representation restamp integration test");
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

[TestFixture(false, Category = "PostgresqlIntegration")]
[TestFixture(true, Category = "MssqlIntegration")]
[NonParallelizable]
[Category("RepresentationRestamp")]
public sealed class Given_RepresentationRestampAuthoritativeStamps(bool mssql)
{
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task It_commits_authoritative_descriptor_stamps_for_each_selection_scope(
        bool resourceScope,
        bool tracking
    )
    {
        await using DocumentCacheAdminCliTarget target = mssql
            ? await Given_DocumentCacheAdminMssqlRebuildOnline.CreateReadyMssqlTargetAsync()
            : await DocumentCacheAdminCliTarget.CreatePostgresqlAsync();
        if (mssql)
        {
            await Given_DocumentCacheAdminMssqlStatus.WithNestedTriggersAsync(target, true, VerifyAsync);
        }
        else
        {
            await VerifyAsync();
        }

        async Task VerifyAsync()
        {
            DocumentCacheAdminCliSeededDocument first = await SeedAsync("Selected", 10);
            DocumentCacheAdminCliSeededDocument second = await SeedAsync("Other", 11);
            await using DbConnection connection = mssql
                ? new SqlConnection(target.ConnectionString)
                : new NpgsqlConnection(target.ConnectionString);
            await connection.OpenAsync();
            await ExecuteAsync(
                """
                UPDATE dms."Descriptor" SET "ContentLastModifiedAt" = '2001-02-03T04:05:06' WHERE "CodeValue" = 'Selected';
                UPDATE dms."Descriptor" SET "ContentLastModifiedAt" = '2002-03-04T05:06:07' WHERE "CodeValue" = 'Other';
                DELETE FROM dms."DocumentProjectionWork";
                """
            );
            if (mssql)
            {
                await target.State.AdvanceMssqlChangeVersionSequencePastAsync(11);
            }
            else
            {
                await ExecuteAsync("""SELECT setval('dms."ChangeVersionSequence"', 11);""");
            }
            Stamp[] before = await ReadStampsAsync();
            before.Select(row => row.DocumentId).Should().Equal(first.DocumentId, second.DocumentId);
            foreach (Stamp row in before)
            {
                ((long)row.DescriptorId).Should().NotBe(row.DocumentId);
                row.OwnerVersion.Should().Be(row.DocumentVersion);
            }
            // The shared cache seed must follow DocumentId to the authoritative owner too.
            if (mssql)
            {
                await target.State.InsertMssqlDocumentCacheAsync(first);
            }
            else
            {
                await target.State.InsertPostgresqlDocumentCacheAsync(first);
            }
            await using (DbCommand cacheCommand = connection.CreateCommand())
            {
                cacheCommand.CommandText = """SELECT "LastModifiedAt" FROM dms."DocumentCache";""";
                ((DateTime)(await cacheCommand.ExecuteScalarAsync())!).Should().Be(before[0].Timestamp);
            }
            await target.State.SetLifecycleAsync(tracking ? "Tracking" : "Disabled", false);
            await using DocumentCacheAdminCliProcessHarness harness =
                await DocumentCacheAdminCliProcessHarness.CreateAsync(target);
            string[] scope = resourceScope
                ? ["--project-name", "Ed-Fi", "--resource-name", "SchoolTypeDescriptor"]
                : ["--document-uuid", first.DocumentUuid.ToString()];
            JsonObject preview = AssertSuccess(
                await harness.RunAsync([
                    "restamp-preview",
                    "--data-store-id",
                    "1",
                    "--mode",
                    tracking ? "tracking" : "disabled",
                    "--reason",
                    "authoritative timestamp integration test",
                    "--offline-writer-admission",
                    "closedAndDrained",
                    "--json",
                    .. scope,
                ])
            );
            Guid operationId = preview["result"]!["operationId"]!.GetValue<Guid>();
            JsonObject executed = AssertSuccess(
                await harness.RunAsync(
                    "restamp-execute",
                    "--data-store-id",
                    "1",
                    "--operation-id",
                    operationId.ToString(),
                    "--confirm",
                    "representationRestamp",
                    "--offline-writer-admission",
                    "closedAndDrained",
                    "--json"
                )
            );
            JsonObject result = executed["result"]!.AsObject();
            result["scope"]!["scopeType"]!
                .GetValue<string>()
                .Should()
                .Be(resourceScope ? "resource" : "documentUuids");
            result["state"]!.GetValue<string>().Should().Be("completed");
            result["claimLevel"]!
                .GetValue<string>()
                .Should()
                .Be(tracking ? "projectionWorkQueued" : "canonicalOnlyComplete");
            result["committedDocumentCount"]!.GetValue<long>().Should().Be(resourceScope ? 2 : 1);
            result["remainingEligibleDocumentCount"]!.GetValue<long>().Should().Be(0);
            Stamp[] after = await ReadStampsAsync();
            long[] selected = resourceScope ? [first.DocumentId, second.DocumentId] : [first.DocumentId];
            foreach (long documentId in selected)
            {
                Stamp oldRow = before.Single(row => row.DocumentId == documentId);
                Stamp newRow = after.Single(row => row.DocumentId == documentId);
                newRow.DocumentVersion.Should().BeGreaterThan(11);
                newRow.OwnerVersion.Should().Be(newRow.DocumentVersion);
                newRow.Timestamp.Should().BeAfter(oldRow.Timestamp);
                newRow.DescriptorId.Should().Be(oldRow.DescriptorId);
                newRow.CodeValue.Should().Be(oldRow.CodeValue);
            }
            after
                .Where(row => selected.Contains(row.DocumentId))
                .Select(row => row.DocumentVersion)
                .Should()
                .BeEquivalentTo(resourceScope ? new long[] { 12, 13 } : new long[] { 12 });
            if (!resourceScope)
            {
                after.Single(row => row.DocumentId == second.DocumentId).Should().Be(before[1]);
            }
            IReadOnlyDictionary<long, long> work = mssql
                ? await target.State.ReadMssqlWorkVersionsByDocumentIdAsync()
                : await target.State.ReadPostgresqlWorkVersionsByDocumentIdAsync();
            work.Should()
                .BeEquivalentTo(
                    tracking
                        ? after
                            .Where(row => selected.Contains(row.DocumentId))
                            .ToDictionary(row => row.DocumentId, row => row.DocumentVersion)
                        : new Dictionary<long, long>()
                );

            async Task ExecuteAsync(string sql)
            {
                await using DbCommand command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync();
            }

            async Task<Stamp[]> ReadStampsAsync()
            {
                await using DbCommand command = connection.CreateCommand();
                command.CommandText = """
                    SELECT d."DocumentId", descriptor."DescriptorId", d."ContentVersion",
                           descriptor."ContentVersion", descriptor."ContentLastModifiedAt", descriptor."CodeValue"
                    FROM dms."Document" d JOIN dms."Descriptor" descriptor ON descriptor."DocumentId" = d."DocumentId"
                    ORDER BY d."DocumentId";
                    """;
                await using DbDataReader reader = await command.ExecuteReaderAsync();
                List<Stamp> rows = [];
                while (await reader.ReadAsync())
                {
                    rows.Add(
                        new(
                            reader.GetInt64(0),
                            reader.GetInt32(1),
                            reader.GetInt64(2),
                            reader.GetInt64(3),
                            reader.GetDateTime(4),
                            reader.GetString(5)
                        )
                    );
                }
                return [.. rows];
            }

            JsonObject AssertSuccess(DocumentCacheAdminCliProcessResult process) =>
                DocumentCacheAdminCliCommandResultAssertions.AssertCommandResult(
                    process,
                    target,
                    DocumentCacheAdminExitCodes.Success
                );
        }

        Task<DocumentCacheAdminCliSeededDocument> SeedAsync(string codeValue, long version) =>
            mssql
                ? target.State.InsertMssqlDescriptorDocumentAsync(codeValue, version)
                : target.State.InsertPostgresqlDescriptorDocumentAsync(codeValue, version);
    }

    private sealed record Stamp(
        long DocumentId,
        int DescriptorId,
        long DocumentVersion,
        long OwnerVersion,
        DateTime Timestamp,
        string CodeValue
    );
}
