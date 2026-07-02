// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Tests.E2E.Management;
using Microsoft.Data.SqlClient;
using Npgsql;
using Reqnroll;

namespace EdFi.DmsConfigurationService.Tests.E2E.Hooks;

[Binding]
public static class SetupHooks
{
    private const string PgAdminUser = "postgres";
    private const string PgAdminPassword = "abcdefgh1!";
    private const ushort DbPortExternal = 5435;
    private const string DatabaseName = "edfi_configurationservice";

    private const string MssqlSaUser = "sa";
    private const string MssqlSaPassword = "abcdefgh1!";
    private const ushort MssqlDbPortExternal = 1435;

    private static bool UseMssql =>
        string.Equals(
            Environment.GetEnvironmentVariable("DMS_CONFIG_DATASTORE"),
            "mssql",
            StringComparison.OrdinalIgnoreCase
        );

    // HTTP Basic auth on /connect/token is only supported in self-contained
    // (OpenIddict) mode; keycloak mode expects credentials in the form body. Scope
    // scenarios tagged @SelfContainedOnly accordingly so they don't run under keycloak.
    [BeforeScenario("SelfContainedOnly")]
    public static void SkipUnlessSelfContainedIdentityProvider()
    {
        var identityProvider = Environment.GetEnvironmentVariable("DMS_CONFIG_IDENTITY_PROVIDER");
        if (
            !string.IsNullOrEmpty(identityProvider)
            && !identityProvider.Equals("self-contained", StringComparison.OrdinalIgnoreCase)
        )
        {
            Assert.Ignore(
                $"Requires the self-contained identity provider (HTTP Basic on /connect/token is self-contained only); current provider is '{identityProvider}'."
            );
        }
    }

    // Tenant endpoints are only mapped when multi-tenancy is enabled, so scenarios
    // tagged @MultitenantOnly would get 404s against a single-tenant stack. Skip them
    // unless the environment explicitly enables multi-tenancy.
    [BeforeScenario("MultitenantOnly")]
    public static void SkipUnlessMultiTenancyEnabled()
    {
        var multiTenancy = Environment.GetEnvironmentVariable("DMS_CONFIG_MULTI_TENANCY");
        if (!string.Equals(multiTenancy, "true", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Ignore(
                "Requires a multi-tenant CMS (tenant endpoints are only mapped when multi-tenancy is enabled); "
                    + $"DMS_CONFIG_MULTI_TENANCY is '{multiTenancy ?? "unset"}'."
            );
        }
    }

    [BeforeFeature]
    public static async Task BeforeFeature(PlaywrightContext context)
    {
        // Clean database BEFORE tests to ensure clean state
        await CleanTestData();
        await context.InitializeApiContext();
    }

    [AfterFeature]
    public static async Task AfterFeature()
    {
        await CleanTestData();
    }

    private static async Task CleanTestData()
    {
        try
        {
            if (!UseMssql)
            {
                var hostConnectionString =
                    $"host=localhost;port={DbPortExternal};username={PgAdminUser};password={PgAdminPassword};database={DatabaseName};";
                using var conn = new NpgsqlConnection(hostConnectionString);
                await conn.OpenAsync();

                // Delete in reverse dependency order
                await DeleteData(@"""dmscs"".""DataStoreContext""");
                await DeleteData(@"""dmscs"".""DataStore""");
                await DeleteData(@"""dmscs"".""ApiClientDataStore""");
                await DeleteData(@"""dmscs"".""ApiClient""");
                await DeleteData(@"""dmscs"".""Application""");
                await DeleteData(@"""dmscs"".""Vendor""");
                // Clean up test-created claimsets (not system-reserved ones)
                await DeleteTestClaimSets();

                async Task DeleteData(string tableName)
                {
                    var deleteRefCmd = new NpgsqlCommand($"DELETE FROM {tableName};", conn);
                    await deleteRefCmd.ExecuteNonQueryAsync();
                }

                async Task DeleteTestClaimSets()
                {
                    // Delete only non-system-reserved claimsets created by tests
                    var deleteTestClaimSetsCmd = new NpgsqlCommand(
                        """
                        DELETE FROM "dmscs"."ClaimSet"
                        WHERE "ClaimSetName" IN ('TestClaimSet1', 'TestClaimSet2', 'TestClaimSet3',
                                              'TestClaimSet4', 'AcademicHonorClaimSet',
                                              'NewClaimSet', 'ImportedClaimSet')
                        AND "IsSystemReserved" = false;
                        """,
                        conn
                    );
                    await deleteTestClaimSetsCmd.ExecuteNonQueryAsync();
                }
            }
            else
            {
                var mssqlConnectionString =
                    $"Server=localhost,{MssqlDbPortExternal};User Id={MssqlSaUser};Password={MssqlSaPassword};TrustServerCertificate=true;Database={DatabaseName};";
                using var conn = new SqlConnection(mssqlConnectionString);
                await conn.OpenAsync();

                // Delete in reverse dependency order
                await DeleteData("dmscs.DataStoreContext");
                await DeleteData("dmscs.DataStore");
                await DeleteData("dmscs.ApiClientDataStore");
                await DeleteData("dmscs.ApiClient");
                await DeleteData("dmscs.Application");
                await DeleteData("dmscs.Vendor");
                // Clean up test-created claimsets (not system-reserved ones)
                await DeleteTestClaimSets();

                async Task DeleteData(string tableName)
                {
                    var deleteRefCmd = new SqlCommand($"DELETE FROM {tableName};", conn);
                    await deleteRefCmd.ExecuteNonQueryAsync();
                }

                async Task DeleteTestClaimSets()
                {
                    // Delete only non-system-reserved claimsets created by tests
                    var deleteTestClaimSetsCmd = new SqlCommand(
                        @"
                        DELETE FROM dmscs.ClaimSet
                        WHERE ClaimSetName IN ('TestClaimSet1', 'TestClaimSet2', 'TestClaimSet3',
                                              'TestClaimSet4', 'AcademicHonorClaimSet',
                                              'NewClaimSet', 'ImportedClaimSet')
                        AND IsSystemReserved = 0;",
                        conn
                    );
                    await deleteTestClaimSetsCmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch (Exception)
        {
            // intentionally swallow — best-effort test cleanup
        }
    }

    [AfterTestRun]
    public static void AfterTestRun(PlaywrightContext context)
    {
        context.Dispose();
    }
}
