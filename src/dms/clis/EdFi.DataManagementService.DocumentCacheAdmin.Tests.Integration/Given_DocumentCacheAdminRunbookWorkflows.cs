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
[Category("Runbook")]
public sealed class Given_DocumentCacheAdminPostgresqlRunbookWorkflows
{
    [Test]
    public async Task It_routes_suspected_direct_mutation_to_scrub_and_reports_processing_backlog()
    {
        await using DocumentCacheAdminCliTarget target =
            await DocumentCacheAdminCliTarget.CreatePostgresqlAsync();
        DocumentCacheAdminCliSeededDocument document =
            await target.State.InsertPostgresqlDescriptorDocumentAsync(
                "RunbookPostgresqlDirectMutation",
                contentVersion: 41
            );
        await target.State.ClearPostgresqlProjectionWorkAsync();
        await target.State.SetLifecycleAsync("Tracking", cacheAheadRecoveryRequired: false);

        await using DocumentCacheAdminCliProcessHarness harness =
            await DocumentCacheAdminCliProcessHarness.CreateAsync(target);
        JsonObject initialStatus = await RunbookWorkflowAssertions.RunStatusAndReadTargetAsync(
            harness,
            target
        );
        RunbookWorkflowAssertions.AssertTrackingStatus(initialStatus);
        RunbookWorkflowAssertions.AssertClearCacheAheadStatus(initialStatus);
        RunbookWorkflowAssertions.AssertQueuePresence(initialStatus, "empty");

        DocumentCacheAdminCliProcessResult scrubResult = await RunbookWorkflowAssertions.RunScrubAsync(
            harness,
            target,
            initialStatus["physicalSourceFingerprint"]!.GetValue<string>()
        );

        JsonObject scrub = DocumentCacheAdminCliCommandResultAssertions.AssertCommandResult(
            scrubResult,
            target,
            DocumentCacheAdminExitCodes.Success
        );
        RunbookWorkflowAssertions.AssertCommandContract(
            scrub,
            expectedCommand: "explicitIntegrityScrub",
            expectedStatus: "completed",
            expectedClassification: "succeeded",
            expectedMutated: true,
            expectedLifecycle: "tracking",
            expectedCacheAheadRecoveryRequired: false
        );

        IReadOnlyDictionary<long, long> workVersions =
            await target.State.ReadPostgresqlWorkVersionsByDocumentIdAsync();
        workVersions.Should().ContainKey(document.DocumentId).WhoseValue.Should().Be(document.ContentVersion);
        DocumentCacheAdminCliMutableCounts counts = await target.State.ReadMutableCountsAsync();
        counts.DocumentCacheRows.Should().Be(0);
        counts.WorkRows.Should().Be(1);

        JsonObject postScrubStatus = await RunbookWorkflowAssertions.RunStatusAndReadTargetAsync(
            harness,
            target
        );
        RunbookWorkflowAssertions.AssertTrackingStatus(postScrubStatus);
        RunbookWorkflowAssertions.AssertQueuePresence(postScrubStatus, "notEmpty");
        Given_DocumentCacheAdminPostgresqlStatus.AssertStandaloneRuntimeNotObserved(postScrubStatus);
    }

