// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.DocumentCacheAdmin;
using FluentAssertions;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("MssqlIntegration")]
[Category("MssqlRebuildOnline")]
public sealed class Given_DocumentCacheAdminMssqlRebuildOnline
{
    [Test]
    public async Task It_rebuilds_tracking_cache_and_drains_work()
    {
        await using DocumentCacheAdminCliTarget target = await CreateReadyMssqlTargetAsync();

        await Given_DocumentCacheAdminMssqlStatus.WithNestedTriggersAsync(
            target,
            enabled: true,
            async () =>
            {
                IReadOnlyList<DocumentCacheAdminCliSeededDocument> documents =
                    await InsertProjectedDescriptorRowsAsync(
                        target,
                        "MssqlRebuildTracking",
                        documentCount: 3
                    );
                await target.State.SetLifecycleAsync("Tracking", cacheAheadRecoveryRequired: false);

                DocumentCacheAdminCliProcessResult result = await RunRebuildOnlineAsync(target);

                JsonObject commandResult = AssertCommandResult(
                    result,
                    target,
                    DocumentCacheAdminExitCodes.Success
                );
                commandResult["command"]!.GetValue<string>().Should().Be("onlineCacheRebuild");
                commandResult["status"]!.GetValue<string>().Should().Be("completed");
                commandResult["classification"]!.GetValue<string>().Should().Be("succeeded");
                commandResult["mutated"]!.GetValue<bool>().Should().BeTrue();
                commandResult["lifecycle"]!.GetValue<string>().Should().Be("tracking");
                commandResult["cacheAheadRecoveryRequired"]!.GetValue<bool>().Should().BeFalse();

                DocumentCacheAdminCliLifecycleState lifecycle = await target.State.ReadLifecycleAsync();
                lifecycle.ProjectionLifecycleState.Should().Be("Tracking");
                lifecycle.CacheAheadRecoveryRequired.Should().BeFalse();
                DocumentCacheAdminCliMutableCounts counts = await target.State.ReadMutableCountsAsync();
                counts.DocumentCacheRows.Should().Be(documents.Count);
                counts.WorkRows.Should().Be(0);
                IReadOnlyDictionary<long, string> cachedJsonByDocumentId =
                    await target.State.ReadMssqlCachedJsonByDocumentIdAsync();
                cachedJsonByDocumentId
                    .Keys.Should()
                    .BeEquivalentTo(documents.Select(document => document.DocumentId));
                cachedJsonByDocumentId.Values.Should().OnlyContain(json => !json.Contains("stale-cache"));
                cachedJsonByDocumentId
                    .Values.Should()
                    .OnlyContain(json => json.Contains("MssqlRebuildTracking"));
            }
        );
    }

    [Test]
    public async Task It_resumes_rebuilding_without_repeating_cache_clearing()
    {
        await using DocumentCacheAdminCliTarget target = await CreateReadyMssqlTargetAsync();

        await Given_DocumentCacheAdminMssqlStatus.WithNestedTriggersAsync(
            target,
            enabled: true,
            async () =>
            {
                DocumentCacheAdminCliSeededDocument document =
                    await target.State.InsertMssqlDescriptorDocumentAsync(
                        "MssqlRebuildResume",
                        contentVersion: 21
                    );
                await target.State.ClearMssqlProjectionWorkAsync();
                await target.State.InsertMssqlDocumentCacheAsync(
                    document,
                    documentJson: """{"value":"kept-cache"}"""
                );
                await target.State.SetLifecycleAsync("Rebuilding", cacheAheadRecoveryRequired: false);

                DocumentCacheAdminCliProcessResult result = await RunRebuildOnlineAsync(target);

                JsonObject commandResult = AssertCommandResult(
                    result,
                    target,
                    DocumentCacheAdminExitCodes.Success
                );
                commandResult["status"]!.GetValue<string>().Should().Be("completed");
                commandResult["classification"]!.GetValue<string>().Should().Be("succeeded");
                commandResult["lifecycle"]!.GetValue<string>().Should().Be("tracking");

                DocumentCacheAdminCliLifecycleState lifecycle = await target.State.ReadLifecycleAsync();
                lifecycle.ProjectionLifecycleState.Should().Be("Tracking");
                lifecycle.CacheAheadRecoveryRequired.Should().BeFalse();
                DocumentCacheAdminCliMutableCounts counts = await target.State.ReadMutableCountsAsync();
                counts.DocumentCacheRows.Should().Be(1);
                counts.WorkRows.Should().Be(0);
                IReadOnlyDictionary<long, string> cachedJsonByDocumentId =
                    await target.State.ReadMssqlCachedJsonByDocumentIdAsync();
                cachedJsonByDocumentId[document.DocumentId].Should().Contain("kept-cache");
            }
        );
    }

