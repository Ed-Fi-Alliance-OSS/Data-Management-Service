// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using Dapper;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Test.Integration;

public static class ClaimsHierarchyTestHelper
{
    private const string DeleteSql = """
            DELETE FROM dmscs.claimshierarchy;
        """;

    private const string InsertSqlFormat = """
           INSERT INTO dmscs.claimshierarchy(hierarchy)
           VALUES ('{0}'::jsonb);
        """;

    public static async Task ReinitializeClaimsHierarchy(bool clearOnly = false)
    {
        await using var conn = new NpgsqlConnection(Configuration.DatabaseOptions.Value.DatabaseConnection);
        await conn.OpenAsync();
        await conn.ExecuteAsync(DeleteSql);

        if (!clearOnly)
        {
            await conn.ExecuteAsync(string.Format(InsertSqlFormat, LoadClaimsHierarchyJson()));
        }
    }

    private static string LoadClaimsHierarchyJson()
    {
        var assembly = Assembly.GetExecutingAssembly();

        const string ResourceName =
            "EdFi.DmsConfigurationService.Backend.Postgresql.Test.Integration.ClaimsHierarchyMetadata.json";

        using Stream stream =
            assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Resource '{ResourceName}' not found.");

        using StreamReader reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
