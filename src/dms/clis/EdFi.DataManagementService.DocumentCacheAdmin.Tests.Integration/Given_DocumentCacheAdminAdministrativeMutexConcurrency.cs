// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.DocumentCacheAdmin;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Npgsql;
using static EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration.DocumentCacheAdminAdministrativeMutexConcurrencySupport;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("PostgresqlIntegration")]
[Category("Mutex")]
public sealed class Given_DocumentCacheAdminPostgresqlAdministrativeMutex
{
    [Test]
    public async Task It_serializes_aliases_of_the_same_physical_database_through_the_shared_mutex()
    {
        await using DocumentCacheAdminCliTarget target =
            await DocumentCacheAdminCliTarget.CreatePostgresqlAsync();
        await using DocumentCacheAdminCliTarget aliasTarget = target.CreateAlias(
            dataStoreId: 2,
            tenantKey: "alias-tenant",
            PostgresqlAliasConnectionString(target.ConnectionString)
        );

        DocumentCacheAdminCliPostgresqlInsertTransaction? blockingInsert =
            await target.State.BeginPostgresqlCanonicalInsertTransactionAsync();

        try
        {
            await using DocumentCacheAdminCliProcessHarness ownerHarness =
                await DocumentCacheAdminCliProcessHarness.CreateAsync(target);
            await using DocumentCacheAdminCliProcessHarness contenderHarness =
                await DocumentCacheAdminCliProcessHarness.CreateAsync(aliasTarget);

            await using DocumentCacheAdminCliRunningProcess ownerProcess = StartActivateNewEmpty(
                ownerHarness,
                target
            );
            await WaitForAdministrativeMutexOwnerAsync(target, ownerProcess);

            await using DocumentCacheAdminCliRunningProcess contenderProcess = StartScrub(
                contenderHarness,
                aliasTarget
            );
            await WaitForAdministrativeMutexWaiterAsync(target, contenderProcess);
            await AssertStillRunningAsync(
                contenderProcess,
                "aliases of the same physical PostgreSQL database must wait on the shared mutex"
            );

            (await target.State.ReadAdministrativeMutexGrantedCountAsync()).Should().Be(1);

            await blockingInsert.DisposeAsync();
            blockingInsert = null;

            AssertActivationSucceeded(await ownerProcess.WaitForExitAsync(TimeSpan.FromSeconds(60)), target);
            AssertScrubSucceeded(
                await contenderProcess.WaitForExitAsync(TimeSpan.FromSeconds(60)),
                aliasTarget
            );
        }
        finally
        {
            if (blockingInsert is not null)
            {
                await blockingInsert.DisposeAsync();
            }
        }
    }

    [Test]
    [Category("Cancellation")]
    public async Task It_returns_Cancellation_result_when_mutex_acquisition_wait_exceeds_command_timeout()
    {
        await using DocumentCacheAdminCliTarget target =
            await DocumentCacheAdminCliTarget.CreatePostgresqlAsync();
        DocumentCacheAdminCliPostgresqlInsertTransaction? blockingInsert =
            await target.State.BeginPostgresqlCanonicalInsertTransactionAsync();

        try
        {
            await using DocumentCacheAdminCliProcessHarness ownerHarness =
                await DocumentCacheAdminCliProcessHarness.CreateAsync(target);
            await using DocumentCacheAdminCliProcessHarness contenderHarness =
                await DocumentCacheAdminCliProcessHarness.CreateAsync(target);

            await using DocumentCacheAdminCliRunningProcess ownerProcess = StartActivateNewEmpty(
                ownerHarness,
                target
            );
            await WaitForAdministrativeMutexOwnerAsync(target, ownerProcess);

            await using DocumentCacheAdminCliRunningProcess contenderProcess = StartScrub(
                contenderHarness,
                target,
                commandTimeoutSeconds: "1"
            );
            await WaitForAdministrativeMutexWaiterAsync(target, contenderProcess);

            DocumentCacheAdminCliProcessResult timeoutResult = await contenderProcess.WaitForExitAsync(
                TimeSpan.FromSeconds(30)
            );
            JsonObject commandResult = DocumentCacheAdminCliCommandResultAssertions.AssertCommandResult(
                timeoutResult,
                target,
                DocumentCacheAdminExitCodes.FailedNoMutation
            );
            commandResult["command"]!.GetValue<string>().Should().Be("explicitIntegrityScrub");
            commandResult["status"]!.GetValue<string>().Should().Be("failedNoMutation");
            commandResult["classification"]!.GetValue<string>().Should().Be("mutexAcquisitionCancelled");
            commandResult["mutated"]!.GetValue<bool>().Should().BeFalse();
            commandResult["elapsedCommandTimeSeconds"].Should().BeNull();
            AssertPhaseDiagnostic(
                commandResult,
                expectedCategory: "mutexAcquisitionCancelled",
                expectedPhase: "acquireMutex",
                expectedRetryable: false
            );

            await blockingInsert.DisposeAsync();
            blockingInsert = null;

            AssertActivationSucceeded(await ownerProcess.WaitForExitAsync(TimeSpan.FromSeconds(60)), target);
        }
        finally
        {
            if (blockingInsert is not null)
            {
                await blockingInsert.DisposeAsync();
            }
        }
    }

