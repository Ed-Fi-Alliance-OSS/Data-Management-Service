// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.DocumentCacheAdmin;
using FluentAssertions;
using Npgsql;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("PostgresqlIntegration")]
[Category("PostgresqlStatus")]
public sealed class Given_DocumentCacheAdminPostgresqlStatus
{
    [Test]
    public async Task It_returns_complete_status_for_a_resolved_tracking_target()
    {
        await using DocumentCacheAdminCliTarget target =
            await DocumentCacheAdminCliTarget.CreatePostgresqlAsync();
        await target.State.SetLifecycleAsync("Tracking", cacheAheadRecoveryRequired: false);

        DocumentCacheAdminCliProcessResult result = await RunStatusAsync(target);

        JsonObject targetStatus = AssertStatusResult(result, target, expectedDataStoreId: target.DataStoreId);
        AssertCompleteStatusShape(targetStatus);
        AssertResolvedPostgresqlTarget(targetStatus);
        RequiredObject(targetStatus, "lifecycle")["state"]!.GetValue<string>().Should().Be("tracking");
        RequiredObject(targetStatus, "cacheAhead")["state"]!.GetValue<string>().Should().Be("clear");
        RequiredObject(targetStatus, "cacheAhead")["recoveryRequired"]!.GetValue<bool>().Should().BeFalse();
        RequiredObject(targetStatus, "queueSummary")["presence"]!.GetValue<string>().Should().Be("empty");
        RequiredObject(targetStatus, "queueSummary")["oldestWorkFirstEnqueuedAt"].Should().BeNull();
        RequiredObject(targetStatus, "queueSummary")["oldestWorkAgeSeconds"].Should().BeNull();
        AssertStandaloneRuntimeNotObserved(targetStatus);
    }

    [Test]
    public async Task It_returns_complete_status_for_an_unresolved_target()
    {
        await using DocumentCacheAdminCliTarget target =
            await DocumentCacheAdminCliTarget.CreatePostgresqlAsync();
        long missingDataStoreId = target.DataStoreId + 1;

        DocumentCacheAdminCliProcessResult result = await RunStatusAsync(target, missingDataStoreId);

        JsonObject targetStatus = AssertStatusResult(result, target, expectedDataStoreId: missingDataStoreId);
        AssertCompleteStatusShape(targetStatus);
        RequiredObject(targetStatus, "resolution")["status"]!.GetValue<string>().Should().Be("unresolved");
        RequiredObject(targetStatus, "resolution")["reason"]!
            .GetValue<string>()
            .Should()
            .Be("targetNotFound");
        RequiredObject(targetStatus, "eligibility")["status"]!.GetValue<string>().Should().Be("ineligible");
        targetStatus["durableObservedAt"].Should().BeNull();
        targetStatus["provider"].Should().BeNull();
        targetStatus["physicalSourceFingerprint"].Should().BeNull();
        RequiredObject(targetStatus, "lifecycle")["state"]!.GetValue<string>().Should().Be("unknown");
        RequiredObject(targetStatus, "queueSummary")["presence"]!
            .GetValue<string>()
            .Should()
            .Be("unavailable");
        RequiredObject(targetStatus, "operationalHealth")["status"]!
            .GetValue<string>()
            .Should()
            .Be("nonOperational");
    }

    [Test]
    public async Task It_returns_complete_status_for_non_operational_durable_state()
    {
        await using DocumentCacheAdminCliTarget target =
            await DocumentCacheAdminCliTarget.CreatePostgresqlAsync();

        DocumentCacheAdminCliProcessResult result = await RunStatusAsync(target);

        JsonObject targetStatus = AssertStatusResult(result, target, expectedDataStoreId: target.DataStoreId);
        AssertCompleteStatusShape(targetStatus);
        AssertResolvedPostgresqlTarget(targetStatus);
        RequiredObject(targetStatus, "lifecycle")["state"]!.GetValue<string>().Should().Be("disabled");
        RequiredObject(targetStatus, "lifecycle")["availability"]!
            .GetValue<string>()
            .Should()
            .Be("available");
        RequiredObject(targetStatus, "queueSummary")["presence"]!.GetValue<string>().Should().Be("empty");
        targetStatus["durableObservedAt"]!.GetValue<string>().Should().EndWith("Z");
        AssertStandaloneRuntimeNotObserved(targetStatus);
    }