    [Test]
    public async Task It_rejects_a_set_latch_without_lifecycle_cache_work_or_latch_mutation()
    {
        await using DocumentCacheAdminCliTarget target = await CreateReadyMssqlTargetAsync();

        await Given_DocumentCacheAdminMssqlStatus.WithNestedTriggersAsync(
            target,
            enabled: true,
            async () =>
            {
                IReadOnlyList<DocumentCacheAdminCliSeededDocument> documents =
                    await InsertProjectedDescriptorRowsAsync(target, "MssqlRebuildLatched", documentCount: 2);
                await target.State.SetLifecycleAsync("Tracking", cacheAheadRecoveryRequired: true);

                DocumentCacheAdminCliProcessResult result = await RunRebuildOnlineAsync(target);

                JsonObject commandResult = AssertCommandResult(
                    result,
                    target,
                    DocumentCacheAdminExitCodes.RejectedNoMutation
                );
                commandResult["status"]!.GetValue<string>().Should().Be("rejectedNoMutation");
                commandResult["classification"]!.GetValue<string>().Should().Be("cacheAheadLatchSet");
                commandResult["mutated"]!.GetValue<bool>().Should().BeFalse();
                commandResult["lifecycle"]!.GetValue<string>().Should().Be("tracking");
                commandResult["cacheAheadRecoveryRequired"]!.GetValue<bool>().Should().BeTrue();

                DocumentCacheAdminCliLifecycleState lifecycle = await target.State.ReadLifecycleAsync();
                lifecycle.ProjectionLifecycleState.Should().Be("Tracking");
                lifecycle.CacheAheadRecoveryRequired.Should().BeTrue();
                DocumentCacheAdminCliMutableCounts counts = await target.State.ReadMutableCountsAsync();
                counts.DocumentCacheRows.Should().Be(documents.Count);
                counts.WorkRows.Should().Be(documents.Count);
                IReadOnlyDictionary<long, string> cachedJsonByDocumentId =
                    await target.State.ReadMssqlCachedJsonByDocumentIdAsync();
                cachedJsonByDocumentId.Values.Should().OnlyContain(json => json.Contains("stale-cache"));
            }
        );
    }

    private static async Task<
        IReadOnlyList<DocumentCacheAdminCliSeededDocument>
    > InsertProjectedDescriptorRowsAsync(
        DocumentCacheAdminCliTarget target,
        string codeValuePrefix,
        int documentCount
    )
    {
        List<DocumentCacheAdminCliSeededDocument> documents = [];
        for (var index = 0; index < documentCount; index++)
        {
            DocumentCacheAdminCliSeededDocument document =
                await target.State.InsertMssqlDescriptorDocumentAsync(
                    $"{codeValuePrefix}-{index}",
                    contentVersion: 10 + index
                );
            await target.State.InsertMssqlDocumentCacheAsync(
                document,
                documentJson: $$"""{"value":"stale-cache-{{index}}"}"""
            );
            await target.State.InsertMssqlProjectionWorkAsync(document);
            documents.Add(document);
        }

        return documents;
    }

    private static async Task<DocumentCacheAdminCliProcessResult> RunRebuildOnlineAsync(
        DocumentCacheAdminCliTarget target
    )
    {
        await using DocumentCacheAdminCliProcessHarness harness =
            await DocumentCacheAdminCliProcessHarness.CreateAsync(target);

        return await harness.RunAsync(
            DocumentCacheAdminCommandSurface.RebuildOnlineCommandName,
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            target.DataStoreId.ToString(),
            DocumentCacheAdminCommandSurface.ConfirmOptionName,
            "onlineCacheRebuild",
            DocumentCacheAdminCommandSurface.JsonOptionName,
            DocumentCacheAdminCommandSurface.CommandTimeoutSecondsOptionName,
            "30"
        );
    }