    [Test]
    public async Task It_allows_different_physical_databases_on_the_same_cluster_to_administer_concurrently()
    {
        await using DocumentCacheAdminCliTarget blockedTarget =
            await DocumentCacheAdminCliTarget.CreatePostgresqlAsync();
        await using DocumentCacheAdminCliTarget independentTarget =
            await DocumentCacheAdminCliTarget.CreatePostgresqlAsync();
        DocumentCacheAdminCliPostgresqlInsertTransaction? blockingInsert =
            await blockedTarget.State.BeginPostgresqlCanonicalInsertTransactionAsync();

        try
        {
            await using DocumentCacheAdminCliProcessHarness ownerHarness =
                await DocumentCacheAdminCliProcessHarness.CreateAsync(blockedTarget);
            await using DocumentCacheAdminCliProcessHarness independentHarness =
                await DocumentCacheAdminCliProcessHarness.CreateAsync(independentTarget);

            await using DocumentCacheAdminCliRunningProcess ownerProcess = StartActivateNewEmpty(
                ownerHarness,
                blockedTarget
            );
            await WaitForAdministrativeMutexOwnerAsync(blockedTarget, ownerProcess);

            await using DocumentCacheAdminCliRunningProcess independentProcess = StartActivateNewEmpty(
                independentHarness,
                independentTarget
            );

            AssertActivationSucceeded(
                await independentProcess.WaitForExitAsync(TimeSpan.FromSeconds(60)),
                independentTarget
            );
            await AssertStillRunningAsync(
                ownerProcess,
                "a PostgreSQL command blocked after mutex acquisition should not block independent databases"
            );
            (await blockedTarget.State.ReadAdministrativeMutexGrantedCountAsync()).Should().Be(1);

            await blockingInsert.DisposeAsync();
            blockingInsert = null;

            AssertActivationSucceeded(
                await ownerProcess.WaitForExitAsync(TimeSpan.FromSeconds(60)),
                blockedTarget
            );
        }
        finally
        {
            if (blockingInsert is not null)
            {
                await blockingInsert.DisposeAsync();
            }
        }
    }

    private static string PostgresqlAliasConnectionString(string connectionString)
    {
        NpgsqlConnectionStringBuilder builder = new(connectionString)
        {
            Host = LoopbackHostAlias(new NpgsqlConnectionStringBuilder(connectionString).Host),
            ApplicationName = "document-cache-admin-cli-mutex-alias",
        };

        return builder.ConnectionString;
    }
}

