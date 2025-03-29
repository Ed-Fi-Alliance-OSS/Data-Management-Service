// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.NormalizedSqlServer;

public static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: Program <path-to-json-file>");
            return;
        }

        var jsonFilePath = args[0];

        if (!File.Exists(jsonFilePath))
        {
            Console.WriteLine($"Error: File not found at path '{jsonFilePath}'.");
            return;
        }

        var jsonContent = await File.ReadAllTextAsync(jsonFilePath);

        // Retrieve the connection string from an environment variable or configuration
        var connectionString =
            Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING")
            ?? "Server=localhost,1435;Database=master;User Id=sa;Password=abcdefgh1!;Encrypt=false;TrustServerCertificate=true;";

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await new JsonToSqlTableCreator(connection).CreateTablesAsync(jsonContent);
        await new JsonToSqlSaver(connection).SaveJsonAsync(jsonContent);

        Console.WriteLine("JSON data successfully saved to the database.");
    }
}
