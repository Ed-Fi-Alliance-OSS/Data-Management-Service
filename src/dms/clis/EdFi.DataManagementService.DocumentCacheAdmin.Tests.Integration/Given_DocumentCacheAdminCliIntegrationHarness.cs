// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.DocumentCacheAdmin;
using FluentAssertions;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration;

[TestFixture]
[Category("CliIntegrationHarness")]
public sealed class Given_DocumentCacheAdminCliIntegrationHarness
{
    [Test]
    [Category("PostgresqlIntegration")]
    public async Task It_invokes_status_through_the_real_process_against_an_isolated_postgresql_target()
    {
        await using DocumentCacheAdminCliTarget target =
            await DocumentCacheAdminCliTarget.CreatePostgresqlAsync();

        JsonObject targetStatus = await AssertRealProcessStatusAsync(
            target,
            expectedOperationalHealthReason: "runtimeNotObserved"
        );

        targetStatus["provider"]!.GetValue<string>().Should().Be("postgresql");
        targetStatus["physicalSourceFingerprint"]!
            .GetValue<string>()
            .Should()
            .Be(await target.State.ReadPhysicalSourceFingerprintAsync());
    }

    [Test]
    [Category("MssqlIntegration")]
    public async Task It_invokes_status_through_the_real_process_against_an_isolated_sql_server_target_when_configured()
    {
        await using DocumentCacheAdminCliTarget target = await DocumentCacheAdminCliTarget.CreateMssqlAsync();

        JsonObject targetStatus = await AssertRealProcessStatusAsync(
            target,
            expectedOperationalHealthReason: null
        );

        targetStatus["provider"]!.GetValue<string>().Should().Be("sqlserver");
        (await target.State.ReadPhysicalSourceFingerprintAsync()).Should().StartWith("sha256:");
    }

    private static async Task<JsonObject> AssertRealProcessStatusAsync(
        DocumentCacheAdminCliTarget target,
        string? expectedOperationalHealthReason
    )
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
        result.StandardOutput.TrimEnd().Should().NotContain("\n");
        result.StandardError.Should().NotContain(target.ConnectionString);
        result.StandardError.Should().NotContain(harness.SecretFromEnvironment);

        JsonObject root = result.ReadStandardOutputJsonObject();
        root["contractVersion"]!.GetValue<int>().Should().Be(1);
        JsonArray targets = root["targets"]!.AsArray();
        targets.Should().ContainSingle();

        JsonObject targetStatus = targets[0]!.AsObject();
        targetStatus["targetKey"]!["tenantKey"]!.GetValue<string>().Should().Be(target.TenantKey);
        targetStatus["targetKey"]!["dataStoreId"]!.GetValue<long>().Should().Be(target.DataStoreId);
        targetStatus["resolution"]!["status"]!.GetValue<string>().Should().Be("resolved");
        targetStatus["physicalSourceFingerprint"]!.GetValue<string>().Should().StartWith("sha256:");

        if (expectedOperationalHealthReason is not null)
        {
            targetStatus["operationalHealth"]!["reason"]!
                .GetValue<string>()
                .Should()
                .Be(expectedOperationalHealthReason);
        }

        DocumentCacheAdminCliLifecycleState lifecycle = await target.State.ReadLifecycleAsync();
        lifecycle.ProjectionLifecycleState.Should().Be("Disabled");
        lifecycle.CacheAheadRecoveryRequired.Should().BeFalse();

        DocumentCacheAdminCliMutableCounts counts = await target.State.ReadMutableCountsAsync();
        counts.DocumentCacheRows.Should().Be(0);
        counts.WorkRows.Should().Be(0);
        (await target.State.ReadOldestWorkFirstEnqueuedAtAsync()).Should().BeNull();

        harness.ConfigurationService.TokenRequestCount.Should().BeGreaterThan(0);
        harness.ConfigurationService.DataStoresRequestCount.Should().BeGreaterThan(0);
        harness.ConfigurationService.LastTokenRequestBody.Should().Contain(harness.SecretFromEnvironment);
        harness
            .ConfigurationService.LastDataStoresAuthorizationHeader.Should()
            .Be("Bearer document-cache-admin-cli-harness-token");
        harness.ConfigurationService.LastDataStoresTenantHeader.Should().BeNull();

        return targetStatus;
    }
}
