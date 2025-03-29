// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

public class JsonToSqlSaver
{
    private readonly SqlConnection _connection;

    public JsonToSqlSaver(SqlConnection connection)
    {
        _connection = connection;
    }

    public async Task SaveJsonAsync(string json)
    {
        var jsonObject = JsonSerializer.Deserialize<Dictionary<string, object>>(json);

        if (jsonObject == null)
        {
            throw new ArgumentException("Invalid JSON object.");
        }

        using var transaction = _connection.BeginTransaction();

        try
        {
            await SaveObjectAsync("StudentEducationOrganizationAssociation", jsonObject, transaction);

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private async Task<int> SaveObjectAsync(
        string tableName,
        Dictionary<string, object> data,
        SqlTransaction transaction
    )
    {
        var columns = new List<string>();
        var values = new List<string>();
        var parameters = new DynamicParameters();

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
                var referenceId = await SaveObjectAsync(referenceTableName, referenceData, transaction);

                columns.Add($"{referenceTableName}Id");
                values.Add($"@{referenceTableName}Id");
                parameters.Add($"@{referenceTableName}Id", referenceId);
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
                        throw new InvalidOperationException($"Invalid nested data for key '{key}'.");
                    }

                    await SaveObjectAsync(nestedTableName, nestedData, transaction);
                }
            }
            else
            {
                // Handle scalar values
                columns.Add(key);
                values.Add($"@{key}");
                parameters.Add($"@{key}", value.ToString());
            }
        }

        var sql =
            $"INSERT INTO {tableName} ({string.Join(", ", columns)}) OUTPUT INSERTED.DocumentId VALUES ({string.Join(", ", values)})";

        return await _connection.ExecuteScalarAsync<int>(sql, parameters, transaction);
    }
}
