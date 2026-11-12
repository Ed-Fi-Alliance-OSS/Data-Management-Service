// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace EdFi.DataManagementService.Performance.Harness.Smoke;

internal static class PerfFixtureSmokeDatabase
{
    public static async Task<DbConnection> OpenLoaderConnectionAsync(
        string connectionString,
        PerfProvider provider
    )
    {
        DbConnection connection =
            provider == PerfProvider.Postgresql
                ? new NpgsqlConnection(connectionString)
                : new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync();
            if (provider == PerfProvider.Mssql)
            {
                // These direct loader/seed connections inherit SqlClient defaults, independently
                // of the provisioning session. ANSI_WARNINGS implies effective ARITHABORT at
                // compatibility level >= 90, even when SESSIONPROPERTY('ARITHABORT') is zero.
                await using DbCommand command = connection.CreateCommand();
                command.CommandText = """
                    SELECT SESSIONPROPERTY('ANSI_NULLS'), SESSIONPROPERTY('ANSI_PADDING'),
                        SESSIONPROPERTY('ANSI_WARNINGS'), SESSIONPROPERTY('CONCAT_NULL_YIELDS_NULL'),
                        SESSIONPROPERTY('QUOTED_IDENTIFIER'), SESSIONPROPERTY('NUMERIC_ROUNDABORT'),
                        SESSIONPROPERTY('ARITHABORT'), compatibility_level
                    FROM sys.databases WHERE name = DB_NAME();
                    """;
                await using DbDataReader reader = await command.ExecuteReaderAsync();
                (await reader.ReadAsync()).Should().BeTrue();
                for (int index = 0; index < 5; index++)
                {
                    reader.GetInt32(index).Should().Be(1, "computed URI index DML requires this SET option");
                }

                reader.GetInt32(5).Should().Be(0, "computed URI index DML requires NUMERIC_ROUNDABORT OFF");
                (reader.GetInt32(6) == 1 || reader.GetByte(7) >= 90)
                    .Should()
                    .BeTrue("computed URI index DML requires effective ARITHABORT ON");
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public static async Task OffsetDescriptorAllocatorAsync(DbConnection connection, PerfProvider provider)
    {
        // Deliberately separate compact keys from both fixture document ranges. The loaders
        // must capture native identities and leave this allocator ready for a later API POST.
        await using DbCommand command = connection.CreateCommand();
        command.CommandText =
            provider == PerfProvider.Postgresql
                ? """ALTER TABLE "dms"."Descriptor" ALTER COLUMN "DescriptorId" RESTART WITH 100001;"""
                : "DBCC CHECKIDENT ('[dms].[Descriptor]', RESEED, 100001);";
        await command.ExecuteNonQueryAsync();
    }
}
