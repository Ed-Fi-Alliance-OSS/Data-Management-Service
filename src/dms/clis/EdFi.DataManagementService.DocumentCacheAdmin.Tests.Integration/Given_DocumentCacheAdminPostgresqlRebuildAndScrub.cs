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
[Category("PostgresqlIntegration")]
[Category("PostgresqlRebuildOnline")]
public sealed class Given_DocumentCacheAdminPostgresqlRebuildOnline
{
    [Test]
    public async Task It_rebuilds_tracking_cache_and_drains_work()
    {
        await using DocumentCacheAdminCliTarget target =
            await DocumentCacheAdminCliTarget.CreatePostgresqlAsync();
        IReadOnlyList<DocumentCacheAdminCliSeededDocument> documents =
            await InsertProjectedDescriptorRowsAsync(target, "RebuildTracking", documentCount: 3);
        await target.State.SetLifecycleAsync("Tracking", cacheAheadRecoveryRequired: false);

        DocumentCacheAdminCliProcessResult result = await RunRebuildOnlineAsync(target);

        JsonObject commandResult = AssertCommandResult(result, target, DocumentCacheAdminExitCodes.Success);
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
            await target.State.ReadPostgresqlCachedJsonByDocumentIdAsync();
        cachedJsonByDocumentId
            .Keys.Should()
            .BeEquivalentTo(documents.Select(document => document.DocumentId));
        cachedJsonByDocumentId.Values.Should().OnlyContain(json => !json.Contains("stale-cache"));
        cachedJsonByDocumentId.Values.Should().OnlyContain(json => json.Contains("RebuildTracking"));
    }

    [Test]
    public async Task It_resumes_rebuilding_without_repeating_cache_clearing()
    {
        await using DocumentCacheAdminCliTarget target =
            await DocumentCacheAdminCliTarget.CreatePostgresqlAsync();
        DocumentCacheAdminCliSeededDocument document =
            await target.State.InsertPostgresqlDescriptorDocumentAsync("RebuildResume", contentVersion: 21);
        await target.State.ClearPostgresqlProjectionWorkAsync();
        await target.State.InsertPostgresqlDocumentCacheAsync(
            document,
            documentJson: """{"value":"kept-cache"}"""
        );
        await target.State.SetLifecycleAsync("Rebuilding", cacheAheadRecoveryRequired: false);

        DocumentCacheAdminCliProcessResult result = await RunRebuildOnlineAsync(target);

        JsonObject commandResult = AssertCommandResult(result, target, DocumentCacheAdminExitCodes.Success);
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
            await target.State.ReadPostgresqlCachedJsonByDocumentIdAsync();
        cachedJsonByDocumentId[document.DocumentId].Should().Contain("kept-cache");
    }

    [Test]
    public async Task It_rejects_a_set_latch_without_lifecycle_cache_work_or_latch_mutation()
    {
        await using DocumentCacheAdminCliTarget target =
            await DocumentCacheAdminCliTarget.CreatePostgresqlAsync();
        IReadOnlyList<DocumentCacheAdminCliSeededDocument> documents =
            await InsertProjectedDescriptorRowsAsync(target, "RebuildLatched", documentCount: 2);
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
            await target.State.ReadPostgresqlCachedJsonByDocumentIdAsync();
        cachedJsonByDocumentId.Values.Should().OnlyContain(json => json.Contains("stale-cache"));
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
                await target.State.InsertPostgresqlDescriptorDocumentAsync(
                    $"{codeValuePrefix}-{index}",
                    contentVersion: 10 + index
                );
            await target.State.InsertPostgresqlDocumentCacheAsync(
                document,
                documentJson: $$"""{"value":"stale-cache-{{index}}"}"""
            );
            await target.State.InsertPostgresqlProjectionWorkAsync(document);
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
    ) => PostgresqlCommandResultAssertions.AssertCommandResult(result, target, expectedExitCode);
}