    private static JsonObject AssertCommandResult(
        DocumentCacheAdminCliProcessResult result,
        DocumentCacheAdminCliTarget target,
        int expectedExitCode
    ) => DocumentCacheAdminCliCommandResultAssertions.AssertCommandResult(result, target, expectedExitCode);

    internal static async Task<DocumentCacheAdminCliTarget> CreateReadyMssqlTargetAsync()
    {
        DocumentCacheAdminCliTarget target = await DocumentCacheAdminCliTarget.CreateMssqlAsync();
        await target.State.SetMssqlReadCommittedSnapshotAsync(enabled: true);
        return target;
    }
}

[TestFixture]
[NonParallelizable]
[Category("MssqlIntegration")]
[Category("MssqlScrub")]
public sealed class Given_DocumentCacheAdminMssqlScrub
{
    [Test]
    public async Task It_repairs_missing_and_mismatched_work_without_changing_lifecycle_or_cache()
    {
        await using DocumentCacheAdminCliTarget target =
            await Given_DocumentCacheAdminMssqlRebuildOnline.CreateReadyMssqlTargetAsync();

        await Given_DocumentCacheAdminMssqlStatus.WithNestedTriggersAsync(
            target,
            enabled: true,
            async () =>
            {
                DocumentCacheAdminCliSeededDocument missingWork =
                    await target.State.InsertMssqlDescriptorDocumentAsync(
                        "MssqlScrubMissing",
                        contentVersion: 10
                    );
                DocumentCacheAdminCliSeededDocument staleWork =
                    await target.State.InsertMssqlDescriptorDocumentAsync(
                        "MssqlScrubStale",
                        contentVersion: 20
                    );
                DocumentCacheAdminCliSeededDocument aheadWork =
                    await target.State.InsertMssqlDescriptorDocumentAsync(
                        "MssqlScrubAhead",
                        contentVersion: 30
                    );
                await target.State.ClearMssqlProjectionWorkAsync();
                await target.State.InsertMssqlProjectionWorkAsync(staleWork, requiredContentVersion: 15);
                await target.State.InsertMssqlProjectionWorkAsync(aheadWork, requiredContentVersion: 35);
                await target.State.SetLifecycleAsync("Tracking", cacheAheadRecoveryRequired: false);

                DocumentCacheAdminCliProcessResult result = await RunScrubAsync(target);

                JsonObject commandResult = AssertCommandResult(
                    result,
                    target,
                    DocumentCacheAdminExitCodes.Success
                );
                commandResult["command"]!.GetValue<string>().Should().Be("explicitIntegrityScrub");
                commandResult["status"]!.GetValue<string>().Should().Be("completed");
                commandResult["classification"]!.GetValue<string>().Should().Be("succeeded");
                commandResult["mutated"]!.GetValue<bool>().Should().BeTrue();
                commandResult["lifecycle"]!.GetValue<string>().Should().Be("tracking");
                commandResult["cacheAheadRecoveryRequired"]!.GetValue<bool>().Should().BeFalse();

                IReadOnlyDictionary<long, long> workVersions =
                    await target.State.ReadMssqlWorkVersionsByDocumentIdAsync();
                workVersions[missingWork.DocumentId].Should().Be(missingWork.ContentVersion);
                workVersions[staleWork.DocumentId].Should().Be(staleWork.ContentVersion);
                workVersions[aheadWork.DocumentId].Should().Be(aheadWork.ContentVersion);
                DocumentCacheAdminCliMutableCounts counts = await target.State.ReadMutableCountsAsync();
                counts.DocumentCacheRows.Should().Be(0);
                counts.WorkRows.Should().Be(3);
                DocumentCacheAdminCliLifecycleState lifecycle = await target.State.ReadLifecycleAsync();
                lifecycle.ProjectionLifecycleState.Should().Be("Tracking");
                lifecycle.CacheAheadRecoveryRequired.Should().BeFalse();

                JsonObject statusTarget = await RunStatusAndReadTargetAsync(target);
                Given_DocumentCacheAdminPostgresqlStatus.AssertStandaloneRuntimeNotObserved(statusTarget);
            }
        );
    }

