// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Tests.E2E.Management;
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

    [BeforeFeature]
    public static async Task BeforeFeature(PlaywrightContext context)
    {
        await context.InitializeApiContext();
    }

    [AfterFeature]
    public static async Task AfterFeature()
    {
        try
        {
            var hostConnectionString =
                $"host=localhost;port={DbPortExternal};username={PgAdminUser};password={PgAdminPassword};database={DatabaseName};";
            using var conn = new NpgsqlConnection(hostConnectionString);
            await conn.OpenAsync();

            await DeleteData("dmscs.Vendor");
            async Task DeleteData(string tableName)
            {
                var deleteRefCmd = new NpgsqlCommand($"DELETE FROM {tableName};", conn);
                await deleteRefCmd.ExecuteNonQueryAsync();
            }
        }
        catch (Exception ex)
        {
            var message = ex.Message;
        }
    }

    [AfterTestRun]
    public static void AfterTestRun(PlaywrightContext context)
    {
        context.Dispose();
    }
}