    [Test]
    public async Task It_routes_cache_ahead_status_to_internal_recovery_and_preserves_evidence_when_history_is_unknown()
    {
        await using DocumentCacheAdminCliTarget target =
            await DocumentCacheAdminCliTarget.CreatePostgresqlAsync();
        DocumentCacheAdminCliSeededDocument document =
            await target.State.InsertPostgresqlDescriptorDocumentAsync(
                "RunbookPostgresqlCacheAhead",
                contentVersion: 51
            );
        await target.State.ClearPostgresqlProjectionWorkAsync();
        await target.State.InsertPostgresqlProjectionWorkAsync(document);
        await target.State.InsertPostgresqlDocumentCacheAsync(
            document,
            cacheContentVersion: document.ContentVersion + 1,
            documentJson: """{"value":"cache-ahead-evidence"}"""
        );
        await target.State.SetLifecycleAsync("Tracking", cacheAheadRecoveryRequired: true);

        DocumentCacheAdminCliLifecycleState originalLifecycle = await target.State.ReadLifecycleAsync();
        DocumentCacheAdminCliMutableCounts originalCounts = await target.State.ReadMutableCountsAsync();
        IReadOnlyDictionary<long, long> originalWorkRows =
            await target.State.ReadPostgresqlWorkVersionsByDocumentIdAsync();
        IReadOnlyDictionary<long, string> originalCacheRows =
            await target.State.ReadPostgresqlCachedJsonByDocumentIdAsync();

        await using DocumentCacheAdminCliProcessHarness harness =
            await DocumentCacheAdminCliProcessHarness.CreateAsync(target);
        JsonObject status = await RunbookWorkflowAssertions.RunStatusAndReadTargetAsync(harness, target);
        RunbookWorkflowAssertions.AssertTrackingStatus(status);
        RunbookWorkflowAssertions.AssertCacheAheadRecoveryRequiredStatus(status);
        RunbookWorkflowAssertions.AssertQueuePresence(status, "notEmpty");

        DocumentCacheAdminCliProcessResult recoveryResult =
            await RunbookWorkflowAssertions.RunRecoverCacheAheadAsync(
                harness,
                target,
                status["physicalSourceFingerprint"]!.GetValue<string>()
            );

        JsonObject recovery = DocumentCacheAdminCliCommandResultAssertions.AssertCommandResult(
            recoveryResult,
            target,
            DocumentCacheAdminExitCodes.RejectedNoMutation
        );
        RunbookWorkflowAssertions.AssertCommandContract(
            recovery,
            expectedCommand: "internalOnlyCacheAheadRecovery",
            expectedStatus: "rejectedNoMutation",
            expectedClassification: "downstreamHistoryPresentOrUnknown",
            expectedMutated: false,
            expectedLifecycle: "tracking",
            expectedCacheAheadRecoveryRequired: true
        );

        (await target.State.ReadLifecycleAsync()).Should().Be(originalLifecycle);
        (await target.State.ReadMutableCountsAsync()).Should().Be(originalCounts);
        (await target.State.ReadPostgresqlWorkVersionsByDocumentIdAsync())
            .Should()
            .BeEquivalentTo(originalWorkRows);
        (await target.State.ReadPostgresqlCachedJsonByDocumentIdAsync())
            .Should()
            .BeEquivalentTo(originalCacheRows);
    }
}