[TestFixture]
[NonParallelizable]
[Category("PostgresqlIntegration")]
[Category("RetryableIncomplete")]
public sealed class Given_DocumentCacheAdminPostgresqlRetryableIncompleteWorkflows
{
    [Test]
    public async Task It_serializes_retryable_incomplete_rebuild_status_and_reissue_resumes_the_workflow()
    {
        await using DocumentCacheAdminCliTarget target =
            await DocumentCacheAdminCliTarget.CreatePostgresqlAsync();
        DocumentCacheAdminCliSeededDocument document =
            await target.State.InsertPostgresqlDescriptorDocumentAsync(
                "RetryableIncomplete",
                contentVersion: 31
            );
        await target.State.InsertPostgresqlDocumentCacheAsync(
            document,
            documentJson: """{"value":"stale-cache-before-timeout"}"""
        );
        await target.State.InsertPostgresqlProjectionWorkAsync(document);
        await target.State.SetLifecycleAsync("Tracking", cacheAheadRecoveryRequired: false);

        DocumentCacheAdminCliPostgresqlDocumentCacheLockTransaction? cacheLock =
            await target.State.BeginPostgresqlDocumentCacheLockTransactionAsync(document.DocumentId);

        try
        {
            await using DocumentCacheAdminCliProcessHarness harness =
                await DocumentCacheAdminCliProcessHarness.CreateAsync(target);
            await using DocumentCacheAdminCliRunningProcess timedOutProcess = StartRebuildOnline(
                harness,
                target,
                commandTimeoutSeconds: "10"
            );

            await WaitForLifecycleStateAsync(target, timedOutProcess, "Resetting");
            await WaitForRetryableIncompleteRebuildTimeoutAsync(
                timedOutProcess,
                target,
                TimeSpan.FromSeconds(60)
            );
            await cacheLock.DisposeAsync();
            cacheLock = null;

            DocumentCacheAdminCliLifecycleState interruptedLifecycle =
                await target.State.ReadLifecycleAsync();
            interruptedLifecycle.ProjectionLifecycleState.Should().Be("Resetting");
            interruptedLifecycle.CacheAheadRecoveryRequired.Should().BeFalse();
            DocumentCacheAdminCliMutableCounts interruptedCounts =
                await target.State.ReadMutableCountsAsync();
            interruptedCounts.DocumentCacheRows.Should().Be(1);
            interruptedCounts.WorkRows.Should().Be(1);

            JsonObject interruptedStatus = await RunStatusAndReadTargetAsync(harness, target);
            AssertRetryableIncompleteRebuildStatus(interruptedStatus);

            DocumentCacheAdminCliProcessResult retryResult = await RunRebuildOnlineAsync(harness, target);
            JsonObject completedResult = DocumentCacheAdminCliCommandResultAssertions.AssertCommandResult(
                retryResult,
                target,
                DocumentCacheAdminExitCodes.Success
            );
            completedResult["status"]!.GetValue<string>().Should().Be("completed");
            completedResult["classification"]!.GetValue<string>().Should().Be("succeeded");
            completedResult["mutated"]!.GetValue<bool>().Should().BeTrue();
            completedResult["lifecycle"]!.GetValue<string>().Should().Be("tracking");

            DocumentCacheAdminCliLifecycleState lifecycle = await target.State.ReadLifecycleAsync();
            lifecycle.ProjectionLifecycleState.Should().Be("Tracking");
            lifecycle.CacheAheadRecoveryRequired.Should().BeFalse();
            DocumentCacheAdminCliMutableCounts counts = await target.State.ReadMutableCountsAsync();
            counts.DocumentCacheRows.Should().Be(1);
            counts.WorkRows.Should().Be(0);
        }
        finally
        {
            if (cacheLock is not null)
            {
                await cacheLock.DisposeAsync();
            }
        }
    }
}

