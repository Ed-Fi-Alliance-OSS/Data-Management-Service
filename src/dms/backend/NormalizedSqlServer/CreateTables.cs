// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

public class JsonToSqlTableCreator
{
    private readonly SqlConnection _connection;

    public JsonToSqlTableCreator(SqlConnection connection)
    {
        _connection = connection;
    }

    public async Task CreateTablesAsync(string json)
    {
        var jsonObject = JsonSerializer.Deserialize<Dictionary<string, object>>(json);

        if (jsonObject == null)
        {
            throw new ArgumentException("Invalid JSON object.");
        }

        using var transaction = _connection.BeginTransaction();

        try
        {
            await CreateTableAsync("StudentEducationOrganizationAssociation", jsonObject, transaction);

            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            Console.WriteLine($"Error: {ex.Message}");
            throw;
        }
    }

    private async Task CreateTableAsync(
        string tableName,
        Dictionary<string, object> data,
        SqlTransaction transaction
    )
    {
        var columns = new List<string>();
        var foreignKeys = new List<string>();

        foreach (var (key, value) in data)
        {
            if (
                key.EndsWith("Reference")
                && value is JsonElement referenceElement
                && referenceElement.ValueKind == JsonValueKind.Object
            )
            {
                // Handle foreign key references
                var referenceData = JsonSerializer.Deserialize<Dictionary<string, object>>(
                    referenceElement.GetRawText()
                );

                if (referenceData == null)
                {
                    throw new InvalidOperationException($"Invalid reference data for key '{key}'.");
                }

                var referenceTableName = key.Replace("Reference", "");
                await CreateTableAsync(referenceTableName, referenceData, transaction);

                foreignKeys.Add(
                    $"[{referenceTableName}Id] INT FOREIGN KEY REFERENCES [{referenceTableName}](DocumentId)"
                );
            }
            else if (value is JsonElement arrayElement && arrayElement.ValueKind == JsonValueKind.Array)
            {
                // Handle nested arrays
                var nestedTableName = $"{tableName}{key}";
                foreach (var item in arrayElement.EnumerateArray())
                {
                    var nestedData = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        item.GetRawText()
                    );

                    if (nestedData == null)
                    {
                        throw new InvalidOperationException($"Invalid reference data for key '{key}'.");
                    }

                    await CreateTableAsync(nestedTableName, nestedData, transaction);
                }
            }
            else if (value is JsonElement scalarElement)
            {
                // Handle scalar values
                var columnType = GetSqlColumnType(scalarElement);
                if (columnType != null)
                {
                    columns.Add($"[{key}] {columnType}");
                }
            }
        }

        // Create the table
        var createTableSql =
            $@"
            IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='{tableName}' AND xtype='U')
            CREATE TABLE [{tableName}] (
                DocumentId INT IDENTITY(1,1) PRIMARY KEY,
                {string.Join(", ", columns.Concat(foreignKeys))}
            )";

        using var command = new SqlCommand(createTableSql, _connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    private string? GetSqlColumnType(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => "NVARCHAR(MAX)",
            JsonValueKind.Number => element.TryGetInt32(out _) ? "INT" : "FLOAT",
            JsonValueKind.True or JsonValueKind.False => "BIT",
            _ => null, // Ignore unsupported types
        };
    }
}