[TestFixture]
[NonParallelizable]
[Category("MssqlIntegration")]
[Category("Runbook")]
public sealed class Given_DocumentCacheAdminMssqlRunbookWorkflows
{
    [Test]
    public async Task It_routes_suspected_direct_mutation_to_scrub_and_reports_processing_backlog()
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
                        "RunbookMssqlDirectMutation",
                        contentVersion: 41
                    );
                await target.State.ClearMssqlProjectionWorkAsync();
                await target.State.SetLifecycleAsync("Tracking", cacheAheadRecoveryRequired: false);

                await using DocumentCacheAdminCliProcessHarness harness =
                    await DocumentCacheAdminCliProcessHarness.CreateAsync(target);
                JsonObject initialStatus = await RunbookWorkflowAssertions.RunStatusAndReadTargetAsync(
                    harness,
                    target
                );
                RunbookWorkflowAssertions.AssertTrackingStatus(initialStatus);
                RunbookWorkflowAssertions.AssertClearCacheAheadStatus(initialStatus);
                RunbookWorkflowAssertions.AssertQueuePresence(initialStatus, "empty");

                DocumentCacheAdminCliProcessResult scrubResult =
                    await RunbookWorkflowAssertions.RunScrubAsync(
                        harness,
                        target,
                        initialStatus["physicalSourceFingerprint"]!.GetValue<string>()
                    );

                JsonObject scrub = DocumentCacheAdminCliCommandResultAssertions.AssertCommandResult(
                    scrubResult,
                    target,
                    DocumentCacheAdminExitCodes.Success
                );
                RunbookWorkflowAssertions.AssertCommandContract(
                    scrub,
                    expectedCommand: "explicitIntegrityScrub",
                    expectedStatus: "completed",
                    expectedClassification: "succeeded",
                    expectedMutated: true,
                    expectedLifecycle: "tracking",
                    expectedCacheAheadRecoveryRequired: false
                );

                IReadOnlyDictionary<long, long> workVersions =
                    await target.State.ReadMssqlWorkVersionsByDocumentIdAsync();
                workVersions
                    .Should()
                    .ContainKey(document.DocumentId)
                    .WhoseValue.Should()
                    .Be(document.ContentVersion);
                DocumentCacheAdminCliMutableCounts counts = await target.State.ReadMutableCountsAsync();
                counts.DocumentCacheRows.Should().Be(0);
                counts.WorkRows.Should().Be(1);

                JsonObject postScrubStatus = await RunbookWorkflowAssertions.RunStatusAndReadTargetAsync(
                    harness,
                    target
                );
                RunbookWorkflowAssertions.AssertTrackingStatus(postScrubStatus);
                RunbookWorkflowAssertions.AssertQueuePresence(postScrubStatus, "notEmpty");
                Given_DocumentCacheAdminPostgresqlStatus.AssertStandaloneRuntimeNotObserved(postScrubStatus);
            }
        );
    }

    [Test]
    public async Task It_routes_cache_ahead_status_to_internal_recovery_and_preserves_evidence_when_history_is_unknown()
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
                        "RunbookMssqlCacheAhead",
                        contentVersion: 51
                    );
                await target.State.ClearMssqlProjectionWorkAsync();
                await target.State.InsertMssqlProjectionWorkAsync(document);
                await target.State.InsertMssqlDocumentCacheAsync(
                    document,
                    cacheContentVersion: document.ContentVersion + 1,
                    documentJson: """{"value":"cache-ahead-evidence"}"""
                );
                await target.State.SetLifecycleAsync("Tracking", cacheAheadRecoveryRequired: true);

                DocumentCacheAdminCliLifecycleState originalLifecycle =
                    await target.State.ReadLifecycleAsync();
                DocumentCacheAdminCliMutableCounts originalCounts =
                    await target.State.ReadMutableCountsAsync();
                IReadOnlyDictionary<long, long> originalWorkRows =
                    await target.State.ReadMssqlWorkVersionsByDocumentIdAsync();
                IReadOnlyDictionary<long, string> originalCacheRows =
                    await target.State.ReadMssqlCachedJsonByDocumentIdAsync();

                await using DocumentCacheAdminCliProcessHarness harness =
                    await DocumentCacheAdminCliProcessHarness.CreateAsync(target);
                JsonObject status = await RunbookWorkflowAssertions.RunStatusAndReadTargetAsync(
                    harness,
                    target
                );
                RunbookWorkflowAssertions.AssertTrackingStatus(status);
                RunbookWorkflowAssertions.AssertCacheAheadRecoveryRequiredStatus(status);
                RunbookWorkflowAssertions.AssertQueuePresence(status, "notEmpty");

                DocumentCacheAdminCliProcessResult recoveryResult =
                    await RunbookWorkflowAssertions.RunRecoverCacheAheadAsync(
                        harness,
                        target,
                        status["physicalSourceFingerprint"]!.GetValue<string>()
                    );

                JsonObject recovery = DocumentCacheAdminCliCommandResultAssertions.AssertCommandResult(
                    recoveryResult,
                    target,
                    DocumentCacheAdminExitCodes.RejectedNoMutation
                );
                RunbookWorkflowAssertions.AssertCommandContract(
                    recovery,
                    expectedCommand: "internalOnlyCacheAheadRecovery",
                    expectedStatus: "rejectedNoMutation",
                    expectedClassification: "downstreamHistoryPresentOrUnknown",
                    expectedMutated: false,
                    expectedLifecycle: "tracking",
                    expectedCacheAheadRecoveryRequired: true
                );

                (await target.State.ReadLifecycleAsync()).Should().Be(originalLifecycle);
                (await target.State.ReadMutableCountsAsync()).Should().Be(originalCounts);
                (await target.State.ReadMssqlWorkVersionsByDocumentIdAsync())
                    .Should()
                    .BeEquivalentTo(originalWorkRows);
                (await target.State.ReadMssqlCachedJsonByDocumentIdAsync())
                    .Should()
                    .BeEquivalentTo(originalCacheRows);
            }
        );
    }

    [Test]
    public async Task It_corrects_disabled_lifecycle_prerequisite_status_before_activation()
    {
        await using DocumentCacheAdminCliTarget target = await DocumentCacheAdminCliTarget.CreateMssqlAsync();

        await Given_DocumentCacheAdminMssqlStatus.WithNestedTriggersAsync(
            target,
            enabled: true,
            async () =>
            {
                await target.State.SetMssqlReadCommittedSnapshotAsync(enabled: false);

                try
                {
                    await using DocumentCacheAdminCliProcessHarness harness =
                        await DocumentCacheAdminCliProcessHarness.CreateAsync(target);
                    JsonObject failedStatus = await RunbookWorkflowAssertions.RunStatusAndReadTargetAsync(
                        harness,
                        target
                    );
                    Given_DocumentCacheAdminMssqlStatus.AssertSqlServerPrerequisiteFailure(failedStatus);
                    RunbookWorkflowAssertions.AssertProviderPrerequisite(
                        failedStatus,
                        expectedStatus: "unsatisfied",
                        expectedReason: "disabled"
                    );

                    DocumentCacheAdminCliProcessResult rejectedActivation =
                        await RunbookWorkflowAssertions.RunActivateNewEmptyAsync(harness, target);
                    JsonObject rejected = DocumentCacheAdminCliCommandResultAssertions.AssertCommandResult(
                        rejectedActivation,
                        target,
                        DocumentCacheAdminExitCodes.RejectedNoMutation
                    );
                    RunbookWorkflowAssertions.AssertCommandContract(
                        rejected,
                        expectedCommand: "guardedNewEmptyActivation",
                        expectedStatus: "rejectedNoMutation",
                        expectedClassification: "providerPrerequisiteFailed",
                        expectedMutated: false
                    );
                    await AssertEmptyDisabledTargetAsync(target);

                    await target.State.SetMssqlReadCommittedSnapshotAsync(enabled: true);
                    JsonObject correctedStatus = await RunbookWorkflowAssertions.RunStatusAndReadTargetAsync(
                        harness,
                        target
                    );
                    Given_DocumentCacheAdminMssqlStatus.AssertResolvedMssqlTarget(correctedStatus);
                    RunbookWorkflowAssertions.AssertProviderPrerequisite(
                        correctedStatus,
                        expectedStatus: "satisfied",
                        expectedReason: "none"
                    );
                    Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(correctedStatus, "lifecycle")[
                        "state"
                    ]!
                        .GetValue<string>()
                        .Should()
                        .Be("disabled");

                    DocumentCacheAdminCliProcessResult successfulActivation =
                        await RunbookWorkflowAssertions.RunActivateNewEmptyAsync(harness, target);
                    JsonObject activated = DocumentCacheAdminCliCommandResultAssertions.AssertCommandResult(
                        successfulActivation,
                        target,
                        DocumentCacheAdminExitCodes.Success
                    );
                    RunbookWorkflowAssertions.AssertCommandContract(
                        activated,
                        expectedCommand: "guardedNewEmptyActivation",
                        expectedStatus: "completed",
                        expectedClassification: "succeeded",
                        expectedMutated: true,
                        expectedLifecycle: "tracking",
                        expectedCacheAheadRecoveryRequired: false
                    );

                    DocumentCacheAdminCliLifecycleState lifecycle = await target.State.ReadLifecycleAsync();
                    lifecycle.ProjectionLifecycleState.Should().Be("Tracking");
                    lifecycle.CacheAheadRecoveryRequired.Should().BeFalse();
                    DocumentCacheAdminCliMutableCounts counts = await target.State.ReadMutableCountsAsync();
                    counts.DocumentCacheRows.Should().Be(0);
                    counts.WorkRows.Should().Be(0);
                }
                finally
                {
                    await target.State.SetMssqlReadCommittedSnapshotAsync(enabled: true);
                }
            }
        );
    }

    private static async Task AssertEmptyDisabledTargetAsync(DocumentCacheAdminCliTarget target)
    {
        DocumentCacheAdminCliLifecycleState lifecycle = await target.State.ReadLifecycleAsync();
        lifecycle.ProjectionLifecycleState.Should().Be("Disabled");
        lifecycle.CacheAheadRecoveryRequired.Should().BeFalse();
        (await target.State.ReadCanonicalDocumentCountAsync()).Should().Be(0);
        DocumentCacheAdminCliMutableCounts counts = await target.State.ReadMutableCountsAsync();
        counts.DocumentCacheRows.Should().Be(0);
        counts.WorkRows.Should().Be(0);
    }
}