[TestFixture]
[NonParallelizable]
[Category("PostgresqlIntegration")]
[Category("PostgresqlScrub")]
public sealed class Given_DocumentCacheAdminPostgresqlScrub
{
    [Test]
    public async Task It_repairs_missing_and_mismatched_work_without_changing_lifecycle_or_cache()
    {
        await using DocumentCacheAdminCliTarget target =
            await DocumentCacheAdminCliTarget.CreatePostgresqlAsync();
        DocumentCacheAdminCliSeededDocument missingWork =
            await target.State.InsertPostgresqlDescriptorDocumentAsync("ScrubMissing", contentVersion: 10);
        DocumentCacheAdminCliSeededDocument staleWork =
            await target.State.InsertPostgresqlDescriptorDocumentAsync("ScrubStale", contentVersion: 20);
        DocumentCacheAdminCliSeededDocument aheadWork =
            await target.State.InsertPostgresqlDescriptorDocumentAsync("ScrubAhead", contentVersion: 30);
        await target.State.ClearPostgresqlProjectionWorkAsync();
        await target.State.InsertPostgresqlProjectionWorkAsync(staleWork, requiredContentVersion: 15);
        await target.State.InsertPostgresqlProjectionWorkAsync(aheadWork, requiredContentVersion: 35);
        await target.State.SetLifecycleAsync("Tracking", cacheAheadRecoveryRequired: false);

        DocumentCacheAdminCliProcessResult result = await RunScrubAsync(target);

        JsonObject commandResult = AssertCommandResult(result, target, DocumentCacheAdminExitCodes.Success);
        commandResult["command"]!.GetValue<string>().Should().Be("explicitIntegrityScrub");
        commandResult["status"]!.GetValue<string>().Should().Be("completed");
        commandResult["classification"]!.GetValue<string>().Should().Be("succeeded");
        commandResult["mutated"]!.GetValue<bool>().Should().BeTrue();
        commandResult["lifecycle"]!.GetValue<string>().Should().Be("tracking");
        commandResult["cacheAheadRecoveryRequired"]!.GetValue<bool>().Should().BeFalse();

        IReadOnlyDictionary<long, long> workVersions =
            await target.State.ReadPostgresqlWorkVersionsByDocumentIdAsync();
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

    [Test]
    public async Task It_sets_the_cache_ahead_latch_without_repairing_that_work_row()
    {
        await using DocumentCacheAdminCliTarget target =
            await DocumentCacheAdminCliTarget.CreatePostgresqlAsync();
        DocumentCacheAdminCliSeededDocument document =
            await target.State.InsertPostgresqlDescriptorDocumentAsync("ScrubAheadCache", contentVersion: 10);
        await target.State.ClearPostgresqlProjectionWorkAsync();
        await target.State.InsertPostgresqlProjectionWorkAsync(document, requiredContentVersion: 5);
        await target.State.InsertPostgresqlDocumentCacheAsync(
            document,
            cacheContentVersion: document.ContentVersion + 1,
            documentJson: """{"value":"ahead-cache"}"""
        );
        await target.State.SetLifecycleAsync("Tracking", cacheAheadRecoveryRequired: false);

        DocumentCacheAdminCliProcessResult result = await RunScrubAsync(target);

        JsonObject commandResult = AssertCommandResult(result, target, DocumentCacheAdminExitCodes.Success);
        commandResult["status"]!.GetValue<string>().Should().Be("completed");
        commandResult["classification"]!.GetValue<string>().Should().Be("cacheAheadLatchSet");
        commandResult["mutated"]!.GetValue<bool>().Should().BeTrue();
        commandResult["lifecycle"]!.GetValue<string>().Should().Be("tracking");
        commandResult["cacheAheadRecoveryRequired"]!.GetValue<bool>().Should().BeTrue();

        IReadOnlyDictionary<long, long> workVersions =
            await target.State.ReadPostgresqlWorkVersionsByDocumentIdAsync();
        workVersions[document.DocumentId].Should().Be(5);
        DocumentCacheAdminCliLifecycleState lifecycle = await target.State.ReadLifecycleAsync();
        lifecycle.ProjectionLifecycleState.Should().Be("Tracking");
        lifecycle.CacheAheadRecoveryRequired.Should().BeTrue();
        DocumentCacheAdminCliMutableCounts counts = await target.State.ReadMutableCountsAsync();
        counts.DocumentCacheRows.Should().Be(1);
        counts.WorkRows.Should().Be(1);
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
            await DocumentCacheAdminCliTarget.CreatePostgresqlAsync();
        DocumentCacheAdminCliSeededDocument document =
            await target.State.InsertPostgresqlDescriptorDocumentAsync(
                $"ScrubRejected-{lifecycleState}-{cacheAheadRecoveryRequired}",
                contentVersion: 10
            );
        await target.State.ClearPostgresqlProjectionWorkAsync();
        await target.State.SetLifecycleAsync(lifecycleState, cacheAheadRecoveryRequired);

        DocumentCacheAdminCliProcessResult result = await RunScrubAsync(target);

        JsonObject commandResult = AssertCommandResult(
            result,
            target,
            DocumentCacheAdminExitCodes.RejectedNoMutation
        );
        commandResult["status"]!.GetValue<string>().Should().Be("rejectedNoMutation");
        commandResult["mutated"]!.GetValue<bool>().Should().BeFalse();
        commandResult["lifecycle"]!.GetValue<string>().Should().Be(ToLowerCamelLifecycle(lifecycleState));
        commandResult["cacheAheadRecoveryRequired"]!.GetValue<bool>().Should().Be(cacheAheadRecoveryRequired);

        (await target.State.ReadPostgresqlWorkVersionsByDocumentIdAsync()).Should().BeEmpty();
        DocumentCacheAdminCliLifecycleState lifecycle = await target.State.ReadLifecycleAsync();
        lifecycle.ProjectionLifecycleState.Should().Be(lifecycleState);
        lifecycle.CacheAheadRecoveryRequired.Should().Be(cacheAheadRecoveryRequired);
        document.DocumentId.Should().BePositive();
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
    ) => PostgresqlCommandResultAssertions.AssertCommandResult(result, target, expectedExitCode);
}

internal static class PostgresqlCommandResultAssertions
{
    public static JsonObject AssertCommandResult(
        DocumentCacheAdminCliProcessResult result,
        DocumentCacheAdminCliTarget target,
        int expectedExitCode
    )
    {
        result
            .ExitCode.Should()
            .Be(expectedExitCode, "stderr:\n{0}\nstdout:\n{1}", result.StandardError, result.StandardOutput);
        result.StandardOutput.TrimEnd().Should().NotContain("\n");
        result.StandardError.Should().NotContain(target.ConnectionString);

        JsonObject commandResult = result.ReadStandardOutputJsonObject();
        Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(commandResult, "targetKey")["tenantKey"]!
            .GetValue<string>()
            .Should()
            .Be(target.TenantKey);
        Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(commandResult, "targetKey")["dataStoreId"]!
            .GetValue<long>()
            .Should()
            .Be(target.DataStoreId);
        commandResult["targetGeneration"]!.GetValue<long>().Should().BeGreaterThan(0);
        commandResult["physicalSourceFingerprint"]!.GetValue<string>().Should().StartWith("sha256:");
        return commandResult;
    }
}