    [Test]
    public async Task It_sets_the_cache_ahead_latch_without_repairing_that_work_row()
    {
        await using DocumentCacheAdminCliTarget target =
            await Given_DocumentCacheAdminMssqlRebuildOnline.CreateReadyMssqlTargetAsync();

        await Given_DocumentCacheAdminMssqlStatus.WithNestedTriggersAsync(
            target,
            enabled: true,
            async () =>
            {
                DocumentCacheAdminCliSeededDocument document =
                    await target.State.InsertMssqlDescriptorDocumentAsync(
                        "MssqlScrubAheadCache",
                        contentVersion: 10
                    );
                await target.State.ClearMssqlProjectionWorkAsync();
                await target.State.InsertMssqlProjectionWorkAsync(document, requiredContentVersion: 5);
                await target.State.InsertMssqlDocumentCacheAsync(
                    document,
                    cacheContentVersion: document.ContentVersion + 1,
                    documentJson: """{"value":"ahead-cache"}"""
                );
                await target.State.SetLifecycleAsync("Tracking", cacheAheadRecoveryRequired: false);

                DocumentCacheAdminCliProcessResult result = await RunScrubAsync(target);

                JsonObject commandResult = AssertCommandResult(
                    result,
                    target,
                    DocumentCacheAdminExitCodes.Success
                );
                commandResult["status"]!.GetValue<string>().Should().Be("completed");
                commandResult["classification"]!.GetValue<string>().Should().Be("cacheAheadLatchSet");
                commandResult["mutated"]!.GetValue<bool>().Should().BeTrue();
                commandResult["lifecycle"]!.GetValue<string>().Should().Be("tracking");
                commandResult["cacheAheadRecoveryRequired"]!.GetValue<bool>().Should().BeTrue();

                IReadOnlyDictionary<long, long> workVersions =
                    await target.State.ReadMssqlWorkVersionsByDocumentIdAsync();
                workVersions[document.DocumentId].Should().Be(5);
                DocumentCacheAdminCliLifecycleState lifecycle = await target.State.ReadLifecycleAsync();
                lifecycle.ProjectionLifecycleState.Should().Be("Tracking");
                lifecycle.CacheAheadRecoveryRequired.Should().BeTrue();
                DocumentCacheAdminCliMutableCounts counts = await target.State.ReadMutableCountsAsync();
                counts.DocumentCacheRows.Should().Be(1);
                counts.WorkRows.Should().Be(1);
            }
        );
    }

    [TestCase("Disabled", false)]
    [TestCase("Resetting", false)]
    [TestCase("Rebuilding", false)]
    [TestCase("Tracking", true)]
    public async Task It_rejects_before_scan_when_lifecycle_or_latch_is_not_admitted(
        string lifecycleState,
        bool cacheAheadRecoveryRequired
    )
    {
        await using DocumentCacheAdminCliTarget target =
            await Given_DocumentCacheAdminMssqlRebuildOnline.CreateReadyMssqlTargetAsync();

        await Given_DocumentCacheAdminMssqlStatus.WithNestedTriggersAsync(
            target,
            enabled: true,
            async () =>
            {
                DocumentCacheAdminCliSeededDocument document =
                    await target.State.InsertMssqlDescriptorDocumentAsync(
                        $"MssqlScrubRejected-{lifecycleState}-{cacheAheadRecoveryRequired}",
                        contentVersion: 10
                    );
                await target.State.ClearMssqlProjectionWorkAsync();
                await target.State.SetLifecycleAsync(lifecycleState, cacheAheadRecoveryRequired);

                DocumentCacheAdminCliProcessResult result = await RunScrubAsync(target);

                JsonObject commandResult = AssertCommandResult(
                    result,
                    target,
                    DocumentCacheAdminExitCodes.RejectedNoMutation
                );
                commandResult["status"]!.GetValue<string>().Should().Be("rejectedNoMutation");
                commandResult["mutated"]!.GetValue<bool>().Should().BeFalse();
                commandResult["lifecycle"]!
                    .GetValue<string>()
                    .Should()
                    .Be(ToLowerCamelLifecycle(lifecycleState));
                commandResult["cacheAheadRecoveryRequired"]!
                    .GetValue<bool>()
                    .Should()
                    .Be(cacheAheadRecoveryRequired);

                (await target.State.ReadMssqlWorkVersionsByDocumentIdAsync()).Should().BeEmpty();
                DocumentCacheAdminCliLifecycleState lifecycle = await target.State.ReadLifecycleAsync();
                lifecycle.ProjectionLifecycleState.Should().Be(lifecycleState);
                lifecycle.CacheAheadRecoveryRequired.Should().Be(cacheAheadRecoveryRequired);
                document.DocumentId.Should().BePositive();
            }
        );
    }