[TestFixture]
[NonParallelizable]
[Category("MssqlIntegration")]
[Category("RetryableIncomplete")]
public sealed class Given_DocumentCacheAdminMssqlRetryableIncompleteWorkflows
{
    [Test]
    public async Task It_serializes_retryable_incomplete_rebuild_status_and_reissue_resumes_the_workflow()
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
                        "MssqlRetryableIncomplete",
                        contentVersion: 31
                    );
                await target.State.InsertMssqlDocumentCacheAsync(
                    document,
                    documentJson: """{"value":"stale-cache-before-timeout"}"""
                );
                await target.State.InsertMssqlProjectionWorkAsync(document);
                await target.State.SetLifecycleAsync("Tracking", cacheAheadRecoveryRequired: false);

                DocumentCacheAdminCliMssqlDocumentCacheLockTransaction? cacheLock =
                    await target.State.BeginMssqlDocumentCacheLockTransactionAsync(document.DocumentId);

                try
                {
                    await using DocumentCacheAdminCliProcessHarness harness =
                        await DocumentCacheAdminCliProcessHarness.CreateAsync(target);
                    await using DocumentCacheAdminCliRunningProcess timedOutProcess = StartRebuildOnline(
                        harness,
                        target,
                        commandTimeoutSeconds: "10"
                    );

                    await WaitForLifecycleStateAsync(target, timedOutProcess, "Resetting");
                    await WaitForRetryableIncompleteRebuildTimeoutAsync(
                        timedOutProcess,
                        target,
                        TimeSpan.FromSeconds(60)
                    );
                    await cacheLock.DisposeAsync();
                    cacheLock = null;

                    DocumentCacheAdminCliLifecycleState interruptedLifecycle =
                        await target.State.ReadLifecycleAsync();
                    interruptedLifecycle.ProjectionLifecycleState.Should().Be("Resetting");
                    interruptedLifecycle.CacheAheadRecoveryRequired.Should().BeFalse();
                    DocumentCacheAdminCliMutableCounts interruptedCounts =
                        await target.State.ReadMutableCountsAsync();
                    interruptedCounts.DocumentCacheRows.Should().Be(1);
                    interruptedCounts.WorkRows.Should().Be(1);

                    JsonObject interruptedStatus = await RunStatusAndReadTargetAsync(harness, target);
                    AssertRetryableIncompleteRebuildStatus(interruptedStatus);

                    DocumentCacheAdminCliProcessResult retryResult = await RunRebuildOnlineAsync(
                        harness,
                        target
                    );
                    JsonObject completedResult =
                        DocumentCacheAdminCliCommandResultAssertions.AssertCommandResult(
                            retryResult,
                            target,
                            DocumentCacheAdminExitCodes.Success
                        );
                    completedResult["status"]!.GetValue<string>().Should().Be("completed");
                    completedResult["classification"]!.GetValue<string>().Should().Be("succeeded");
                    completedResult["mutated"]!.GetValue<bool>().Should().BeTrue();
                    completedResult["lifecycle"]!.GetValue<string>().Should().Be("tracking");

                    DocumentCacheAdminCliLifecycleState lifecycle = await target.State.ReadLifecycleAsync();
                    lifecycle.ProjectionLifecycleState.Should().Be("Tracking");
                    lifecycle.CacheAheadRecoveryRequired.Should().BeFalse();
                    DocumentCacheAdminCliMutableCounts counts = await target.State.ReadMutableCountsAsync();
                    counts.DocumentCacheRows.Should().Be(1);
                    counts.WorkRows.Should().Be(0);
                }
                finally
                {
                    if (cacheLock is not null)
                    {
                        await cacheLock.DisposeAsync();
                    }
                }
            }
        );
    }
}

[TestFixture]
[NonParallelizable]
[Category("MssqlIntegration")]
[Category("Mutex")]
public sealed class Given_DocumentCacheAdminMssqlAdministrativeMutex
{
    [Test]
    public async Task It_serializes_aliases_of_the_same_physical_database_through_the_shared_mutex()
    {
        await using DocumentCacheAdminCliTarget target =
            await Given_DocumentCacheAdminMssqlRebuildOnline.CreateReadyMssqlTargetAsync();
        await using DocumentCacheAdminCliTarget aliasTarget = target.CreateAlias(
            dataStoreId: 2,
            tenantKey: "alias-tenant",
            MssqlAliasConnectionString(target.ConnectionString)
        );

        await Given_DocumentCacheAdminMssqlStatus.WithNestedTriggersAsync(
            target,
            enabled: true,
            async () =>
            {
                DocumentCacheAdminCliMssqlInsertTransaction? blockingInsert =
                    await target.State.BeginMssqlCanonicalInsertTransactionAsync();

                try
                {
                    await using DocumentCacheAdminCliProcessHarness ownerHarness =
                        await DocumentCacheAdminCliProcessHarness.CreateAsync(target);
                    await using DocumentCacheAdminCliProcessHarness contenderHarness =
                        await DocumentCacheAdminCliProcessHarness.CreateAsync(aliasTarget);

                    await using DocumentCacheAdminCliRunningProcess ownerProcess = StartActivateNewEmpty(
                        ownerHarness,
                        target
                    );
                    await WaitForAdministrativeMutexOwnerAsync(target, ownerProcess);

                    await using DocumentCacheAdminCliRunningProcess contenderProcess = StartScrub(
                        contenderHarness,
                        aliasTarget
                    );
                    await WaitForAdministrativeMutexWaiterAsync(target, contenderProcess);
                    await AssertStillRunningAsync(
                        contenderProcess,
                        "aliases of the same SQL Server database must wait on the shared mutex"
                    );

                    (await target.State.ReadAdministrativeMutexGrantedCountAsync()).Should().Be(1);

                    await blockingInsert.DisposeAsync();
                    blockingInsert = null;

                    AssertActivationSucceeded(
                        await ownerProcess.WaitForExitAsync(TimeSpan.FromSeconds(60)),
                        target
                    );
                    AssertScrubSucceeded(
                        await contenderProcess.WaitForExitAsync(TimeSpan.FromSeconds(60)),
                        aliasTarget
                    );
                }
                finally
                {
                    if (blockingInsert is not null)
                    {
                        await blockingInsert.DisposeAsync();
                    }
                }
            }
        );
    }