internal static class RunbookWorkflowAssertions
{
    public static async Task<JsonObject> RunStatusAndReadTargetAsync(
        DocumentCacheAdminCliProcessHarness harness,
        DocumentCacheAdminCliTarget target
    )
    {
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
        result.StandardOutput.TrimEnd().Should().NotContain("\n");
        result.StandardError.Should().NotContain(target.ConnectionString);

        JsonObject root = result.ReadStandardOutputJsonObject();
        root["contractVersion"]!.GetValue<int>().Should().Be(1);
        root["observedAt"]!.GetValue<string>().Should().EndWith("Z");

        JsonArray targets = root["targets"]!.AsArray();
        targets.Should().ContainSingle();
        JsonObject targetStatus = targets[0]!.AsObject();
        Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(targetStatus, "targetKey")["tenantKey"]!
            .GetValue<string>()
            .Should()
            .Be(target.TenantKey);
        Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(targetStatus, "targetKey")["dataStoreId"]!
            .GetValue<long>()
            .Should()
            .Be(target.DataStoreId);

        return targetStatus;
    }

    public static Task<DocumentCacheAdminCliProcessResult> RunScrubAsync(
        DocumentCacheAdminCliProcessHarness harness,
        DocumentCacheAdminCliTarget target,
        string expectedPhysicalSourceFingerprint
    ) =>
        harness.RunAsync(
            DocumentCacheAdminCommandSurface.ScrubCommandName,
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            target.DataStoreId.ToString(),
            DocumentCacheAdminCommandSurface.ConfirmOptionName,
            "integrityScrub",
            DocumentCacheAdminCommandSurface.ExpectedPhysicalSourceFingerprintOptionName,
            expectedPhysicalSourceFingerprint,
            DocumentCacheAdminCommandSurface.JsonOptionName,
            DocumentCacheAdminCommandSurface.CommandTimeoutSecondsOptionName,
            "30"
        );

