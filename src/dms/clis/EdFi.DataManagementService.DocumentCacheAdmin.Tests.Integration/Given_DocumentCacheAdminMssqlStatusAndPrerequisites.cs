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
[Category("MssqlStatus")]
public sealed class Given_DocumentCacheAdminMssqlStatus
{
    [Test]
    public async Task It_returns_complete_status_with_satisfied_sqlserver_provider_prerequisites()
    {
        await using DocumentCacheAdminCliTarget target = await DocumentCacheAdminCliTarget.CreateMssqlAsync();
        await target.State.SetMssqlReadCommittedSnapshotAsync(enabled: true);

        await WithNestedTriggersAsync(
            target,
            enabled: true,
            async () =>
            {
                DocumentCacheAdminCliProcessResult result = await RunStatusAsync(target);

                JsonObject targetStatus = AssertStatusResult(result, target);
                Given_DocumentCacheAdminPostgresqlStatus.AssertCompleteStatusShape(targetStatus);
                AssertResolvedMssqlTarget(targetStatus);
                AssertProviderPrerequisite(
                    Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(
                        targetStatus,
                        "providerPrerequisites"
                    ),
                    expectedStatus: "satisfied",
                    expectedReason: "none"
                );
                AssertProviderPrerequisiteComponent(
                    Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(
                        Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(
                            targetStatus,
                            "providerPrerequisites"
                        ),
                        "sqlServerReadCommittedSnapshot"
                    ),
                    expectedStatus: "satisfied",
                    expectedReason: "none"
                );
                AssertProviderPrerequisiteComponent(
                    Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(
                        Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(
                            targetStatus,
                            "providerPrerequisites"
                        ),
                        "sqlServerNestedTriggers"
                    ),
                    expectedStatus: "satisfied",
                    expectedReason: "none"
                );
                Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(targetStatus, "lifecycle")["state"]!
                    .GetValue<string>()
                    .Should()
                    .Be("disabled");
                Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(targetStatus, "queueSummary")[
                    "presence"
                ]!
                    .GetValue<string>()
                    .Should()
                    .Be("empty");
                Given_DocumentCacheAdminPostgresqlStatus.AssertStandaloneRuntimeNotObserved(targetStatus);
            }
        );
    }

    [Test]
    public async Task It_surfaces_disabled_read_committed_snapshot_without_failing_status_output()
    {
        await using DocumentCacheAdminCliTarget target = await DocumentCacheAdminCliTarget.CreateMssqlAsync();
        await target.State.SetMssqlReadCommittedSnapshotAsync(enabled: false);

        try
        {
            await WithNestedTriggersAsync(
                target,
                enabled: true,
                async () =>
                {
                    DocumentCacheAdminCliProcessResult result = await RunStatusAsync(target);

                    JsonObject targetStatus = AssertStatusResult(result, target);
                    Given_DocumentCacheAdminPostgresqlStatus.AssertCompleteStatusShape(targetStatus);
                    AssertSqlServerPrerequisiteFailure(targetStatus);
                    JsonObject providerPrerequisites =
                        Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(
                            targetStatus,
                            "providerPrerequisites"
                        );
                    AssertProviderPrerequisite(
                        providerPrerequisites,
                        expectedStatus: "unsatisfied",
                        expectedReason: "disabled"
                    );
                    AssertProviderPrerequisiteComponent(
                        Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(
                            providerPrerequisites,
                            "sqlServerReadCommittedSnapshot"
                        ),
                        expectedStatus: "unsatisfied",
                        expectedReason: "disabled"
                    );
                    AssertProviderPrerequisiteComponent(
                        Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(
                            providerPrerequisites,
                            "sqlServerNestedTriggers"
                        ),
                        expectedStatus: "satisfied",
                        expectedReason: "none"
                    );
                    targetStatus["durableObservedAt"].Should().BeNull();
                    (await target.State.ReadMssqlReadCommittedSnapshotEnabledAsync()).Should().BeFalse();
                }
            );
        }
        finally
        {
            await target.State.SetMssqlReadCommittedSnapshotAsync(enabled: true);
        }
    }