    [Test]
    public async Task It_allows_different_physical_databases_on_the_same_server_to_administer_concurrently()
    {
        await using DocumentCacheAdminCliTarget blockedTarget =
            await Given_DocumentCacheAdminMssqlRebuildOnline.CreateReadyMssqlTargetAsync();
        await using DocumentCacheAdminCliTarget independentTarget =
            await Given_DocumentCacheAdminMssqlRebuildOnline.CreateReadyMssqlTargetAsync();

        await Given_DocumentCacheAdminMssqlStatus.WithNestedTriggersAsync(
            blockedTarget,
            enabled: true,
            async () =>
            {
                DocumentCacheAdminCliMssqlInsertTransaction? blockingInsert =
                    await blockedTarget.State.BeginMssqlCanonicalInsertTransactionAsync();

                try
                {
                    await using DocumentCacheAdminCliProcessHarness ownerHarness =
                        await DocumentCacheAdminCliProcessHarness.CreateAsync(blockedTarget);
                    await using DocumentCacheAdminCliProcessHarness independentHarness =
                        await DocumentCacheAdminCliProcessHarness.CreateAsync(independentTarget);

                    await using DocumentCacheAdminCliRunningProcess ownerProcess = StartActivateNewEmpty(
                        ownerHarness,
                        blockedTarget
                    );
                    await WaitForAdministrativeMutexOwnerAsync(blockedTarget, ownerProcess);

                    await using DocumentCacheAdminCliRunningProcess independentProcess =
                        StartActivateNewEmpty(independentHarness, independentTarget);

                    AssertActivationSucceeded(
                        await independentProcess.WaitForExitAsync(TimeSpan.FromSeconds(60)),
                        independentTarget
                    );
                    await AssertStillRunningAsync(
                        ownerProcess,
                        "a SQL Server command blocked after mutex acquisition should not block independent databases"
                    );
                    (await blockedTarget.State.ReadAdministrativeMutexGrantedCountAsync()).Should().Be(1);

                    await blockingInsert.DisposeAsync();
                    blockingInsert = null;

                    AssertActivationSucceeded(
                        await ownerProcess.WaitForExitAsync(TimeSpan.FromSeconds(60)),
                        blockedTarget
                    );
                }
                finally
                {
                    if (blockingInsert is not null)
                    {
                        await blockingInsert.DisposeAsync();
                    }
                }
            }
        );
    }

    private static string MssqlAliasConnectionString(string connectionString)
    {
        SqlConnectionStringBuilder builder = new(connectionString)
        {
            DataSource = LoopbackDataSourceAlias(new SqlConnectionStringBuilder(connectionString).DataSource),
            ApplicationName = "document-cache-admin-cli-mutex-alias",
        };

        return builder.ConnectionString;
    }

