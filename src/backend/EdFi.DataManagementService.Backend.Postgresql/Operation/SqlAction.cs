// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Postgresql.Model;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql.Operation;

/// <summary>
/// A facade of all the DB interactions. Any action requiring SQL statement execution should be here.
/// Connections and transactions are managed by the caller.
/// Exceptions are handled by the caller.
/// </summary>
public interface ISqlAction
{
    public Task<Document?> FindDocumentByReferentialId(
        ReferentialId referentialId,
        PartitionKey partitionKey,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    );

    public Task<JsonNode[]> GetDocumentsByKey(
        string resourceName,
        IPaginationParameters paginationParameters,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    );

    public Task<JsonNode?> GetDocumentById(
        Guid documentUuid,
        int partitionKey,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    );

    public Task<long> InsertDocument(
        Document document,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    );

    public Task<long> InsertAlias(Alias alias, NpgsqlConnection connection, NpgsqlTransaction transaction);

    public Task<int> UpdateDocumentEdFiDoc(
        int documentPartitionKey,
        Guid documentUuid,
        JsonElement edfiDoc,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    );

    public Task<UpdateDocumentValidationResult> UpdateDocumentValidation(
        DocumentUuid documentUuid,
        PartitionKey documentPartitionKey,
        ReferentialId referentialId,
        PartitionKey referentialPartitionKey,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    );

    public Task<int> DeleteAliasByDocumentId(
        int documentPartitionKey,
        long? documentId,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    );

    public Task<int> DeleteDocumentByDocumentId(
        int documentPartitionKey,
        long? documentId,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    );
}

public record UpsertDocumentSqlResult(bool Inserted, long DocumentId);

public record UpdateDocumentValidationResult(bool DocumentExists, bool ReferentialIdExists);