    [Test]
    public async Task It_surfaces_disabled_nested_triggers_without_failing_status_output()
    {
        await using DocumentCacheAdminCliTarget target = await DocumentCacheAdminCliTarget.CreateMssqlAsync();
        await target.State.SetMssqlReadCommittedSnapshotAsync(enabled: true);

        await WithNestedTriggersAsync(
            target,
            enabled: false,
            async () =>
            {
                DocumentCacheAdminCliProcessResult result = await RunStatusAsync(target);

                JsonObject targetStatus = AssertStatusResult(result, target);
                Given_DocumentCacheAdminPostgresqlStatus.AssertCompleteStatusShape(targetStatus);
                AssertSqlServerPrerequisiteFailure(targetStatus);
                JsonObject providerPrerequisites = Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(
                    targetStatus,
                    "providerPrerequisites"
                );
                AssertProviderPrerequisite(
                    providerPrerequisites,
                    expectedStatus: "unsatisfied",
                    expectedReason: "disabled"
                );
                AssertProviderPrerequisiteComponent(
                    Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(
                        providerPrerequisites,
                        "sqlServerReadCommittedSnapshot"
                    ),
                    expectedStatus: "satisfied",
                    expectedReason: "none"
                );
                AssertProviderPrerequisiteComponent(
                    Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(
                        providerPrerequisites,
                        "sqlServerNestedTriggers"
                    ),
                    expectedStatus: "unsatisfied",
                    expectedReason: "disabled"
                );
                targetStatus["durableObservedAt"].Should().BeNull();
                (await target.State.ReadMssqlNestedTriggersEnabledAsync()).Should().BeFalse();
            }
        );
    }

    internal static async Task<DocumentCacheAdminCliProcessResult> RunStatusAsync(
        DocumentCacheAdminCliTarget target
    )
    {
        await using DocumentCacheAdminCliProcessHarness harness =
            await DocumentCacheAdminCliProcessHarness.CreateAsync(target);

        return await harness.RunAsync(
            DocumentCacheAdminCommandSurface.StatusCommandName,
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            target.DataStoreId.ToString(),
            DocumentCacheAdminCommandSurface.JsonOptionName,
            DocumentCacheAdminCommandSurface.StatusObservationTimeoutSecondsOptionName,
            "1",
            DocumentCacheAdminCommandSurface.StatusTimeoutSecondsOptionName,
            "5"
        );
    }