    private static string LoopbackDataSourceAlias(string dataSource)
    {
        if (dataSource.StartsWith("tcp:localhost", StringComparison.OrdinalIgnoreCase))
        {
            return $"tcp:127.0.0.1{dataSource["tcp:localhost".Length..]}";
        }

        if (dataSource.StartsWith("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return $"127.0.0.1{dataSource["localhost".Length..]}";
        }

        if (dataSource.StartsWith("tcp:127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            return $"tcp:localhost{dataSource["tcp:127.0.0.1".Length..]}";
        }

        if (dataSource.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            return $"localhost{dataSource["127.0.0.1".Length..]}";
        }

        Assert.Inconclusive(
            "The SQL Server CLI administrative mutex alias test requires a localhost or 127.0.0.1 data source."
        );
        return string.Empty;
    }
}

file static class DocumentCacheAdminAdministrativeMutexConcurrencySupport
{
    public static DocumentCacheAdminCliRunningProcess StartActivateNewEmpty(
        DocumentCacheAdminCliProcessHarness harness,
        DocumentCacheAdminCliTarget target
    )
    {
        List<string> arguments =
        [
            DocumentCacheAdminCommandSurface.ActivateNewEmptyCommandName,
            .. TargetArguments(target),
            DocumentCacheAdminCommandSurface.ConfirmOptionName,
            "newEmptyActivation",
            DocumentCacheAdminCommandSurface.JsonOptionName,
            DocumentCacheAdminCommandSurface.CommandTimeoutSecondsOptionName,
            "60",
        ];

        return harness.Start([.. arguments]);
    }

    public static DocumentCacheAdminCliRunningProcess StartScrub(
        DocumentCacheAdminCliProcessHarness harness,
        DocumentCacheAdminCliTarget target,
        string commandTimeoutSeconds = "60"
    )
    {
        List<string> arguments =
        [
            DocumentCacheAdminCommandSurface.ScrubCommandName,
            .. TargetArguments(target),
            DocumentCacheAdminCommandSurface.ConfirmOptionName,
            "integrityScrub",
            DocumentCacheAdminCommandSurface.JsonOptionName,
            DocumentCacheAdminCommandSurface.CommandTimeoutSecondsOptionName,
            commandTimeoutSeconds,
        ];

        return harness.Start([.. arguments]);
    }

    public static DocumentCacheAdminCliRunningProcess StartRebuildOnline(
        DocumentCacheAdminCliProcessHarness harness,
        DocumentCacheAdminCliTarget target,
        string commandTimeoutSeconds = "60"
    )
    {
        List<string> arguments =
        [
            DocumentCacheAdminCommandSurface.RebuildOnlineCommandName,
            .. TargetArguments(target),
            DocumentCacheAdminCommandSurface.ConfirmOptionName,
            "onlineCacheRebuild",
            DocumentCacheAdminCommandSurface.JsonOptionName,
            DocumentCacheAdminCommandSurface.CommandTimeoutSecondsOptionName,
            commandTimeoutSeconds,
        ];

        return harness.Start([.. arguments]);
    }

    public static Task<DocumentCacheAdminCliProcessResult> RunRebuildOnlineAsync(
        DocumentCacheAdminCliProcessHarness harness,
        DocumentCacheAdminCliTarget target,
        string commandTimeoutSeconds = "60"
    )
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(target);

        return harness.RunAsync([
            DocumentCacheAdminCommandSurface.RebuildOnlineCommandName,
            .. TargetArguments(target),
            DocumentCacheAdminCommandSurface.ConfirmOptionName,
            "onlineCacheRebuild",
            DocumentCacheAdminCommandSurface.JsonOptionName,
            DocumentCacheAdminCommandSurface.CommandTimeoutSecondsOptionName,
            commandTimeoutSeconds,
        ]);
    }

    public static async Task<JsonObject> RunStatusAndReadTargetAsync(
        DocumentCacheAdminCliProcessHarness harness,
        DocumentCacheAdminCliTarget target
    )
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(target);

        DocumentCacheAdminCliProcessResult result = await harness.RunAsync([
            DocumentCacheAdminCommandSurface.StatusCommandName,
            .. TargetArguments(target),
            DocumentCacheAdminCommandSurface.JsonOptionName,
            DocumentCacheAdminCommandSurface.StatusObservationTimeoutSecondsOptionName,
            "1",
            DocumentCacheAdminCommandSurface.StatusTimeoutSecondsOptionName,
            "5",
        ]);

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

        JsonArray targets = result.ReadStandardOutputJsonObject()["targets"]!.AsArray();
        targets.Should().ContainSingle();
        return targets[0]!.AsObject();
    }