    [Test]
    public async Task It_returns_complete_status_with_direct_durable_queue_facts_for_not_caught_up_state()
    {
        await using DocumentCacheAdminCliTarget target =
            await DocumentCacheAdminCliTarget.CreatePostgresqlAsync();
        DocumentCacheAdminCliSeededDocument document =
            await target.State.InsertPostgresqlCanonicalDocumentAsync(contentVersion: 20);
        await target.State.SetLifecycleAsync("Tracking", cacheAheadRecoveryRequired: false);
        DateTimeOffset firstEnqueuedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await target.State.InsertPostgresqlProjectionWorkAsync(document, firstEnqueuedAt);

        DocumentCacheAdminCliProcessResult result = await RunStatusAsync(target);

        JsonObject targetStatus = AssertStatusResult(result, target, expectedDataStoreId: target.DataStoreId);
        AssertCompleteStatusShape(targetStatus);
        AssertResolvedPostgresqlTarget(targetStatus);
        JsonObject queueSummary = RequiredObject(targetStatus, "queueSummary");
        queueSummary["presence"]!.GetValue<string>().Should().Be("notEmpty");
        queueSummary["oldestWorkFirstEnqueuedAt"]!.GetValue<string>().Should().EndWith("Z");
        queueSummary["oldestWorkAgeSeconds"]!.GetValue<double>().Should().BeGreaterThanOrEqualTo(0);
        RequiredObject(queueSummary, "backlogEstimate")["kind"]!
            .GetValue<string>()
            .Should()
            .Be("unavailable");
        AssertStandaloneRuntimeNotObserved(targetStatus);
    }