    public static Task<DocumentCacheAdminCliProcessResult> RunRecoverCacheAheadAsync(
        DocumentCacheAdminCliProcessHarness harness,
        DocumentCacheAdminCliTarget target,
        string expectedPhysicalSourceFingerprint
    ) =>
        harness.RunAsync(
            DocumentCacheAdminCommandSurface.RecoverCacheAheadCommandName,
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            target.DataStoreId.ToString(),
            DocumentCacheAdminCommandSurface.ConfirmOptionName,
            "internalCacheAheadRecovery",
            DocumentCacheAdminCommandSurface.OfflineWriterAdmissionOptionName,
            DocumentCacheAdminCommandSurface.OfflineWriterAdmissionClosedAndDrainedOptionValue,
            DocumentCacheAdminCommandSurface.ExpectedPhysicalSourceFingerprintOptionName,
            expectedPhysicalSourceFingerprint,
            DocumentCacheAdminCommandSurface.JsonOptionName,
            DocumentCacheAdminCommandSurface.CommandTimeoutSecondsOptionName,
            "30"
        );

    public static Task<DocumentCacheAdminCliProcessResult> RunActivateNewEmptyAsync(
        DocumentCacheAdminCliProcessHarness harness,
        DocumentCacheAdminCliTarget target
    ) =>
        harness.RunAsync(
            DocumentCacheAdminCommandSurface.ActivateNewEmptyCommandName,
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            target.DataStoreId.ToString(),
            DocumentCacheAdminCommandSurface.ConfirmOptionName,
            "newEmptyActivation",
            DocumentCacheAdminCommandSurface.JsonOptionName,
            DocumentCacheAdminCommandSurface.CommandTimeoutSecondsOptionName,
            "30"
        );