    public static void AssertRetryableIncompleteRebuildStatus(JsonObject targetStatus)
    {
        targetStatus["durableObservedAt"]!.GetValue<string>().Should().EndWith("Z");
        Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(targetStatus, "lifecycle")["state"]!
            .GetValue<string>()
            .Should()
            .Be("resetting");
        Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(targetStatus, "lifecycle")["availability"]!
            .GetValue<string>()
            .Should()
            .Be("available");
        JsonObject cacheAhead = Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(
            targetStatus,
            "cacheAhead"
        );
        cacheAhead["state"]!.GetValue<string>().Should().Be("clear");
        cacheAhead["recoveryRequired"]!.GetValue<bool>().Should().BeFalse();
        JsonObject queueSummary = Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(
            targetStatus,
            "queueSummary"
        );
        queueSummary["presence"]!.GetValue<string>().Should().Be("notEmpty");
        queueSummary["oldestWorkFirstEnqueuedAt"]!.GetValue<string>().Should().EndWith("Z");
        queueSummary["oldestWorkAgeSeconds"]!.GetValue<double>().Should().BeGreaterThanOrEqualTo(0);
        Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(targetStatus, "operationalHealth")["reason"]!
            .GetValue<string>()
            .Should()
            .Be("runtimeNotObserved");
        Given_DocumentCacheAdminPostgresqlStatus.RequiredObject(targetStatus, "caughtUp")["reason"]!
            .GetValue<string>()
            .Should()
            .Be("runtimeNotObserved");
    }

    public static async Task WaitForAdministrativeMutexOwnerAsync(
        DocumentCacheAdminCliTarget target,
        DocumentCacheAdminCliRunningProcess process
    )
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (await target.State.ReadAdministrativeMutexGrantedCountAsync() > 0)
            {
                return;
            }

