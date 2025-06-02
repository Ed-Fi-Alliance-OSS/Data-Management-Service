// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Postgresql.Model;
using EdFi.DataManagementService.Core.External.Model;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql.Operation;

public record UpdateDocumentValidationResult(bool DocumentExists, bool ReferentialIdUnchanged);

/// <summary>
/// A facade of all the DB interactions. Any action requiring SQL statement execution should be here.
/// Connections and transactions are managed by the caller.
/// Exceptions are handled by the caller.
/// </summary>
public class SqlAction() : ISqlAction
{
    /// <summary>
    /// Returns a Document from a data reader row for the Document table
    /// </summary>
    private static async Task<Document> ExtractDocumentFrom(NpgsqlDataReader reader)
    {
        return new(
            Id: reader.GetInt64(reader.GetOrdinal("Id")),
            DocumentPartitionKey: reader.GetInt16(reader.GetOrdinal("DocumentPartitionKey")),
            DocumentUuid: reader.GetGuid(reader.GetOrdinal("DocumentUuid")),
            ResourceName: reader.GetString(reader.GetOrdinal("ResourceName")),
            ResourceVersion: reader.GetString(reader.GetOrdinal("ResourceVersion")),
            IsDescriptor: reader.GetBoolean(reader.GetOrdinal("IsDescriptor")),
            ProjectName: reader.GetString(reader.GetOrdinal("ProjectName")),
            EdfiDoc: await reader.GetFieldValueAsync<JsonElement>(reader.GetOrdinal("EdfiDoc")),
            SecurityElements: await reader.GetFieldValueAsync<JsonElement>(
                reader.GetOrdinal("SecurityElements")
            ),
            StudentSchoolAuthorizationEdOrgIds: await reader.GetFieldValueAsync<JsonElement?>(
                reader.GetOrdinal("StudentSchoolAuthorizationEdOrgIds")
            ),
            ContactStudentSchoolAuthorizationEdOrgIds: await reader.GetFieldValueAsync<JsonElement?>(
                reader.GetOrdinal("ContactStudentSchoolAuthorizationEdOrgIds")
            ),
            StaffEducationOrganizationAuthorizationEdOrgIds: await reader.GetFieldValueAsync<JsonElement?>(
                reader.GetOrdinal("StaffEducationOrganizationAuthorizationEdOrgIds")
            ),
            CreatedAt: reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
            LastModifiedAt: reader.GetDateTime(reader.GetOrdinal("LastModifiedAt")),
            LastModifiedTraceId: reader.GetString(reader.GetOrdinal("LastModifiedTraceId"))
        );
    }

    /// <summary>
    /// Returns the EdfiDoc of single Document from the database corresponding to the given DocumentUuid,
    /// or null if no matching Document was found.
    /// </summary>
    public async Task<DocumentSummary?> FindDocumentEdfiDocByDocumentUuid(
        DocumentUuid documentUuid,
        string resourceName,
        PartitionKey partitionKey,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TraceId traceId
    )
    {
        await using NpgsqlCommand command = new(
            $@"SELECT EdfiDoc, SecurityElements, LastModifiedAt, LastModifiedTraceId, Id  FROM dms.Document WHERE DocumentPartitionKey = $1 AND DocumentUuid = $2 AND ResourceName = $3 {SqlBuilder.SqlFor(LockOption.BlockUpdateDelete)};",
            connection,
            transaction
        )
        {
            Parameters =
            {
                new() { Value = partitionKey.Value },
                new() { Value = documentUuid.Value },
                new() { Value = resourceName },
            },
        };

        await command.PrepareAsync();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        if (!reader.HasRows)
        {
            return null;
        }

        // Assumes only one row returned
        await reader.ReadAsync();

        return new DocumentSummary(
            EdfiDoc: await reader.GetFieldValueAsync<JsonElement>(reader.GetOrdinal("EdfiDoc")),
            SecurityElements: await reader.GetFieldValueAsync<JsonElement>(
                reader.GetOrdinal("SecurityElements")
            ),
            LastModifiedAt: reader.GetDateTime(reader.GetOrdinal("LastModifiedAt")),
            LastModifiedTraceId: reader.GetString(reader.GetOrdinal("LastModifiedTraceId")),
            DocumentId: reader.GetInt64(reader.GetOrdinal("Id"))
        );
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
        TraceId traceId
    )
    {
        await using NpgsqlCommand command = new(
            $@"SELECT * FROM dms.Document d
                INNER JOIN dms.Alias a ON a.DocumentId = d.Id AND a.DocumentPartitionKey = d.DocumentPartitionKey
                WHERE a.ReferentialPartitionKey = $1 AND a.ReferentialId = $2 {SqlBuilder.SqlFor(LockOption.BlockUpdateDelete)};",
            connection,
            transaction
        )
        {
            Parameters =
            {
                new() { Value = partitionKey.Value },
                new() { Value = referentialId.Value },
            },
        };

        await command.PrepareAsync();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        if (!reader.HasRows)
        {
            return null;
        }

        // Assumes only one row returned (should never be more due to DB unique constraint)
        await reader.ReadAsync();
        return await ExtractDocumentFrom(reader);
    }

