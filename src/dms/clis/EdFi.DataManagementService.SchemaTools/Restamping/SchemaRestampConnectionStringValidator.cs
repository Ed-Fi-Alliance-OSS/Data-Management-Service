// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace EdFi.DataManagementService.SchemaTools.Restamping;

internal static class SchemaRestampConnectionStringValidator
{
    internal static void ValidateOrThrow(string connectionString, SqlDialect dialect)
    {
        try
        {
            var namedDatabase = dialect switch
            {
                SqlDialect.Pgsql => new NpgsqlConnectionStringBuilder(connectionString) is var pg
                    && pg.ContainsKey("Database")
                    && !string.IsNullOrWhiteSpace(pg.Database),
                SqlDialect.Mssql => new SqlConnectionStringBuilder(connectionString) is var ms
                    && (ms.ContainsKey("Initial Catalog") || ms.ContainsKey("Database"))
                    && !string.IsNullOrWhiteSpace(ms.InitialCatalog),
                _ => false,
            };
            if (!namedDatabase)
            {
                throw new SchemaRestampException(
                    SchemaRestampFailure.Validation,
                    "An explicit target database name is required."
                );
            }
        }
        catch (ArgumentException)
        {
            throw new SchemaRestampException(
                SchemaRestampFailure.Validation,
                "The target connection string is invalid."
            );
        }
    }
}
