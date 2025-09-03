// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Npgsql;

namespace EdFi.DataManagementService.Tests.E2E.Management;

public abstract class ContainerSetupBase
{
    private const string PgAdminUser = "postgres";
    private const string PgAdminPassword = "abcdefgh1!";

    private const ushort DbPortExternal = 5435;
    private const string DatabaseName = "edfi_datamanagementservice";

    public abstract Task ResetData();

    public abstract string ApiUrl();

    public static async Task ResetDatabase()
    {
        // Add delay for Kafka CDC to process any pending changes before cleanup
        await Task.Delay(2000);

        var hostConnectionString =
            $"host=localhost;port={DbPortExternal};username={PgAdminUser};password={PgAdminPassword};database={DatabaseName};";
        using var conn = new NpgsqlConnection(hostConnectionString);
        await conn.OpenAsync();

        await DeleteData("dms.Reference");
        await DeleteData("dms.Alias");
        await DeleteDataWithCondition("dms.Document", "'SchoolYearType'");
        await DeleteData("dms.EducationOrganizationHierarchyTermsLookup");
        await DeleteData("dms.EducationOrganizationHierarchy");

        async Task DeleteData(string tableName)
        {
            var deleteRefCmd = new NpgsqlCommand($"DELETE FROM {tableName};", conn);
            await deleteRefCmd.ExecuteNonQueryAsync();
        }
        async Task DeleteDataWithCondition(string tableName, string resourcename)
        {
            var deleteCmd = new NpgsqlCommand(
                $"DELETE FROM {tableName} WHERE resourcename != {resourcename};",
                conn
            );
            await deleteCmd.ExecuteNonQueryAsync();
        }
    }
}