    private static async Task<DocumentCacheAdminCliProcessResult> RunScrubAsync(
        DocumentCacheAdminCliTarget target
    )
    {
        await using DocumentCacheAdminCliProcessHarness harness =
            await DocumentCacheAdminCliProcessHarness.CreateAsync(target);

        return await harness.RunAsync(
            DocumentCacheAdminCommandSurface.ScrubCommandName,
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            target.DataStoreId.ToString(),
            DocumentCacheAdminCommandSurface.ConfirmOptionName,
            "integrityScrub",
            DocumentCacheAdminCommandSurface.JsonOptionName,
            DocumentCacheAdminCommandSurface.CommandTimeoutSecondsOptionName,
            "30"
        );
    }

    private static async Task<JsonObject> RunStatusAndReadTargetAsync(DocumentCacheAdminCliTarget target)
    {
        await using DocumentCacheAdminCliProcessHarness harness =
            await DocumentCacheAdminCliProcessHarness.CreateAsync(target);

        DocumentCacheAdminCliProcessResult result = await harness.RunAsync(
            DocumentCacheAdminCommandSurface.StatusCommandName,
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            target.DataStoreId.ToString(),
            DocumentCacheAdminCommandSurface.JsonOptionName,
            DocumentCacheAdminCommandSurface.StatusObservationTimeoutSecondsOptionName,
            "1",
            DocumentCacheAdminCommandSurface.StatusTimeoutSecondsOptionName,
            "5"
        );

        result
            .ExitCode.Should()
            .Be(
                DocumentCacheAdminExitCodes.Success,
                "stderr:\n{0}\nstdout:\n{1}",
                result.StandardError,
                result.StandardOutput
            );
        JsonArray targets = result.ReadStandardOutputJsonObject()["targets"]!.AsArray();
        targets.Should().ContainSingle();
        return targets[0]!.AsObject();
    }

    private static string ToLowerCamelLifecycle(string lifecycleState) =>
        char.ToLowerInvariant(lifecycleState[0]) + lifecycleState[1..];

    private static JsonObject AssertCommandResult(
        DocumentCacheAdminCliProcessResult result,
        DocumentCacheAdminCliTarget target,
        int expectedExitCode
    ) => DocumentCacheAdminCliCommandResultAssertions.AssertCommandResult(result, target, expectedExitCode);
}