    public static void AssertTrackingStatus(JsonObject targetStatus)
    {
        Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(targetStatus, "lifecycle")["state"]!
            .GetValue<string>()
            .Should()
            .Be("tracking");
    }

    public static void AssertClearCacheAheadStatus(JsonObject targetStatus)
    {
        JsonObject cacheAhead = Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(
            targetStatus,
            "cacheAhead"
        );
        cacheAhead["state"]!.GetValue<string>().Should().Be("clear");
        cacheAhead["recoveryRequired"]!.GetValue<bool>().Should().BeFalse();
    }

    public static void AssertCacheAheadRecoveryRequiredStatus(JsonObject targetStatus)
    {
        JsonObject cacheAhead = Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(
            targetStatus,
            "cacheAhead"
        );
        cacheAhead["state"]!.GetValue<string>().Should().Be("recoveryRequired");
        cacheAhead["recoveryRequired"]!.GetValue<bool>().Should().BeTrue();
    }

    public static void AssertQueuePresence(JsonObject targetStatus, string expectedPresence)
    {
        JsonObject queueSummary = Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(
            targetStatus,
            "queueSummary"
        );
        queueSummary["presence"]!.GetValue<string>().Should().Be(expectedPresence);

        if (expectedPresence == "notEmpty")
        {
            queueSummary["oldestWorkFirstEnqueuedAt"]!.GetValue<string>().Should().EndWith("Z");
            queueSummary["oldestWorkAgeSeconds"]!.GetValue<double>().Should().BeGreaterThanOrEqualTo(0);
        }
        else
        {
            queueSummary["oldestWorkFirstEnqueuedAt"].Should().BeNull();
            queueSummary["oldestWorkAgeSeconds"].Should().BeNull();
        }
    }

    public static void AssertProviderPrerequisite(
        JsonObject targetStatus,
        string expectedStatus,
        string expectedReason
    )
    {
        JsonObject providerPrerequisites = Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(
            targetStatus,
            "providerPrerequisites"
        );
        providerPrerequisites["status"]!.GetValue<string>().Should().Be(expectedStatus);
        providerPrerequisites["reason"]!.GetValue<string>().Should().Be(expectedReason);
    }

    public static void AssertCommandContract(
        JsonObject commandResult,
        string expectedCommand,
        string expectedStatus,
        string expectedClassification,
        bool expectedMutated,
        string? expectedLifecycle = null,
        bool? expectedCacheAheadRecoveryRequired = null
    )
    {
        commandResult["command"]!.GetValue<string>().Should().Be(expectedCommand);
        commandResult["status"]!.GetValue<string>().Should().Be(expectedStatus);
        commandResult["classification"]!.GetValue<string>().Should().Be(expectedClassification);
        commandResult["mutated"]!.GetValue<bool>().Should().Be(expectedMutated);

        if (expectedLifecycle is not null)
        {
            commandResult["lifecycle"]!.GetValue<string>().Should().Be(expectedLifecycle);
        }

        if (expectedCacheAheadRecoveryRequired is not null)
        {
            commandResult["cacheAheadRecoveryRequired"]!
                .GetValue<bool>()
                .Should()
                .Be(expectedCacheAheadRecoveryRequired.Value);
        }
    }
}
