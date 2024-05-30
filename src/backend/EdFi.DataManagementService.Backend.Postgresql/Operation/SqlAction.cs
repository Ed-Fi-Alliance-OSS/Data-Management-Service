// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Postgresql.Model;
using EdFi.DataManagementService.Core.External.Model;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql.Operation;

/// <summary>
/// A facade of all the DB interactions. Any action requiring SQL statement execution should be here.
/// </summary>
public interface ISqlAction
{
    /// <summary>
    /// Returns a single Document from the database corresponding to the given ReferentialId,
    /// or null if no matching Document was found.
    /// </summary>
    public Task<Document?> FindDocumentByReferentialId(
        ReferentialId referentialId,
        PartitionKey partitionKey,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    );

    /// <summary>
    /// Insert a single Document into the database and returns the number of rows affected
    /// </summary>
    public Task<int> InsertDocument(
        Document document,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    );
}

/// <summary>
/// A facade of all the DB interactions. Any action requiring SQL statement execution should be here.
/// </summary>
public class SqlAction : ISqlAction
{
    /// <summary>
    /// Returns a single Document from the database corresponding to the given ReferentialId,
    /// or null if no matching Document was found.
    /// </summary>
    public async Task<Document?> FindDocumentByReferentialId(
        ReferentialId referentialId,
        PartitionKey partitionKey,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        await using NpgsqlCommand command =
            new(
                @"SELECT * FROM public.Documents d
                INNER JOIN public.Aliases a ON a.DocumentId = d.Id AND a.DocumentPartitionKey = d.DocumentPartitionKey
                WHERE a.ReferentialPartitionKey = $1 AND a.ReferentialId = $2;",
                connection,
                transaction
            )
            {
                Parameters =
                {
                    new() { Value = partitionKey.Value },
                    new() { Value = referentialId.Value },
                }
            };

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        if (!reader.HasRows)
        {
            return null;
        }

        // Assumes only one row returned (should never be more due to DB unique constraint)
        await reader.ReadAsync();

        return new(
            Id: reader.GetInt64(reader.GetOrdinal("Id")),
            DocumentPartitionKey: reader.GetInt16(reader.GetOrdinal("DocumentPartitionKey")),
            DocumentUuid: reader.GetGuid(reader.GetOrdinal("DocumentUuid")),
            ResourceName: reader.GetString(reader.GetOrdinal("ResourceName")),
            ResourceVersion: reader.GetString(reader.GetOrdinal("ResourceVersion")),
            ProjectName: reader.GetString(reader.GetOrdinal("ProjectName")),
            EdfiDoc: await reader.GetFieldValueAsync<JsonElement>(reader.GetOrdinal("EdfiDoc")),
            CreatedAt: reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
            LastModifiedAt: reader.GetDateTime(reader.GetOrdinal("LastModifiedAt"))
        );
    }

    /// <summary>
    /// Insert a single Document into the database and returns the number of rows affected
    /// </summary>
    public async Task<int> InsertDocument(
        Document document,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        await using var insertDocumentCmd = new NpgsqlCommand(
            @"INSERT INTO public.Documents(DocumentPartitionKey, DocumentUuid, ResourceName, ResourceVersion, ProjectName, EdfiDoc)
                    VALUES ($1, $2, $3, $4, $5, $6);",
            connection,
            transaction
        )
        {
            Parameters =
            {
                new() { Value = document.DocumentPartitionKey },
                new() { Value = document.DocumentUuid },
                new() { Value = document.ResourceName },
                new() { Value = document.ResourceVersion },
                new() { Value = document.ProjectName },
                new() { Value = document.EdfiDoc },
            }
        };

        return await insertDocumentCmd.ExecuteNonQueryAsync();
    }
}