            await FailIfExitedAsync(process, "before acquiring the expected administrative mutex");
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        Assert.Fail("The CLI process did not acquire the expected administrative mutex within 30 seconds.");
    }

    public static async Task WaitForAdministrativeMutexWaiterAsync(
        DocumentCacheAdminCliTarget target,
        DocumentCacheAdminCliRunningProcess process
    )
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (await target.State.ReadAdministrativeMutexWaitingCountAsync() > 0)
            {
                return;
            }

            await FailIfExitedAsync(process, "before waiting on the expected administrative mutex");
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        Assert.Fail("The CLI process did not wait on the expected administrative mutex within 30 seconds.");
    }

    public static async Task AssertStillRunningAsync(
        DocumentCacheAdminCliRunningProcess process,
        string because
    )
    {
        DocumentCacheAdminCliProcessResult? result = await process.TryWaitForExitAsync(
            TimeSpan.FromMilliseconds(500)
        );

        if (result is not null)
        {
            Assert.Fail(
                $"Expected CLI process to still be running because {because}.\n"
                    + $"Exit code: {result.ExitCode.ToString(CultureInfo.InvariantCulture)}\n"
                    + $"stdout:\n{result.StandardOutput}\n"
                    + $"stderr:\n{result.StandardError}"
            );
        }
    }

    public static async Task WaitForLifecycleStateAsync(
        DocumentCacheAdminCliTarget target,
        DocumentCacheAdminCliRunningProcess process,
        string lifecycleState
    )
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
        {
            DocumentCacheAdminCliLifecycleState state = await target.State.ReadLifecycleAsync();
            if (string.Equals(state.ProjectionLifecycleState, lifecycleState, StringComparison.Ordinal))
            {
                return;
            }

            await FailIfExitedAsync(process, $"before lifecycle reached {lifecycleState}");
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        Assert.Fail($"The CLI process did not move lifecycle to {lifecycleState} within 30 seconds.");
    }

    public static async Task WaitForRetryableIncompleteRebuildTimeoutAsync(
        DocumentCacheAdminCliRunningProcess process,
        DocumentCacheAdminCliTarget target,
        TimeSpan timeout
    )
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            TimeSpan remaining = timeout - stopwatch.Elapsed;
            TimeSpan pollTimeout =
                remaining < TimeSpan.FromMilliseconds(100) ? remaining : TimeSpan.FromMilliseconds(100);

            DocumentCacheAdminCliProcessResult? result = await process.TryWaitForExitAsync(pollTimeout);
            if (result is null)
            {
                continue;
            }

            JsonObject commandResult = DocumentCacheAdminCliCommandResultAssertions.AssertCommandResult(
                result,
                target,
                DocumentCacheAdminExitCodes.IncompleteRetryable
            );
            commandResult["command"]!.GetValue<string>().Should().Be("onlineCacheRebuild");
            commandResult["status"]!.GetValue<string>().Should().Be("incompleteRetryable");
            commandResult["classification"]!.GetValue<string>().Should().Be("providerCommandTimeout");
            commandResult["mutated"]!.GetValue<bool>().Should().BeTrue();
            commandResult["lifecycle"]!.GetValue<string>().Should().Be("resetting");
            AssertPhaseDiagnostic(
                commandResult,
                expectedCategory: "providerCommandTimeout",
                expectedPhase: "clearCache",
                expectedRetryable: true
            );

            return;
        }

        Assert.Fail(
            $"The CLI process did not exit with retryable provider command timeout within {timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds."
        );
    }

    public static void AssertPhaseDiagnostic(
        JsonObject commandResult,
        string expectedCategory,
        string expectedPhase,
        bool expectedRetryable
    )
    {
        JsonArray phaseDiagnostics = commandResult["phaseDiagnostics"]!.AsArray();
        bool hasExpectedDiagnostic = phaseDiagnostics.Any(diagnostic =>
        {
            JsonObject? diagnosticObject = diagnostic as JsonObject;
            return diagnosticObject is not null
                && diagnosticObject["diagnosticCategory"]!.GetValue<string>() == expectedCategory
                && diagnosticObject["currentPhase"]!.GetValue<string>() == expectedPhase
                && diagnosticObject["retryable"]!.GetValue<bool>() == expectedRetryable;
        });

        hasExpectedDiagnostic.Should().BeTrue();
    }

    public static void AssertActivationSucceeded(
        DocumentCacheAdminCliProcessResult result,
        DocumentCacheAdminCliTarget target
    )
    {
        JsonObject commandResult = DocumentCacheAdminCliCommandResultAssertions.AssertCommandResult(
            result,
            target,
            DocumentCacheAdminExitCodes.Success
        );
        commandResult["command"]!.GetValue<string>().Should().Be("guardedNewEmptyActivation");
        commandResult["status"]!.GetValue<string>().Should().Be("completed");
        commandResult["classification"]!.GetValue<string>().Should().Be("succeeded");
        commandResult["mutated"]!.GetValue<bool>().Should().BeTrue();
        commandResult["lifecycle"]!.GetValue<string>().Should().Be("tracking");
    }

    public static void AssertScrubSucceeded(
        DocumentCacheAdminCliProcessResult result,
        DocumentCacheAdminCliTarget target
    )
    {
        JsonObject commandResult = DocumentCacheAdminCliCommandResultAssertions.AssertCommandResult(
            result,
            target,
            DocumentCacheAdminExitCodes.Success
        );
        commandResult["command"]!.GetValue<string>().Should().Be("explicitIntegrityScrub");
        commandResult["status"]!.GetValue<string>().Should().Be("completed");
        commandResult["classification"]!.GetValue<string>().Should().Be("succeeded");
    }

    public static string LoopbackHostAlias(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            Assert.Inconclusive(
                "The PostgreSQL CLI administrative mutex alias test requires a configured localhost or 127.0.0.1 host."
            );
            return string.Empty;
        }

        return host.ToLowerInvariant() switch
        {
            "localhost" => "127.0.0.1",
            "127.0.0.1" => "localhost",
            _ => InconclusivePostgresqlHost(),
        };
    }

    private static IEnumerable<string> TargetArguments(DocumentCacheAdminCliTarget target)
    {
        if (!string.IsNullOrEmpty(target.TenantKey))
        {
            yield return DocumentCacheAdminCommandSurface.TenantKeyOptionName;
            yield return target.TenantKey;
        }

        yield return DocumentCacheAdminCommandSurface.DataStoreIdOptionName;
        yield return target.DataStoreId.ToString(CultureInfo.InvariantCulture);
    }

    private static async Task FailIfExitedAsync(DocumentCacheAdminCliRunningProcess process, string context)
    {
        if (!process.HasExited)
        {
            return;
        }

        DocumentCacheAdminCliProcessResult result = await process.WaitForExitAsync(TimeSpan.FromSeconds(5));
        Assert.Fail(
            $"The CLI process exited {context}.\n"
                + $"Exit code: {result.ExitCode.ToString(CultureInfo.InvariantCulture)}\n"
                + $"stdout:\n{result.StandardOutput}\n"
                + $"stderr:\n{result.StandardError}"
        );
    }

    private static string InconclusivePostgresqlHost()
    {
        Assert.Inconclusive(
            "The PostgreSQL CLI administrative mutex alias test requires a localhost or 127.0.0.1 host."
        );
        return string.Empty;
    }
}