[TestFixture]
[NonParallelizable]
[Category("MssqlIntegration")]
[Category("MssqlOffline")]
public sealed class Given_DocumentCacheAdminMssqlOfflineAndCacheAheadRecovery
{
    [TestCase(
        DocumentCacheAdminCommandSurface.ActivateOfflineCommandName,
        "offlineActivation",
        "Disabled",
        false,
        null,
        TestName = "activate-offline missing admission"
    )]
    [TestCase(
        DocumentCacheAdminCommandSurface.ActivateOfflineCommandName,
        "offlineActivation",
        "Disabled",
        false,
        "ClosedAndDrained",
        TestName = "activate-offline wrong admission"
    )]
    [TestCase(
        DocumentCacheAdminCommandSurface.DeactivateOfflineCommandName,
        "offlineDeactivation",
        "Tracking",
        false,
        null,
        TestName = "deactivate-offline missing admission"
    )]
    [TestCase(
        DocumentCacheAdminCommandSurface.DeactivateOfflineCommandName,
        "offlineDeactivation",
        "Tracking",
        false,
        "ClosedAndDrained",
        TestName = "deactivate-offline wrong admission"
    )]
    [TestCase(
        DocumentCacheAdminCommandSurface.RecoverCacheAheadCommandName,
        "internalCacheAheadRecovery",
        "Tracking",
        true,
        null,
        TestName = "recover-cache-ahead missing admission"
    )]
    [TestCase(
        DocumentCacheAdminCommandSurface.RecoverCacheAheadCommandName,
        "internalCacheAheadRecovery",
        "Tracking",
        true,
        "ClosedAndDrained",
        TestName = "recover-cache-ahead wrong admission"
    )]
    public async Task It_rejects_missing_or_wrong_offline_writer_admission_as_argument_errors(
        string commandName,
        string confirmation,
        string lifecycleState,
        bool cacheAheadRecoveryRequired,
        string? offlineWriterAdmission
    )
    {
        await using DocumentCacheAdminCliTarget target =
            await Given_DocumentCacheAdminMssqlRebuildOnline.CreateReadyMssqlTargetAsync();
        await target.State.SetLifecycleAsync(lifecycleState, cacheAheadRecoveryRequired);
        DocumentCacheAdminCliLifecycleState originalLifecycle = await target.State.ReadLifecycleAsync();

        await using DocumentCacheAdminCliProcessHarness harness =
            await DocumentCacheAdminCliProcessHarness.CreateAsync(target);

        DocumentCacheAdminCliProcessResult result = await RunWriterFencedCommandAsync(
            harness,
            target,
            commandName,
            confirmation,
            offlineWriterAdmission
        );

        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.ArgumentError);
        result.StandardOutput.Should().BeEmpty();
        result
            .StandardError.Should()
            .Contain(DocumentCacheAdminCommandSurface.OfflineWriterAdmissionOptionName);
        result.StandardError.Should().NotContain(target.ConnectionString);
        harness.ConfigurationService.TokenRequestCount.Should().Be(0);
        harness.ConfigurationService.DataStoresRequestCount.Should().Be(0);
        (await target.State.ReadLifecycleAsync()).Should().Be(originalLifecycle);
    }

    [TestCase(
        DocumentCacheAdminCommandSurface.ActivateOfflineCommandName,
        "offlineActivation",
        "offlineActivation",
        "Disabled",
        false,
        TestName = "activate-offline"
    )]
    [TestCase(
        DocumentCacheAdminCommandSurface.DeactivateOfflineCommandName,
        "offlineDeactivation",
        "offlineDeactivation",
        "Tracking",
        false,
        TestName = "deactivate-offline"
    )]
    [TestCase(
        DocumentCacheAdminCommandSurface.RecoverCacheAheadCommandName,
        "internalCacheAheadRecovery",
        "internalOnlyCacheAheadRecovery",
        "Tracking",
        true,
        TestName = "recover-cache-ahead"
    )]
    public async Task It_rejects_production_default_unknown_downstream_history_without_mutation(
        string commandName,
        string confirmation,
        string expectedJsonCommand,
        string lifecycleState,
        bool cacheAheadRecoveryRequired
    )
    {
        await using DocumentCacheAdminCliTarget target =
            await Given_DocumentCacheAdminMssqlRebuildOnline.CreateReadyMssqlTargetAsync();

        await Given_DocumentCacheAdminMssqlStatus.WithNestedTriggersAsync(
            target,
            enabled: true,
            async () =>
            {
                IReadOnlyList<DocumentCacheAdminCliSeededDocument> documents =
                    await InsertProjectedDescriptorRowsAsync(target, commandName, documentCount: 2);
                await target.State.SetLifecycleAsync(lifecycleState, cacheAheadRecoveryRequired);
                DocumentCacheAdminCliLifecycleState originalLifecycle =
                    await target.State.ReadLifecycleAsync();
                DocumentCacheAdminCliMutableCounts originalCounts =
                    await target.State.ReadMutableCountsAsync();
                IReadOnlyDictionary<long, string> originalCacheRows =
                    await target.State.ReadMssqlCachedJsonByDocumentIdAsync();
                IReadOnlyDictionary<long, long> originalWorkRows =
                    await target.State.ReadMssqlWorkVersionsByDocumentIdAsync();

                await using DocumentCacheAdminCliProcessHarness harness =
                    await DocumentCacheAdminCliProcessHarness.CreateAsync(target);

                DocumentCacheAdminCliProcessResult result = await RunWriterFencedCommandAsync(
                    harness,
                    target,
                    commandName,
                    confirmation,
                    DocumentCacheAdminCommandSurface.OfflineWriterAdmissionClosedAndDrainedOptionValue,
                    expectedPhysicalSourceFingerprint: await target.State.ReadPhysicalSourceFingerprintAsync()
                );

                JsonObject commandResult = DocumentCacheAdminCliCommandResultAssertions.AssertCommandResult(
                    result,
                    target,
                    DocumentCacheAdminExitCodes.RejectedNoMutation
                );
                commandResult["command"]!.GetValue<string>().Should().Be(expectedJsonCommand);
                commandResult["status"]!.GetValue<string>().Should().Be("rejectedNoMutation");
                commandResult["classification"]!
                    .GetValue<string>()
                    .Should()
                    .Be("downstreamHistoryPresentOrUnknown");
                commandResult["mutated"]!.GetValue<bool>().Should().BeFalse();
                commandResult["lifecycle"]!
                    .GetValue<string>()
                    .Should()
                    .Be(ToLowerCamelLifecycle(lifecycleState));
                commandResult["cacheAheadRecoveryRequired"]!
                    .GetValue<bool>()
                    .Should()
                    .Be(cacheAheadRecoveryRequired);
                (await target.State.ReadLifecycleAsync()).Should().Be(originalLifecycle);
                (await target.State.ReadMutableCountsAsync()).Should().Be(originalCounts);
                (await target.State.ReadMssqlCachedJsonByDocumentIdAsync())
                    .Should()
                    .BeEquivalentTo(originalCacheRows);
                (await target.State.ReadMssqlWorkVersionsByDocumentIdAsync())
                    .Should()
                    .BeEquivalentTo(originalWorkRows);
                harness.ConfigurationService.TokenRequestCount.Should().BeGreaterThan(0);
                harness.ConfigurationService.DataStoresRequestCount.Should().BeGreaterThan(0);
                documents.Count.Should().Be((int)originalCounts.DocumentCacheRows);
            }
        );
    }

    private static Task<DocumentCacheAdminCliProcessResult> RunWriterFencedCommandAsync(
        DocumentCacheAdminCliProcessHarness harness,
        DocumentCacheAdminCliTarget target,
        string commandName,
        string confirmation,
        string? offlineWriterAdmission,
        string? expectedPhysicalSourceFingerprint = null
    ) =>
        harness.RunAsync(
            CreateWriterFencedArguments(
                target,
                commandName,
                confirmation,
                offlineWriterAdmission,
                expectedPhysicalSourceFingerprint
            )
        );

    private static string[] CreateWriterFencedArguments(
        DocumentCacheAdminCliTarget target,
        string commandName,
        string confirmation,
        string? offlineWriterAdmission,
        string? expectedPhysicalSourceFingerprint
    )
    {
        List<string> arguments =
        [
            commandName,
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            target.DataStoreId.ToString(),
            DocumentCacheAdminCommandSurface.ConfirmOptionName,
            confirmation,
            DocumentCacheAdminCommandSurface.JsonOptionName,
            DocumentCacheAdminCommandSurface.CommandTimeoutSecondsOptionName,
            "30",
        ];

        if (offlineWriterAdmission is not null)
        {
            arguments.Add(DocumentCacheAdminCommandSurface.OfflineWriterAdmissionOptionName);
            arguments.Add(offlineWriterAdmission);
        }

        if (expectedPhysicalSourceFingerprint is not null)
        {
            arguments.Add(DocumentCacheAdminCommandSurface.ExpectedPhysicalSourceFingerprintOptionName);
            arguments.Add(expectedPhysicalSourceFingerprint);
        }

        return [.. arguments];
    }

    private static async Task<
        IReadOnlyList<DocumentCacheAdminCliSeededDocument>
    > InsertProjectedDescriptorRowsAsync(
        DocumentCacheAdminCliTarget target,
        string codeValuePrefix,
        int documentCount
    )
    {
        List<DocumentCacheAdminCliSeededDocument> documents = [];
        for (var index = 0; index < documentCount; index++)
        {
            DocumentCacheAdminCliSeededDocument document =
                await target.State.InsertMssqlDescriptorDocumentAsync(
                    $"{codeValuePrefix}-{index}",
                    contentVersion: 10 + index
                );
            await target.State.InsertMssqlDocumentCacheAsync(
                document,
                documentJson: $$"""{"value":"projected-{{index}}"}"""
            );
            await target.State.InsertMssqlProjectionWorkAsync(document);
            documents.Add(document);
        }

        return documents;
    }

    private static string ToLowerCamelLifecycle(string lifecycleState) =>
        char.ToLowerInvariant(lifecycleState[0]) + lifecycleState[1..];
}