/// <summary>
/// A facade of all the DB interactions. Any action requiring SQL statement execution should be here.
/// Connections and transactions are managed by the caller.
/// Exceptions are handled by the caller.
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
    /// Returns Documents from the database corresponding to the given ResourceName,
    /// or null if no matching Document was found.
    /// </summary>
    public async Task<JsonNode[]> GetDocumentsByKey(
        string resourceName,
        IPaginationParameters paginationParameters,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        await using NpgsqlCommand command =
            new(
                @"SELECT EdfiDoc FROM public.Documents WHERE resourcename = $1 ORDER BY createdat OFFSET $2 ROWS FETCH FIRST $3 ROWS ONLY;",
                connection
            )
            {
                Parameters =
                {
                    new() { Value = resourceName},
                    new() { Value = paginationParameters.offset },
                    new() { Value = paginationParameters.limit },
                }
            };

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        var documents = new List<JsonNode>();

        while (await reader.ReadAsync())
        {
            JsonNode? edfiDoc = (await reader.GetFieldValueAsync<JsonElement>(0)).Deserialize<JsonNode>();

            if (edfiDoc != null)
            {
                documents.Add(edfiDoc);
            }
        }

        return documents.ToArray();
    }

    /// <summary>
    /// Returns a single Document from the database corresponding to the given Id,
    /// or null if no matching Document was found.
    /// </summary>
    public async Task<JsonNode?> GetDocumentById(
        Guid documentUuid,
        int partitionKey,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using NpgsqlCommand command =
            new(
                @"SELECT EdfiDoc FROM public.Documents WHERE DocumentPartitionKey = $1 AND DocumentUuid = $2;",
                connection
            )
            {
                Parameters =
                {
                    new() { Value = partitionKey },
                    new() { Value = documentUuid },
                }
            };

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        if (!reader.HasRows)
        {
            return null;
        }

        await reader.ReadAsync();
        JsonNode? edfiDoc = (await reader.GetFieldValueAsync<JsonElement>(0)).Deserialize<JsonNode>();

        return edfiDoc;
    }

    /// <summary>
    /// Insert a single Document into the database and return the Id of the new document
    /// </summary>
    public async Task<long> InsertDocument(
        Document document,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        await using var insertDocumentCmd = new NpgsqlCommand(
            @"INSERT INTO public.Documents(DocumentPartitionKey, DocumentUuid, ResourceName, ResourceVersion, ProjectName, EdfiDoc)
                    VALUES ($1, $2, $3, $4, $5, $6)
              RETURNING Id;",
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

        return Convert.ToInt64(await insertDocumentCmd.ExecuteScalarAsync());
    }

    /// <summary>
    /// Update the EdfiDoc of a Document and return the number of rows affected
    /// </summary>
    public async Task<int> UpdateDocumentEdFiDoc(
        int documentPartitionKey,
        Guid documentUuid,
        JsonElement edfiDoc,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        await using var upsertDocumentCmd = new NpgsqlCommand(
            @"UPDATE public.Documents
              SET EdfiDoc = $1
              WHERE DocumentPartitionKey = $2 AND DocumentUuid = $3;",
            connection,
            transaction
        )
        {
            Parameters =
            {
                new() { Value = edfiDoc },
                new() { Value = documentPartitionKey },
                new() { Value = documentUuid },
            }
        };

        return await upsertDocumentCmd.ExecuteNonQueryAsync();
    }

    public async Task<UpdateDocumentValidationResult> UpdateDocumentValidation(
        DocumentUuid documentUuid,
        PartitionKey documentPartitionKey,
        ReferentialId referentialId,
        PartitionKey referentialPartitionKey,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        await using NpgsqlCommand validationCommand =
            new(
                @"SELECT DocumentUuid, ReferentialId
                FROM public.documents d
                LEFT JOIN public.aliases a ON
                    a.DocumentId = d.Id
                    AND a.DocumentPartitionKey = d.DocumentPartitionKey
                    AND a.ReferentialId = $1 and a.ReferentialPartitionKey = $2
                WHERE d.DocumentUuid = $3 AND d.DocumentPartitionKey = $4",
                connection,
                transaction
            )
            {
                Parameters =
                {
                    new() { Value = referentialId.Value },
                    new() { Value = referentialPartitionKey.Value },
                    new() { Value = documentUuid.Value },
                    new() { Value = documentPartitionKey.Value },
                }
            };

        await using NpgsqlDataReader reader = await validationCommand.ExecuteReaderAsync();

        if (!reader.HasRows)
        {
            // Document does not exist
            return (new UpdateDocumentValidationResult(false, false));
        }

        // Assumes only one row returned (should never be more due to DB unique constraint)
        await reader.ReadAsync();

        if (await reader.IsDBNullAsync(reader.GetOrdinal("ReferentialId")))
        {
            // Extracted referential id does not match stored. Must be attempting to change natural key.
            return (new UpdateDocumentValidationResult(true, false));
        }

        return (new UpdateDocumentValidationResult(true, true));
    }

    /// <summary>
    /// Insert a single Alias into the database and return the Id of the new document
    /// </summary>
    public async Task<long> InsertAlias(
        Alias alias,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        await using var insertAliasCmd = new NpgsqlCommand(
            @"INSERT INTO public.Aliases(ReferentialPartitionKey, ReferentialId, DocumentId, DocumentPartitionKey)
                    VALUES ($1, $2, $3, $4)
              RETURNING Id;",
            connection,
            transaction
        )
        {
            Parameters =
            {
                new() { Value = alias.ReferentialPartitionKey },
                new() { Value = alias.ReferentialId },
                new() { Value = alias.DocumentId },
                new() { Value = alias.DocumentPartitionKey },
            }
        };

        return Convert.ToInt64(await insertAliasCmd.ExecuteScalarAsync());
    }

    /// <summary>
    /// Delete associated Alias records for a given DocumentId return the number of rows affected
    /// </summary>
    public async Task<int> DeleteAliasByDocumentId(
        int documentPartitionKey,
        long? documentId,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        await using NpgsqlCommand command =
            new(
                @"DELETE from public.Aliases WHERE DocumentId = $1 AND DocumentPartitionKey = $2",
                connection,
                transaction
            )
            {
                Parameters =
                {
                    new() { Value = documentId },
                    new() { Value = documentPartitionKey }
                }
            };

        int rowsAffected = await command.ExecuteNonQueryAsync();
        return rowsAffected;
    }

    /// <summary>
    /// Delete a document for a given Id and returns the number of rows affected
    /// </summary>
    public async Task<int> DeleteDocumentByDocumentId(
        int documentPartitionKey,
        long? documentId,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        await using NpgsqlCommand command =
            new(
                @"DELETE from public.Documents WHERE Id = $1 AND DocumentPartitionKey = $2",
                connection,
                transaction
            )
            {
                Parameters =
                {
                    new() { Value = documentId },
                    new() { Value = documentPartitionKey }
                }
            };

        int rowsAffected = await command.ExecuteNonQueryAsync();
        return rowsAffected;
    }

    public async Task<Document?> FindDocumentByDocumentUuid(
        int documentPartitionKey,
        DocumentUuid documentUuid,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        await using NpgsqlCommand command =
            new(
                @"SELECT * from public.Documents WHERE DocumentUuid = $1 AND DocumentPartitionKey = $2",
                connection,
                transaction
            )
            {
                Parameters =
                {
                    new() { Value = documentUuid.Value },
                    new() { Value = documentPartitionKey }
                }
            };

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        if (!reader.HasRows)
        {
            return null;
        }
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
}
