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
        DocumentCacheAdminCliTarget target
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
            "60",
        ];

        return harness.Start([.. arguments]);
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