    [Test]
    public async Task It_returns_complete_unknown_status_when_direct_durable_observation_times_out()
    {
        await using DocumentCacheAdminCliTarget target =
            await DocumentCacheAdminCliTarget.CreatePostgresqlAsync();
        await target.State.SetLifecycleAsync("Tracking", cacheAheadRecoveryRequired: false);
        await using NpgsqlConnection connection = new(target.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """LOCK TABLE "dms"."DocumentProjectionWork" IN ACCESS EXCLUSIVE MODE;""";
        await command.ExecuteNonQueryAsync();

        try
        {
            DocumentCacheAdminCliProcessResult result = await RunStatusAsync(
                target,
                target.DataStoreId,
                statusObservationTimeoutSeconds: "0.1",
                statusTimeoutSeconds: "3"
            );

            JsonObject targetStatus = AssertStatusResult(
                result,
                target,
                expectedDataStoreId: target.DataStoreId
            );
            AssertCompleteStatusShape(targetStatus);
            AssertResolvedPostgresqlTarget(targetStatus);
            targetStatus["durableObservedAt"].Should().BeNull();
            RequiredObject(targetStatus, "lifecycle")["state"]!.GetValue<string>().Should().Be("unknown");
            RequiredObject(targetStatus, "cacheAhead")["state"]!.GetValue<string>().Should().Be("unknown");
            RequiredObject(targetStatus, "queueSummary")["presence"]!
                .GetValue<string>()
                .Should()
                .Be("unknown");
            AssertStandaloneRuntimeNotObserved(targetStatus);
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private static async Task<DocumentCacheAdminCliProcessResult> RunStatusAsync(
        DocumentCacheAdminCliTarget target,
        long? dataStoreId = null,
        string statusObservationTimeoutSeconds = "1",
        string statusTimeoutSeconds = "5"
    )
    {
        await using DocumentCacheAdminCliProcessHarness harness =
            await DocumentCacheAdminCliProcessHarness.CreateAsync(target);

        return await harness.RunAsync(
            DocumentCacheAdminCommandSurface.StatusCommandName,
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            (dataStoreId ?? target.DataStoreId).ToString(),
            DocumentCacheAdminCommandSurface.JsonOptionName,
            DocumentCacheAdminCommandSurface.StatusObservationTimeoutSecondsOptionName,
            statusObservationTimeoutSeconds,
            DocumentCacheAdminCommandSurface.StatusTimeoutSecondsOptionName,
            statusTimeoutSeconds
        );
    }

    private static JsonObject AssertStatusResult(
        DocumentCacheAdminCliProcessResult result,
        DocumentCacheAdminCliTarget target,
        long expectedDataStoreId
    )
    {
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
        RequiredObject(targetStatus, "targetKey")["tenantKey"]!
            .GetValue<string>()
            .Should()
            .Be(target.TenantKey);
        RequiredObject(targetStatus, "targetKey")["dataStoreId"]!
            .GetValue<long>()
            .Should()
            .Be(expectedDataStoreId);
        return targetStatus;
    }

    internal static void AssertCompleteStatusShape(JsonObject targetStatus)
    {
        string[] requiredProperties =
        [
            "targetKey",
            "targetGeneration",
            "processObservedAt",
            "durableObservedAt",
            "provider",
            "physicalSourceFingerprint",
            "resolution",
            "eligibility",
            "inventory",
            "providerPrerequisites",
            "lifecycle",
            "cacheAhead",
            "operationalHealth",
            "caughtUp",
            "queueSummary",
            "executionState",
            "activeCommand",
            "lastEndedDiagnostic",
            "targetDiagnostics",
            "documentDiagnostics",
            "poisonTraversalDiagnostics",
            "effectiveSettings",
            "enqueueFailures",
        ];

        foreach (string propertyName in requiredProperties)
        {
            targetStatus.ContainsKey(propertyName).Should().BeTrue($"{propertyName} is part of the v1 shape");
        }

        JsonObject inventory = RequiredObject(targetStatus, "inventory");
        RequiredObject(inventory, "state");
        RequiredObject(inventory, "work");
        RequiredObject(inventory, "cache");
        RequiredObject(inventory, "dataStoreIdentity");
        RequiredObject(inventory, "enqueueTrigger");

        JsonObject providerPrerequisites = RequiredObject(targetStatus, "providerPrerequisites");
        RequiredObject(providerPrerequisites, "sqlServerReadCommittedSnapshot");
        RequiredObject(providerPrerequisites, "sqlServerNestedTriggers");

        AssertDiagnosticWindow(targetStatus, "targetDiagnostics");
        AssertDiagnosticWindow(targetStatus, "documentDiagnostics");
        AssertDiagnosticWindow(targetStatus, "poisonTraversalDiagnostics");

        JsonObject effectiveSettings = RequiredObject(targetStatus, "effectiveSettings");
        RequiredObject(effectiveSettings, "projector");
        RequiredObject(effectiveSettings, "readAcceleration");
        RequiredObject(effectiveSettings, "status");
        effectiveSettings.ContainsKey("administration").Should().BeFalse();

        JsonObject enqueueFailures = RequiredObject(targetStatus, "enqueueFailures");
        RequiredArray(enqueueFailures, "recentEvents");
        RequiredArray(enqueueFailures, "byCategory");
        enqueueFailures["evictedCount"]!.GetValue<int>().Should().BeGreaterThanOrEqualTo(0);
    }

    internal static void AssertResolvedPostgresqlTarget(JsonObject targetStatus)
    {
        RequiredObject(targetStatus, "resolution")["status"]!.GetValue<string>().Should().Be("resolved");
        RequiredObject(targetStatus, "eligibility")["status"]!.GetValue<string>().Should().Be("unknown");
        RequiredObject(targetStatus, "eligibility")["reason"]!
            .GetValue<string>()
            .Should()
            .Be("runtimeNotObserved");
        targetStatus["provider"]!.GetValue<string>().Should().Be("postgresql");
        targetStatus["physicalSourceFingerprint"]!.GetValue<string>().Should().StartWith("sha256:");
        RequiredObject(targetStatus, "inventory")["observedAt"]!.GetValue<string>().Should().EndWith("Z");
        RequiredObject(RequiredObject(targetStatus, "inventory"), "state")["status"]!
            .GetValue<string>()
            .Should()
            .Be("valid");
        RequiredObject(RequiredObject(targetStatus, "inventory"), "work")["status"]!
            .GetValue<string>()
            .Should()
            .Be("valid");
        RequiredObject(RequiredObject(targetStatus, "inventory"), "cache")["status"]!
            .GetValue<string>()
            .Should()
            .Be("valid");
        RequiredObject(RequiredObject(targetStatus, "inventory"), "enqueueTrigger")["status"]!
            .GetValue<string>()
            .Should()
            .Be("enabled");
        RequiredObject(targetStatus, "providerPrerequisites")["status"]!
            .GetValue<string>()
            .Should()
            .Be("satisfied");
        RequiredObject(targetStatus, "executionState")["status"]!
            .GetValue<string>()
            .Should()
            .Be("notObserved");
        targetStatus["activeCommand"].Should().BeNull();
        targetStatus["lastEndedDiagnostic"].Should().BeNull();
    }

    internal static void AssertStandaloneRuntimeNotObserved(JsonObject targetStatus)
    {
        RequiredObject(targetStatus, "operationalHealth")["status"]!
            .GetValue<string>()
            .Should()
            .Be("unknown");
        RequiredObject(targetStatus, "operationalHealth")["reason"]!
            .GetValue<string>()
            .Should()
            .Be("runtimeNotObserved");
        RequiredObject(targetStatus, "caughtUp")["status"]!.GetValue<string>().Should().Be("unknown");
        RequiredObject(targetStatus, "caughtUp")["reason"]!
            .GetValue<string>()
            .Should()
            .Be("runtimeNotObserved");
    }

    internal static JsonObject RequiredObject(JsonObject parent, string propertyName)
    {
        parent.ContainsKey(propertyName).Should().BeTrue();
        return parent[propertyName] as JsonObject
            ?? throw new AssertionException($"Expected '{propertyName}' to be a JSON object.");
    }

    internal static JsonArray RequiredArray(JsonObject parent, string propertyName)
    {
        parent.ContainsKey(propertyName).Should().BeTrue();
        return parent[propertyName] as JsonArray
            ?? throw new AssertionException($"Expected '{propertyName}' to be a JSON array.");
    }

    private static void AssertDiagnosticWindow(JsonObject targetStatus, string propertyName)
    {
        JsonObject diagnostics = RequiredObject(targetStatus, propertyName);
        RequiredArray(diagnostics, "recentEvents");
        diagnostics["evictedCount"]!.GetValue<int>().Should().BeGreaterThanOrEqualTo(0);
    }
}

[TestFixture]
[NonParallelizable]
[Category("PostgresqlIntegration")]
[Category("PostgresqlActivateNewEmpty")]
public sealed class Given_DocumentCacheAdminPostgresqlActivateNewEmpty
{
    [Test]
    public async Task It_activates_an_empty_disabled_target()
    {
        await using DocumentCacheAdminCliTarget target =
            await DocumentCacheAdminCliTarget.CreatePostgresqlAsync();

        DocumentCacheAdminCliProcessResult result = await RunActivateNewEmptyAsync(target);

        JsonObject commandResult = AssertActivationResult(
            result,
            target,
            DocumentCacheAdminExitCodes.Success
        );
        commandResult["command"]!.GetValue<string>().Should().Be("guardedNewEmptyActivation");
        commandResult["status"]!.GetValue<string>().Should().Be("completed");
        commandResult["classification"]!.GetValue<string>().Should().Be("succeeded");
        commandResult["mutated"]!.GetValue<bool>().Should().BeTrue();
        commandResult["lifecycle"]!.GetValue<string>().Should().Be("tracking");
        commandResult["cacheAheadRecoveryRequired"]!.GetValue<bool>().Should().BeFalse();

        DocumentCacheAdminCliLifecycleState lifecycle = await target.State.ReadLifecycleAsync();
        lifecycle.ProjectionLifecycleState.Should().Be("Tracking");
        lifecycle.CacheAheadRecoveryRequired.Should().BeFalse();
        (await target.State.ReadCanonicalDocumentCountAsync()).Should().Be(0);
        DocumentCacheAdminCliMutableCounts counts = await target.State.ReadMutableCountsAsync();
        counts.DocumentCacheRows.Should().Be(0);
        counts.WorkRows.Should().Be(0);
    }

    [Test]
    public async Task It_rejects_nonempty_state_without_lifecycle_cache_work_or_latch_mutation()
    {
        await using DocumentCacheAdminCliTarget target =
            await DocumentCacheAdminCliTarget.CreatePostgresqlAsync();
        DocumentCacheAdminCliSeededDocument document =
            await target.State.InsertPostgresqlCanonicalDocumentAsync(contentVersion: 30);
        await target.State.InsertPostgresqlDocumentCacheAsync(document);
        await target.State.InsertPostgresqlProjectionWorkAsync(document);

        DocumentCacheAdminCliProcessResult result = await RunActivateNewEmptyAsync(target);

        JsonObject commandResult = AssertActivationResult(
            result,
            target,
            DocumentCacheAdminExitCodes.RejectedNoMutation
        );
        commandResult["status"]!.GetValue<string>().Should().Be("rejectedNoMutation");
        commandResult["classification"]!.GetValue<string>().Should().Be("nonemptyGuardedActivationState");
        commandResult["mutated"]!.GetValue<bool>().Should().BeFalse();
        commandResult["lifecycle"]!.GetValue<string>().Should().Be("disabled");
        commandResult["phaseDiagnostics"]!.AsArray().Should().NotBeEmpty();

        DocumentCacheAdminCliLifecycleState lifecycle = await target.State.ReadLifecycleAsync();
        lifecycle.ProjectionLifecycleState.Should().Be("Disabled");
        lifecycle.CacheAheadRecoveryRequired.Should().BeFalse();
        (await target.State.ReadCanonicalDocumentCountAsync()).Should().Be(1);
        DocumentCacheAdminCliMutableCounts counts = await target.State.ReadMutableCountsAsync();
        counts.DocumentCacheRows.Should().Be(1);
        counts.WorkRows.Should().Be(1);
    }

    [Test]
    public async Task It_rejects_a_racing_canonical_insert_that_commits_before_activation_completes()
    {
        await using DocumentCacheAdminCliTarget target =
            await DocumentCacheAdminCliTarget.CreatePostgresqlAsync();
        await using DocumentCacheAdminCliPostgresqlInsertTransaction insertTransaction =
            await target.State.BeginPostgresqlCanonicalInsertTransactionAsync(contentVersion: 40);
        await using DocumentCacheAdminCliProcessHarness harness =
            await DocumentCacheAdminCliProcessHarness.CreateAsync(target);

        Task<DocumentCacheAdminCliProcessResult> activationTask = harness.RunAsync(
            DocumentCacheAdminCommandSurface.ActivateNewEmptyCommandName,
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            target.DataStoreId.ToString(),
            DocumentCacheAdminCommandSurface.ConfirmOptionName,
            "newEmptyActivation",
            DocumentCacheAdminCommandSurface.JsonOptionName,
            DocumentCacheAdminCommandSurface.CommandTimeoutSecondsOptionName,
            "30"
        );

        await Task.Delay(TimeSpan.FromMilliseconds(250));
        await insertTransaction.CommitAsync();

        DocumentCacheAdminCliProcessResult result = await activationTask.WaitAsync(TimeSpan.FromSeconds(120));
        JsonObject commandResult = AssertActivationResult(
            result,
            target,
            DocumentCacheAdminExitCodes.RejectedNoMutation
        );
        commandResult["classification"]!.GetValue<string>().Should().Be("nonemptyGuardedActivationState");
        commandResult["mutated"]!.GetValue<bool>().Should().BeFalse();

        DocumentCacheAdminCliLifecycleState lifecycle = await target.State.ReadLifecycleAsync();
        lifecycle.ProjectionLifecycleState.Should().Be("Disabled");
        (await target.State.ReadCanonicalDocumentCountAsync()).Should().Be(1);
        DocumentCacheAdminCliMutableCounts counts = await target.State.ReadMutableCountsAsync();
        counts.DocumentCacheRows.Should().Be(0);
        counts.WorkRows.Should().Be(0);
    }

    private static async Task<DocumentCacheAdminCliProcessResult> RunActivateNewEmptyAsync(
        DocumentCacheAdminCliTarget target
    )
    {
        await using DocumentCacheAdminCliProcessHarness harness =
            await DocumentCacheAdminCliProcessHarness.CreateAsync(target);

        return await harness.RunAsync(
            DocumentCacheAdminCommandSurface.ActivateNewEmptyCommandName,
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            target.DataStoreId.ToString(),
            DocumentCacheAdminCommandSurface.ConfirmOptionName,
            "newEmptyActivation",
            DocumentCacheAdminCommandSurface.JsonOptionName
        );
    }

    private static JsonObject AssertActivationResult(
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
