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
    // The docker-compose stack publishes the database with values from the active
    // environment file; build-config.ps1 propagates those variables into this test
    // process. The fallbacks match the checked-in .env.config*.e2e files so a bare
    // `dotnet test` against a standard stack still cleans the right database.
    private const string PgAdminUser = "postgres";
    private static string PgAdminPassword => EnvOrDefault("POSTGRES_PASSWORD", "abcdefgh1!");
    private static string DbPortExternal => EnvOrDefault("POSTGRES_PORT", "5435");

    // The CMS database name is driven by POSTGRES_DB_NAME for both engines (the
    // MSSQL connection string in the env files interpolates it too).
    private static string DatabaseName => EnvOrDefault("POSTGRES_DB_NAME", "edfi_configurationservice");

    private const string MssqlSaUser = "sa";
    private static string MssqlSaPassword => EnvOrDefault("MSSQL_SA_PASSWORD", "abcdefgh1!");
    private static string MssqlDbPortExternal => EnvOrDefault("MSSQL_PORT", "1435");

    private static string EnvOrDefault(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

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
        catch (Exception ex)
        {
            // Best-effort cleanup: never fail a scenario over it, but surface the
            // problem so a misconfigured connection doesn't hide stale test data.
            Console.WriteLine($"Warning: E2E test-data cleanup failed: {ex.Message}");
        }
    }

    [AfterTestRun]
    public static void AfterTestRun(PlaywrightContext context)
    {
        context.Dispose();
    }
}