    /// <summary>
    /// Returns an array of Documents from the database corresponding to the given ResourceName
    /// </summary>
    public async Task<JsonArray> GetAllDocumentsByResourceName(
        string resourceName,
        PaginationParameters paginationParameters,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TraceId traceId
    )
    {
        await using NpgsqlCommand command = new(
            @"SELECT EdfiDoc FROM dms.Document WHERE ResourceName = $1 ORDER BY CreatedAt OFFSET $2 ROWS FETCH FIRST $3 ROWS ONLY;",
            connection,
            transaction
        )
        {
            Parameters =
            {
                new() { Value = resourceName },
                new() { Value = paginationParameters.Offset ?? 0 },
                new() { Value = paginationParameters.Limit ?? 25 },
            },
        };

        await command.PrepareAsync();
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

        return new(documents.ToArray());
    }

    /// <summary>
    /// Returns total number of Documents from the database corresponding to the given ResourceName,
    /// or 0 if no matching Document was found.
    /// </summary>
    public async Task<int> GetTotalDocumentsForResourceName(
        string resourceName,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TraceId traceId
    )
    {
        await using NpgsqlCommand command = new(
            @"SELECT Count(1) Total FROM dms.Document WHERE resourcename = $1;",
            connection,
            transaction
        )
        {
            Parameters = { new() { Value = resourceName } },
        };

        await command.PrepareAsync();
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
    public async Task<long> InsertDocumentAndAlias(
        Document document,
        int referentialPartitionKey,
        Guid referentialId,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        await using var command = new NpgsqlCommand(
            @"
            WITH Documents AS (
            INSERT INTO dms.Document (DocumentPartitionKey, DocumentUuid, ResourceName, ResourceVersion, IsDescriptor, ProjectName, EdfiDoc, SecurityElements, StudentSchoolAuthorizationEdOrgIds, ContactStudentSchoolAuthorizationEdOrgIds, StaffEducationOrganizationAuthorizationEdOrgIds, LastModifiedTraceId)
              VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)
              RETURNING Id
            )
            INSERT INTO dms.Alias (ReferentialPartitionKey, ReferentialId, DocumentId, DocumentPartitionKey)
              SELECT $13, $14, Id, $1 FROM Documents RETURNING DocumentId;
            ",
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
                new() { Value = document.IsDescriptor },
                new() { Value = document.ProjectName },
                new() { Value = document.EdfiDoc },
                new() { Value = document.SecurityElements },
                new()
                {
                    Value = document.StudentSchoolAuthorizationEdOrgIds.HasValue
                        ? document.StudentSchoolAuthorizationEdOrgIds
                        : DBNull.Value,
                },
                new()
                {
                    Value = document.ContactStudentSchoolAuthorizationEdOrgIds.HasValue
                        ? document.ContactStudentSchoolAuthorizationEdOrgIds
                        : DBNull.Value,
                },
                new()
                {
                    Value = document.StaffEducationOrganizationAuthorizationEdOrgIds.HasValue
                        ? document.StaffEducationOrganizationAuthorizationEdOrgIds
                        : DBNull.Value,
                },
                new() { Value = document.LastModifiedTraceId },
                new() { Value = referentialPartitionKey },
                new() { Value = referentialId },
            },
        };