    internal static JsonObject AssertStatusResult(
        DocumentCacheAdminCliProcessResult result,
        DocumentCacheAdminCliTarget target
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

    internal static void AssertResolvedMssqlTarget(JsonObject targetStatus)
    {
        Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(targetStatus, "resolution")["status"]!
            .GetValue<string>()
            .Should()
            .Be("resolved");
        Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(targetStatus, "eligibility")["status"]!
            .GetValue<string>()
            .Should()
            .Be("unknown");
        Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(targetStatus, "eligibility")["reason"]!
            .GetValue<string>()
            .Should()
            .Be("runtimeNotObserved");
        targetStatus["provider"]!.GetValue<string>().Should().Be("sqlserver");
        targetStatus["physicalSourceFingerprint"]!.GetValue<string>().Should().StartWith("sha256:");
        Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(targetStatus, "inventory")["observedAt"]!
            .GetValue<string>()
            .Should()
            .EndWith("Z");
        Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(
            Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(targetStatus, "inventory"),
            "state"
        )["status"]!
            .GetValue<string>()
            .Should()
            .Be("valid");
        Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(
            Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(targetStatus, "inventory"),
            "work"
        )["status"]!
            .GetValue<string>()
            .Should()
            .Be("valid");
        Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(
            Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(targetStatus, "inventory"),
            "cache"
        )["status"]!
            .GetValue<string>()
            .Should()
            .Be("valid");
        Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(
            Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(targetStatus, "inventory"),
            "enqueueTrigger"
        )["status"]!
            .GetValue<string>()
            .Should()
            .Be("enabled");
        Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(targetStatus, "executionState")["status"]!
            .GetValue<string>()
            .Should()
            .Be("notObserved");
        targetStatus["activeCommand"].Should().BeNull();
        targetStatus["lastEndedDiagnostic"].Should().BeNull();
    }

    internal static void AssertSqlServerPrerequisiteFailure(JsonObject targetStatus)
    {
        Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(targetStatus, "resolution")["status"]!
            .GetValue<string>()
            .Should()
            .Be("resolved");
        Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(targetStatus, "eligibility")["status"]!
            .GetValue<string>()
            .Should()
            .Be("ineligible");
        Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(targetStatus, "eligibility")["reason"]!
            .GetValue<string>()
            .Should()
            .Be("sqlServerPrerequisiteFailed");
        Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(targetStatus, "operationalHealth")["status"]!
            .GetValue<string>()
            .Should()
            .Be("nonOperational");
        Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(targetStatus, "operationalHealth")["reason"]!
            .GetValue<string>()
            .Should()
            .Be("sqlServerPrerequisiteFailed");
    }

    internal static void AssertProviderPrerequisite(
        JsonObject prerequisite,
        string expectedStatus,
        string expectedReason
    )
    {
        prerequisite["status"]!.GetValue<string>().Should().Be(expectedStatus);
        prerequisite["reason"]!.GetValue<string>().Should().Be(expectedReason);

        if (expectedStatus == "satisfied")
        {
            prerequisite["observedAt"]!.GetValue<string>().Should().EndWith("Z");
        }
    }

    internal static void AssertProviderPrerequisiteComponent(
        JsonObject prerequisite,
        string expectedStatus,
        string expectedReason
    )
    {
        prerequisite["status"]!.GetValue<string>().Should().Be(expectedStatus);
        prerequisite["reason"]!.GetValue<string>().Should().Be(expectedReason);

        if (expectedStatus == "satisfied")
        {
            prerequisite["message"].Should().BeNull();
        }
        else
        {
            prerequisite["message"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        }
    }

    internal static async Task WithNestedTriggersAsync(
        DocumentCacheAdminCliTarget target,
        bool enabled,
        Func<Task> action
    )
    {
        bool originalNestedTriggersEnabled = await target.State.ReadMssqlNestedTriggersEnabledAsync();
        await target.State.SetMssqlNestedTriggersAsync(enabled);

        try
        {
            await action();
        }
        finally
        {
            await target.State.SetMssqlNestedTriggersAsync(originalNestedTriggersEnabled);
        }
    }
}

[TestFixture]
[NonParallelizable]
[Category("MssqlIntegration")]
[Category("MssqlActivationPrerequisite")]
public sealed class Given_DocumentCacheAdminMssqlActivationPrerequisite
{
    [Test]
    public async Task It_rejects_activation_when_read_committed_snapshot_is_disabled_without_mutation_then_succeeds_after_correction()
    {
        await using DocumentCacheAdminCliTarget target = await DocumentCacheAdminCliTarget.CreateMssqlAsync();

        await Given_DocumentCacheAdminMssqlStatus.WithNestedTriggersAsync(
            target,
            enabled: true,
            async () =>
            {
                await target.State.SetMssqlReadCommittedSnapshotAsync(enabled: false);
                DocumentCacheAdminCliProcessResult rejectionResult = await RunActivateNewEmptyAsync(target);

                JsonObject rejection = AssertActivationResult(
                    rejectionResult,
                    target,
                    DocumentCacheAdminExitCodes.RejectedNoMutation
                );
                rejection["status"]!.GetValue<string>().Should().Be("rejectedNoMutation");
                rejection["classification"]!.GetValue<string>().Should().Be("providerPrerequisiteFailed");
                rejection["mutated"]!.GetValue<bool>().Should().BeFalse();
                await AssertEmptyDisabledTargetAsync(target);

                await target.State.SetMssqlReadCommittedSnapshotAsync(enabled: true);
                DocumentCacheAdminCliProcessResult successResult = await RunActivateNewEmptyAsync(target);

                JsonObject success = AssertActivationResult(
                    successResult,
                    target,
                    DocumentCacheAdminExitCodes.Success
                );
                success["status"]!.GetValue<string>().Should().Be("completed");
                success["classification"]!.GetValue<string>().Should().Be("succeeded");
                success["mutated"]!.GetValue<bool>().Should().BeTrue();
                success["lifecycle"]!.GetValue<string>().Should().Be("tracking");

                DocumentCacheAdminCliLifecycleState lifecycle = await target.State.ReadLifecycleAsync();
                lifecycle.ProjectionLifecycleState.Should().Be("Tracking");
                lifecycle.CacheAheadRecoveryRequired.Should().BeFalse();
                DocumentCacheAdminCliMutableCounts counts = await target.State.ReadMutableCountsAsync();
                counts.DocumentCacheRows.Should().Be(0);
                counts.WorkRows.Should().Be(0);
            }
        );
    }

    [Test]
    public async Task It_rejects_activation_when_nested_triggers_are_disabled_without_mutation()
    {
        await using DocumentCacheAdminCliTarget target = await DocumentCacheAdminCliTarget.CreateMssqlAsync();
        await target.State.SetMssqlReadCommittedSnapshotAsync(enabled: true);

        await Given_DocumentCacheAdminMssqlStatus.WithNestedTriggersAsync(
            target,
            enabled: false,
            async () =>
            {
                DocumentCacheAdminCliProcessResult result = await RunActivateNewEmptyAsync(target);

                JsonObject commandResult = AssertActivationResult(
                    result,
                    target,
                    DocumentCacheAdminExitCodes.RejectedNoMutation
                );
                commandResult["status"]!.GetValue<string>().Should().Be("rejectedNoMutation");
                commandResult["classification"]!.GetValue<string>().Should().Be("providerPrerequisiteFailed");
                commandResult["mutated"]!.GetValue<bool>().Should().BeFalse();
                await AssertEmptyDisabledTargetAsync(target);
                (await target.State.ReadMssqlNestedTriggersEnabledAsync()).Should().BeFalse();
            }
        );
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
            DocumentCacheAdminCommandSurface.JsonOptionName,
            DocumentCacheAdminCommandSurface.CommandTimeoutSecondsOptionName,
            "30"
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
