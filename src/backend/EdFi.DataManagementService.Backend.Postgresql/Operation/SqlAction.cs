// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
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
    public Task<JsonNode?> FindDocumentEdfiDocByDocumentUuid(
        DocumentUuid documentUuid,
        PartitionKey partitionKey,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LockOption lockOption
    );

    public Task<Document?> FindDocumentByReferentialId(
        ReferentialId referentialId,
        PartitionKey partitionKey,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LockOption lockOption
    );

    public Task<string?> FindReferencingResourceNameByDocumentUuid(
        DocumentUuid documentUuid,
        PartitionKey documentPartitionKey,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LockOption lockOption
    );

    public Task<JsonNode[]> GetAllDocuments(
        string resourceName,
        IPaginationParameters paginationParameters,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    );

    public Task<int> GetTotalDocuments(
        string resourceName,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    );

    public Task<long> InsertDocument(
        Document document,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    );

    public Task<long> InsertAlias(Alias alias, NpgsqlConnection connection, NpgsqlTransaction transaction);

    /// <summary>
    /// Insert a set of rows into the References table and return the number of rows affected
    /// </summary>
    public Task<int> InsertReferences(
        BulkReferences bulkReferences,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    );

    /// <summary>
    /// Given an array of referentialId guids and a parallel array of partition keys, returns
    /// an array of invalid referentialId guids, if any
    /// </summary>
    public Task<Guid[]> FindInvalidReferentialIds(
        DocumentReferenceIds documentReferenceIds,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    );

    /// <summary>
    /// Delete associated Reference records for a given DocumentUuid, returning the number of rows affected
    /// </summary>
    public Task<int> DeleteReferencesByDocumentUuid(
        int parentDocumentPartitionKey,
        Guid parentDocumentUuidGuid,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    );

    /// <summary>
    /// Delete a document for a given documentUuid and returns the number of rows affected.
    /// Delete cascades to Aliases and References tables
    /// </summary>
    public Task<int> DeleteDocumentByDocumentUuid(
        PartitionKey documentPartitionKey,
        DocumentUuid documentUuid,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    );

    public Task<int> UpdateDocumentEdfiDoc(
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
        NpgsqlTransaction transaction,
        LockOption lockOption
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
    private static string SqlFor(LockOption lockOption)
    {
        return lockOption switch
        {
            LockOption.None => "",
            LockOption.BlockUpdateDelete => "FOR SHARE",
            LockOption.BlockAll => "FOR UPDATE",
            _ => throw new InvalidOperationException("Unknown lock option type"),
        };
    }

    /// <summary>
    /// Returns the EdfiDoc of single Document from the database corresponding to the given DocumentUuid,
    /// or null if no matching Document was found.
    /// </summary>
    public async Task<JsonNode?> FindDocumentEdfiDocByDocumentUuid(
        DocumentUuid documentUuid,
        PartitionKey partitionKey,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LockOption lockOption
    )
    {
        await using NpgsqlCommand command =
            new(
                $@"SELECT EdfiDoc FROM public.Documents WHERE DocumentPartitionKey = $1 AND DocumentUuid = $2 {SqlFor(lockOption)};",
                connection,
                transaction
            )
            {
                Parameters =
                {
                    new() { Value = partitionKey.Value },
                    new() { Value = documentUuid.Value },
                }
            };

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        if (!reader.HasRows)
        {
            return null;
        }

        // Assumes only one row returned
        await reader.ReadAsync();
        return (await reader.GetFieldValueAsync<JsonElement>(0)).Deserialize<JsonNode>();
    }

    /// <summary>
    /// Returns a single Document from the database corresponding to the given ReferentialId,
    /// or null if no matching Document was found.
    /// </summary>
    public async Task<Document?> FindDocumentByReferentialId(
        ReferentialId referentialId,
        PartitionKey partitionKey,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LockOption lockOption
    )
    {
        await using NpgsqlCommand command =
            new(
                $@"SELECT * FROM public.Documents d
                INNER JOIN public.Aliases a ON a.DocumentId = d.Id AND a.DocumentPartitionKey = d.DocumentPartitionKey
                WHERE a.ReferentialPartitionKey = $1 AND a.ReferentialId = $2 {SqlFor(lockOption)};",
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
    /// Returns an array of Documents from the database corresponding to the given ResourceName
    /// </summary>
    public async Task<JsonNode[]> GetAllDocuments(
        string resourceName,
        IPaginationParameters paginationParameters,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        await using NpgsqlCommand command =
            new(
                @"SELECT EdfiDoc FROM public.Documents WHERE ResourceName = $1 ORDER BY CreatedAt OFFSET $2 ROWS FETCH FIRST $3 ROWS ONLY;",
                connection,
                transaction
            )
            {
                Parameters =
                {
                    new() { Value = resourceName },
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
    /// Returns total number of Documents from the database corresponding to the given ResourceName,
    /// or 0 if no matching Document was found.
    /// </summary>
    public async Task<int> GetTotalDocuments(
        string resourceName,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        await using NpgsqlCommand command =
            new(@"SELECT Count(1) Total FROM public.Documents WHERE resourcename = $1;", connection)
            {
                Parameters = { new() { Value = resourceName }, }
            };

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        if (!reader.HasRows)
        {
            return 0;
        }

        await reader.ReadAsync();

        return reader.GetInt16(reader.GetOrdinal("Total"));
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
    public async Task<int> UpdateDocumentEdfiDoc(
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
              WHERE DocumentPartitionKey = $2 AND DocumentUuid = $3
              RETURNING Id;",
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
        NpgsqlTransaction transaction,
        LockOption lockOption
    )
    {
        string sqlForLockOption = SqlFor(lockOption);
        if (sqlForLockOption != "")
        {
            // Only lock the Documents table
            sqlForLockOption += " OF d";
        }

        await using NpgsqlCommand validationCommand =
            new(
                $@"SELECT DocumentUuid, ReferentialId
                FROM public.documents d
                LEFT JOIN public.aliases a ON
                    a.DocumentId = d.Id
                    AND a.DocumentPartitionKey = d.DocumentPartitionKey
                    AND a.ReferentialId = $1
                    AND a.ReferentialPartitionKey = $2
                WHERE d.DocumentUuid = $3 AND d.DocumentPartitionKey = $4 {sqlForLockOption};",
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
            return new UpdateDocumentValidationResult(false, false);
        }

        // Assumes only one row returned (should never be more due to DB unique constraint)
        await reader.ReadAsync();

        if (await reader.IsDBNullAsync(reader.GetOrdinal("ReferentialId")))
        {
            // Extracted referential id does not match stored. Must be attempting to change natural key.
            return new UpdateDocumentValidationResult(true, false);
        }

        return new UpdateDocumentValidationResult(true, true);
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
    /// Insert a set of rows into the References table and return the number of rows affected
    /// </summary>
    public async Task<int> InsertReferences(
        BulkReferences bulkReferences,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        Trace.Assert(
            bulkReferences.ReferentialIds.Length == bulkReferences.ReferentialPartitionKeys.Length,
            "Arrays of ReferentialIds and ReferentialPartitionKeys must be the same length"
        );

        long[] parentDocumentIds = new long[bulkReferences.ReferentialIds.Length];
        Array.Fill(parentDocumentIds, bulkReferences.ParentDocumentId);

        int[] parentDocumentPartitionKeys = new int[bulkReferences.ReferentialIds.Length];
        Array.Fill(parentDocumentPartitionKeys, bulkReferences.ParentDocumentPartitionKey);

        await using var insertBulkReferencesCmd = new NpgsqlCommand(
            @"INSERT INTO public.""references""(ParentDocumentId, ParentDocumentPartitionKey, ReferentialId, ReferentialPartitionKey)
                    SELECT * FROM unnest($1, $2, $3, $4)",
            connection,
            transaction
        )
        {
            Parameters =
            {
                new() { Value = parentDocumentIds },
                new() { Value = parentDocumentPartitionKeys },
                new() { Value = bulkReferences.ReferentialIds },
                new() { Value = bulkReferences.ReferentialPartitionKeys },
            }
        };

        return await insertBulkReferencesCmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Given an array of referentialId guids and a parallel array of partition keys, returns
    /// an array of invalid referentialId guids, if any.
    ///
    /// Note the db command is run in a separate transaction because the original transaction
    /// is invalidated by FK violations. This means there is a slight chance of staleness,
    /// which should be acceptable.
    /// </summary>
    public async Task<Guid[]> FindInvalidReferentialIds(
        DocumentReferenceIds documentReferenceIds,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        await using var command = new NpgsqlCommand(
            @$"SELECT r.ReferentialId
               FROM ROWS FROM
                 (unnest($1::uuid[]), unnest($2::integer[]))
                 AS r (ReferentialId, ReferentialPartitionKey)
               WHERE NOT EXISTS (
                 SELECT 1
                 FROM Aliases a
                 WHERE r.ReferentialId = a.ReferentialId
                 AND r.ReferentialPartitionKey = a.ReferentialPartitionKey)",
            connection,
            transaction
        )
        {
            Parameters =
            {
                new() { Value = documentReferenceIds.ReferentialIds },
                new() { Value = documentReferenceIds.ReferentialPartitionKeys },
            }
        };

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        List<Guid> result = [];
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetGuid(reader.GetOrdinal("ReferentialId")));
        }

        return result.ToArray();
    }

    /// <summary>
    /// Delete associated Reference records for a given DocumentUuid, returning the number of rows affected
    /// </summary>
    public async Task<int> DeleteReferencesByDocumentUuid(
        int parentDocumentPartitionKey,
        Guid parentDocumentUuidGuid,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        await using NpgsqlCommand command =
            new(
                @"DELETE from public.""references"" r
                  USING public.Documents d
                  WHERE d.Id = r.ParentDocumentId AND d.DocumentPartitionKey = r.ParentDocumentPartitionKey
                  AND d.DocumentPartitionKey = $1 AND d.DocumentUuid = $2;",
                connection,
                transaction
            )
            {
                Parameters =
                {
                    new() { Value = parentDocumentPartitionKey },
                    new() { Value = parentDocumentUuidGuid }
                }
            };

        int rowsAffected = await command.ExecuteNonQueryAsync();
        return rowsAffected;
    }

    /// <summary>
    /// Delete a document for a given documentUuid and returns the number of rows affected.
    /// Delete cascades to Aliases and References tables
    /// </summary>
    public async Task<int> DeleteDocumentByDocumentUuid(
        PartitionKey documentPartitionKey,
        DocumentUuid documentUuid,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        await using NpgsqlCommand command =
            new(
                @"DELETE from public.Documents WHERE DocumentPartitionKey = $1 AND DocumentUuid = $2;",
                connection,
                transaction
            )
            {
                Parameters =
                {
                    new() { Value = documentPartitionKey.Value },
                    new() { Value = documentUuid.Value },
                }
            };

        int rowsAffected = await command.ExecuteNonQueryAsync();
        return rowsAffected;
    }

    public async Task<string?> FindReferencingResourceNameByDocumentUuid(
        DocumentUuid documentUuid,
        PartitionKey documentPartitionKey,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LockOption lockOption
    )
    {
        await using NpgsqlCommand command =
            new(
                $@"SELECT  d.ResourceName FROM public.Documents d INNER JOIN (
                   SELECT ParentDocumentId, ParentDocumentPartitionKey FROM public.""references"" r
                   INNER JOIN public.Aliases a ON r.ReferentialId = a.ReferentialId AND r.ReferentialPartitionKey = a.ReferentialPartitionKey
                   INNER JOIN public.Documents d2 ON d2.Id = a.DocumentId AND d2.DocumentPartitionKey = a.DocumentPartitionKey
                   WHERE d2.DocumentUuid =$1 AND d2.DocumentPartitionKey = $2) AS re
                   ON re.ParentDocumentId = d.id AND re.ParentDocumentPartitionKey = d.DocumentPartitionKey {SqlFor(lockOption)};",
                connection,
                transaction
            )
            {
                Parameters =
                {
                    new() { Value = documentUuid.Value },
                    new() { Value = documentPartitionKey.Value }
                }
            };
        try
        {
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            if (!reader.HasRows)
            {
                return null;
            }
            await reader.ReadAsync();

            return reader.GetString(reader.GetOrdinal("ResourceName"));
        }
        catch (Exception ex)
        {
            throw new NpgsqlException(ex.Message, ex);
        }
    }
}