        await command.PrepareAsync();
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    /// <summary>
    /// Update the EdfiDoc of a Document and return the number of rows affected
    /// </summary>
    public async Task<int> UpdateDocumentEdfiDoc(
        int documentPartitionKey,
        Guid documentUuid,
        JsonElement edfiDoc,
        JsonElement securityElements,
        JsonElement? studentSchoolAuthorizationEdOrgIds,
        JsonElement? contactStudentSchoolAuthorizationEdOrgIds,
        JsonElement? staffEducationOrganizationAuthorizationEdOrgIds,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TraceId traceId
    )
    {
        await using var command = new NpgsqlCommand(
            @"UPDATE dms.Document
              SET EdfiDoc = $1,
                LastModifiedAt = clock_timestamp(),
                LastModifiedTraceId = $4,
                SecurityElements = $5,
                StudentSchoolAuthorizationEdOrgIds = $6,
                ContactStudentSchoolAuthorizationEdOrgIds = $7,
                StaffEducationOrganizationAuthorizationEdOrgIds = $8
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
                new() { Value = traceId.Value },
                new() { Value = securityElements },
                new()
                {
                    Value = studentSchoolAuthorizationEdOrgIds.HasValue
                        ? studentSchoolAuthorizationEdOrgIds
                        : DBNull.Value,
                },
                new()
                {
                    Value = contactStudentSchoolAuthorizationEdOrgIds.HasValue
                        ? contactStudentSchoolAuthorizationEdOrgIds
                        : DBNull.Value,
                },
                new()
                {
                    Value = staffEducationOrganizationAuthorizationEdOrgIds.HasValue
                        ? staffEducationOrganizationAuthorizationEdOrgIds
                        : DBNull.Value,
                },
            },
        };

        await command.PrepareAsync();
        return await command.ExecuteNonQueryAsync();
    }

    public async Task<UpdateDocumentValidationResult> UpdateDocumentValidation(
        DocumentUuid documentUuid,
        PartitionKey documentPartitionKey,
        ReferentialId referentialId,
        PartitionKey referentialPartitionKey,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TraceId traceId
    )
    {
        string sqlForLockOption = SqlBuilder.SqlFor(LockOption.BlockUpdateDelete);
        if (sqlForLockOption != "")
        {
            // Only lock the Documents table
            sqlForLockOption += " OF d";
        }

        await using NpgsqlCommand command = new(
            $@"SELECT DocumentUuid, ReferentialId
                   FROM dms.Document d
                   LEFT JOIN dms.Alias a ON
                       a.DocumentId = d.Id
                       AND a.DocumentPartitionKey = d.DocumentPartitionKey
                       AND a.ReferentialId = $1 and a.ReferentialPartitionKey = $2
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
            },
        };

        await command.PrepareAsync();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        if (!reader.HasRows)
        {
            // Document does not exist
            return new UpdateDocumentValidationResult(DocumentExists: false, ReferentialIdUnchanged: false);
        }

        // Assumes only one row returned (should never be more due to DB unique constraint)
        await reader.ReadAsync();

        if (await reader.IsDBNullAsync(reader.GetOrdinal("ReferentialId")))
        {
            // Extracted referential id does not match stored. Must be attempting to change natural key.
            return new UpdateDocumentValidationResult(DocumentExists: true, ReferentialIdUnchanged: false);
        }

        return new UpdateDocumentValidationResult(DocumentExists: true, ReferentialIdUnchanged: true);
    }

    /// <summary>
    /// Insert a single Alias into the database and return the Id of the new document
    /// </summary>
    public async Task<long> InsertAlias(
        Alias alias,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TraceId traceId
    )
    {
        await using var command = new NpgsqlCommand(
            @"INSERT INTO dms.Alias (ReferentialPartitionKey, ReferentialId, DocumentId, DocumentPartitionKey)
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
            },
        };

        await command.PrepareAsync();
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    /// <summary>
    /// Update the ReferentialId of a document by its DocumentUuid for cases
    /// when identity updates are permitted.
    /// </summary>
    public async Task<int> UpdateAliasReferentialIdByDocumentUuid(
        short referentialPartitionKey,
        Guid referentialId,
        short documentPartitionKey,
        Guid documentUuid,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TraceId traceId
    )
    {
        await using var command = new NpgsqlCommand(
            @"UPDATE dms.Alias AS a
              SET ReferentialPartitionKey = $1, ReferentialId = $2
              FROM dms.Document AS d
              WHERE d.Id = a.DocumentId AND d.DocumentPartitionKey = a.DocumentPartitionKey
              AND d.DocumentPartitionKey = $3 AND d.DocumentUuid = $4;",
            connection,
            transaction
        )
        {
            Parameters =
            {
                new() { Value = referentialPartitionKey },
                new() { Value = referentialId },
                new() { Value = documentPartitionKey },
                new() { Value = documentUuid },
            },
        };

        await command.PrepareAsync();
        return await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Attempt to insert references into the Reference table.
    /// If any referentialId is invalid, rolls back and returns an array of invalid referentialIds.
    /// </summary>
    public async Task<Guid[]> InsertReferences(
        BulkReferences bulkReferences,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TraceId traceId
    )
    {
        Trace.Assert(
            bulkReferences.ReferentialIds.Length == bulkReferences.ReferentialPartitionKeys.Length,
            "Arrays of ReferentialIds and ReferentialPartitionKeys must be the same length"
        );

        long[] parentDocumentIds = new long[bulkReferences.ReferentialIds.Length];
        Array.Fill(parentDocumentIds, bulkReferences.ParentDocumentId);

        short[] parentDocumentPartitionKeys = new short[bulkReferences.ReferentialIds.Length];
        Array.Fill(parentDocumentPartitionKeys, bulkReferences.ParentDocumentPartitionKey);

        await using var command = new NpgsqlCommand(
            @"SELECT dms.InsertReferences($1, $2, $3, $4)",
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
            },
        };
        await command.PrepareAsync();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        List<Guid> result = [];
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetGuid(0));
        }

        return result.ToArray();
    }

    /// <summary>
    /// Delete a document for a given documentUuid and returns the number of rows affected.
    /// Delete cascades to Aliases and References tables
    /// </summary>
    public async Task<int> DeleteDocumentByDocumentUuid(
        PartitionKey documentPartitionKey,
        DocumentUuid documentUuid,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TraceId traceId
    )
    {
        await using NpgsqlCommand command = new(
            @"DELETE from dms.Document WHERE DocumentPartitionKey = $1 AND DocumentUuid = $2;",
            connection,
            transaction
        )
        {
            Parameters =
            {
                new() { Value = documentPartitionKey.Value },
                new() { Value = documentUuid.Value },
            },
        };

        await command.PrepareAsync();
        return await command.ExecuteNonQueryAsync();
    }

    public async Task<string[]> FindReferencingResourceNamesByDocumentUuid(
        DocumentUuid documentUuid,
        PartitionKey documentPartitionKey,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TraceId traceId
    )
    {
        await using NpgsqlCommand command = new(
            $@"SELECT d.ResourceName FROM dms.Document d
                   INNER JOIN (
                     SELECT ParentDocumentId, ParentDocumentPartitionKey
                     FROM dms.Reference r
                     INNER JOIN dms.Document d2 ON d2.Id = r.ReferencedDocumentId
                       AND d2.DocumentPartitionKey = r.ReferencedDocumentPartitionKey
                       WHERE d2.DocumentUuid = $1 AND d2.DocumentPartitionKey = $2) AS re
                     ON re.ParentDocumentId = d.id AND re.ParentDocumentPartitionKey = d.DocumentPartitionKey
                   ORDER BY d.ResourceName {SqlBuilder.SqlFor(LockOption.BlockUpdateDelete)};",
            connection,
            transaction
        )
        {
            Parameters =
            {
                new() { Value = documentUuid.Value },
                new() { Value = documentPartitionKey.Value },
            },
        };

        await command.PrepareAsync();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        var resourceNames = new List<string>();

        while (await reader.ReadAsync())
        {
            resourceNames.Add(reader.GetString(reader.GetOrdinal("ResourceName")));
        }

        return resourceNames.Distinct().ToArray();
    }

    public async Task<Document[]> FindReferencingDocumentsByDocumentId(
        long documentId,
        short documentPartitionKey,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TraceId traceId
    )
    {
        await using NpgsqlCommand command = new(
            $@"SELECT * FROM dms.Document d
                                INNER JOIN dms.Reference r ON d.Id = r.ParentDocumentId And d.DocumentPartitionKey = r.ParentDocumentPartitionKey
                                WHERE r.ReferencedDocumentId = $1 AND r.ReferencedDocumentPartitionKey = $2
                                ORDER BY d.ResourceName {SqlBuilder.SqlFor(LockOption.BlockUpdateDelete)};",
            connection,
            transaction
        )
        {
            Parameters =
            {
                new() { Value = documentId },
                new() { Value = documentPartitionKey },
            },
        };

        await command.PrepareAsync();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        List<Document> documents = [];

        while (await reader.ReadAsync())
        {
            documents.Add(await ExtractDocumentFrom(reader));
        }

        return documents.ToArray();
    }

    public async Task<int> InsertEducationOrganizationHierarchy(
        string projectName,
        string resourceName,
        long educationOrganizationId,
        long? parentEducationOrganizationId,
        long documentId,
        short documentPartitionKey,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        await using NpgsqlCommand command = new(
            $@"INSERT INTO dms.EducationOrganizationHierarchy(ProjectName, ResourceName, EducationOrganizationId, ParentId, DocumentId, DocumentPartitionKey)
	            VALUES ($1, $2, $3, (SELECT Id FROM dms.EducationOrganizationHierarchy WHERE EducationOrganizationId = $4), $5, $6);",
            connection,
            transaction
        )
        {
            Parameters =
            {
                new() { Value = projectName },
                new() { Value = resourceName },
                new() { Value = educationOrganizationId },
                new()
                {
                    Value = parentEducationOrganizationId.HasValue
                        ? parentEducationOrganizationId.Value
                        : DBNull.Value,
                },
                new() { Value = documentId },
                new() { Value = documentPartitionKey },
            },
        };
        await command.PrepareAsync();
        return await command.ExecuteNonQueryAsync();
    }

    public async Task<int> UpdateEducationOrganizationHierarchy(
        string projectName,
        string resourceName,
        long educationOrganizationId,
        long? parentEducationOrganizationId,
        long documentId,
        short documentPartitionKey,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        await using NpgsqlCommand updateCommand = new(
            $@"UPDATE dms.EducationOrganizationHierarchy
                SET ParentId = (SELECT Id FROM dms.EducationOrganizationHierarchy WHERE EducationOrganizationId = $4)
                WHERE ProjectName = $1
                AND ResourceName = $2
                AND EducationOrganizationId = $3;",
            connection,
            transaction
        )
        {
            Parameters =
            {
                new() { Value = projectName },
                new() { Value = resourceName },
                new() { Value = educationOrganizationId },
                new()
                {
                    Value = parentEducationOrganizationId.HasValue
                        ? parentEducationOrganizationId.Value
                        : DBNull.Value,
                },
            },
        };
        await updateCommand.PrepareAsync();
        return await updateCommand.ExecuteNonQueryAsync();
    }

    public async Task<int> DeleteEducationOrganizationHierarchy(
        string projectName,
        string resourceName,
        long documentId,
        short documentPartitionKey,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        await using NpgsqlCommand command = new(
            $@"DELETE FROM dms.EducationOrganizationHierarchy
	            WHERE ProjectName = $1
                AND ResourceName = $2
                AND DocumentId = $3
                AND DocumentPartitionKey = $4;",
            connection,
            transaction
        )
        {
            Parameters =
            {
                new() { Value = projectName },
                new() { Value = resourceName },
                new() { Value = documentId },
                new() { Value = documentPartitionKey },
            },
        };
        await command.PrepareAsync();
        return await command.ExecuteNonQueryAsync();
    }

    public async Task<long[]> GetAncestorEducationOrganizationIds(
        long[] educationOrganizationIds,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        await using NpgsqlCommand command = new(
            $"""
                WITH RECURSIVE ParentHierarchy(Id, EducationOrganizationId, ParentId) AS (
                SELECT h.Id, h.EducationOrganizationId, h.ParentId
                FROM dms.EducationOrganizationHierarchy h
                WHERE h.EducationOrganizationId = ANY($1)

                UNION ALL

                SELECT parent.Id, parent.EducationOrganizationId, parent.ParentId
                FROM dms.EducationOrganizationHierarchy parent
                JOIN ParentHierarchy child ON parent.Id = child.ParentId
                )
                SELECT EducationOrganizationId
                FROM ParentHierarchy
                ORDER BY EducationOrganizationId
                {SqlBuilder.SqlFor(LockOption.BlockUpdateDelete)};
            """,
            connection,
            transaction
        )
        {
            Parameters = { new() { Value = educationOrganizationIds } },
        };
        await command.PrepareAsync();

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        List<long> edOrgIds = [];

        while (await reader.ReadAsync())
        {
            edOrgIds.Add(reader.GetInt64(reader.GetOrdinal("EducationOrganizationId")));
        }

        return edOrgIds.Distinct().ToArray();
    }

    public async Task<JsonElement?> GetStudentSchoolAuthorizationEducationOrganizationIds(
        string studentUniqueId,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        await using NpgsqlCommand command = new(
            $"""
                SELECT jsonb_agg(DISTINCT value)
                FROM (
                    SELECT DISTINCT jsonb_array_elements(StudentSchoolAuthorizationEducationOrganizationIds) AS value
                    FROM dms.StudentSchoolAssociationAuthorization
                    WHERE StudentUniqueId = $1
                ) subquery;
            """,
            connection,
            transaction
        )
        {
            Parameters = { new() { Value = studentUniqueId } },
        };

        await command.PrepareAsync();
        object? result = await command.ExecuteScalarAsync();

        return result == DBNull.Value || result == null
            ? null
            : JsonSerializer.Deserialize<JsonElement>((string)result);
    }

    public async Task<JsonElement?> GetContactStudentSchoolAuthorizationEducationOrganizationIds(
        string contactUniqueId,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        await using NpgsqlCommand command = new(
            $"""
                SELECT jsonb_agg(DISTINCT value)
                FROM (
                    SELECT DISTINCT jsonb_array_elements(ContactStudentSchoolAuthorizationEducationOrganizationIds) AS value
                    FROM dms.ContactStudentSchoolAuthorization
                    WHERE ContactUniqueId = $1
                ) subquery;
            """,
            connection,
            transaction
        )
        {
            Parameters = { new() { Value = contactUniqueId } },
        };

        await command.PrepareAsync();
        object? result = await command.ExecuteScalarAsync();

        return result == DBNull.Value || result == null
            ? null
            : JsonSerializer.Deserialize<JsonElement>((string)result);
    }

    public async Task<JsonElement?> GetStaffEducationOrganizationAuthorizationEdOrgIds(
        string staffUniqueId,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        await using NpgsqlCommand command = new(
            $"""
                SELECT jsonb_agg(DISTINCT value)
                FROM (
                    SELECT DISTINCT jsonb_array_elements(staffeducationorganizationauthorizationedorgids) AS value
                    FROM dms.StaffEducationOrganizationAuthorization
                    WHERE StaffUniqueId = $1
                ) subquery;
            """,
            connection,
            transaction
        )
        {
            Parameters = { new() { Value = staffUniqueId } },
        };

        await command.PrepareAsync();
        object? result = await command.ExecuteScalarAsync();

        return result == DBNull.Value || result == null
            ? null
            : JsonSerializer.Deserialize<JsonElement>((string)result);
    }

    public async Task<int> InsertStudentSecurableDocument(
        string studentUniqueId,
        long documentId,
        short documentPartitionKey,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        await using NpgsqlCommand command = new(
            $@"INSERT INTO dms.studentsecurabledocument(
	            studentuniqueid, studentsecurabledocumentid, studentsecurabledocumentpartitionkey)
	          VALUES ($1, $2, $3);",
            connection,
            transaction
        )
        {
            Parameters =
            {
                new() { Value = studentUniqueId },
                new() { Value = documentId },
                new() { Value = documentPartitionKey },
            },
        };
        await command.PrepareAsync();
        return await command.ExecuteNonQueryAsync();
    }

    public async Task<int> InsertContactSecurableDocument(
        string contactUniqueId,
        long documentId,
        short documentPartitionKey,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        await using NpgsqlCommand command = new(
            $@"INSERT INTO dms.contactsecurabledocument(
	            contactuniqueid, contactsecurabledocumentid, contactsecurabledocumentpartitionkey)
	          VALUES ($1, $2, $3);",
            connection,
            transaction
        )
        {
            Parameters =
            {
                new() { Value = contactUniqueId },
                new() { Value = documentId },
                new() { Value = documentPartitionKey },
            },
        };
        await command.PrepareAsync();
        return await command.ExecuteNonQueryAsync();
    }

    public async Task<int> InsertStaffSecurableDocument(
        string staffUniqueId,
        long documentId,
        short documentPartitionKey,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        await using NpgsqlCommand command = new(
            $@"INSERT INTO dms.staffsecurabledocument(
	            staffuniqueid, staffsecurabledocumentid, staffsecurabledocumentpartitionkey)
	          VALUES ($1, $2, $3);",
            connection,
            transaction
        )
        {
            Parameters =
            {
                new() { Value = staffUniqueId },
                new() { Value = documentId },
                new() { Value = documentPartitionKey },
            },
        };
        await command.PrepareAsync();
        return await command.ExecuteNonQueryAsync();
    }

    public async Task<int> UpdateStudentSecurableDocument(
        string studentUniqueId,
        long documentId,
        short documentPartitionKey,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        await using NpgsqlCommand command = new(
            $@"UPDATE dms.studentsecurabledocument
	            SET studentuniqueid = $1
                WHERE studentsecurabledocumentid = $2 AND studentsecurabledocumentpartitionkey = $3",
            connection,
            transaction
        )
        {
            Parameters =
            {
                new() { Value = studentUniqueId },
                new() { Value = documentId },
                new() { Value = documentPartitionKey },
            },
        };
        await command.PrepareAsync();
        return await command.ExecuteNonQueryAsync();
    }

    public async Task<int> UpdateContactSecurableDocument(
        string contactUniqueId,
        long documentId,
        short documentPartitionKey,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        await using NpgsqlCommand command = new(
            $@"UPDATE dms.contactsecurabledocument
	            SET contactuniqueid = $1
                WHERE contactsecurabledocumentid = $2 AND contactsecurabledocumentpartitionkey = $3",
            connection,
            transaction
        )
        {
            Parameters =
            {
                new() { Value = contactUniqueId },
                new() { Value = documentId },
                new() { Value = documentPartitionKey },
            },
        };
        await command.PrepareAsync();
        return await command.ExecuteNonQueryAsync();
    }

    public async Task<int> UpdateStaffSecurableDocument(
        string staffUniqueId,
        long documentId,
        short documentPartitionKey,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction
    )
    {
        await using NpgsqlCommand command = new(
            $@"UPDATE dms.staffsecurabledocument
	            SET staffuniqueid = $1
                WHERE staffsecurabledocumentid = $2 AND staffsecurabledocumentpartitionkey = $3",
            connection,
            transaction
        )
        {
            Parameters =
            {
                new() { Value = staffUniqueId },
                new() { Value = documentId },
                new() { Value = documentPartitionKey },
            },
        };
        await command.PrepareAsync();
        return await command.ExecuteNonQueryAsync();
    }
}
